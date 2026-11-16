"""Import the TEST-ONLY seed rules into a throwaway memories database.

Usage (from ``PawnMind/``)::

    python -m agent.memory.seed --memories-db /tmp/pm/memories.db \
        --db /tmp/pm/events.db

``--db`` names the events database; when omitted it defaults to ``events.db``
next to the memories database.  For compatibility with the pre-split command,
``--db`` alone is treated as the memories database and the events database is
derived beside it.

The JSON under ``agent/memory/seeds/verified_rules.json`` is **synthetic test
data**.  It is not a recording, not a real save observation, and not a
production recommendation.  Import is idempotent: a second run adds nothing.
"""

import argparse
import json
import os
import sys

from .memories import (
    STATUS_CANDIDATE,
    STATUS_CONFLICT,
    STATUS_DEPRECATED,
    STATUS_SUPERSEDED,
    STATUS_VERIFIED,
    MEMORY_SCHEMA_VERSION,
    MemoryStore,
    MemoryValidationError,
)
from .store import SCHEMA_VERSION as EVENTS_SCHEMA_VERSION
from .store import EventStore, MemoryUnavailable

DEFAULT_SEED_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "seeds", "verified_rules.json")

STATUS_TARGETS = frozenset({
    STATUS_CANDIDATE, STATUS_VERIFIED, STATUS_CONFLICT,
    STATUS_DEPRECATED, STATUS_SUPERSEDED,
})


def load_seed(path=None):
    path = path or DEFAULT_SEED_PATH
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    if not data.get("test_data"):
        raise ValueError("seed file must be marked test_data=true")
    return data


def _ensure_timelines(events_store, timelines):
    conn = events_store._require()
    added = 0
    with events_store._lock:
        for spec in timelines:
            timeline_id = spec["timeline_id"]
            row = conn.execute(
                "SELECT 1 FROM timelines WHERE timeline_id=?",
                (timeline_id,)).fetchone()
            if row is not None:
                continue
            conn.execute(
                "INSERT INTO timelines(timeline_id, agent_session_id, run_id,"
                " save_id, slot, run_number, parent_timeline_id, reason,"
                " game_version, game_mvid, instance_id, created_at)"
                " VALUES(?,?,?,?,?,?,NULL,?,?,?,?,?)",
                (timeline_id, spec["agent_session_id"], spec.get("run_id"),
                 spec.get("save_id"), spec.get("slot"), spec.get("run_number"),
                 spec.get("reason", "seed"), spec["game_version"],
                 spec.get("game_mvid"), spec.get("instance_id"),
                 spec.get("created_at", "2026-11-01T00:00:00Z")))
            added += 1
    return added


def _ensure_events(events_store, memories):
    added = 0
    for memory in memories:
        for spec in memory.get("evidence", []):
            try:
                events_store.get_event(spec["event_id"])
                continue
            except MemoryUnavailable:
                pass
            events_store.append_event(
                spec["timeline_id"],
                event_type=spec.get("event_type", "observation"),
                tool_name=spec.get("tool_name"),
                result_status=spec.get("result_status", "success"),
                result=spec.get("result"),
                args=spec.get("args"),
                action_id=spec.get("action_id"),
                rev=spec.get("rev"),
                game_day=spec.get("game_day"),
                capture_quality=spec.get("capture_quality", "complete"),
                event_id=spec["event_id"])
            added += 1
    return added


def _import_one(memory_store, spec, actor):
    target = spec.get("target_status", STATUS_CANDIDATE)
    if target not in STATUS_TARGETS:
        raise ValueError("unknown target_status %r for %s"
                         % (target, spec.get("memory_id")))
    create_status = (STATUS_CONFLICT if target == STATUS_CONFLICT
                     else STATUS_CANDIDATE)
    created = memory_store.add_candidate(
        kind=spec["kind"],
        text=spec["text"],
        evidence=spec.get("evidence", []),
        game_id=spec.get("game_id", "probably_stolen"),
        game_version=spec.get("game_version", "unknown"),
        save_id=spec.get("save_id"),
        run_id=spec.get("run_id"),
        timeline_id=spec.get("timeline_id"),
        scope_extra=spec.get("scope_extra"),
        confidence=spec.get("confidence", 0.5),
        source=spec.get("source", "seed_test_data"),
        dedupe_key=spec.get("dedupe_key"),
        status=create_status,
        memory_id=spec["memory_id"],
        valid_from=spec.get("valid_from"),
        valid_until=spec.get("valid_until") if target == STATUS_DEPRECATED
        else None,
        actor=actor,
        reason="seed_import")
    if not created.get("ok"):
        raise RuntimeError("cannot import %s: %r"
                           % (spec["memory_id"], created))
    if created.get("deduped"):
        return False

    memory_id = spec["memory_id"]
    if target == STATUS_VERIFIED:
        verified = memory_store.verify(memory_id, actor=actor)
        if not verified.get("ok"):
            raise RuntimeError("seed memory %s failed verification: %r"
                               % (memory_id, verified))
    elif target == STATUS_DEPRECATED:
        result = memory_store.deprecate(
            memory_id, actor=actor, reason="seed fixture: deprecated on purpose",
            valid_until=spec.get("valid_until"))
        if not result.get("ok"):
            raise RuntimeError("cannot deprecate seed %s: %r"
                               % (memory_id, result))
    elif target == STATUS_SUPERSEDED:
        by = spec.get("superseded_by")
        if not by:
            raise ValueError("superseded memory %s needs superseded_by"
                             % memory_id)
        result = memory_store.supersede(
            memory_id, by, actor=actor,
            reason="seed fixture: superseded on purpose")
        if not result.get("ok"):
            raise RuntimeError("cannot supersede seed %s: %r"
                               % (memory_id, result))
    elif target in (STATUS_CANDIDATE, STATUS_CONFLICT):
        pass
    return True


def seed_database(memories_path, events_path=None, seed_path=None, actor="seed"):
    """Import the seed file into ``memories_path``; returns a summary."""
    memories_path = os.path.abspath(memories_path)
    if events_path is None:
        events_path = os.path.join(
            os.path.dirname(memories_path), "events.db")
    events_path = os.path.abspath(events_path)
    if os.path.normcase(memories_path) == os.path.normcase(events_path):
        raise ValueError("memories database and events database must differ")

    data = load_seed(seed_path)
    memories = data.get("memories", [])

    events_store = EventStore(events_path)
    try:
        timelines_added = _ensure_timelines(
            events_store, data.get("timelines", []))
        events_added = _ensure_events(events_store, memories)
    finally:
        events_store.close()

    memory_store = MemoryStore(memories_path, events_db=events_path)
    try:
        memories_added = 0
        memories_skipped = 0
        for spec in memories:
            if _import_one(memory_store, spec, actor):
                memories_added += 1
            else:
                memories_skipped += 1
        unverified = sum(1 for item in memory_store.list_memories(limit=1000)
                         if item["evidence_unverified"])
    finally:
        memory_store.close()

    return {
        "ok": True,
        "test_data": True,
        "db": memories_path,
        "memories_db": memories_path,
        "events_db": events_path,
        "memory_schema_version": MEMORY_SCHEMA_VERSION,
        "events_schema_version": EVENTS_SCHEMA_VERSION,
        "seed_file": seed_path or DEFAULT_SEED_PATH,
        "timelines_added": timelines_added,
        "events_added": events_added,
        "memories_added": memories_added,
        "memories_skipped": memories_skipped,
        "memories_unverified": unverified,
    }


def build_parser():
    parser = argparse.ArgumentParser(
        description="Import TEST-ONLY seed memories (idempotent)")
    parser.add_argument("--memories-db", default=None,
                        help="path to the separate memories.db")
    parser.add_argument("--db", default=None,
                        help="path to the events memory.db (legacy: memories.db"
                             " when --memories-db is omitted)")
    parser.add_argument("--seed", default=None, help="override seed JSON path")
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    memories_db = args.memories_db or args.db
    if not memories_db:
        parser.error("--memories-db is required")
    events_db = args.db if args.memories_db else None
    try:
        summary = seed_database(memories_db, events_db, args.seed)
    except (MemoryUnavailable, MemoryValidationError, OSError, ValueError,
            RuntimeError) as exc:
        print("seed failed: %s" % exc, file=sys.stderr)
        return 1
    print(json.dumps(summary, ensure_ascii=False, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
