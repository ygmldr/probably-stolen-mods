"""P1 event store tests: schema, seq, timeline isolation, pending reconcile."""

import json
import os
import sqlite3
import sys
import tempfile
import threading
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.store import EventStore, MemoryUnavailable  # noqa: E402


def _state(day=1, cash=100, rev=10):
    return {
        "rev": rev,
        "in_game": True,
        "day": day,
        "cash": cash,
        "shop_state": "CLOSED",
        "rent": {"due": 50, "days_left": 7, "rent_paid_total": 0},
        "inventory_count": 0,
    }


class StoreSchemaTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "memory.db")
        self.store = EventStore(self.path)

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_creates_expected_tables(self):
        names = self.store.table_names()
        for expected in ("meta", "timelines", "snapshots", "events"):
            self.assertIn(expected, names)

    def test_migration_is_idempotent(self):
        version = self.store.schema_version()
        self.assertEqual(1, version)
        self.store.close()
        reopened = EventStore(self.path)
        try:
            self.assertEqual(version, reopened.schema_version())
            self.assertEqual(0, reopened.count_events())
        finally:
            reopened.close()

    def test_rejects_unknown_schema_version(self):
        self.store.close()
        conn = sqlite3.connect(self.path)
        conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
        conn.commit()
        conn.close()
        with self.assertRaises(MemoryUnavailable):
            EventStore(self.path)
        os.unlink(self.path)
        self.store = EventStore(self.path)  # keep tearDown happy


class TimelineTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = EventStore(os.path.join(self.tmp.name, "memory.db"))
        self.session = self.store.start_session(
            agent_session_id="sess-1",
            game_version="VERSION 100-Rev13",
            game_mvid="911508a6-e0eb-4cc0-91e8-e14b16ea28d7",
            instance_id="local-01",
        )

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_session_timeline_has_no_run_identity_yet(self):
        row = self.store.get_timeline(self.session.timeline_id)
        self.assertIsNone(row["run_id"])
        self.assertEqual("session", row["reason"])
        self.assertEqual("VERSION 100-Rev13", row["game_version"])

    def test_binding_attaches_identity_to_the_same_timeline(self):
        self.store.attach_identity(self.session.timeline_id, run_id="run-a",
                                   slot=40, run_number=1)
        row = self.store.get_timeline(self.session.timeline_id)
        self.assertEqual("run-a", row["run_id"])
        self.assertEqual("slot:40", row["save_id"])
        self.assertEqual(40, row["slot"])

    def test_run_change_forks_a_new_timeline_with_parent(self):
        self.store.attach_identity(self.session.timeline_id, run_id="run-a",
                                   slot=40, run_number=1)
        child = self.store.fork_timeline(self.session.timeline_id,
                                         run_id="run-b", slot=40, run_number=2,
                                         reason="external_replace")
        self.assertNotEqual(self.session.timeline_id, child)
        row = self.store.get_timeline(child)
        self.assertEqual(self.session.timeline_id, row["parent_timeline_id"])
        self.assertEqual("external_replace", row["reason"])

    def test_rollback_fork_keeps_old_timeline_readable(self):
        self.store.attach_identity(self.session.timeline_id, run_id="run-a",
                                   slot=40, run_number=1)
        self.store.append_event(self.session.timeline_id, event_type="result",
                                tool_name="confirm_trade", result_status="success")
        child = self.store.fork_timeline(self.session.timeline_id, run_id="run-a",
                                         slot=40, run_number=1, reason="rollback")
        self.assertEqual(1, self.store.count_events(self.session.timeline_id))
        self.assertEqual(0, self.store.count_events(child))


class EventTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = EventStore(os.path.join(self.tmp.name, "memory.db"))
        self.tl = self.store.start_session(
            agent_session_id="sess-1", game_version="v1", game_mvid="m1",
            instance_id="local-01").timeline_id

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_sequence_is_monotonic_per_timeline(self):
        for i in range(5):
            self.store.append_event(self.tl, event_type="observation",
                                    tool_name="get_state", result_status="success")
        seqs = [row["seq"] for row in self.store.history(self.tl)]
        self.assertEqual([1, 2, 3, 4, 5], seqs)

    def test_concurrent_appends_do_not_duplicate_seq(self):
        errors = []

        def worker():
            try:
                for _ in range(20):
                    self.store.append_event(self.tl, event_type="observation",
                                            tool_name="get_state",
                                            result_status="success")
            except Exception as exc:  # pragma: no cover - surfaced below
                errors.append(exc)

        threads = [threading.Thread(target=worker) for _ in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual([], errors)
        events = self.store.history(self.tl, limit=1000)
        self.assertEqual(80, len(events))
        seqs = [e["seq"] for e in events]
        self.assertEqual(len(set(seqs)), len(seqs), "seq must be unique")

    def test_intent_then_result_links_by_event_id(self):
        intent = self.store.append_intent(self.tl, tool_name="confirm_trade",
                                          args={"item_id": 1, "side": "buy"},
                                          action_id="act-1", rev=10, game_day=3)
        self.assertEqual("pending", self.store.get_event(intent)["result_status"])
        self.store.append_result(intent, status="success",
                                 result={"ok": True, "cash": 90},
                                 capture_quality="complete")
        row = self.store.get_event(intent)
        self.assertEqual("success", row["result_status"])
        self.assertEqual("act-1", row["action_id"])
        self.assertEqual(3, row["game_day"])

    def test_pending_intents_are_reported_for_reconciliation(self):
        self.store.append_intent(self.tl, tool_name="sleep", args={},
                                 action_id="act-2", rev=11, game_day=4)
        pending = self.store.pending_events(self.tl)
        self.assertEqual(1, len(pending))
        self.assertEqual("sleep", pending[0]["tool_name"])
        self.assertEqual(1, self.store.reconcile_pending(self.tl))
        self.assertEqual([], self.store.pending_events(self.tl))
        row = self.store.history(self.tl, limit=1)[0]
        self.assertEqual("unknown", row["result_status"])

    def test_reconcile_is_idempotent(self):
        self.store.append_intent(self.tl, tool_name="sleep", args={},
                                 action_id="act-3", rev=11, game_day=4)
        self.assertEqual(1, self.store.reconcile_pending(self.tl))
        self.assertEqual(0, self.store.reconcile_pending(self.tl))


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = EventStore(os.path.join(self.tmp.name, "memory.db"))
        self.tl = self.store.start_session(
            agent_session_id="s", game_version="v1", game_mvid="m1",
            instance_id="i").timeline_id

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_first_observation_is_a_baseline_without_delta(self):
        snap = self.store.observe_state(self.tl, _state(day=1, cash=100))
        self.assertIsNotNone(snap.snapshot_id)
        self.assertIsNone(snap.delta)
        self.assertFalse(snap.changed)

    def test_identical_state_does_not_create_a_second_snapshot(self):
        a = self.store.observe_state(self.tl, _state())
        b = self.store.observe_state(self.tl, _state())
        self.assertEqual(a.snapshot_id, b.snapshot_id)
        self.assertFalse(b.changed)
        self.assertEqual(1, self.store.count_snapshots(self.tl))

    def test_changed_state_records_minimal_delta(self):
        self.store.observe_state(self.tl, _state(day=1, cash=100, rev=10))
        snap = self.store.observe_state(self.tl, _state(day=2, cash=80, rev=12))
        self.assertTrue(snap.changed)
        self.assertEqual({"day": 2, "cash": 80}, {
            "day": snap.delta["day"]["after"],
            "cash": snap.delta["cash"]["after"],
        })
        self.assertEqual(100, snap.delta["cash"]["before"])

    def test_unchanged_noise_fields_do_not_create_delta(self):
        first = _state()
        second = _state()
        second["timestamp"] = "2026-11-15T00:00:00Z"
        self.store.observe_state(self.tl, first)
        snap = self.store.observe_state(self.tl, second)
        self.assertFalse(snap.changed, "timestamp must be excluded from the hash")

    def test_delta_survives_reload(self):
        self.store.observe_state(self.tl, _state(day=1))
        self.store.observe_state(self.tl, _state(day=2))
        snapshots = self.store.snapshots(self.tl)
        self.assertEqual(2, len(snapshots))
        self.assertIsNotNone(snapshots[-1]["delta_json"])


class DatabaseFailureTests(unittest.TestCase):
    def test_unwritable_path_raises_memory_unavailable(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            blocker = os.path.join(tmp.name, "not-a-directory")
            with open(blocker, "w", encoding="utf-8") as handle:
                handle.write("x")
            target = os.path.join(blocker, "memory.db")
            with self.assertRaises(MemoryUnavailable):
                EventStore(target)
        finally:
            tmp.cleanup()

    def test_closed_store_raises_memory_unavailable(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            store = EventStore(os.path.join(tmp.name, "memory.db"))
            tl = store.start_session(agent_session_id="s", game_version="v1",
                                     game_mvid="m1", instance_id="i").timeline_id
            store.close()
            with self.assertRaises(MemoryUnavailable):
                store.append_event(tl, event_type="observation", tool_name="x",
                                   result_status="success")
        finally:
            tmp.cleanup()


if __name__ == "__main__":
    unittest.main()
