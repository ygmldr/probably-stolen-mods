"""P1 event store: append-only interaction history on top of SQLite.

Design notes (see docs/ADR-001-memory-architecture.md, ADR-008/009/010):

* ``timelines`` isolate history branches (new game, rollback, external replace).
* ``events`` is append-only. A write action produces one ``intent`` row *before*
  the HTTP call and that row is later completed with the result, so a crash can
  never hide a possibly-dispatched action.
* ``snapshots`` store hashed state observations and the minimal delta against
  the previous observation of the same timeline. Deltas observed without any
  intervening successful write are reported as external state changes.
* Every failure surfaces as :class:`MemoryUnavailable` so callers can fail
  closed instead of silently losing history.
"""

import hashlib
import json
import os
import sqlite3
import threading
import time
import uuid

SCHEMA_VERSION = 1

# Fields that change without any player action (or are derived) and therefore
# must not affect the state hash or the delta.
VOLATILE_KEYS = frozenset({
    "timestamp", "legal_actions", "run_summary", "rev",
})

EVENT_INTENT = "intent"
EVENT_RESULT = "result"
EVENT_OBSERVATION = "observation"
EVENT_SNAPSHOT = "snapshot"
EVENT_EXTERNAL = "external_state_change"
EVENT_SLEEP_COMPLETED = "sleep_completed"
EVENT_SLEEP_UNRESOLVED = "sleep_unresolved"

STATUS_PENDING = "pending"
STATUS_SUCCESS = "success"
STATUS_REJECTED = "rejected"
STATUS_FAILED = "failed"
STATUS_UNKNOWN = "unknown"

_SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS timelines (
  timeline_id TEXT PRIMARY KEY,
  agent_session_id TEXT NOT NULL,
  run_id TEXT,
  save_id TEXT,
  slot INTEGER,
  run_number INTEGER,
  parent_timeline_id TEXT REFERENCES timelines(timeline_id),
  reason TEXT NOT NULL,
  game_version TEXT NOT NULL,
  game_mvid TEXT,
  instance_id TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_timelines_run ON timelines(run_id, created_at);

CREATE TABLE IF NOT EXISTS snapshots (
  snapshot_id TEXT PRIMARY KEY,
  timeline_id TEXT NOT NULL REFERENCES timelines(timeline_id),
  state_json TEXT NOT NULL,
  state_hash TEXT NOT NULL,
  delta_json TEXT,
  game_day INTEGER,
  rev INTEGER,
  completeness TEXT NOT NULL DEFAULT 'partial',
  observed_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_snapshots_timeline
  ON snapshots(timeline_id, observed_at);

CREATE TABLE IF NOT EXISTS events (
  event_id TEXT PRIMARY KEY,
  timeline_id TEXT NOT NULL REFERENCES timelines(timeline_id),
  seq INTEGER NOT NULL,
  action_id TEXT,
  event_type TEXT NOT NULL,
  tool_name TEXT,
  args_json TEXT,
  result_status TEXT,
  result_json TEXT,
  before_snapshot_id TEXT REFERENCES snapshots(snapshot_id),
  after_snapshot_id TEXT REFERENCES snapshots(snapshot_id),
  delta_json TEXT,
  capture_quality TEXT NOT NULL DEFAULT 'partial',
  game_day INTEGER,
  rev INTEGER,
  occurred_at TEXT NOT NULL,
  UNIQUE(timeline_id, seq)
);
CREATE INDEX IF NOT EXISTS idx_events_timeline ON events(timeline_id, seq);
CREATE INDEX IF NOT EXISTS idx_events_action ON events(action_id);
CREATE INDEX IF NOT EXISTS idx_events_status ON events(result_status);
"""


class MemoryUnavailable(Exception):
    """Raised when event history cannot be read or written.

    Callers must treat this as fail-closed for write actions: history that
    cannot be recorded must not be produced.
    """


class SessionTimeline(object):
    __slots__ = ("timeline_id", "agent_session_id")

    def __init__(self, timeline_id, agent_session_id):
        self.timeline_id = timeline_id
        self.agent_session_id = agent_session_id

    def __repr__(self):
        return "SessionTimeline(%s)" % self.timeline_id


class Observation(object):
    __slots__ = ("snapshot_id", "changed", "delta", "state_hash")

    def __init__(self, snapshot_id, changed, delta, state_hash):
        self.snapshot_id = snapshot_id
        self.changed = changed
        self.delta = delta
        self.state_hash = state_hash

    def __repr__(self):
        return "Observation(%s, changed=%s)" % (self.snapshot_id, self.changed)


def _now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _dumps(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"))


def _flatten(state, prefix=""):
    """Flatten scalar values one level deep so deltas stay small and readable."""
    flat = {}
    if not isinstance(state, dict):
        return flat
    for key, value in state.items():
        if key in VOLATILE_KEYS or key.startswith("_"):
            continue
        name = prefix + key
        if isinstance(value, dict):
            for sub_key, sub_value in value.items():
                if sub_key in VOLATILE_KEYS:
                    continue
                if isinstance(sub_value, (str, int, float, bool)) or sub_value is None:
                    flat[name + "." + sub_key] = sub_value
        elif isinstance(value, list):
            flat[name + ".__len__"] = len(value)
        elif isinstance(value, (str, int, float, bool)) or value is None:
            flat[name] = value
    return flat


class EventStore(object):
    """SQLite-backed append-only history. Safe for concurrent threads."""

    def __init__(self, path):
        self.path = os.path.abspath(path)
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
            conn = sqlite3.connect(self.path, timeout=15,
                                   isolation_level=None,
                                   check_same_thread=False)
            conn.row_factory = sqlite3.Row
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA busy_timeout=15000")
            conn.execute("PRAGMA foreign_keys=ON")
            self._conn = conn
            self._migrate()
        except MemoryUnavailable:
            raise
        except Exception as exc:
            raise MemoryUnavailable("cannot open event store: %s" % exc)

    def _migrate(self):
        with self._lock:
            self._conn.executescript(_SCHEMA)
            row = self._conn.execute(
                "SELECT value FROM meta WHERE key='schema_version'").fetchone()
            if row is None:
                self._conn.execute(
                    "INSERT INTO meta(key, value) VALUES('schema_version', ?)",
                    (str(SCHEMA_VERSION),))
                return
            found = int(row["value"])
            if found != SCHEMA_VERSION:
                raise MemoryUnavailable(
                    "unsupported schema version %d (expected %d)"
                    % (found, SCHEMA_VERSION))

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
            raise MemoryUnavailable("event store is closed")
        return self._conn

    # ------------------------------------------------------------- inspection

    def table_names(self):
        conn = self._require()
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table'").fetchall()
        return {row["name"] for row in rows}

    def schema_version(self):
        conn = self._require()
        row = conn.execute(
            "SELECT value FROM meta WHERE key='schema_version'").fetchone()
        return int(row["value"]) if row else None

    def count_events(self, timeline_id=None):
        conn = self._require()
        if timeline_id is None:
            row = conn.execute("SELECT COUNT(*) AS n FROM events").fetchone()
        else:
            row = conn.execute(
                "SELECT COUNT(*) AS n FROM events WHERE timeline_id=?",
                (timeline_id,)).fetchone()
        return row["n"]

    def count_snapshots(self, timeline_id):
        conn = self._require()
        row = conn.execute(
            "SELECT COUNT(*) AS n FROM snapshots WHERE timeline_id=?",
            (timeline_id,)).fetchone()
        return row["n"]

    # --------------------------------------------------------------- timelines

    def start_session(self, agent_session_id, game_version, game_mvid=None,
                      instance_id=None):
        timeline_id = "tl_" + uuid.uuid4().hex
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "INSERT INTO timelines(timeline_id, agent_session_id, run_id,"
                    " save_id, slot, run_number, parent_timeline_id, reason,"
                    " game_version, game_mvid, instance_id, created_at)"
                    " VALUES(?,?,NULL,NULL,NULL,NULL,NULL,'session',?,?,?,?)",
                    (timeline_id, agent_session_id, game_version, game_mvid,
                     instance_id, _now()))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot start session: %s" % exc)
        return SessionTimeline(timeline_id, agent_session_id)

    def get_timeline(self, timeline_id):
        conn = self._require()
        row = conn.execute("SELECT * FROM timelines WHERE timeline_id=?",
                           (timeline_id,)).fetchone()
        if row is None:
            raise MemoryUnavailable("unknown timeline %s" % timeline_id)
        return dict(row)

    def attach_identity(self, timeline_id, run_id=None, slot=None,
                        run_number=None):
        save_id = None if slot is None else "slot:%d" % int(slot)
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "UPDATE timelines SET run_id=?, save_id=?, slot=?,"
                    " run_number=? WHERE timeline_id=?",
                    (run_id, save_id, slot, run_number, timeline_id))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot attach identity: %s" % exc)
        return save_id

    def fork_timeline(self, parent_timeline_id, run_id=None, slot=None,
                      run_number=None, reason="rollback"):
        parent = self.get_timeline(parent_timeline_id)
        timeline_id = "tl_" + uuid.uuid4().hex
        save_id = None if slot is None else "slot:%d" % int(slot)
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "INSERT INTO timelines(timeline_id, agent_session_id, run_id,"
                    " save_id, slot, run_number, parent_timeline_id, reason,"
                    " game_version, game_mvid, instance_id, created_at)"
                    " VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                    (timeline_id, parent["agent_session_id"], run_id, save_id,
                     slot, run_number, parent_timeline_id, reason,
                     parent["game_version"], parent["game_mvid"],
                     parent["instance_id"], _now()))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot fork timeline: %s" % exc)
        return timeline_id

    # ------------------------------------------------------------------ events

    def _next_seq(self, conn, timeline_id):
        row = conn.execute(
            "SELECT COALESCE(MAX(seq), 0) + 1 AS n FROM events WHERE timeline_id=?",
            (timeline_id,)).fetchone()
        return row["n"]

    def append_event(self, timeline_id, event_type, tool_name=None,
                     result_status=None, args=None, result=None,
                     action_id=None, rev=None, game_day=None,
                     capture_quality="partial",
                     before_snapshot_id=None, after_snapshot_id=None,
                     delta=None, event_id=None):
        event_id = event_id or ("ev_" + uuid.uuid4().hex)
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                seq = self._next_seq(conn, timeline_id)
                conn.execute(
                    "INSERT INTO events(event_id, timeline_id, seq, action_id,"
                    " event_type, tool_name, args_json, result_status, result_json,"
                    " before_snapshot_id, after_snapshot_id, delta_json,"
                    " capture_quality, game_day, rev, occurred_at)"
                    " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    (event_id, timeline_id, seq, action_id, event_type, tool_name,
                     _dumps(args) if args is not None else None, result_status,
                     _dumps(result) if result is not None else None,
                     before_snapshot_id, after_snapshot_id,
                     _dumps(delta) if delta is not None else None,
                     capture_quality, game_day, rev, _now()))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot append event: %s" % exc)
        return event_id

    def append_intent(self, timeline_id, tool_name, args=None, action_id=None,
                      rev=None, game_day=None, before_snapshot_id=None):
        return self.append_event(
            timeline_id, event_type=EVENT_INTENT, tool_name=tool_name,
            result_status=STATUS_PENDING, args=args, action_id=action_id,
            rev=rev, game_day=game_day, capture_quality="pending",
            before_snapshot_id=before_snapshot_id)

    def append_result(self, event_id, status, result=None,
                      capture_quality="complete", after_snapshot_id=None,
                      delta=None):
        with self._lock:
            conn = self._require()
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "UPDATE events SET result_status=?, result_json=?,"
                    " capture_quality=?, after_snapshot_id=?, delta_json=?"
                    " WHERE event_id=?",
                    (status, _dumps(result) if result is not None else None,
                     capture_quality, after_snapshot_id,
                     _dumps(delta) if delta is not None else None, event_id))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot append result: %s" % exc)

    def get_event(self, event_id):
        conn = self._require()
        row = conn.execute("SELECT * FROM events WHERE event_id=?",
                           (event_id,)).fetchone()
        if row is None:
            raise MemoryUnavailable("unknown event %s" % event_id)
        return dict(row)

    def recent_events(self, timeline_id, limit=50):
        """Newest-first window; history() is oldest-first and truncates the tail."""
        conn = self._require()
        rows = conn.execute(
            "SELECT * FROM events WHERE timeline_id=? ORDER BY seq DESC LIMIT ?",
            (timeline_id, int(limit))).fetchall()
        return [dict(row) for row in rows]

    def history(self, timeline_id=None, limit=200, event_types=None,
                since_seq=0):
        conn = self._require()
        sql = "SELECT * FROM events WHERE seq > ?"
        params = [since_seq]
        if timeline_id is not None:
            sql += " AND timeline_id=?"
            params.append(timeline_id)
        if event_types:
            placeholders = ",".join("?" for _ in event_types)
            sql += " AND event_type IN (%s)" % placeholders
            params.extend(event_types)
        sql += " ORDER BY seq ASC LIMIT ?"
        params.append(int(limit))
        return [dict(row) for row in conn.execute(sql, params).fetchall()]

    def pending_events(self, timeline_id=None):
        conn = self._require()
        sql = "SELECT * FROM events WHERE result_status=? AND event_type=?"
        params = [STATUS_PENDING, EVENT_INTENT]
        if timeline_id is not None:
            sql += " AND timeline_id=?"
            params.append(timeline_id)
        sql += " ORDER BY seq ASC"
        return [dict(row) for row in conn.execute(sql, params).fetchall()]

    def reconcile_pending(self, timeline_id=None):
        """Mark every pending intent as unknown. Idempotent, never re-dispatches."""
        pending = self.pending_events(timeline_id)
        for row in pending:
            self.append_result(
                row["event_id"], status=STATUS_UNKNOWN,
                result={"error": "outcome_unknown", "detail": "recovered_pending"},
                capture_quality="partial")
        return len(pending)

    # --------------------------------------------------------------- snapshots

    def observe_state(self, timeline_id, state):
        if not isinstance(state, dict):
            raise MemoryUnavailable("state must be a mapping")
        flat = _flatten(state)
        digest = hashlib.sha256(_dumps(flat).encode("utf-8")).hexdigest()
        conn = self._require()
        previous = conn.execute(
            "SELECT snapshot_id, state_hash, state_json FROM snapshots"
            " WHERE timeline_id=? ORDER BY observed_at DESC, rowid DESC LIMIT 1",
            (timeline_id,)).fetchone()

        if previous is not None and previous["state_hash"] == digest:
            return Observation(previous["snapshot_id"], False, None, digest)

        delta = None
        before_id = None
        if previous is not None:
            before_id = previous["snapshot_id"]
            before_flat = _flatten(json.loads(previous["state_json"]))
            delta = {}
            for key in sorted(set(before_flat) | set(flat)):
                before = before_flat.get(key)
                after = flat.get(key)
                if before != after:
                    delta[key] = {"before": before, "after": after}

        snapshot_id = "sn_" + uuid.uuid4().hex
        game_day = state.get("day") if isinstance(state.get("day"), int) else None
        rev = state.get("rev") if isinstance(state.get("rev"), int) else None
        with self._lock:
            try:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute(
                    "INSERT INTO snapshots(snapshot_id, timeline_id, state_json,"
                    " state_hash, delta_json, game_day, rev, completeness,"
                    " observed_at) VALUES(?,?,?,?,?,?,?,?,?)",
                    (snapshot_id, timeline_id, _dumps(state), digest,
                     _dumps(delta) if delta is not None else None,
                     game_day, rev, "partial", _now()))
                conn.execute("COMMIT")
            except Exception as exc:
                self._rollback()
                raise MemoryUnavailable("cannot store snapshot: %s" % exc)

        # ``changed`` means "differs from a previously observed state"; the very
        # first observation is a baseline, not a change.
        return Observation(snapshot_id, previous is not None, delta, digest)

    def snapshots(self, timeline_id, limit=500):
        conn = self._require()
        rows = conn.execute(
            "SELECT * FROM snapshots WHERE timeline_id=?"
            " ORDER BY observed_at ASC, rowid ASC LIMIT ?",
            (timeline_id, int(limit))).fetchall()
        return [dict(row) for row in rows]

    def latest_snapshot(self, timeline_id):
        conn = self._require()
        row = conn.execute(
            "SELECT * FROM snapshots WHERE timeline_id=?"
            " ORDER BY observed_at DESC, rowid DESC LIMIT 1",
            (timeline_id,)).fetchone()
        return dict(row) if row else None

    # ---------------------------------------------------------------- internals

    def _rollback(self):
        try:
            self._conn.execute("ROLLBACK")
        except Exception:
            pass
