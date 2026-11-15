"""P2 memory store tests: schema v2, evidence/scope validation, FTS5 sync.

All databases live in temporary directories. Nothing here touches a real save,
the live recording database, or the game bridge.
"""

import json
import os
import sqlite3
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.store import (  # noqa: E402
    EventStore,
    MemoryUnavailable,
    MemoryValidationError,
)

VERSION = "VERSION 100-Rev13"
OLD_VERSION = "VERSION 100-Rev12"

#: The exact schema P1 shipped. Used only to prove an in-place v1 -> v2 upgrade.
V1_SCHEMA = """
CREATE TABLE meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE timelines (
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
CREATE TABLE snapshots (
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
CREATE TABLE events (
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
INSERT INTO meta(key, value) VALUES('schema_version', '1');
INSERT INTO timelines(timeline_id, agent_session_id, run_id, save_id, slot,
  run_number, parent_timeline_id, reason, game_version, game_mvid,
  instance_id, created_at)
VALUES('tl_v1', 'sess-v1', 'run-v1', 'slot:40', 40, 1, NULL, 'session',
  'VERSION 100-Rev13', 'mvid', 'local-01', '2026-11-15T00:00:00Z');
INSERT INTO events(event_id, timeline_id, seq, action_id, event_type,
  tool_name, args_json, result_status, result_json, before_snapshot_id,
  after_snapshot_id, delta_json, capture_quality, game_day, rev, occurred_at)
VALUES('ev_v1', 'tl_v1', 1, 'act-v1', 'intent', 'confirm_trade',
  '{"item_id":1}', 'success', '{"ok":true}', NULL, NULL, NULL, 'complete',
  1, 10, '2026-11-15T00:00:01Z');
"""


class StoreFixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "memory.db")
        self.store = EventStore(self.path)
        self.session = self.store.start_session(
            agent_session_id="sess-a", game_version=VERSION,
            game_mvid="mvid", instance_id="local-01")
        self.tl = self.session.timeline_id
        self.store.attach_identity(self.tl, run_id="run-a", slot=40,
                                   run_number=1)
        self.other = self.store.start_session(
            agent_session_id="sess-b", game_version=VERSION,
            game_mvid="mvid", instance_id="local-01")
        self.other_tl = self.other.timeline_id
        self.store.attach_identity(self.other_tl, run_id="run-b", slot=41,
                                   run_number=1)

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def event(self, timeline_id=None, quality="complete", status="success",
              tool_name="get_state", event_type="observation", result=None):
        return self.store.append_event(
            timeline_id or self.tl, event_type=event_type,
            tool_name=tool_name, result_status=status,
            result=result or {"ok": True}, capture_quality=quality)

    def verified(self, *, memory_id, kind="warning", text="test rule",
                 entities=None, conditions=None, evidence=None, **scope):
        evidence = evidence or [self.event()]
        return self.store.create_memory(
            memory_id=memory_id, kind=kind, text=text,
            status="verified", confidence="high", source="seed",
            entities=entities or ["inventory"],
            conditions=conditions if conditions is not None else {},
            evidence=[{"event_id": event_id} for event_id in evidence],
            **scope)


class SchemaAndMigrationTests(unittest.TestCase):
    def test_v2_schema_has_memory_tables_and_fts(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            store = EventStore(os.path.join(tmp.name, "memory.db"))
            try:
                names = store.table_names()
                for expected in ("memories", "memory_evidence",
                                 "memory_revisions", "memories_fts"):
                    self.assertIn(expected, names)
                self.assertEqual(2, store.schema_version())
            finally:
                store.close()
        finally:
            tmp.cleanup()

    def test_v1_database_upgrades_in_place_and_keeps_events(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            path = os.path.join(tmp.name, "v1.db")
            conn = sqlite3.connect(path)
            conn.executescript(V1_SCHEMA)
            conn.commit()
            conn.close()

            store = EventStore(path)
            try:
                self.assertEqual(2, store.schema_version())
                for expected in ("memories", "memory_evidence",
                                 "memory_revisions", "memories_fts"):
                    self.assertIn(expected, store.table_names())
                self.assertEqual(1, store.count_events("tl_v1"))
                event = store.get_event("ev_v1")
                self.assertEqual("confirm_trade", event["tool_name"])
                self.assertEqual("success", event["result_status"])
                memory = store.create_memory(
                    memory_id="mem_after_upgrade", kind="fact",
                    text="v1 upgrade keeps working", status="candidate")
                self.assertEqual("candidate", memory["status"])
            finally:
                store.close()

            reopened = EventStore(path)
            try:
                self.assertEqual(2, reopened.schema_version())
                self.assertEqual(1, reopened.count_events("tl_v1"))
                self.assertEqual("mem_after_upgrade",
                                 reopened.get_memory("mem_after_upgrade")["memory_id"])
            finally:
                reopened.close()
        finally:
            tmp.cleanup()

    def test_v1_without_schema_row_is_initialised_idempotently(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            path = os.path.join(tmp.name, "empty-v1.db")
            conn = sqlite3.connect(path)
            conn.executescript(V1_SCHEMA.replace(
                "INSERT INTO meta(key, value) VALUES('schema_version', '1');", ""))
            conn.commit()
            conn.close()
            store = EventStore(path)
            try:
                self.assertEqual(2, store.schema_version())
            finally:
                store.close()
            store = EventStore(path)
            try:
                self.assertEqual(2, store.schema_version())
            finally:
                store.close()
        finally:
            tmp.cleanup()


class FtsTests(StoreFixture):
    def test_fts_insert_update_delete_and_rebuild_stay_in_sync(self):
        memory = self.store.create_memory(
            memory_id="mem_fts", kind="fact",
            text="inventory capacity warning", status="candidate")
        self.assertEqual("mem_fts", memory["memory_id"])
        conn = self.store._require()

        def matches(term):
            return conn.execute(
                "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
                (term,)).fetchall()

        self.assertEqual(1, len(matches('"inventory"*')))
        conn.execute("UPDATE memories SET text=? WHERE memory_id=?",
                     ("rent due warning", "mem_fts"))
        self.assertEqual([], matches('"inventory"*'))
        self.assertEqual(1, len(matches('"rent"*')))

        conn.execute("DELETE FROM memories WHERE memory_id='mem_fts'")
        self.assertEqual([], matches('"rent"*'))

        self.store.create_memory(memory_id="mem_fts_2", kind="fact",
                                 text="rebuild me", status="candidate")
        conn.execute("INSERT INTO memories_fts(memories_fts) VALUES('delete-all')")
        self.assertEqual([], matches('"rebuild"*'))
        self.store.rebuild_fts()
        self.assertEqual(1, len(matches('"rebuild"*')))

    def test_unicode61_needs_prefix_but_retrieval_keyword_covers_chinese(self):
        evidence = self.event()
        self.store.create_memory(
            memory_id="mem_cn_fts", kind="warning",
            text="库存接近上限时不要继续进货", status="verified",
            confidence="high", source="seed", entities=["库存"],
            conditions={"requires_any": ["库存"]},
            evidence=[{"event_id": evidence}])
        conn = self.store._require()
        self.assertEqual(
            [], conn.execute(
                "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
                ('"库存"',)).fetchall(),
            "unicode61 does not segment Chinese; exact token query misses")
        self.assertEqual(
            1, len(conn.execute(
                "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?",
                ('"库存"*',)).fetchall()),
            "prefix query is the FTS fallback for a Chinese token prefix")
        result = self.store.retrieve("库存接近上限还进货吗", limit=5,
                                     scope_filters={"game_version": VERSION})
        self.assertIn("mem_cn_fts", [m["memory_id"] for m in result["memories"]])
        self.assertIn(result["memories"][0]["recall_reason"],
                      ("fulltext", "keyword", "entity"))


class StateMachineTests(StoreFixture):
    def test_candidate_without_evidence_cannot_be_verified(self):
        self.store.create_memory(memory_id="mem_no_ev", kind="fact",
                                 text="no evidence", status="candidate")
        with self.assertRaises(MemoryValidationError):
            self.store.promote_memory("mem_no_ev")
        self.assertEqual("candidate",
                         self.store.get_memory("mem_no_ev")["status"])

    def test_verified_without_evidence_is_rejected_and_not_stored(self):
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(memory_id="mem_bad", kind="fact",
                                     text="cannot verify", status="verified")
        with self.assertRaises(MemoryUnavailable):
            self.store.get_memory("mem_bad")

    def test_partial_evidence_cannot_verify(self):
        partial = self.event(quality="partial")
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_partial", kind="fact", text="partial evidence",
                status="verified", evidence=[{"event_id": partial}])

    def test_mechanism_needs_two_evidence_events_and_conditions(self):
        first = self.event()
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_mech_1", kind="mechanism",
                text="one event is not enough", status="verified",
                conditions={"requires_any": ["sleep"]},
                evidence=[{"event_id": first}])
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_mech_2", kind="mechanism",
                text="conditions are mandatory", status="verified",
                conditions={}, evidence=[{"event_id": first},
                                         {"event_id": self.event()}])

    def test_evidence_from_another_save_cannot_verify_save_memory(self):
        other_event = self.event(timeline_id=self.other_tl)
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_wrong_save", kind="fact", text="wrong save",
                status="verified", scope_type="save", save_id="slot:40",
                evidence=[{"event_id": other_event}])

    def test_evidence_from_another_run_cannot_verify_run_memory(self):
        other_event = self.event(timeline_id=self.other_tl)
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_wrong_run", kind="fact", text="wrong run",
                status="verified", scope_type="run", run_id="run-a",
                evidence=[{"event_id": other_event}])

    def test_evidence_from_another_version_cannot_verify_version_memory(self):
        old = self.store.start_session(
            agent_session_id="sess-old", game_version=OLD_VERSION,
            game_mvid="mvid", instance_id="local-01")
        old_event = self.event(timeline_id=old.timeline_id)
        with self.assertRaises(MemoryValidationError):
            self.store.create_memory(
                memory_id="mem_wrong_version", kind="fact", text="wrong version",
                status="verified", scope_type="game_version",
                game_version=VERSION, evidence=[{"event_id": old_event}])

    def test_valid_verified_memory_records_evidence_and_revisions(self):
        first = self.event(result={"inventory_occupancy": 0.95})
        memory = self.store.create_memory(
            memory_id="mem_ok", kind="warning", text="inventory is nearly full",
            status="verified", confidence="high", source="seed",
            scope_type="game_version", game_version=VERSION,
            conditions={"requires_any": ["inventory"]},
            entities=["inventory"], evidence=[{"event_id": first}])
        self.assertEqual("verified", memory["status"])
        self.assertEqual(1, memory["evidence_count"])
        loaded = self.store.get_memory("mem_ok")
        self.assertEqual(1, len(loaded["evidence"]))
        self.assertEqual(first, loaded["evidence"][0]["event_id"])
        revisions = self.store._require().execute(
            "SELECT * FROM memory_revisions WHERE memory_id='mem_ok'").fetchall()
        self.assertGreaterEqual(len(revisions), 1)

    def test_deprecate_and_supersede_are_terminal_and_logged(self):
        self.store.create_memory(memory_id="mem_old", kind="fact",
                                 text="old rule", status="candidate")
        self.store.create_memory(memory_id="mem_new", kind="fact",
                                 text="new rule", status="candidate")
        self.store.deprecate_memory("mem_old", reason="manual correction")
        old = self.store.get_memory("mem_old")
        self.assertEqual("deprecated", old["status"])
        with self.assertRaises(MemoryValidationError):
            self.store.promote_memory("mem_old")

        self.store.supersede_memory("mem_new", "mem_old",
                                    reason="replaced by better rule")
        new = self.store.get_memory("mem_new")
        self.assertEqual("superseded", new["status"])
        self.assertEqual("mem_old", new["superseded_by"])

    def test_conflict_marks_both_and_never_overwrites_text(self):
        first = self.event()
        second = self.event()
        self.store.create_memory(
            memory_id="mem_high", kind="strategy",
            text="陈酿葡萄酒应该报高价", status="verified",
            confidence="low", source="seed",
            conditions={"requires_any": ["陈酿"]},
            entities=["陈酿", "报价"],
            evidence=[{"event_id": first}, {"event_id": second}])
        self.store.create_memory(
            memory_id="mem_low", kind="strategy",
            text="陈酿葡萄酒应该报低价", status="verified",
            confidence="low", source="seed",
            conditions={"requires_any": ["陈酿"]},
            entities=["陈酿", "报价"],
            evidence=[{"event_id": self.event()},
                      {"event_id": self.event()}])
        high = self.store.get_memory("mem_high")
        low = self.store.get_memory("mem_low")
        self.assertEqual("conflict", high["status"])
        self.assertEqual("conflict", low["status"])
        self.assertEqual(high["conflict_group"], low["conflict_group"])
        self.assertEqual("陈酿葡萄酒应该报高价", high["text"])
        self.assertEqual("陈酿葡萄酒应该报低价", low["text"])

    def test_same_kind_and_entity_without_contradiction_is_not_conflict(self):
        self.store.create_memory(
            memory_id="mem_neg_a", kind="warning",
            text="现金不足时不要进货", status="candidate",
            entities=["cash", "rent"])
        self.store.create_memory(
            memory_id="mem_neg_b", kind="warning",
            text="Never confirm a purchase when cash is below rent",
            status="candidate", entities=["cash", "rent"])
        self.assertEqual("candidate",
                         self.store.get_memory("mem_neg_a")["status"])
        self.assertEqual("candidate",
                         self.store.get_memory("mem_neg_b")["status"])


class ScopeTests(StoreFixture):
    def setUp(self):
        super().setUp()
        self.version_mem = self.verified(
            memory_id="mem_version", text="version rule",
            scope_type="game_version", game_version=VERSION)
        self.save_mem = self.verified(
            memory_id="mem_save", text="save private rule",
            scope_type="save", save_id="slot:40", run_id="run-a",
            evidence=[self.event()])
        self.run_mem = self.verified(
            memory_id="mem_run", text="run private fact", kind="fact",
            scope_type="run", run_id="run-a", save_id="slot:40",
            evidence=[self.event()])

    def _ids(self, scope, query="rule OR fact OR private OR version OR save OR run"):
        result = self.store.retrieve(query, limit=20, scope_filters=scope)
        return {m["memory_id"] for m in result["memories"]}

    def test_full_scope_sees_all_compatible_memories(self):
        ids = self._ids({"game_version": VERSION, "save_id": "slot:40",
                         "run_id": "run-a", "timeline_id": self.tl})
        self.assertEqual({"mem_version", "mem_save", "mem_run"}, ids)

    def test_other_save_never_sees_save_or_run_memories(self):
        ids = self._ids({"game_version": VERSION, "save_id": "slot:41",
                         "run_id": "run-b", "timeline_id": self.other_tl})
        self.assertEqual({"mem_version"}, ids)

    def test_missing_scope_degrades_to_global_memories(self):
        ids = self._ids({})
        self.assertEqual({"mem_version"}, ids)

    def test_missing_scope_does_not_hide_version_memories(self):
        ids = self._ids({"save_id": "slot:40", "run_id": "run-a"})
        self.assertEqual({"mem_version", "mem_save", "mem_run"}, ids)

    def test_version_mismatch_hides_old_memories(self):
        ids = self._ids({"game_version": OLD_VERSION})
        self.assertEqual(set(), ids)

    def test_another_run_does_not_see_run_private_memory(self):
        ids = self._ids({"game_version": VERSION, "save_id": "slot:41",
                         "run_id": "run-b"})
        self.assertEqual(set(), ids)


class ConditionsTests(StoreFixture):
    def setUp(self):
        super().setUp()
        self.event_id = self.event()
        self.store.create_memory(
            memory_id="mem_cond", kind="strategy",
            text="只有当库存占用达到九成以上且动作是买入时才先清理库存",
            status="verified", confidence="medium", source="seed",
            conditions={"requires_any": ["库存", "inventory"],
                        "state": {"inventory_occupancy_min": 0.9}},
            entities=["库存", "inventory"],
            evidence=[{"event_id": self.event_id},
                      {"event_id": self.event()}])

    def test_condition_state_below_threshold_is_not_applicable(self):
        result = self.store.retrieve(
            "库存买入", scope_filters={"game_version": VERSION,
                                        "state": {"inventory_occupancy": 0.5}})
        self.assertNotIn("mem_cond",
                         [m["memory_id"] for m in result["memories"]])

    def test_condition_state_above_threshold_is_applicable(self):
        result = self.store.retrieve(
            "库存买入", scope_filters={"game_version": VERSION,
                                        "state": {"inventory_occupancy": 0.95}})
        self.assertIn("mem_cond",
                      [m["memory_id"] for m in result["memories"]])

    def test_condition_without_state_is_not_applicable(self):
        result = self.store.retrieve(
            "库存买入", scope_filters={"game_version": VERSION})
        self.assertNotIn("mem_cond",
                         [m["memory_id"] for m in result["memories"]])


class RetrievalTests(StoreFixture):
    def setUp(self):
        super().setUp()
        self.store.create_memory(
            memory_id="mem_boot_warning", kind="warning",
            text="库存接近上限时不要继续进货", status="verified",
            confidence="high", source="seed",
            conditions={"requires_any": ["库存"]},
            entities=["库存"], evidence=[{"event_id": self.event()}])
        self.store.create_memory(
            memory_id="mem_boot_lesson", kind="lesson",
            text="交易前先核对 rev", status="verified",
            confidence="medium", source="seed",
            conditions={"requires_any": ["交易", "rev"]},
            entities=["交易"], evidence=[{"event_id": self.event()}])

    def test_bootstrap_returns_required_fields_and_priority(self):
        result = self.store.bootstrap(limit=8, scope_filters={"game_version": VERSION})
        self.assertTrue(result["ok"])
        ids = [m["memory_id"] for m in result["memories"]]
        self.assertEqual("mem_boot_warning", ids[0])
        for item in result["memories"]:
            for key in ("memory_id", "kind", "text", "confidence", "why",
                        "evidence_count"):
                self.assertIn(key, item)

    def test_bootstrap_excludes_unverified_and_conflict_memories(self):
        self.store.create_memory(memory_id="mem_boot_candidate", kind="lesson",
                                 text="candidate", status="candidate")
        result = self.store.bootstrap(limit=8, scope_filters={"game_version": VERSION})
        ids = [m["memory_id"] for m in result["memories"]]
        self.assertNotIn("mem_boot_candidate", ids)

    def test_budget_cap_returns_subset_and_never_exceeds(self):
        full = self.store.retrieve("库存 交易", limit=5,
                                   scope_filters={"game_version": VERSION},
                                   token_budget=800)
        self.assertGreaterEqual(len(full["memories"]), 2)
        capped = self.store.retrieve("库存 交易", limit=5,
                                     scope_filters={"game_version": VERSION},
                                     token_budget=full["memories"][0]["token_estimate"])
        self.assertLessEqual(capped["token_estimate"],
                             full["memories"][0]["token_estimate"])
        self.assertEqual(1, len(capped["memories"]))
        self.assertTrue(capped["truncated"])

    def test_unknown_query_returns_empty(self):
        result = self.store.retrieve("量子香蕉曲率引擎", scope_filters={})
        self.assertEqual([], result["memories"])
        self.assertFalse(result["truncated"])

    def test_get_memory_raises_for_unknown_id(self):
        with self.assertRaises(MemoryUnavailable):
            self.store.get_memory("mem_missing")

    def test_status_reports_counts_path_and_version(self):
        status = self.store.memory_status()
        self.assertEqual(self.store.path, status["db"])
        self.assertEqual(2, status["schema_version"])
        self.assertEqual(2, status["memories"])
        self.assertEqual(2, status["by_status"]["verified"])


class SeedImportTests(unittest.TestCase):
    def test_seed_import_is_idempotent(self):
        from agent.memory import seed

        tmp = tempfile.TemporaryDirectory()
        try:
            path = os.path.join(tmp.name, "seed.db")
            first = seed.seed_database(path)
            self.assertGreaterEqual(first["memories_added"], 10)
            second = seed.seed_database(path)
            self.assertEqual(0, second["memories_added"])
            self.assertEqual(0, second["events_added"])
            store = EventStore(path)
            try:
                status = store.memory_status()
                self.assertEqual(2, status["schema_version"])
                self.assertGreaterEqual(status["memories"], 10)
                self.assertGreaterEqual(status["by_status"].get("conflict", 0), 2)
                self.assertGreaterEqual(status["by_status"].get("deprecated", 0), 1)
                self.assertGreaterEqual(status["by_status"].get("candidate", 0), 1)
                for memory_id in ("mem_seed_inv_capacity",
                                  "mem_seed_water_price",
                                  "mem_seed_inventory_condition"):
                    self.assertEqual("verified",
                                     store.get_memory(memory_id)["status"])
                    self.assertGreaterEqual(
                        store.get_memory(memory_id)["evidence_count"], 1)
            finally:
                store.close()
        finally:
            tmp.cleanup()


if __name__ == "__main__":
    unittest.main()
