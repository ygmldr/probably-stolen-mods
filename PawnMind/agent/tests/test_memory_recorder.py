"""P1 recorder tests: intent-before-dispatch, read classification, unknown, sleep."""

import os
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.recorder import (
    RecordingClient,
    SLEEP_BLOCKED_ERROR,
    SLEEP_PENDING_ERROR,
)
from agent.memory.store import (
    EVENT_EXTERNAL,
    EVENT_INTENT,
    EVENT_OBSERVATION,
    EVENT_RESULT,
    EVENT_SLEEP_COMPLETED,
    EVENT_SLEEP_UNRESOLVED,
    EventStore,
    MemoryUnavailable,
)


def state(day=1, cash=100, rev=10, **extra):
    payload = {
        "rev": rev,
        "in_game": True,
        "day": day,
        "cash": cash,
        "shop_state": "CLOSED",
        "inventory_count": 0,
        "rent": {"due": 50, "days_left": 7, "rent_paid_total": 0},
    }
    payload.update(extra)
    return payload


class FakeBridge(object):
    """Stands in for BridgeClient; records call order."""

    def __init__(self, store=None, timeline_id=None):
        self.calls = []
        self.state_value = state()
        self.responses = {}
        self.raise_on = {}
        self.store = store
        self.timeline_id = timeline_id
        self.pending_seen_during_dispatch = None
        self.port = 15528
        self.token = "t" * 32

    def get_state(self):
        self.calls.append(("get_state", {}))
        return dict(self.state_value)

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        self.calls.append(("wait_events", {"since_rev": since_rev}))
        return {"ok": True, "changed": False, "rev": since_rev}

    def health(self):
        self.calls.append(("health", {}))
        return {"ok": True, "game_version": "v1"}

    def action(self, op, **args):
        self.calls.append((op, dict(args)))
        if self.store is not None and op not in READ_ONLY_FOR_TEST:
            self.pending_seen_during_dispatch = len(
                self.store.pending_events(self.timeline_id))
        if op in self.raise_on:
            raise self.raise_on[op]
        return self.responses.get(op, {"ok": True, "op": op, "rev": 11})


READ_ONLY_FOR_TEST = {
    "get_state", "get_recorded_status", "list_goods", "get_item",
}


class RecorderTestBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = EventStore(os.path.join(self.tmp.name, "memory.db"))
        self.session = self.store.start_session(
            agent_session_id="sess-1", game_version="VERSION 100-Rev13",
            game_mvid="mvid-1", instance_id="local-01")
        self.tl = self.session.timeline_id
        self.bridge = FakeBridge(self.store, self.tl)
        self.client = RecordingClient(self.bridge, self.store, self.session)

    def tearDown(self):
        self.client.close()
        self.store.close()
        self.tmp.cleanup()

    def events(self, event_type=None):
        return self.store.history(self.tl, limit=500,
                                  event_types=[event_type] if event_type else None)


class ClassificationTests(RecorderTestBase):
    def test_read_only_action_records_observation_without_intent(self):
        self.client.action("list_goods", scope="all")
        self.assertEqual(0, len(self.events(EVENT_INTENT)))
        observations = self.events(EVENT_OBSERVATION)
        self.assertEqual(1, len(observations))
        self.assertEqual("list_goods", observations[0]["tool_name"])

    def test_write_action_records_intent_then_result(self):
        self.client.action("confirm_trade", item_id=1, side="buy", rev=10)
        intents = self.events(EVENT_INTENT)
        self.assertEqual(1, len(intents))
        self.assertEqual("confirm_trade", intents[0]["tool_name"])
        row = self.store.get_event(intents[0]["event_id"])
        self.assertEqual("success", row["result_status"])

    def test_intent_exists_before_dispatch(self):
        self.bridge.responses["confirm_trade"] = {"ok": True, "rev": 12}
        self.client.action("confirm_trade", item_id=2, side="buy", rev=10)
        self.assertEqual(1, self.bridge.pending_seen_during_dispatch,
                         "intent must be persisted before the HTTP call")

    def test_read_classification_matches_csharp_allowlist(self):
        from agent.memory.classification import READ_ONLY_ACTION_OPS
        source = os.path.join(ROOT, "src", "Bridge", "ActionRouter.cs")
        text = open(source, encoding="utf-8").read()
        block = text.split("ReadOnlyOps =", 1)[1].split("};", 1)[0]
        import re
        csharp = set(re.findall(r'"([a-z_0-9]+)"', block))
        self.assertEqual(csharp, set(READ_ONLY_ACTION_OPS),
                         "Python read-only classification drifted from C#")


class DispatchFailureTests(RecorderTestBase):
    def test_action_exception_becomes_unknown_and_is_not_retried(self):
        self.bridge.raise_on["confirm_trade"] = TimeoutError("boom")
        with self.assertRaises(Exception):
            self.client.action("confirm_trade", item_id=1, side="buy", rev=10)
        row = self.store.get_event(self.events(EVENT_INTENT)[0]["event_id"])
        self.assertEqual("unknown", row["result_status"])
        self.assertEqual(1, len([c for c in self.bridge.calls
                                 if c[0] == "confirm_trade"]),
                         "unknown outcome must not be retried")

    def test_rejected_result_is_recorded_as_rejected(self):
        self.bridge.responses["confirm_trade"] = {
            "ok": False, "error": "cannot_afford", "rev": 10}
        self.client.action("confirm_trade", item_id=1, side="buy", rev=10)
        row = self.store.get_event(self.events(EVENT_INTENT)[0]["event_id"])
        self.assertEqual("rejected", row["result_status"])

    def test_intent_write_failure_blocks_dispatch(self):
        self.store.close()
        with self.assertRaises(MemoryUnavailable):
            self.client.action("confirm_trade", item_id=1, side="buy", rev=10)
        self.assertEqual([], [c for c in self.bridge.calls
                              if c[0] == "confirm_trade"],
                         "history that cannot be recorded must not be produced")

    def test_result_write_failure_is_counted_not_raised(self):
        class FailingResultStore(EventStore):
            def append_result(self, *args, **kwargs):
                raise MemoryUnavailable("simulated write failure")

        failing = FailingResultStore(os.path.join(self.tmp.name, "fail.db"))
        try:
            session = failing.start_session(
                agent_session_id="s2", game_version="v1", game_mvid="m",
                instance_id="i")
            client = RecordingClient(self.bridge, failing, session)
            result = client.action("confirm_trade", item_id=1, side="buy", rev=10)
            self.assertTrue(result["ok"], "the game result still reaches the caller")
            self.assertGreaterEqual(client.lost_events, 1,
                                    "a lost result must be counted, not swallowed")
        finally:
            failing.close()


class SleepTests(RecorderTestBase):
    def test_sleep_is_not_complete_until_day_advances(self):
        self.bridge.responses["sleep"] = {"ok": True, "slept": True,
                                          "dispatched": True, "rev": 12}
        self.client.get_state()
        self.client.action("sleep", rev=11)
        self.assertEqual(0, len(self.events(EVENT_SLEEP_COMPLETED)),
                         "request only, not completion")

        self.bridge.state_value = state(day=2, rev=13)
        self.client.get_state()
        completed = self.events(EVENT_SLEEP_COMPLETED)
        self.assertEqual(1, len(completed))
        self.assertEqual(2, completed[0]["game_day"])

    def test_second_sleep_while_pending_is_refused_locally(self):
        self.bridge.responses["sleep"] = {"ok": True, "slept": True, "rev": 12}
        self.client.get_state()
        self.client.action("sleep", rev=11)
        result = self.client.action("sleep", rev=12)
        self.assertFalse(result["ok"])
        self.assertEqual(SLEEP_PENDING_ERROR, result["error"])
        self.assertEqual(1, len([c for c in self.bridge.calls
                                 if c[0] == "sleep"]),
                         "the second sleep must not reach the game")

    def test_sleep_without_day_advance_becomes_unresolved(self):
        self.bridge.responses["sleep"] = {"ok": True, "slept": True, "rev": 12}
        self.client.get_state()
        self.client.action("sleep", rev=11)
        for _ in range(self.client.sleep_observation_limit):
            self.client.get_state()
        unresolved = self.events(EVENT_SLEEP_UNRESOLVED)
        self.assertEqual(1, len(unresolved))

        blocked = self.client.action("sleep", rev=13)
        self.assertEqual(SLEEP_BLOCKED_ERROR, blocked["error"])
        self.assertEqual(1, len([c for c in self.bridge.calls
                                 if c[0] == "sleep"]),
                         "must not resend sleep after an unresolved outcome")


class SnapshotAndScopeTests(RecorderTestBase):
    def test_get_state_records_snapshot_and_external_change(self):
        self.client.get_state()
        self.bridge.state_value = state(day=2, cash=100, rev=14)
        self.client.get_state()
        external = self.events(EVENT_EXTERNAL)
        self.assertEqual(1, len(external),
                         "a state change with no preceding write is external")

    def test_change_after_a_successful_write_is_not_external(self):
        self.client.get_state()
        self.client.action("confirm_trade")
        self.bridge.state_value = state(day=2, cash=100, rev=14)
        self.client.get_state()

        self.assertEqual(0, len(self.events(EVENT_EXTERNAL)),
                         "our own successful write explains the change")

    def test_change_after_success_then_rejection_is_not_external(self):
        self.client.get_state()
        self.client.action("confirm_trade")
        self.bridge.responses["bargain"] = {"ok": False, "error": "stale_rev", "rev": 12}
        self.client.action("bargain")
        self.bridge.state_value = state(day=2, cash=100, rev=14)
        self.client.get_state()

        self.assertEqual(0, len(self.events(EVENT_EXTERNAL)),
                         "an earlier success still explains the change")

    def test_binding_attaches_identity(self):
        self.bridge.responses["get_recording_save_status"] = {
            "ok": True, "recording_mode": "bound", "bound": True,
            "configured_slot": 40,
            "binding": {"slot": 40, "run_id": "run-a", "run_number": 1},
        }
        self.client.action("get_recording_save_status")
        row = self.store.get_timeline(self.tl)
        self.assertEqual("run-a", row["run_id"])
        self.assertEqual(40, row["slot"])

    def test_new_run_forks_a_timeline(self):
        self._bind("run-a")
        self._bind("run-b")
        row = self.store.get_timeline(self.client.timeline_id)
        self.assertNotEqual(self.tl, self.client.timeline_id)
        self.assertEqual("external_replace", row["reason"])
        self.assertEqual(self.tl, row["parent_timeline_id"])

    def test_load_forks_a_rollback_timeline(self):
        self._bind("run-a")
        self.client.action("load_recording_game", rev=11)
        self._bind("run-a")
        row = self.store.get_timeline(self.client.timeline_id)
        self.assertEqual("rollback", row["reason"])

    def _bind(self, run_id):
        self.bridge.responses["get_recording_save_status"] = {
            "ok": True, "recording_mode": "bound", "bound": True,
            "configured_slot": 40,
            "binding": {"slot": 40, "run_id": run_id, "run_number": 1},
        }
        self.client.action("get_recording_save_status")


if __name__ == "__main__":
    unittest.main()
