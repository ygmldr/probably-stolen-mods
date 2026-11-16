"""P2 second half: memory schema v3 (evidence role/added_at) and governance.

The fixture builds a *real* schema-v2 file with the old ``memory_evidence``
shape, then opens it with ``MemoryStore`` to prove the migration is additive,
idempotent and lossless.  Everything runs in a temporary directory: no live
recording database, no ``PawnMind/play/`` file and no bridge is touched.
"""

import os
import sqlite3
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.memories import (  # noqa: E402
    EVIDENCE_ROLES,
    MEMORY_SCHEMA_VERSION,
    STATUS_CANDIDATE,
    STATUS_VERIFIED,
    MemoryNotFound,
    MemoryStore,
    MemoryValidationError,
)
from agent.memory.store import EventStore  # noqa: E402

VERSION = "VERSION 100-Rev13"
GAME_ID = "probably_stolen"

V2_SCHEMA = """
CREATE TABLE meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
INSERT INTO meta(key, value) VALUES('schema_version', '2');

CREATE TABLE memories (
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

CREATE TABLE memory_evidence (
  memory_id TEXT NOT NULL REFERENCES memories(memory_id) ON DELETE CASCADE,
  event_id TEXT NOT NULL,
  weight REAL NOT NULL DEFAULT 1.0,
  evidence_grade TEXT NOT NULL
    CHECK (evidence_grade IN ('observed', 'reported', 'unknown')),
  PRIMARY KEY(memory_id, event_id)
);

CREATE TABLE memory_revisions (
  memory_id TEXT NOT NULL REFERENCES memories(memory_id) ON DELETE CASCADE,
  revision INTEGER NOT NULL,
  changed_fields_json TEXT NOT NULL DEFAULT '[]',
  reason TEXT NOT NULL,
  actor TEXT NOT NULL,
  created_at TEXT NOT NULL,
  PRIMARY KEY(memory_id, revision)
);
"""


class MemorySchemaV3Tests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "memories.db")

    def tearDown(self):
        self.tmp.cleanup()

    def _make_v2_database(self):
        conn = sqlite3.connect(self.path)
        conn.executescript(V2_SCHEMA)
        conn.execute(
            "INSERT INTO memories(memory_id, game_id, game_version, kind, text,"
            " status, confidence, source, dedupe_key, created_at, updated_at)"
            " VALUES('mem_v2', ?, ?, 'warning', 'legacy rule survives',"
            " 'verified', 0.9, 'seed', 'v2-dedupe',"
            " '2026-11-01T00:00:00Z', '2026-11-02T00:00:00Z')",
            (GAME_ID, VERSION))
        conn.execute(
            "INSERT INTO memory_evidence(memory_id, event_id, weight,"
            " evidence_grade) VALUES('mem_v2', 'ev_v2', 1.0, 'observed')")
        conn.execute(
            "INSERT INTO memory_revisions(memory_id, revision,"
            " changed_fields_json, reason, actor, created_at)"
            " VALUES('mem_v2', 1, '[\"created\"]', 'seed_import', 'seed',"
            " '2026-11-01T00:00:00Z')")
        conn.commit()
        conn.close()

    def _evidence_columns(self):
        conn = sqlite3.connect(self.path)
        try:
            return [row[1] for row in conn.execute(
                "PRAGMA table_info(memory_evidence)")]
        finally:
            conn.close()

    def test_new_database_is_schema_v3_with_evidence_role_and_added_at(self):
        store = MemoryStore(self.path)
        try:
            self.assertEqual(3, MEMORY_SCHEMA_VERSION)
            self.assertEqual(3, store.schema_version())
            columns = self._evidence_columns()
            self.assertIn("evidence_role", columns)
            self.assertIn("added_at", columns)
        finally:
            store.close()

    def test_v2_database_migrates_in_place_without_losing_rows(self):
        self._make_v2_database()
        store = MemoryStore(self.path)
        try:
            self.assertEqual(3, store.schema_version())
            memory = store.get_memory("mem_v2")
            self.assertEqual("legacy rule survives", memory["text"])
            self.assertEqual("verified", memory["status"])
            self.assertEqual(1, memory["evidence_count"])
            evidence = memory["evidence"][0]
            self.assertEqual("ev_v2", evidence["event_id"])
            self.assertEqual("observed", evidence["evidence_grade"])
            self.assertEqual("support", evidence["evidence_role"])
            self.assertEqual("2026-11-01T00:00:00Z", evidence["added_at"],
                             "migration must backfill added_at")
            self.assertEqual(1, memory["support_count"])
            self.assertEqual(0, memory["contradict_count"])
            self.assertEqual(1, len(store.revisions("mem_v2")))
            hits = store.search("legacy rule", scope={
                "game_id": GAME_ID, "game_version": VERSION})
            self.assertEqual(["mem_v2"], [row["memory_id"] for row in hits])
        finally:
            store.close()

    def test_reopening_a_migrated_database_is_idempotent(self):
        self._make_v2_database()
        MemoryStore(self.path).close()
        before = self._evidence_columns()
        store = MemoryStore(self.path)
        try:
            self.assertEqual(before, self._evidence_columns(),
                             "migration must not add duplicate columns")
            self.assertEqual(3, store.schema_version())
            memory = store.get_memory("mem_v2")
            self.assertEqual(1, memory["evidence_count"])
            self.assertEqual("support",
                             memory["evidence"][0]["evidence_role"])
        finally:
            store.close()

    def test_unsupported_schema_version_is_still_rejected(self):
        store = MemoryStore(self.path)
        store.close()
        conn = sqlite3.connect(self.path)
        conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
        conn.commit()
        conn.close()
        from agent.memory.store import MemoryUnavailable
        with self.assertRaises(MemoryUnavailable):
            MemoryStore(self.path)


class EvidenceRoleTestBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.events_path = os.path.join(self.tmp.name, "events.db")
        self.memories_path = os.path.join(self.tmp.name, "memories.db")
        self.events = EventStore(self.events_path)
        self.timeline = self.events.start_session(
            agent_session_id="sess-v3", game_version=VERSION,
            game_mvid="mvid", instance_id="local-01").timeline_id
        self.events.attach_identity(self.timeline, run_id="run-v3", slot=40,
                                    run_number=1)
        self.store = MemoryStore(self.memories_path,
                                 events_db=self.events_path)

    def tearDown(self):
        try:
            self.store.close()
            self.events.close()
        finally:
            self.tmp.cleanup()

    def add_event(self, name):
        return self.events.append_event(
            self.timeline, event_type="observation", tool_name=name,
            result_status="success", result={"ok": True},
            capture_quality="complete")

    def add_memory(self, memory_id="mem_role", evidence=None, **overrides):
        evidence = evidence if evidence is not None else [self.add_event("get_state")]
        kwargs = dict(
            kind="warning", text="role rule", evidence=evidence,
            game_version=VERSION, save_id="slot:40", run_id="run-v3",
            timeline_id=self.timeline, memory_id=memory_id)
        kwargs.update(overrides)
        result = self.store.add_candidate(**kwargs)
        self.assertTrue(result["ok"], result)
        return result

    def scope(self):
        return {"game_id": GAME_ID, "game_version": VERSION,
                "save_id": "slot:40", "run_id": "run-v3",
                "timeline_id": self.timeline}


class EvidenceRoleTests(EvidenceRoleTestBase):
    def test_evidence_roles_are_stored_and_counted(self):
        support = self.add_event("get_state")
        contradict = self.add_event("confirm_trade")
        context = self.add_event("list_goods")
        self.add_memory(evidence=[
            {"event_id": support, "role": "support"},
            {"event_id": contradict, "role": "contradict"},
            {"event_id": context, "role": "context"},
        ])
        memory = self.store.get_memory("mem_role")
        counts = {role: sum(1 for row in memory["evidence"]
                            if row["evidence_role"] == role)
                  for role in EVIDENCE_ROLES}
        self.assertEqual({"support": 1, "contradict": 1, "context": 1}, counts)
        self.assertEqual(1, memory["contradict_count"])
        self.assertEqual(1, memory["support_count"])
        self.assertEqual(1, memory["context_count"])
        for row in memory["evidence"]:
            self.assertTrue(row["added_at"])

    def test_string_evidence_defaults_to_support(self):
        self.add_memory(evidence=[self.add_event("get_state")])
        memory = self.store.get_memory("mem_role")
        self.assertEqual("support", memory["evidence"][0]["evidence_role"])

    def test_invalid_evidence_role_is_rejected(self):
        with self.assertRaises(MemoryValidationError):
            self.add_memory(evidence=[{
                "event_id": self.add_event("get_state"),
                "role": "maybe"}])

    def test_search_and_bootstrap_report_contradict_counts(self):
        first = self.add_event("get_state")
        self.add_memory(evidence=[first])
        self.assertTrue(self.store.verify("mem_role", actor="user:test")["ok"])
        contradict = self.add_event("confirm_trade")
        added = self.store.add_evidence(
            "mem_role", [{"event_id": contradict, "role": "contradict"}],
            actor="user:test", reason="counterexample")
        self.assertTrue(added["ok"], added)
        hits = self.store.search("role rule", scope=self.scope(), state={})
        self.assertEqual(["mem_role"], [row["memory_id"] for row in hits])
        self.assertEqual(1, hits[0]["contradict_count"])
        boot = self.store.bootstrap(limit=5, scope=self.scope())
        self.assertEqual(["mem_role"], [row["memory_id"] for row in boot])
        self.assertEqual(1, boot[0]["contradict_count"])
        self.assertIn("contradict=1", boot[0]["why"])

    def test_add_evidence_is_idempotent_and_writes_one_revision(self):
        self.add_memory(evidence=[self.add_event("get_state")])
        extra = self.add_event("list_goods")
        first = self.store.add_evidence(
            "mem_role", [{"event_id": extra, "role": "context"}],
            actor="user:test", reason="supporting context")
        self.assertTrue(first["ok"], first)
        self.assertFalse(first["deduped"])
        revision_count = len(self.store.revisions("mem_role"))
        second = self.store.add_evidence(
            "mem_role", [{"event_id": extra, "role": "context"}],
            actor="user:test", reason="again")
        self.assertTrue(second["ok"], second)
        self.assertTrue(second["deduped"])
        self.assertEqual(revision_count,
                         len(self.store.revisions("mem_role")))
        self.assertEqual(2, self.store.get_memory("mem_role")["evidence_count"])

    def test_add_evidence_rejects_unknown_memory_and_missing_event(self):
        with self.assertRaises(MemoryNotFound):
            self.store.add_evidence("mem_absent", ["ev_absent"])
        self.add_memory(evidence=[self.add_event("get_state")])
        missing = self.store.add_evidence("mem_role", ["ev_ghost"])
        self.assertFalse(missing["ok"])
        self.assertEqual("no_evidence", missing["reason"])
        self.assertEqual(["ev_ghost"], missing["missing_event_ids"])

    def test_existing_event_cannot_be_silently_reclassified(self):
        event = self.add_event("get_state")
        self.add_memory(evidence=[{"event_id": event, "role": "support"}])
        conflict = self.store.add_evidence(
            "mem_role", [{"event_id": event, "role": "contradict"}])
        self.assertFalse(conflict["ok"])
        self.assertEqual("evidence_role_conflict", conflict["reason"])
        self.assertEqual("support",
                         self.store.get_memory("mem_role")["evidence"][0]
                         ["evidence_role"])

    def test_contradicting_evidence_blocks_verification(self):
        self.add_memory(evidence=[
            {"event_id": self.add_event("get_state"), "role": "support"},
            {"event_id": self.add_event("confirm_trade"), "role": "contradict"},
        ])
        result = self.store.verify("mem_role", actor="user:test")
        self.assertFalse(result["ok"])
        self.assertEqual("unverifiable", result["reason"])
        self.assertIn("contradictory_evidence", result["detail"])
        self.assertEqual(STATUS_CANDIDATE,
                         self.store.get_memory("mem_role")["status"])

    def test_adding_contradiction_after_verification_keeps_verified_status(self):
        self.add_memory(evidence=[self.add_event("get_state")])
        self.assertTrue(self.store.verify("mem_role", actor="user:test")["ok"])
        self.store.add_evidence(
            "mem_role", [{"event_id": self.add_event("confirm_trade"),
                          "role": "contradict"}])
        memory = self.store.get_memory("mem_role")
        self.assertEqual(STATUS_VERIFIED, memory["status"],
                         "P3 owns the stale transition; P2 only surfaces the count")
        self.assertEqual(1, memory["contradict_count"])

    def test_status_reports_evidence_roles(self):
        self.add_memory(evidence=[{
            "event_id": self.add_event("get_state"), "role": "support"}])
        self.add_evidence_fixture()
        info = self.store.status()
        self.assertEqual(3, info["schema_version"])
        self.assertEqual({"support": 1, "contradict": 1},
                         info["evidence_by_role"])
        self.assertEqual(1, info["contradict"])

    def add_evidence_fixture(self):
        # A second, unrelated memory carrying one contradiction.
        self.store.add_candidate(
            kind="fact", text="second role rule",
            evidence=[{"event_id": self.add_event("list_goods"),
                       "role": "contradict"}],
            game_version=VERSION, save_id="slot:40", run_id="run-v3",
            timeline_id=self.timeline, memory_id="mem_role_2")


class EventInspectionTests(EvidenceRoleTestBase):
    def test_event_details_are_read_only_and_available(self):
        event = self.add_event("get_state")
        self.add_memory(evidence=[event])
        details = self.store.event_details([event])
        self.assertEqual("get_state", details[event]["tool_name"])
        self.assertEqual("success", details[event]["result_status"])
        self.assertEqual("complete", details[event]["capture_quality"])

    def test_events_status_reports_both_databases_and_degrades(self):
        event = self.add_event("get_state")
        self.add_memory(evidence=[event])
        configured = self.store.events_status()
        self.assertTrue(configured["configured"])
        self.assertTrue(configured["available"])
        self.assertEqual(1, configured["schema_version"])
        self.assertEqual(1, configured["timelines"])
        self.assertEqual(1, configured["events"])
        self.assertEqual({"success": 1}, configured["by_result_status"])

        self.store.close()
        self.store = MemoryStore(
            os.path.join(self.tmp.name, "standalone.db"))
        degraded = self.store.events_status()
        self.assertFalse(degraded["configured"])
        self.assertEqual("not_configured", degraded["reason"])


if __name__ == "__main__":
    unittest.main()
