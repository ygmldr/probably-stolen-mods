"""P2 memory domain: a **separate** SQLite database for correctable knowledge.

Why a separate file: the recorder's ``append_intent`` is fail-closed (if history
cannot be written, the game action is refused).  Keeping reflection/memory
writes in the same database would let a memory writer contend for the event
writer's lock and turn a lock timeout into a refused game action.  The memory
store therefore owns ``play/memory/memories.db`` and never writes the events
database.

Cross-database evidence: ``memory_evidence.event_id`` points at event ids in the
events database, but no SQLite FK can span two files.  When the events database
exists, it is attached read-only as ``events`` for the duration of a write to
validate every event id (the events file is never modified).  When it is absent
or cannot be attached, the claim is still stored -- so the memory store remains
usable standalone -- but with ``evidence_unverified=1`` and evidence grade
``unknown``; ``verify`` refuses to promote such a row until it is revalidated.
"""

import hashlib
import json
import os
import re
import sqlite3
import threading
import time
import unicodedata
import uuid
from contextlib import contextmanager
from urllib.request import pathname2url

from .store import MemoryUnavailable

SCHEMA_VERSION = 2
MEMORY_SCHEMA_VERSION = SCHEMA_VERSION

#: Default location (relative to ``PawnMind/``).  Tests always pass a temp path.
DEFAULT_MEMORIES_DB = os.path.join("play", "memory", "memories.db")

GAME_ID = "probably_stolen"

KINDS = frozenset({
    "fact", "mechanism", "hypothesis", "lesson", "procedure",
    "npc_profile", "price_observation", "strategy", "warning",
})

# P3 design D4: derived from the event, not a copy of P1's capture_quality.
EVIDENCE_GRADES = frozenset({"observed", "reported", "unknown"})

STATUS_CANDIDATE = "candidate"
STATUS_VERIFIED = "verified"
STATUS_CONFLICT = "conflict"
STATUS_DEPRECATED = "deprecated"
STATUS_SUPERSEDED = "superseded"

TERMINAL_STATUSES = frozenset({STATUS_DEPRECATED, STATUS_SUPERSEDED})
DEFAULT_RETRIEVAL_EXCLUDES = (STATUS_DEPRECATED, STATUS_SUPERSEDED)

_CONTENT_FIELDS = frozenset({
    "kind", "text", "game_version", "save_id", "run_id", "scope_extra",
})
_REVISION_FIELDS = _CONTENT_FIELDS | {
    "confidence", "source", "valid_from", "valid_until",
}
_CREATE_STATUSES = frozenset({STATUS_CANDIDATE, STATUS_CONFLICT})

_TOKEN_RE = re.compile(r"[0-9A-Za-z_]+|[\u3400-\u4dbf\u4e00-\u9fff]+")

_TRIM_CHARS = " \t\r\n.。!！?？;；,，:：、"

_MEMORY_SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memories (
  memory_id TEXT PRIMARY KEY,
  game_id TEXT NOT NULL,
  game_version TEXT NOT NULL DEFAULT 'unknown',
  save_id TEXT,
  run_id TEXT,
  scope_extra_json TEXT NOT NULL DEFAULT '{}',
  kind TEXT NOT NULL,
  text TEXT NOT NULL,
  status TEXT NOT NULL,
  confidence REAL NOT NULL DEFAULT 0.5,
  source TEXT NOT NULL DEFAULT 'unknown',
  dedupe_key TEXT UNIQUE,
  timeline_id TEXT,
  valid_from TEXT,
  valid_until TEXT,
  superseded_by TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  hit_count INTEGER NOT NULL DEFAULT 0,
  last_used_at TEXT,
  evidence_unverified INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_mem_scope
  ON memories(game_version, status, kind);
CREATE INDEX IF NOT EXISTS idx_mem_identity
  ON memories(save_id, run_id, timeline_id);
CREATE INDEX IF NOT EXISTS idx_mem_status ON memories(status);

CREATE TABLE IF NOT EXISTS memory_evidence (
  memory_id TEXT NOT NULL REFERENCES memories(memory_id) ON DELETE CASCADE,
  event_id TEXT NOT NULL,
  weight REAL NOT NULL DEFAULT 1.0,
  evidence_grade TEXT NOT NULL
    CHECK (evidence_grade IN ('observed', 'reported', 'unknown')),
  PRIMARY KEY(memory_id, event_id)
);
CREATE INDEX IF NOT EXISTS idx_evidence_event ON memory_evidence(event_id);

CREATE TABLE IF NOT EXISTS memory_revisions (
  memory_id TEXT NOT NULL REFERENCES memories(memory_id) ON DELETE CASCADE,
  revision INTEGER NOT NULL,
  changed_fields_json TEXT NOT NULL DEFAULT '[]',
  reason TEXT NOT NULL,
  actor TEXT NOT NULL,
  created_at TEXT NOT NULL,
  PRIMARY KEY(memory_id, revision)
);

CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
  text, kind,
  content='memories', content_rowid='rowid',
  tokenize='unicode61'
);

CREATE TRIGGER IF NOT EXISTS memories_fts_ai AFTER INSERT ON memories BEGIN
  INSERT INTO memories_fts(rowid, text, kind)
  VALUES(new.rowid, new.text, new.kind);
END;

CREATE TRIGGER IF NOT EXISTS memories_fts_ad AFTER DELETE ON memories BEGIN
  INSERT INTO memories_fts(memories_fts, rowid, text, kind)
  VALUES('delete', old.rowid, old.text, old.kind);
END;

CREATE TRIGGER IF NOT EXISTS memories_fts_au AFTER UPDATE ON memories BEGIN
  INSERT INTO memories_fts(memories_fts, rowid, text, kind)
  VALUES('delete', old.rowid, old.text, old.kind);
  INSERT INTO memories_fts(rowid, text, kind)
  VALUES(new.rowid, new.text, new.kind);
END;

INSERT OR IGNORE INTO meta(key, value) VALUES('schema_version', '%d');
""" % SCHEMA_VERSION


class MemoryValidationError(ValueError):
    """Raised when a memory write is malformed (bad kind, illegal transition)."""


# --------------------------------------------------------------- helpers


def _now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _dumps(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"))


def _clean_str(value, field):
    if not isinstance(value, str) or not value.strip():
        raise MemoryValidationError("%s must be a non-empty string" % field)
    return value.strip()


def _optional_id(value, field):
    if value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        raise MemoryValidationError("%s must be a non-empty string or None"
                                    % field)
    return value.strip()


def _timestamp(value, field):
    if value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        raise MemoryValidationError("%s must be an ISO timestamp" % field)
    return value.strip()


def _confidence(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise MemoryValidationError("confidence must be a number in [0, 1]")
    value = float(value)
    if not 0.0 <= value <= 1.0:
        raise MemoryValidationError("confidence must be in [0, 1]")
    return value


def _weight(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise MemoryValidationError("evidence weight must be a number")
    value = float(value)
    if value < 0:
        raise MemoryValidationError("evidence weight must be >= 0")
    return value


def _normalise_text(text):
    normalised = unicodedata.normalize("NFKC", text).lower()
    normalised = " ".join(normalised.split())
    return normalised.strip(_TRIM_CHARS)


def _derive_dedupe_key(kind, game_id, game_version, save_id, run_id, text,
                       scope_extra):
    """Stable key for "same claim" (P3 §6.3), minus presentation-only notes."""
    stable_extra = {key: value for key, value in (scope_extra or {}).items()
                    if key not in ("why", "notes")}
    payload = "|".join([
        kind,
        game_id or "",
        game_version or "",
        save_id or "",
        run_id or "",
        _normalise_text(text),
        _dumps(stable_extra),
    ])
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _derive_grade(event_row):
    """Derive P3's evidence grade from a P1 event without touching P1.

    ``unknown`` outcomes and still-pending intents stay unknown.  ``complete``
    observations are treated as ``observed``; P1's normal write-result
    ``partial`` becomes ``reported``.  This deliberately does not require
    ``capture_quality == 'complete'`` to verify: P1 marks every write result
    ``partial``, so the literal ADR-005 reading would make verification
    impossible (P3 §9.3).
    """
    if event_row["result_status"] in ("unknown", "pending"):
        return "unknown"
    if event_row["capture_quality"] == "complete":
        return "observed"
    return "reported"


def _escape_like(text):
    return text.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


def _query_tokens(query):
    return _TOKEN_RE.findall(unicodedata.normalize("NFKC", query or ""))


def _build_fts_expression(query):
    """Return ``(match_expression, used_cjk_prefix)``.

    unicode61 treats a run of CJK characters as one token, so a query longer
    than the memory's token never prefix-matches.  Using the first two CJK
    characters as the prefix is the cheap, dependency-free fallback.
    """
    terms = []
    used_cjk = False
    for token in _query_tokens(query):
        if token[0].isascii():
            terms.append('"%s"*' % token)
        else:
            used_cjk = True
            prefix = token[:2] if len(token) > 2 else token
            terms.append('"%s"*' % prefix)
    return " OR ".join(terms), used_cjk


def _like_patterns(query):
    """Substring patterns for mid-token CJK recall (no external tokenizer)."""
    patterns = []
    seen = set()
    for token in _query_tokens(query):
        candidates = [token]
        if token and not token[0].isascii() and len(token) > 2:
            candidates.extend(token[index:index + 2]
                              for index in range(len(token) - 1))
        for candidate in candidates:
            if len(candidate) < 1 or candidate in seen:
                continue
            seen.add(candidate)
            patterns.append("%" + _escape_like(candidate) + "%")
            if len(patterns) >= 12:
                return patterns
    return patterns


# --------------------------------------------------------------- domain


class MemoryStore(object):
    """Correctable memory in its own SQLite file.

    ``events_db`` is optional; when present it is attached read-only during
    writes/verification so event ids and scope can be checked.  Memory writes
    never modify the events database.
    """

    def __init__(self, path=DEFAULT_MEMORIES_DB, events_db=None):
        self.path = os.path.abspath(path)
        self.events_db = os.path.abspath(events_db) if events_db else None
        self._lock = threading.RLock()
        self._conn = None
        self._closed = False
        self._open()

    # ------------------------------------------------------------------ setup

    def _open(self):
        try:
            parent = os.path.dirname(self.path)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            conn = sqlite3.connect(self.path, timeout=15, isolation_level=None,
                                   check_same_thread=False, uri=True)
            conn.row_factory = sqlite3.Row
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA busy_timeout=15000")
            conn.execute("PRAGMA foreign_keys=ON")
            self._conn = conn
            self._migrate()
        except MemoryUnavailable:
            raise
        except Exception as exc:
            raise MemoryUnavailable("cannot open memory store: %s" % exc)

    def _migrate(self):
        """Create/upgrade the memory schema. Idempotent on reopen."""
        with self._lock:
            conn = self._conn
            try:
                row = conn.execute(
                    "SELECT value FROM meta WHERE key='schema_version'"
                ).fetchone()
            except sqlite3.OperationalError:
                row = None
            found = None
            if row is not None:
                try:
                    found = int(row["value"])
                except (TypeError, ValueError):
                    raise MemoryUnavailable(
                        "unsupported memory schema version %r"
                        % (row["value"],))
            if found is not None and found not in (1, SCHEMA_VERSION):
                raise MemoryUnavailable(
                    "unsupported memory schema version %d (expected %d)"
                    % (found, SCHEMA_VERSION))
            conn.executescript(_MEMORY_SCHEMA)
            if found != SCHEMA_VERSION:
                conn.execute(
                    "INSERT INTO memories_fts(memories_fts) VALUES('rebuild')")
                if found is None:
                    conn.execute(
                        "INSERT OR REPLACE INTO meta(key, value)"
                        " VALUES('schema_version', ?)", (str(SCHEMA_VERSION),))
                else:
                    conn.execute(
                        "UPDATE meta SET value=? WHERE key='schema_version'",
                        (str(SCHEMA_VERSION),))

    def close(self):
        with self._lock:
            self._closed = True
            if self._conn is not None:
                try:
                    self._conn.close()
                finally:
                    self._conn = None

    def _require(self):
        if self._closed or self._conn is None:
            raise MemoryUnavailable("memory store is closed")
        return self._conn

    def _rollback(self):
        try:
            self._conn.execute("ROLLBACK")
        except Exception:
            pass

    # ------------------------------------------------------------- inspection

    def table_names(self):
        rows = self._require().execute(
            "SELECT name FROM sqlite_master WHERE type='table'").fetchall()
        return {row["name"] for row in rows}

    def schema_version(self):
        row = self._require().execute(
            "SELECT value FROM meta WHERE key='schema_version'").fetchone()
        return int(row["value"]) if row else None

    def events_db_available(self):
        return bool(self.events_db and os.path.isfile(self.events_db))

    # ------------------------------------------------------- events DB attach

    @contextmanager
    def _attached_events(self):
        """Attach the events DB read-only (``mode=ro``) for validation.

        Yields True only when the attach succeeded *and* the file really looks
        like an events database (both tables and the columns we query).  Any
        other SQLite file is rejected as unavailable, so a foreign/corrupt path
        degrades to an ``evidence_unverified`` write instead of an exception.
        """
        attached = False
        should_attach = (self.events_db
                         and os.path.isfile(self.events_db)
                         and os.path.abspath(self.events_db) != self.path)
        if should_attach:
            try:
                uri = "file:%s?mode=ro" % pathname2url(
                    os.path.abspath(self.events_db))
                self._conn.execute("ATTACH DATABASE ? AS events", (uri,))
            except sqlite3.Error:
                attached = False
            else:
                try:
                    self._conn.execute(
                        "SELECT event_id, timeline_id, result_status,"
                        " capture_quality FROM events.events LIMIT 0")
                    self._conn.execute(
                        "SELECT game_version FROM events.timelines LIMIT 0")
                    attached = True
                except sqlite3.Error:
                    try:
                        self._conn.execute("DETACH DATABASE events")
                    except sqlite3.Error:
                        pass
                    attached = False
        try:
            yield attached
        finally:
            if attached:
                try:
                    self._conn.execute("DETACH DATABASE events")
                except sqlite3.Error:
                    pass

    def _event_rows(self, event_ids, attached):
        if not attached or not event_ids:
            return {}
        placeholders = ",".join("?" for _ in event_ids)
        rows = self._conn.execute(
            "SELECT event_id, timeline_id, result_status, capture_quality"
            " FROM events.events WHERE event_id IN (%s)" % placeholders,
            list(event_ids)).fetchall()
        return {row["event_id"]: row for row in rows}

    def _event_timeline_version(self, timeline_id, attached):
        if not attached or not timeline_id:
            return None
        row = self._conn.execute(
            "SELECT game_version FROM events.timelines WHERE timeline_id=?",
            (timeline_id,)).fetchone()
        return row["game_version"] if row else None

    # --------------------------------------------------------------- helpers

    def _evidence(self, memory_id):
        rows = self._require().execute(
            "SELECT event_id, weight, evidence_grade FROM memory_evidence"
            " WHERE memory_id=? ORDER BY rowid ASC", (memory_id,)).fetchall()
        return [dict(row) for row in rows]

    def _row_to_memory(self, row, include_evidence=True, evidence_count=None):
        item = dict(row)
        raw_extra = item.pop("scope_extra_json", "{}")
        try:
            item["scope_extra"] = json.loads(raw_extra or "{}")
        except ValueError:
            item["scope_extra"] = {}
        item["confidence"] = float(item["confidence"])
        item["evidence_unverified"] = int(item["evidence_unverified"])
        if include_evidence:
            evidence = self._evidence(item["memory_id"])
            item["evidence"] = evidence
            item["evidence_count"] = len(evidence)
        else:
            item["evidence"] = []
            item["evidence_count"] = int(evidence_count or 0)
        return item

    def _normalise_evidence(self, evidence):
        if evidence is None:
            evidence = []
        if isinstance(evidence, (str, dict)):
            evidence = [evidence]
        normalised = []
        seen = set()
        for item in evidence:
            if isinstance(item, str):
                event_id = item
                weight = 1.0
                grade = None
            elif isinstance(item, dict):
                event_id = item.get("event_id")
                weight = item.get("weight", 1.0)
                grade = item.get("evidence_grade")
            else:
                raise MemoryValidationError(
                    "evidence must contain event ids or mappings")
            event_id = _clean_str(event_id, "evidence.event_id")
            if grade is not None and grade not in EVIDENCE_GRADES:
                raise MemoryValidationError(
                    "unknown evidence grade %r" % (grade,))
            if event_id in seen:
                continue
            seen.add(event_id)
            normalised.append({
                "event_id": event_id,
                "weight": _weight(weight),
                "evidence_grade": grade,
            })
        return normalised

    def _resolve_evidence(self, normalised, attached):
        resolved = []
        missing = []
        event_rows = self._event_rows(
            [item["event_id"] for item in normalised], attached)
        for item in normalised:
            event_id = item["event_id"]
            if attached:
                row = event_rows.get(event_id)
                if row is None:
                    missing.append(event_id)
                    continue
                if (item["evidence_grade"] is not None
                        and row["result_status"] in ("unknown", "pending")
                        and item["evidence_grade"] != "unknown"):
                    raise MemoryValidationError(
                        "event %s has an unknown outcome and cannot be graded %r"
                        % (event_id, item["evidence_grade"]))
                grade = item["evidence_grade"] or _derive_grade(row)
                timeline_id = row["timeline_id"]
            else:
                # Without the events DB we cannot derive a grade; explicit
                # grades are kept but the memory is flagged unverified.
                grade = item["evidence_grade"] or "unknown"
                timeline_id = None
            resolved.append({
                "event_id": event_id,
                "weight": item["weight"],
                "evidence_grade": grade,
                "timeline_id": timeline_id,
            })
        return resolved, missing

    def _write_revision(self, conn, memory_id, changed_fields, reason, actor):
        row = conn.execute(
            "SELECT COALESCE(MAX(revision), 0) + 1 AS revision"
            " FROM memory_revisions WHERE memory_id=?",
            (memory_id,)).fetchone()
        conn.execute(
            "INSERT INTO memory_revisions(memory_id, revision,"
            " changed_fields_json, reason, actor, created_at)"
            " VALUES(?,?,?,?,?,?)",
            (memory_id, row["revision"], _dumps(sorted(set(changed_fields))),
             _clean_str(reason, "reason"), _clean_str(actor, "actor"), _now()))

    def _scope_clause(self, scope):
        if not scope:
            return "", []
        if not isinstance(scope, dict):
            raise MemoryValidationError("scope must be a mapping")
        clauses = []
        params = []
        if scope.get("game_id"):
            clauses.append("m.game_id = ?")
            params.append(scope["game_id"])
        if scope.get("game_version"):
            clauses.append("m.game_version = ?")
            params.append(scope["game_version"])
        clauses.append(
            "((m.run_id IS NOT NULL AND m.run_id = ? AND m.save_id = ?"
            " AND m.timeline_id = ?)"
            " OR (m.run_id IS NULL AND m.save_id IS NOT NULL AND m.save_id = ?)"
            " OR (m.run_id IS NULL AND m.save_id IS NULL))")
        params.extend([
            scope.get("run_id"),
            scope.get("save_id"),
            scope.get("timeline_id"),
            scope.get("save_id"),
        ])
        return " AND " + " AND ".join(clauses), params

    def _touch(self, memory_ids, now):
        if not memory_ids:
            return
        placeholders = ",".join("?" for _ in memory_ids)
        with self._lock:
            conn = self._require()
            try:
                conn.execute(
                    "UPDATE memories SET hit_count = hit_count + 1,"
                    " last_used_at=? WHERE memory_id IN (%s)" % placeholders,
                    [now] + list(memory_ids))
            except Exception:
                raise MemoryUnavailable("cannot record memory usage")

    # -------------------------------------------------------------- writes

    def add_candidate(self, kind, text, evidence, *, game_id=GAME_ID,
                      game_version="unknown", save_id=None, run_id=None,
                      timeline_id=None, scope_extra=None, confidence=0.5,
                      source="reflection", dedupe_key=None,
                      status=STATUS_CANDIDATE, memory_id=None,
                      valid_from=None, valid_until=None,
                      superseded_by=None, actor="system", reason=None):
        """Persist a new candidate in the memories database.

        * evidence is mandatory and must reference at least one event;
        * when the events DB is attached every event id is checked, and a
          missing id is rejected with ``no_evidence``;
        * when the events DB is absent the row is stored with
          ``evidence_unverified=1`` instead of failing;
        * the same ``dedupe_key`` is a strict no-op.
        """
        kind = _clean_str(kind, "kind")
        if kind not in KINDS:
            raise MemoryValidationError("unknown kind %r" % (kind,))
        text = _clean_str(text, "text")
        confidence = _confidence(confidence)
        source = _clean_str(source, "source")
        if status not in _CREATE_STATUSES:
            raise MemoryValidationError(
                "memory must start as candidate or conflict; use verify()")
        game_id = _clean_str(game_id or GAME_ID, "game_id")
        game_version = _clean_str(game_version or "unknown", "game_version")
        save_id = _optional_id(save_id, "save_id")
        run_id = _optional_id(run_id, "run_id")
        timeline_id = _optional_id(timeline_id, "timeline_id")
        superseded_by = _optional_id(superseded_by, "superseded_by")
        if scope_extra is None:
            scope_extra = {}
        if not isinstance(scope_extra, dict):
            raise MemoryValidationError("scope_extra must be a mapping")
        valid_from = _timestamp(valid_from, "valid_from") or _now()
        valid_until = _timestamp(valid_until, "valid_until")
        if valid_until is not None and valid_until <= valid_from:
            raise MemoryValidationError("valid_until must be after valid_from")

        normalised = self._normalise_evidence(evidence)
        requested = [item["event_id"] for item in normalised]
        if not normalised:
            return {"ok": False, "reason": "no_evidence", "memory_id": memory_id,
                    "event_ids": [], "missing_event_ids": []}

        with self._lock:
            conn = self._require()
            try:
                with self._attached_events() as attached:
                    resolved, missing = self._resolve_evidence(
                        normalised, attached)
                    if missing:
                        return {"ok": False, "reason": "no_evidence",
                                "memory_id": memory_id, "event_ids": requested,
                                "missing_event_ids": missing}
                    if not resolved:
                        return {"ok": False, "reason": "no_evidence",
                                "memory_id": memory_id, "event_ids": requested,
                                "missing_event_ids": []}

                    if timeline_id is None and attached:
                        timeline_id = resolved[0]["timeline_id"]
                    if attached:
                        timeline_version = self._event_timeline_version(
                            timeline_id, attached)
                        if timeline_id is not None and timeline_version is None:
                            raise MemoryValidationError(
                                "unknown timeline %r in events database"
                                % (timeline_id,))
                        if game_version in ("", "unknown") and timeline_version:
                            game_version = timeline_version

                    evidence_unverified = 0 if attached else 1
                    dedupe_key = dedupe_key or _derive_dedupe_key(
                        kind, game_id, game_version, save_id, run_id, text,
                        scope_extra)
                    if len(dedupe_key) > 200:
                        raise MemoryValidationError("dedupe_key is too long")
                    memory_id = memory_id or ("mem_" + uuid.uuid4().hex)
                    now = _now()
                    reason = reason or (
                        "seed_import" if source.startswith("seed")
                        else "add_candidate")

                    conn.execute("BEGIN IMMEDIATE")
                    existing = conn.execute(
                        "SELECT * FROM memories WHERE dedupe_key=?",
                        (dedupe_key,)).fetchone()
                    if existing is not None:
                        conn.execute("COMMIT")
                        item = self._row_to_memory(existing)
                        item["ok"] = True
                        item["deduped"] = True
                        return item

                    conn.execute(
                        "INSERT INTO memories(memory_id, game_id,"
                        " game_version, save_id, run_id, scope_extra_json,"
                        " kind, text, status, confidence, source, dedupe_key,"
                        " timeline_id, valid_from, valid_until, superseded_by,"
                        " created_at, updated_at, hit_count, last_used_at,"
                        " evidence_unverified)"
                        " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,0,NULL,?)",
                        (memory_id, game_id, game_version, save_id, run_id,
                         _dumps(scope_extra), kind, text, status, confidence,
                         source, dedupe_key, timeline_id, valid_from,
                         valid_until, superseded_by, now, now,
                         evidence_unverified))
                    conn.executemany(
                        "INSERT INTO memory_evidence(memory_id, event_id,"
                        " weight, evidence_grade) VALUES(?,?,?,?)",
                        [(memory_id, row["event_id"], row["weight"],
                          row["evidence_grade"]) for row in resolved])
                    self._write_revision(conn, memory_id, ["created"], reason,
                                         actor)
                    conn.execute("COMMIT")
            except MemoryValidationError:
                self._rollback()
                raise
            except sqlite3.IntegrityError as exc:
                self._rollback()
                raise MemoryValidationError("cannot store memory: %s" % exc)
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot store memory: %s" % exc)

        item = self.get_memory(memory_id)
        item["ok"] = True
        item["deduped"] = False
        return item

    def verify(self, memory_id, actor="user"):
        """Candidate -> verified, or ``{'ok': False, 'reason': 'unverifiable'}``.

        Gate (the only path to verified):
        * the memory was written with the events DB available
          (``evidence_unverified=0``);
        * every evidence row has ``evidence_grade != 'unknown'``;
        * the events DB can still be attached and the evidence still matches
          the memory's version/save/run identity;
        * scope is complete (game id, real game version, timeline; run scope
          also needs save id).
        """
        with self._lock:
            with self._attached_events() as attached:
                memory = self.get_memory(memory_id)
                if memory["status"] == STATUS_VERIFIED:
                    memory["ok"] = True
                    memory["deduped"] = True
                    return memory
                if memory["status"] != STATUS_CANDIDATE:
                    return {"ok": False, "reason": "invalid_state",
                            "memory_id": memory_id,
                            "status": memory["status"]}
                blocker = self._verification_blocker(memory, attached)
                if blocker is not None:
                    return {"ok": False, "reason": "unverifiable",
                            "memory_id": memory_id, "detail": blocker}
                actor = _clean_str(actor, "actor")
                now = _now()
                conn = self._conn
                try:
                    conn.execute("BEGIN IMMEDIATE")
                    conn.execute(
                        "UPDATE memories SET status=?, updated_at=?"
                        " WHERE memory_id=?",
                        (STATUS_VERIFIED, now, memory_id))
                    self._write_revision(conn, memory_id, ["status"], "verify",
                                         actor)
                    conn.execute("COMMIT")
                except Exception as exc:
                    self._rollback()
                    raise MemoryUnavailable("cannot verify memory: %s" % exc)
        item = self.get_memory(memory_id)
        item["ok"] = True
        item["deduped"] = False
        return item

    def _verification_blocker(self, memory, attached):
        if not memory.get("game_id"):
            return "incomplete_scope: game_id is missing"
        if memory.get("game_version") in (None, "", "unknown"):
            return "incomplete_scope: game_version is unknown"
        if not memory.get("timeline_id"):
            return "incomplete_scope: timeline_id is missing"
        if memory.get("run_id") and not memory.get("save_id"):
            return "incomplete_scope: run scope needs save_id"
        if memory.get("evidence_unverified"):
            return ("evidence_unverified: stored without the events database;"
                    " revalidate before verifying")
        if not attached:
            return "events_unavailable: cannot re-check evidence"
        evidence = self._evidence(memory["memory_id"])
        if not evidence:
            return "no_evidence"
        unknown = [row["event_id"] for row in evidence
                   if row["evidence_grade"] == "unknown"]
        if unknown:
            return "evidence_grade_unknown: %s" % ",".join(unknown)
        rows = self._conn.execute(
            "SELECT e.event_id, e.evidence_grade, ev.timeline_id,"
            " t.game_version AS event_version, t.save_id AS event_save_id,"
            " t.run_id AS event_run_id"
            " FROM memory_evidence e"
            " JOIN events.events ev ON ev.event_id = e.event_id"
            " JOIN events.timelines t ON t.timeline_id = ev.timeline_id"
            " WHERE e.memory_id=?", (memory["memory_id"],)).fetchall()
        if len(rows) != len(evidence):
            return ("evidence_scope_mismatch: evidence missing from the events"
                    " database")
        for row in rows:
            if (memory.get("game_version") not in (None, "", "unknown")
                    and row["event_version"] != memory["game_version"]):
                return ("evidence_scope_mismatch: event %s is version %r"
                        % (row["event_id"], row["event_version"]))
            if memory.get("run_id"):
                if (row["event_run_id"] != memory["run_id"]
                        or row["timeline_id"] != memory["timeline_id"]):
                    return ("evidence_scope_mismatch: event %s is not from"
                            " run/timeline %s/%s"
                            % (row["event_id"], memory["run_id"],
                               memory["timeline_id"]))
            elif memory.get("save_id"):
                if row["event_save_id"] != memory["save_id"]:
                    return ("evidence_scope_mismatch: event %s is from save"
                            " %r" % (row["event_id"], row["event_save_id"]))
        return None

    def revalidate_evidence(self, memory_id, actor="user"):
        """Recheck evidence after the events DB becomes available.

        Clears ``evidence_unverified`` and refreshes every derived grade only
        when all referenced events exist in the attached events DB.  The events
        database is opened read-only and never modified.  Idempotent: a second
        call with no grade/flag change writes no revision.
        """
        actor = _clean_str(actor, "actor")
        with self._lock:
            memory = self.get_memory(memory_id)
            evidence = self._evidence(memory_id)
            if not evidence:
                return {"ok": False, "reason": "no_evidence",
                        "memory_id": memory_id}
            with self._attached_events() as attached:
                if not attached:
                    return {"ok": False, "reason": "events_unavailable",
                            "memory_id": memory_id}
                event_rows = self._event_rows(
                    [row["event_id"] for row in evidence], attached)
                missing = [row["event_id"] for row in evidence
                           if row["event_id"] not in event_rows]
                if missing:
                    return {"ok": False, "reason": "no_evidence",
                            "memory_id": memory_id,
                            "missing_event_ids": missing}
                refreshed = [{
                    "event_id": row["event_id"],
                    "evidence_grade": _derive_grade(
                        event_rows[row["event_id"]]),
                } for row in evidence]
                grade_changed = any(
                    old["evidence_grade"] != new["evidence_grade"]
                    for old, new in zip(evidence, refreshed))
                was_unverified = bool(memory["evidence_unverified"])
                if not grade_changed and not was_unverified:
                    memory["ok"] = True
                    memory["revalidated"] = False
                    memory["deduped"] = True
                    return memory

                conn = self._conn
                try:
                    conn.execute("BEGIN IMMEDIATE")
                    conn.executemany(
                        "UPDATE memory_evidence SET evidence_grade=?"
                        " WHERE memory_id=? AND event_id=?",
                        [(row["evidence_grade"], memory_id, row["event_id"])
                         for row in refreshed])
                    conn.execute(
                        "UPDATE memories SET evidence_unverified=0,"
                        " updated_at=? WHERE memory_id=?", (_now(), memory_id))
                    self._write_revision(
                        conn, memory_id,
                        ["evidence_grade", "evidence_unverified"],
                        "revalidate_evidence", actor)
                    conn.execute("COMMIT")
                except Exception as exc:
                    self._rollback()
                    raise MemoryUnavailable(
                        "cannot revalidate memory: %s" % exc)
        item = self.get_memory(memory_id)
        item["ok"] = True
        item["revalidated"] = True
        return item

    def deprecate(self, memory_id, actor, reason, valid_until=None):
        """Retire a rule.

        ``valid_until`` is the instant the rule stops being applicable; it
        defaults to now.  The row is kept (status ``deprecated``) for audit but
        is excluded from ``search`` and ``bootstrap``.
        """
        memory = self.get_memory(memory_id)
        reason = _clean_str(reason, "reason")
        actor = _clean_str(actor, "actor")
        if memory["status"] == STATUS_DEPRECATED:
            memory["ok"] = True
            memory["deduped"] = True
            return memory
        if memory["status"] in TERMINAL_STATUSES:
            return {"ok": False, "reason": "invalid_state",
                    "memory_id": memory_id, "status": memory["status"]}
        valid_until = _timestamp(valid_until, "valid_until") or _now()
        now = _now()
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "UPDATE memories SET status=?, valid_until=?, updated_at=?"
                    " WHERE memory_id=?",
                    (STATUS_DEPRECATED, valid_until, now, memory_id))
                self._write_revision(
                    conn, memory_id, ["status", "valid_until"], reason, actor)
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot deprecate memory: %s" % exc)
        item = self.get_memory(memory_id)
        item["ok"] = True
        item["deduped"] = False
        return item

    def supersede(self, memory_id, by_memory_id, actor, reason):
        """Replace ``memory_id`` with ``by_memory_id``; the old row is kept.

        On success the old row gets ``status='superseded'``,
        ``superseded_by=<replacement>`` and ``valid_until=now``.  It is no
        longer returned by default retrieval; the replacement is not modified.
        """
        old = self.get_memory(memory_id)
        new = self.get_memory(by_memory_id)
        reason = _clean_str(reason, "reason")
        actor = _clean_str(actor, "actor")
        if memory_id == by_memory_id:
            raise MemoryValidationError("a memory cannot supersede itself")
        if old["status"] == STATUS_SUPERSEDED:
            if old["superseded_by"] == by_memory_id:
                old["ok"] = True
                old["deduped"] = True
                return old
            return {"ok": False, "reason": "invalid_state",
                    "memory_id": memory_id, "status": old["status"]}
        if old["status"] in TERMINAL_STATUSES:
            return {"ok": False, "reason": "invalid_state",
                    "memory_id": memory_id, "status": old["status"]}
        if new["status"] in TERMINAL_STATUSES:
            raise MemoryValidationError(
                "superseding memory %s is terminal" % by_memory_id)
        valid_until = _now()
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "UPDATE memories SET status=?, superseded_by=?,"
                    " valid_until=?, updated_at=? WHERE memory_id=?",
                    (STATUS_SUPERSEDED, by_memory_id, valid_until, valid_until,
                     memory_id))
                self._write_revision(
                    conn, memory_id,
                    ["status", "superseded_by", "valid_until"], reason, actor)
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot supersede memory: %s" % exc)
        item = self.get_memory(memory_id)
        item["ok"] = True
        item["deduped"] = False
        item["superseding_memory_id"] = by_memory_id
        return item

    def revise(self, memory_id, changes, actor, reason, keep_status=False):
        """Edit a memory and log exactly which semantic fields changed.

        Content changes drop a previously verified/conflicting memory back to
        ``candidate`` unless ``keep_status=True``: silently editing a verified
        rule would make the audit trail lie.  Deprecated/superseded rows are
        terminal and must be re-created instead.
        """
        if not isinstance(changes, dict) or not changes:
            return {"ok": False, "reason": "no_changes",
                    "memory_id": memory_id}
        illegal = set(changes) - _REVISION_FIELDS
        if illegal:
            raise MemoryValidationError(
                "cannot revise field(s): %s" % ", ".join(sorted(illegal)))
        memory = self.get_memory(memory_id)
        if memory["status"] in TERMINAL_STATUSES:
            return {"ok": False, "reason": "invalid_state",
                    "memory_id": memory_id, "status": memory["status"]}
        actor = _clean_str(actor, "actor")
        reason = _clean_str(reason, "reason")

        updates = {}
        changed = []
        for field, value in changes.items():
            if field == "kind":
                value = _clean_str(value, "kind")
                if value not in KINDS:
                    raise MemoryValidationError("unknown kind %r" % (value,))
            elif field == "text":
                value = _clean_str(value, "text")
            elif field == "confidence":
                value = _confidence(value)
            elif field == "source":
                value = _clean_str(value, "source")
            elif field in ("game_version",):
                value = _clean_str(value, "game_version")
            elif field in ("save_id", "run_id"):
                value = _optional_id(value, field)
            elif field == "scope_extra":
                if not isinstance(value, dict):
                    raise MemoryValidationError(
                        "scope_extra must be a mapping")
            elif field in ("valid_from", "valid_until"):
                value = _timestamp(value, field)
            if memory.get(field) != value:
                updates[field] = value
                changed.append(field)

        if not changed:
            return {"ok": False, "reason": "no_changes",
                    "memory_id": memory_id}

        content_changed = bool(set(changed) & _CONTENT_FIELDS)
        if (content_changed and not keep_status
                and memory["status"] in (STATUS_VERIFIED, STATUS_CONFLICT)):
            updates["status"] = STATUS_CANDIDATE
            changed.append("status")
        if content_changed:
            merged = dict(memory)
            merged.update(updates)
            new_key = _derive_dedupe_key(
                merged["kind"], merged["game_id"], merged["game_version"],
                merged["save_id"], merged["run_id"], merged["text"],
                merged["scope_extra"])
            if new_key != memory["dedupe_key"]:
                updates["dedupe_key"] = new_key
                changed.append("dedupe_key")
        updates["updated_at"] = _now()

        assignments = ", ".join("%s=?" % field for field in updates)
        params = list(updates.values()) + [memory_id]
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute("UPDATE memories SET %s WHERE memory_id=?"
                             % assignments, params)
                self._write_revision(conn, memory_id, changed, reason, actor)
                conn.execute("COMMIT")
            except sqlite3.IntegrityError as exc:
                self._rollback()
                raise MemoryValidationError(
                    "revise would duplicate another memory: %s" % exc)
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot revise memory: %s" % exc)
        item = self.get_memory(memory_id)
        item["ok"] = True
        item["deduped"] = False
        return item

    # ------------------------------------------------------------- retrieval

    def search(self, query, limit=5, scope=None):
        """FTS5 + CJK prefix + substring fallback, always scope-filtered.

        Deprecated/superseded/expired memories are never returned.  Each result
        carries ``recall_reason ∈ {fts, fts_prefix, substring}``.  Returned
        rows are marked as used (``hit_count``/``last_used_at``).
        """
        limit = int(limit)
        if limit <= 0:
            return []
        query = (query or "").strip()
        if not query:
            return []
        now = _now()
        scope_sql, scope_params = self._scope_clause(scope)
        base = ("m.status NOT IN (?, ?)"
                " AND (m.valid_until IS NULL OR m.valid_until > ?)")
        base_params = [STATUS_DEPRECATED, STATUS_SUPERSEDED, now] + scope_params

        fts_expression, used_cjk = _build_fts_expression(query)
        reason = "fts_prefix" if used_cjk else "fts"
        rows = []
        if fts_expression:
            sql = (
                "SELECT m.*, bm25(memories_fts) AS rank,"
                " (SELECT COUNT(*) FROM memory_evidence e"
                "  WHERE e.memory_id=m.memory_id) AS evidence_count"
                " FROM memories_fts JOIN memories m"
                " ON m.rowid = memories_fts.rowid"
                " WHERE memories_fts MATCH ? AND " + base + scope_sql +
                " ORDER BY rank ASC, m.confidence DESC, m.updated_at DESC"
                " LIMIT ?")
            params = [fts_expression] + base_params + [limit]
            try:
                rows = self._require().execute(sql, params).fetchall()
            except sqlite3.OperationalError:
                rows = []

        items = []
        for row in rows:
            item = self._row_to_memory(
                row, include_evidence=False,
                evidence_count=row["evidence_count"])
            item["recall_reason"] = reason
            items.append(item)

        if len(items) < limit:
            remaining = limit - len(items)
            patterns = _like_patterns(query)
            if patterns:
                conn = self._require()
                sql = (
                    "SELECT m.*,"
                    " (SELECT COUNT(*) FROM memory_evidence e"
                    "  WHERE e.memory_id=m.memory_id) AS evidence_count"
                    " FROM memories m WHERE " + base + scope_sql)
                params = list(base_params)
                if items:
                    placeholders = ",".join("?" for _ in items)
                    sql += " AND m.memory_id NOT IN (%s)" % placeholders
                    params.extend(item["memory_id"] for item in items)
                sql += (" AND (" + " OR ".join(
                    "m.text LIKE ? ESCAPE '\\'" for _ in patterns) + ")")
                params.extend(patterns)
                sql += (" ORDER BY m.confidence DESC,"
                        " COALESCE(m.last_used_at, m.created_at) DESC,"
                        " m.updated_at DESC LIMIT ?")
                params.append(remaining)
                for row in conn.execute(sql, params).fetchall():
                    item = self._row_to_memory(
                        row, include_evidence=False,
                        evidence_count=row["evidence_count"])
                    item["recall_reason"] = "substring"
                    items.append(item)

        if items:
            self._touch([item["memory_id"] for item in items], now)
            for item in items:
                item["hit_count"] = int(item.get("hit_count") or 0) + 1
                item["last_used_at"] = now
        return items

    def bootstrap(self, limit=8, scope=None):
        """Verified, unexpired memories ordered by confidence then recency."""
        limit = int(limit)
        if limit <= 0:
            return []
        now = _now()
        scope_sql, scope_params = self._scope_clause(scope)
        sql = (
            "SELECT m.*,"
            " (SELECT COUNT(*) FROM memory_evidence e"
            "  WHERE e.memory_id=m.memory_id) AS evidence_count"
            " FROM memories m"
            " WHERE m.status = ?"
            " AND (m.valid_until IS NULL OR m.valid_until > ?)"
            " AND (m.valid_from IS NULL OR m.valid_from <= ?)"
            + scope_sql +
            " ORDER BY m.confidence DESC,"
            " COALESCE(m.last_used_at, '') DESC, m.hit_count DESC,"
            " m.created_at DESC LIMIT ?")
        params = ([STATUS_VERIFIED, now, now] + scope_params + [limit])
        items = []
        for row in self._require().execute(sql, params).fetchall():
            item = self._row_to_memory(
                row, include_evidence=False,
                evidence_count=row["evidence_count"])
            item["recall_reason"] = "bootstrap"
            item["why"] = ("verified confidence=%.2f hits=%d"
                           % (item["confidence"], item["hit_count"]))
            items.append(item)
        if items:
            self._touch([item["memory_id"] for item in items], now)
            for item in items:
                item["hit_count"] = int(item.get("hit_count") or 0) + 1
                item["last_used_at"] = now
        return items

    # --------------------------------------------------------------- reads

    def get_memory(self, memory_id):
        row = self._require().execute(
            "SELECT * FROM memories WHERE memory_id=?",
            (memory_id,)).fetchone()
        if row is None:
            raise MemoryUnavailable("unknown memory %s" % memory_id)
        return self._row_to_memory(row)

    def list_memories(self, status=None, limit=200):
        sql = "SELECT * FROM memories"
        params = []
        if status is not None:
            sql += " WHERE status=?"
            params.append(status)
        sql += " ORDER BY updated_at DESC, memory_id ASC LIMIT ?"
        params.append(int(limit))
        return [self._row_to_memory(row)
                for row in self._require().execute(sql, params).fetchall()]

    def revisions(self, memory_id):
        rows = self._require().execute(
            "SELECT * FROM memory_revisions WHERE memory_id=?"
            " ORDER BY revision ASC", (memory_id,)).fetchall()
        items = []
        for row in rows:
            item = dict(row)
            item["changed_fields"] = json.loads(
                item.pop("changed_fields_json") or "[]")
            items.append(item)
        return items

    def status(self):
        rows = self._require().execute(
            "SELECT status, COUNT(*) AS n FROM memories GROUP BY status"
        ).fetchall()
        by_status = {row["status"]: row["n"] for row in rows}
        return {
            "db": self.path,
            "events_db": self.events_db,
            "events_db_available": self.events_db_available(),
            "schema_version": self.schema_version(),
            "memories": sum(by_status.values()),
            "by_status": by_status,
        }

    def rebuild_fts(self):
        with self._lock:
            self._require().execute(
                "INSERT INTO memories_fts(memories_fts) VALUES('rebuild')")
