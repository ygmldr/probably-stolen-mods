"""TDD tests for the trustworthy append-only JSONL call recorder (R5-C2).

The recorder wraps the MCP bridge client and persists one JSON object per line
*before* dispatch (``phase="intent"``) and *after* dispatch
(``phase="result"``). These tests pin the externally visible contract:

* append-only ordering + canonical byte-stable JSON lines + flush/fsync;
* secrets (bridge bearer token / auth headers) never reach the file;
* duplicate ``action_id`` handling is recorded and never extra-executed;
* crash recovery decisions (crash-after-intent, crash-after-dispatch,
  response-lost, duplicate id, corrupted line, session restart);
* ``get_operation("*")``, manual interventions and stop reasons are logged;
* ``export_trace`` rebuilds a validator-compatible trace from the recorded
  request/result bytes only (model text can never leak in), verifies recorded
  checksums, and fails closed on an incomplete recording.

Only the standard library is used; every test runs in a ``tempfile`` directory
with a fake bridge object. No network, no game.
"""

import copy
import datetime
import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if AGENT_DIR not in sys.path:
    sys.path.insert(0, AGENT_DIR)

import trace_recorder  # noqa: E402
import trace_validator  # noqa: E402

SECRET = "sk-live-SECRET-token-0123456789abcdef"

#: The field list required by docs/reviews/R5_HANDOFF_2026-11-19.md section 4 C2.
HANDOFF_FIELDS = (
    "run_id", "sequence", "wall_time", "monotonic_elapsed_ms", "actor",
    "model_id", "prompt_sha256", "skill_sha256", "host_version",
    "operation_id", "action_id", "op", "args", "observed_rev",
    "policy_sha256", "tool_surface", "recording_slot",
    "request_status", "response_status", "execution_status",
    "dispatch_status", "delivery_status", "evidence", "state_before_ref",
    "state_after_ref", "error", "human_intervention", "duration_ms",
)

PHASES = ("intent", "result", "recovery", "stop", "intervention")


# ---------------------------------------------------------------------------
# deterministic clocks / fake bridge / payload helpers
# ---------------------------------------------------------------------------

class FakeClock:
    def __init__(self, wall=1700000000.0, mono=100.0):
        self.wall = wall
        self.mono = mono

    def wall_time(self):
        return self.wall

    def monotonic(self):
        return self.mono

    def advance(self, seconds):
        self.wall += seconds
        self.mono += seconds


def state_payload(day=1, cash=500, rev=10, shop_state="OPEN"):
    return {
        "ok": True,
        "rev": rev,
        "in_game": True,
        "day": day,
        "cash": cash,
        "shop_state": shop_state,
        "client": None,
        "legal_actions": [],
        "candidate_actions": [],
        "rent": {
            "readable": True,
            "due": 100,
            "rent_value": 100,
            "days_left": 7,
            "due_days_left": 7,
            "can_pay": cash >= 100,
            "rent_paid_total": 0,
            "rent_weeks_paid": 0,
            "rent_weeks_missed": 0,
            "cash": cash,
            "day": day,
            "last_payment": None,
        },
    }


def write_result(op, action_id=None, operation_id="op-1",
                 execution_status="completed", delivery_status="delivered",
                 dispatched=True, **extra):
    """A bridge write response with the frozen two-axis shape."""
    result = {
        "ok": execution_status == "completed",
        "op": op,
        "operation_id": operation_id,
        "execution_status": execution_status,
        "delivery_status": delivery_status,
        "dispatched": dispatched,
    }
    if action_id is not None:
        result["action_id"] = action_id
    result.update(extra)
    return result


def trade_result(action_id="act-trade", execution_status="completed",
                 operation_id="op-trade"):
    """A real partial-trade completion (R5/A3 evidence contract)."""
    completed = execution_status == "completed"
    return {
        "ok": completed,
        "op": "confirm_trade",
        "action_id": action_id,
        "operation_id": operation_id,
        "execution_status": execution_status,
        "delivery_status": "delivered" if completed else "pending_delivery",
        "dispatched": True,
        "item_id": 101,
        "side": "sell",
        "client_unchanged": True,
        "items_transferred": completed,
        "cash_moved": completed,
        "business_complete": False,
        "visit_complete": False,
        "cash_before": 500,
        "cash_after": 560 if completed else 500,
        "rev": 10,
        "evidence": {
            "called": True,
            "error": None,
            "business_complete": False,
            "client_unchanged": True,
            "items_transferred": completed,
            "cash_moved": completed,
            "cash_before": 500,
            "cash_after": 560 if completed else 500,
            "staged_item_id_after": 101,
        },
    }


def end_day_result(action_id="act-end"):
    return write_result("end_day", action_id=action_id, operation_id="op-end",
                        shop_closed_after=True, rev=11)


class FakeBridge:
    """Duck-typed stand-in for ``BridgeClient``; records dispatch order."""

    def __init__(self):
        self.calls = []
        self.action_responses = {}
        self.state_values = []
        self.operation_responses = {}
        self.raise_on_action = {}
        self.token = SECRET
        self.port = 15528
        self.health_value = {
            "ok": True,
            "run_mode": "test",
            "game_version": "VERSION 100-Rev13",
        }

    @staticmethod
    def _take(value):
        if isinstance(value, list):
            if not value:
                raise AssertionError("no scripted response left for this op")
            return value.pop(0)
        return copy.deepcopy(value)

    def get_state(self):
        self.calls.append(("get_state",))
        if not self.state_values:
            return state_payload()
        return self._take(self.state_values)

    def action(self, op, **args):
        self.calls.append(("action", op, dict(args)))
        if op in self.raise_on_action:
            raise self.raise_on_action[op]
        if op in self.action_responses:
            return self._take(self.action_responses[op])
        return {"ok": True, "op": op, "action_id": args.get("action_id"),
                "rev": 10}

    def get_operation(self, operation_id):
        self.calls.append(("get_operation", operation_id))
        if operation_id in self.operation_responses:
            return copy.deepcopy(self.operation_responses[operation_id])
        return {"ok": False, "known": False, "error": "unknown_operation",
                "operation_id": operation_id}

    def wait_operation(self, operation_id, timeout_ms=2000):
        self.calls.append(("wait_operation", operation_id, timeout_ms))
        if operation_id in self.operation_responses:
            return copy.deepcopy(self.operation_responses[operation_id])
        return {"ok": False, "known": False, "error": "unknown_operation",
                "operation_id": operation_id}

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        self.calls.append(("wait_events", since_rev, timeout_ms))
        return {"ok": True, "changed": False, "rev": since_rev}

    def health(self):
        self.calls.append(("health",))
        return copy.deepcopy(self.health_value)


def read_lines(path):
    with open(path, "r", encoding="utf-8") as handle:
        return [line for line in handle.read().splitlines() if line]


def read_jsonl(path):
    return [json.loads(line) for line in read_lines(path)]


def write_jsonl(path, rows):
    with open(path, "a", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, sort_keys=True) + "\n")


def crash_intent(call_id="call-crash-1", op="confirm_trade",
                 action_id="act-crash-1", operation_id=None,
                 is_write=True, sequence=1, dispatch_status="pending",
                 args=None):
    return {
        "schema_version": 1,
        "event": "call",
        "phase": "intent",
        "call_id": call_id,
        "sequence": sequence,
        "op": op,
        "args": args if args is not None else {
            "rev": 10, "item_id": 101, "side": "sell"},
        "action_id": action_id,
        "operation_id": operation_id,
        "is_write": is_write,
        "dispatch_status": dispatch_status,
    }


def crash_result(call_id="call-crash-1", sequence=2, op="confirm_trade",
                 action_id="act-crash-1", operation_id=None,
                 execution_status=None, dispatch_status="dispatched",
                 response_status="received", is_write=True, result=None):
    return {
        "schema_version": 1,
        "event": "call",
        "phase": "result",
        "call_id": call_id,
        "sequence": sequence,
        "op": op,
        "args": {"rev": 10, "item_id": 101, "side": "sell"},
        "action_id": action_id,
        "operation_id": operation_id,
        "is_write": is_write,
        "dispatch_status": dispatch_status,
        "response_status": response_status,
        "execution_status": execution_status,
        "result": result,
    }


def errors_of(violations):
    return [row for row in violations if row.severity == "error"]


def codes_of(violations):
    return sorted({row.code for row in violations})


class RecorderTestBase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.path = os.path.join(self._tmp.name, "call-recording.jsonl")
        self.bridge = FakeBridge()
        self.clock = FakeClock()
        self.recorders = []

    def make(self, bridge=None, path=None, **kwargs):
        recorder = trace_recorder.TraceRecorder(
            bridge if bridge is not None else self.bridge,
            path if path is not None else self.path,
            wall_clock=self.clock.wall_time,
            monotonic_clock=self.clock.monotonic,
            **kwargs
        )
        self.recorders.append(recorder)
        self.addCleanup(recorder.close)
        return recorder

    def events(self):
        return read_jsonl(self.path)


# ---------------------------------------------------------------------------
# append ordering / schema / durability
# ---------------------------------------------------------------------------

class AppendOrderingTests(RecorderTestBase):
    def test_one_json_object_per_line_with_ordered_sequence_and_phases(self):
        recorder = self.make(run_id="run-1")
        recorder.get_state()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        recorder.get_state()

        rows = self.events()
        self.assertEqual(6, len(rows), "two events per recorded call")
        self.assertEqual(list(range(1, 7)), [row["sequence"] for row in rows])
        self.assertEqual(
            ["intent", "result", "intent", "result", "intent", "result"],
            [row["phase"] for row in rows])
        # intent/result pairs share a call_id and keep file order.
        call_ids = [row["call_id"] for row in rows]
        self.assertEqual(3, len({cid for cid in call_ids}))
        self.assertTrue(all(isinstance(cid, str) and cid for cid in call_ids))
        self.assertEqual(3, sum(1 for row in rows if row["phase"] == "intent"))
        # one physical line per event, no embedded newlines / trailing junk
        self.assertEqual(6, len(read_lines(self.path)))
        for row in rows:
            self.assertEqual(trace_recorder.TRACE_RECORDER_SCHEMA_VERSION,
                             row["schema_version"])
            self.assertIn(row["phase"], PHASES)

    def test_handoff_fields_are_present_on_every_event(self):
        recorder = self.make(run_id="run-1", actor="mcp",
                             model_id="test-model",
                             prompt_sha256="p" * 64,
                             skill_sha256="s" * 64,
                             host_version="dsh-test",
                             policy_sha256="q" * 64,
                             tool_surface="recording",
                             recording_slot=53)
        recorder.get_state()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")

        for row in self.events():
            missing = [field for field in HANDOFF_FIELDS if field not in row]
            self.assertEqual([], missing,
                             "event %r misses handoff fields" % (row["phase"],))
            self.assertIn("call_id", row)
            self.assertIn("result", row)
            self.assertEqual("run-1", row["run_id"])
            self.assertEqual("mcp", row["actor"])
            self.assertEqual("test-model", row["model_id"])
            self.assertEqual("dsh-test", row["host_version"])
            self.assertEqual("recording", row["tool_surface"])
            self.assertEqual(53, row["recording_slot"])
            self.assertEqual("q" * 64, row["policy_sha256"])

    def test_wall_time_is_rfc3339_utc_and_elapsed_is_monotonic(self):
        recorder = self.make()
        recorder.get_state()
        self.clock.advance(1.5)
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")

        rows = self.events()
        wall_times = []
        for row in rows:
            parsed = datetime.datetime.fromisoformat(
                row["wall_time"].replace("Z", "+00:00"))
            self.assertIsNotNone(parsed.tzinfo)
            wall_times.append(parsed)
        self.assertEqual(sorted(wall_times), wall_times,
                         "wall_time must not move backwards")
        elapsed = [row["monotonic_elapsed_ms"] for row in rows]
        self.assertEqual(sorted(elapsed), elapsed,
                         "monotonic_elapsed_ms must not move backwards")
        self.assertGreaterEqual(elapsed[-1], 1500)
        durations = [row["duration_ms"] for row in rows
                     if row["phase"] == "result"]
        self.assertEqual(2, len(durations))
        for duration in durations:
            self.assertIsInstance(duration, int)
            self.assertGreaterEqual(duration, 0)

    def test_json_lines_are_canonical_sorted_and_byte_stable(self):
        recorder = self.make(run_id="run-1")
        recorder.get_state()
        for line in read_lines(self.path):
            self.assertEqual(
                json.dumps(json.loads(line), sort_keys=True,
                           separators=(",", ":"), ensure_ascii=False),
                line)

    def test_file_is_append_only(self):
        recorder = self.make()
        recorder.get_state()
        with open(self.path, "rb") as handle:
            first = handle.read()
        self.assertTrue(first)
        recorder.get_state()
        with open(self.path, "rb") as handle:
            second = handle.read()
        self.assertTrue(second.startswith(first),
                        "later events must never rewrite earlier bytes")

    def test_each_event_is_flushed_and_fsynced_and_readable_before_close(self):
        recorder = self.make()
        with mock.patch.object(trace_recorder.os, "fsync") as fsync_mock:
            recorder.get_state()
            self.bridge.action_responses["confirm_trade"] = [trade_result()]
            recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        # four events => four durability calls, each on a real descriptor
        self.assertEqual(4, fsync_mock.call_count)
        for call in fsync_mock.call_args_list:
            self.assertIsInstance(call.args[0], int)
        # the file is complete on disk while the recorder is still open
        self.assertEqual(4, len(self.events()))

    def test_abrupt_close_leaves_complete_parseable_lines(self):
        recorder = self.make()
        recorder.get_state()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        # no close(): simulate the process disappearing after the last event
        raw = open(self.path, "rb").read()
        self.assertTrue(raw.endswith(b"\n"))
        self.assertEqual(4, len(read_jsonl(self.path)))
        # a restart over the same file must not be corrupted by the abrupt end
        restarted = self.make(bridge=FakeBridge(), run_id="run-2")
        self.assertEqual("clean", restarted.recovery.status)


# ---------------------------------------------------------------------------
# secrets
# ---------------------------------------------------------------------------

class SecretRedactionTests(RecorderTestBase):
    def test_never_writes_bridge_token_or_auth_headers(self):
        recorder = self.make()
        self.bridge.action_responses["confirm_trade"] = [
            write_result("confirm_trade", action_id="act-1",
                         detail="auth failed for " + SECRET)]
        recorder.action(
            "confirm_trade", rev=10, action_id="act-1",
            token=SECRET,
            Authorization="Bearer " + SECRET,
            nested={"api_key": SECRET, "note": "prefix " + SECRET},
            password=SECRET,
        )
        raw = open(self.path, "r", encoding="utf-8").read()
        self.assertNotIn(SECRET, raw)
        self.assertNotIn("Bearer", raw)
        self.assertGreaterEqual(raw.count(trace_recorder.REDACTED), 4)
        self.assertIn("auth failed for", raw)

    def test_secret_like_keys_are_redacted_case_insensitively(self):
        recorder = self.make()
        recorder.action("list_goods", Token=SECRET, PASSWORD=SECRET,
                        secret=SECRET)
        raw = open(self.path, "r", encoding="utf-8").read()
        self.assertNotIn(SECRET, raw)

    def test_transport_exception_message_token_is_redacted(self):
        recorder = self.make()
        self.bridge.raise_on_action["confirm_trade"] = RuntimeError(
            "bridge auth failed token=" + SECRET)
        with self.assertRaises(RuntimeError):
            recorder.action("confirm_trade", rev=10, action_id="act-1")
        raw = open(self.path, "r", encoding="utf-8").read()
        self.assertNotIn(SECRET, raw)
        result_rows = [row for row in self.events()
                       if row["phase"] == "result"]
        self.assertEqual("transport_error",
                         result_rows[-1]["response_status"])
        self.assertEqual("unknown", result_rows[-1]["dispatch_status"])
        self.assertIsNotNone(result_rows[-1]["error"])

    def test_secret_guard_rejects_a_leaking_line(self):
        leak = json.dumps({"note": "oops " + SECRET})
        with self.assertRaises(trace_recorder.TraceRecorderError):
            trace_recorder.assert_no_known_secrets(leak, [SECRET])
        # benign text passes
        trace_recorder.assert_no_known_secrets(
            json.dumps({"note": "ok"}), [SECRET])

    def test_recorder_does_not_mutate_the_returned_result(self):
        recorder = self.make()
        payload = write_result("confirm_trade", action_id="act-1",
                               token=SECRET)
        original = copy.deepcopy(payload)
        self.bridge.action_responses["confirm_trade"] = [payload]
        returned = recorder.action("confirm_trade", rev=10,
                                   action_id="act-1")
        self.assertEqual(original, returned)
        raw = open(self.path, "r", encoding="utf-8").read()
        self.assertNotIn(SECRET, raw)


# ---------------------------------------------------------------------------
# duplicate action ids / injected idempotency keys
# ---------------------------------------------------------------------------

class ActionIdTests(RecorderTestBase):
    def test_write_gets_an_action_id_injected_before_dispatch(self):
        recorder = self.make()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")

        dispatch = [call for call in self.bridge.calls
                    if call[0] == "action"][0]
        action_id = dispatch[2].get("action_id")
        self.assertTrue(isinstance(action_id, str) and action_id)
        events = self.events()
        intent = [row for row in events if row["phase"] == "intent"][0]
        self.assertEqual(action_id, intent["action_id"])
        self.assertEqual(action_id, intent["args"]["action_id"])
        result = [row for row in events if row["phase"] == "result"][0]
        self.assertEqual(action_id, result["action_id"])

    def test_duplicate_action_id_is_recorded_and_not_extra_dispatched(self):
        recorder = self.make()
        self.bridge.action_responses["confirm_trade"] = [
            trade_result(action_id="act-dup"),
            trade_result(action_id="act-dup"),
        ]
        first = recorder.action("confirm_trade", rev=10, action_id="act-dup",
                                item_id=101, side="sell")
        second = recorder.action("confirm_trade", rev=10, action_id="act-dup",
                                 item_id=101, side="sell")
        self.assertTrue(first["ok"])
        self.assertTrue(second["ok"])

        dispatches = [call for call in self.bridge.calls
                      if call[0] == "action"]
        self.assertEqual(2, len(dispatches),
                         "the recorder must not swallow or duplicate dispatch")
        intents = [row for row in self.events() if row["phase"] == "intent"]
        self.assertEqual(2, len(intents))
        self.assertFalse(intents[0].get("duplicate_action_id"))
        self.assertTrue(intents[1]["duplicate_action_id"])
        self.assertEqual(intents[0]["call_id"],
                         intents[1]["duplicate_of_call_id"])
        self.assertEqual("clean", recorder.recovery.status)

    def test_new_action_id_after_uncertain_is_refused_until_same_id_replay(self):
        recorder = self.make()
        uncertain = write_result(
            "confirm_trade", action_id="act-uncertain",
            execution_status="uncertain", delivery_status="pending_delivery",
            dispatched=True, error="outcome_unknown",
            operation_id="op-uncertain")
        self.bridge.action_responses["confirm_trade"] = [
            uncertain, trade_result(action_id="act-uncertain")]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        action_id = [row for row in self.events()
                     if row["phase"] == "intent"][0]["action_id"]
        self.assertEqual("needs_operation_query", recorder.recovery.status)

        refused = recorder.action("confirm_trade", rev=10, item_id=101,
                                  side="sell")
        self.assertFalse(refused["ok"])
        self.assertEqual("recovery_pending", refused["error"])
        self.assertEqual(1, len([call for call in self.bridge.calls
                                 if call[0] == "action"]))

        replay = recorder.action("confirm_trade", rev=10, item_id=101,
                                 side="sell", action_id=action_id)
        self.assertTrue(replay["ok"])
        self.assertEqual("clean", recorder.recovery.status)
        dispatches = [call for call in self.bridge.calls
                      if call[0] == "action"]
        self.assertEqual(2, len(dispatches))
        updates = [row for row in self.events()
                   if row["phase"] == "recovery"
                   and row.get("recovery", {}).get("decision") == "resolved"]
        self.assertEqual("same_action_id",
                         updates[-1]["recovery"]["resolution"])


# ---------------------------------------------------------------------------
# crash recovery decisions (startup scan)
# ---------------------------------------------------------------------------

class CrashRecoveryTests(RecorderTestBase):
    def test_crash_after_intent_requires_operation_query_and_no_auto_replay(self):
        write_jsonl(self.path, [crash_intent(action_id="act-1")])
        recorder = self.make()
        # never re-dispatch on its own
        self.assertEqual([], self.bridge.calls)
        self.assertEqual("needs_operation_query", recorder.recovery.status)
        self.assertFalse(recorder.recovery.stop_required)
        entries = recorder.recovery.pending_entries
        self.assertEqual(1, len(entries))
        self.assertEqual("intent_without_result", entries[0].reason)
        self.assertFalse(entries[0].can_query_operation)
        self.assertTrue(entries[0].can_replay_same_action_id)
        scans = [row for row in self.events()
                 if row["phase"] == "recovery"
                 and row.get("recovery", {}).get("decision")
                 == "needs_operation_query"]
        self.assertEqual(1, len(scans))

        refused = recorder.action("confirm_trade", rev=10, item_id=101,
                                  side="sell", action_id="act-new")
        self.assertEqual("recovery_pending", refused["error"])
        self.assertEqual([], self.bridge.calls)

        self.bridge.action_responses["confirm_trade"] = [
            trade_result(action_id="act-1")]
        resolved = recorder.action("confirm_trade", rev=10, item_id=101,
                                   side="sell", action_id="act-1")
        self.assertTrue(resolved["ok"])
        self.assertEqual("clean", recorder.recovery.status)
        self.assertEqual(1, len([call for call in self.bridge.calls
                                 if call[0] == "action"]))

    def test_crash_after_dispatch_requires_operation_query(self):
        write_jsonl(self.path, [
            crash_intent(call_id="call-disp", action_id="act-disp"),
            crash_result(call_id="call-disp", sequence=2,
                         action_id="act-disp", dispatch_status="dispatched",
                         response_status="missing", execution_status=None,
                         result=None),
        ])
        recorder = self.make()
        self.assertEqual([], self.bridge.calls)
        self.assertEqual("needs_operation_query", recorder.recovery.status)
        entries = recorder.recovery.pending_entries
        self.assertEqual(1, len(entries))
        self.assertEqual("response_lost", entries[0].reason)
        self.assertEqual("dispatched", entries[0].dispatch_status)
        self.assertTrue(entries[0].can_replay_same_action_id)

        self.bridge.action_responses["confirm_trade"] = [
            trade_result(action_id="act-disp", operation_id="op-disp")]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell",
                        action_id="act-disp")
        self.assertEqual("clean", recorder.recovery.status)

    def test_response_lost_in_live_session_can_be_resolved_by_same_id(self):
        recorder = self.make()
        self.bridge.raise_on_action["confirm_trade"] = RuntimeError(
            "connection reset after send")
        with self.assertRaises(RuntimeError):
            recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        self.assertEqual("needs_operation_query", recorder.recovery.status)
        entries = recorder.recovery.pending_entries
        self.assertEqual(1, len(entries))
        self.assertEqual("response_lost", entries[0].reason)
        self.assertEqual("unknown", entries[0].dispatch_status)

        refused = recorder.action("confirm_trade", rev=10, item_id=101,
                                  side="sell")
        self.assertEqual("recovery_pending", refused["error"])

        action_id = entries[0].action_id
        self.assertTrue(action_id)
        self.bridge.raise_on_action.pop("confirm_trade")
        self.bridge.action_responses["confirm_trade"] = [
            trade_result(action_id=action_id)]
        recovered = recorder.action("confirm_trade", rev=10, item_id=101,
                                    side="sell", action_id=action_id)
        self.assertTrue(recovered["ok"])
        self.assertEqual("clean", recorder.recovery.status)

    def test_pending_operation_is_resolved_by_real_get_operation(self):
        recorder = self.make()
        running = write_result("confirm_trade", action_id="act-run",
                               operation_id="op-run",
                               execution_status="running",
                               delivery_status="pending_delivery",
                               dispatched=True)
        self.bridge.action_responses["confirm_trade"] = [running]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell",
                        action_id="act-run")
        self.assertEqual("needs_operation_query", recorder.recovery.status)

        self.bridge.operation_responses["op-run"] = {
            "ok": True, "operation_id": "op-run",
            "execution_status": "completed",
            "delivery_status": "delivered", "dispatched": True}
        queried = recorder.get_operation("op-run")
        self.assertEqual("completed", queried["execution_status"])
        self.assertEqual("clean", recorder.recovery.status)
        updates = [row for row in self.events()
                   if row["phase"] == "recovery"
                   and row.get("recovery", {}).get("decision") == "resolved"]
        self.assertEqual("operation_query",
                         updates[-1]["recovery"]["resolution"])
        logged = [row for row in self.events()
                  if row.get("op") == "get_operation"]
        self.assertTrue(logged)
        self.assertEqual("op-run", logged[-1]["args"]["operation_id"])

    def test_get_operation_unknown_marks_unknown_and_stops(self):
        recorder = self.make()
        running = write_result("confirm_trade", action_id="act-gone",
                               operation_id="op-gone",
                               execution_status="running",
                               delivery_status="pending_delivery",
                               dispatched=True)
        self.bridge.action_responses["confirm_trade"] = [running]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell",
                        action_id="act-gone")
        recorder.get_operation("op-gone")

        self.assertEqual("unknown", recorder.recovery.status)
        self.assertTrue(recorder.recovery.stop_required)
        stops = [row for row in self.events() if row["phase"] == "stop"]
        self.assertIn("operation_not_queryable",
                      [row.get("stop_reason") for row in stops])

        refused = recorder.action("confirm_trade", rev=10, item_id=101,
                                  side="sell", action_id="act-2")
        self.assertEqual("recovery_unknown", refused["error"])
        # reads stay usable so a human can diagnose and stop safely
        before = len(self.bridge.calls)
        recorder.get_state()
        self.assertEqual(before + 1, len(self.bridge.calls))

    def test_orphan_intent_without_ids_is_unknown_and_stops(self):
        write_jsonl(self.path, [
            crash_intent(action_id=None, operation_id=None)])
        recorder = self.make()
        self.assertEqual([], self.bridge.calls)
        self.assertEqual("unknown", recorder.recovery.status)
        self.assertTrue(recorder.recovery.stop_required)
        refused = recorder.action("confirm_trade", rev=10, item_id=101,
                                  side="sell")
        self.assertEqual("recovery_unknown", refused["error"])
        self.assertEqual([], self.bridge.calls)

    def test_get_operation_wildcard_is_logged_but_never_resolves(self):
        recorder = self.make()
        running = write_result("confirm_trade", action_id="act-list",
                               operation_id="op-list",
                               execution_status="running",
                               delivery_status="pending_delivery",
                               dispatched=True)
        self.bridge.action_responses["confirm_trade"] = [running]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell",
                        action_id="act-list")
        # A '*' listing may reveal the operation, but it is not a real query
        # of that operation id, so it must not resolve the recovery entry.
        self.bridge.operation_responses["*"] = {
            "ok": True,
            "operations": [{"operation_id": "op-list",
                            "execution_status": "completed"}]}
        recorder.get_operation("*")
        self.assertEqual("needs_operation_query", recorder.recovery.status)
        wildcards = [row for row in self.events()
                     if row["phase"] == "intent"
                     and row.get("op") == "get_operation"
                     and row.get("args", {}).get("operation_id") == "*"]
        self.assertEqual(1, len(wildcards))

    def test_terminal_rejection_is_not_a_recovery_case(self):
        rejected = write_result("confirm_trade", action_id="act-rej",
                                execution_status="rejected",
                                delivery_status="pending_delivery",
                                dispatched=False, error="stale_rev",
                                ok=False)
        write_jsonl(self.path, [
            crash_intent(action_id="act-rej"),
            crash_result(sequence=2, action_id="act-rej",
                         execution_status="rejected", result=rejected),
        ])
        recorder = self.make()
        self.assertEqual("clean", recorder.recovery.status)
        self.assertEqual([], recorder.recovery.pending_entries)
        # writes are allowed again after a terminal rejection
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        result = recorder.action("confirm_trade", rev=10, item_id=101,
                                 side="sell")
        self.assertTrue(result["ok"])

    def test_session_restart_over_completed_log_is_clean_and_continues(self):
        first = self.make(run_id="run-1")
        first.get_state()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        first.action("confirm_trade", rev=10, item_id=101, side="sell")
        first.close()
        prior = self.events()
        max_sequence = max(row["sequence"] for row in prior)

        second_bridge = FakeBridge()
        second = self.make(bridge=second_bridge, run_id="run-2")
        self.assertEqual("clean", second.recovery.status)
        self.assertEqual([], second.recovery.pending_entries)
        scans = [row for row in self.events()
                 if row["phase"] == "recovery" and row["run_id"] == "run-2"]
        self.assertTrue(scans)
        self.assertEqual("clean", scans[0]["recovery"]["decision"])
        self.assertGreater(scans[0]["sequence"], max_sequence,
                           "sequence continues across sessions")

        second_bridge.action_responses["confirm_trade"] = [trade_result()]
        allowed = second.action("confirm_trade", rev=10, item_id=101,
                                side="sell")
        self.assertTrue(allowed["ok"])
        self.assertEqual(1, len([call for call in second_bridge.calls
                                 if call[0] == "action"]))

    def test_get_state_is_usable_while_recovery_pending(self):
        write_jsonl(self.path, [crash_intent()])
        recorder = self.make()
        self.assertEqual("needs_operation_query", recorder.recovery.status)
        recorder.get_state()
        self.assertEqual(1, len([call for call in self.bridge.calls
                                 if call[0] == "get_state"]))


# ---------------------------------------------------------------------------
# corrupted / truncated lines
# ---------------------------------------------------------------------------

class CorruptedLineTests(RecorderTestBase):
    def test_truncated_last_line_is_skipped_with_a_recorded_warning(self):
        recorder = self.make()
        recorder.get_state()          # one complete call = two valid lines
        recorder.close()
        with open(self.path, "ab") as handle:
            handle.write(b'{"schema_version":1,"phase":"intent","call_id":"trc')
        events, warnings = trace_recorder.load_events(self.path)
        self.assertEqual(2, len(events))
        self.assertEqual(1, len(warnings))
        self.assertEqual("truncated", warnings[0]["reason"])

        restarted = self.make(run_id="run-2")
        # the truncated fragment must never crash the replay
        self.assertEqual("clean", restarted.recovery.status)
        scanned, scan_warnings = trace_recorder.load_events(self.path)
        self.assertTrue(scan_warnings)
        scans = [row for row in scanned
                 if row["phase"] == "recovery" and row["run_id"] == "run-2"]
        self.assertTrue(scans)
        self.assertEqual(warnings[0]["reason"],
                         scans[0]["recovery"]["warnings"][0]["reason"])
        # the good prior events are still readable after the repair separator
        self.assertTrue(len(trace_recorder.load_events(self.path)[0]) >= 3)

    def test_corrupted_middle_line_is_skipped_and_export_still_works(self):
        recorder = self.make()
        recorder.get_state()
        self.bridge.action_responses["confirm_trade"] = [trade_result()]
        recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
        recorder.get_state()
        recorder.close()

        lines = read_lines(self.path)
        lines.insert(1, "{not valid json")
        with open(self.path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")

        events, warnings = trace_recorder.load_events(self.path)
        self.assertEqual(6, len(events))
        self.assertEqual(1, len(warnings))
        self.assertEqual("invalid_json", warnings[0]["reason"])
        trace = trace_recorder.export_trace(self.path)
        self.assertEqual(3, len(trace["steps"]))
        self.assertEqual(1, len(trace["recorder"]["warnings"]))
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))


# ---------------------------------------------------------------------------
# manual intervention / stop reasons / read-only calls
# ---------------------------------------------------------------------------

class EventJournalTests(RecorderTestBase):
    def test_manual_intervention_is_recorded_as_an_event(self):
        recorder = self.make()
        recorder.record_intervention("manual_pause", "user asked to stop",
                                     actor="human")
        rows = self.events()
        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("intervention", row["phase"])
        self.assertTrue(row["human_intervention"])
        self.assertEqual("human", row["actor"])
        self.assertEqual("manual_pause", row["intervention_kind"])
        self.assertEqual("user asked to stop", row["intervention_detail"])

    def test_stop_reason_is_recorded_as_an_event(self):
        recorder = self.make()
        recorder.record_stop("budget_exhausted", detail="48 actions")
        rows = self.events()
        self.assertEqual("stop", rows[0]["phase"])
        self.assertEqual("budget_exhausted", rows[0]["stop_reason"])
        trace = trace_recorder.export_trace(self.path)
        self.assertIn("budget_exhausted",
                      [row["reason"] for row in
                       trace["recorder"]["stop_reasons"]])

    def test_read_only_action_is_not_treated_as_a_write(self):
        recorder = self.make()
        recorder.action("list_goods", scope="all")
        intent = self.events()[0]
        self.assertFalse(intent["is_write"])
        dispatch = [call for call in self.bridge.calls
                    if call[0] == "action"][0]
        self.assertNotIn("action_id", dispatch[2])
        self.assertEqual({"scope": "all"}, intent["args"])

    def test_health_probe_is_passed_through_without_recording(self):
        recorder = self.make()
        health = recorder.health()
        self.assertTrue(health["ok"])
        self.assertEqual([], self.events())
        self.assertEqual(SECRET, recorder.token)
        self.assertEqual(15528, recorder.port)

    def test_closed_recorder_refuses_further_calls(self):
        recorder = self.make()
        recorder.close()
        with self.assertRaises(trace_recorder.TraceRecorderError):
            recorder.get_state()
        with self.assertRaises(trace_recorder.TraceRecorderError):
            recorder.action("confirm_trade", rev=10)


# ---------------------------------------------------------------------------
# export contract
# ---------------------------------------------------------------------------

def record_clean_run(test, path):
    """Record a small, fully-successful run and return the recorder."""
    bridge = FakeBridge()
    bridge.state_values = [
        state_payload(day=1, cash=500, rev=10),
        state_payload(day=1, cash=560, rev=11),
        state_payload(day=1, cash=560, rev=11, shop_state="CLOSED"),
    ]
    bridge.action_responses["confirm_trade"] = [trade_result()]
    bridge.action_responses["end_day"] = [end_day_result()]
    recorder = test.make(bridge=bridge, path=path, run_id="run-clean",
                         actor="mcp", model_id="test-model",
                         policy_sha256="q" * 64, tool_surface="recording")
    recorder.get_state()
    recorder.action("confirm_trade", rev=10, item_id=101, side="sell")
    recorder.get_state()
    recorder.action("end_day", rev=11)
    recorder.get_state()
    return recorder


class ExportTests(RecorderTestBase):
    def test_clean_run_export_is_rebuilt_from_recorded_bytes_and_validates(self):
        recorder = record_clean_run(self, self.path)
        recorder.close()
        events = read_jsonl(self.path)
        intents = [row for row in events if row["phase"] == "intent"]
        results = {row["call_id"]: row for row in events
                   if row["phase"] == "result"}

        trace = trace_recorder.export_trace(self.path)
        self.assertEqual(
            getattr(trace_validator, "TRACE_SCHEMA_VERSION", 1),
            trace["schema_version"])
        self.assertIsInstance(trace["steps"], list)
        self.assertEqual(
            ["get_state", "confirm_trade", "get_state", "end_day",
             "get_state"],
            [step["tool"] for step in trace["steps"]])
        # every exported step is exactly the recorded request/result bytes
        for step, intent in zip(trace["steps"], intents):
            self.assertEqual(intent["op"], step["tool"])
            self.assertEqual(intent["args"], step["args"])
            self.assertEqual(results[intent["call_id"]]["result"],
                             step["result"])
            self.assertNotIn("claims", step)
            self.assertNotIn("claim_text", step)

        violations = trace_validator.validate_payload(trace)
        self.assertEqual([], errors_of(violations), codes_of(violations))
        # the trace is plain JSON (files/benchmark reports can embed it)
        json.loads(json.dumps(trace, sort_keys=True))

    def test_export_does_not_leak_model_text_or_claims(self):
        rejected = write_result(
            "confirm_trade", action_id="act-rej",
            execution_status="rejected", delivery_status="pending_delivery",
            dispatched=False, error="stale_rev")
        write_jsonl(self.path, [
            {"schema_version": 1, "event": "call", "phase": "intent",
             "call_id": "call-1", "sequence": 1, "op": "get_state",
             "args": {}, "is_write": False, "dispatch_status": "pending",
             "action_id": None, "operation_id": None},
            {"schema_version": 1, "event": "call", "phase": "result",
             "call_id": "call-1", "sequence": 2, "op": "get_state",
             "args": {}, "is_write": False, "dispatch_status": "dispatched",
             "response_status": "received", "execution_status": None,
             "result": state_payload()},
            # a model/host annotation must never become recorded evidence
            {"schema_version": 1, "event": "call", "phase": "intent",
             "call_id": "call-2", "sequence": 3, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-rej", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "pending",
             "action_id": "act-rej", "operation_id": None},
            {"schema_version": 1, "event": "call", "phase": "result",
             "call_id": "call-2", "sequence": 4, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-rej", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "dispatched",
             "response_status": "received",
             "execution_status": "rejected", "result": rejected,
             "claim_text": "trade completed successfully",
             "claims": ["trade_completed"],
             "status_source": "model_text"},
        ])
        trace = trace_recorder.export_trace(self.path)
        step = [row for row in trace["steps"]
                if row["tool"] == "confirm_trade"][0]
        self.assertFalse(step["result"]["ok"])
        self.assertEqual("rejected", step["result"]["execution_status"])
        self.assertNotIn("claims", step)
        self.assertNotIn("claim_text", step)
        self.assertNotIn("trade completed successfully",
                         json.dumps(trace, sort_keys=True))
        # the rejected step alone is a factual, non-successful step
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))

    def test_forged_success_claim_on_rejected_export_is_still_rejected(self):
        rejected = write_result(
            "confirm_trade", action_id="act-rej",
            execution_status="rejected", delivery_status="pending_delivery",
            dispatched=False, error="stale_rev")
        write_jsonl(self.path, [
            {"schema_version": 1, "event": "call", "phase": "intent",
             "call_id": "call-1", "sequence": 1, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-rej", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "pending",
             "action_id": "act-rej", "operation_id": None},
            {"schema_version": 1, "event": "call", "phase": "result",
             "call_id": "call-1", "sequence": 2, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-rej", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "dispatched",
             "response_status": "received",
             "execution_status": "rejected", "result": rejected},
        ])
        trace = trace_recorder.export_trace(self.path)
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))

        forged = copy.deepcopy(trace)
        forged["steps"][0]["claims"] = ["trade_completed"]
        forged["steps"][0]["claim_text"] = "trade completed successfully"
        violations = trace_validator.validate_payload(forged)
        self.assertTrue(errors_of(violations))
        self.assertIn("REJECTED_CLAIMED_AS_SUCCESS", codes_of(violations))

    def test_uncertain_step_claimed_as_success_is_rejected(self):
        uncertain = write_result(
            "confirm_trade", action_id="act-unc",
            execution_status="uncertain", delivery_status="pending_delivery",
            dispatched=True, error="outcome_unknown", operation_id="op-unc")
        write_jsonl(self.path, [
            {"schema_version": 1, "event": "call", "phase": "intent",
             "call_id": "call-1", "sequence": 1, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-unc", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "pending",
             "action_id": "act-unc", "operation_id": None},
            {"schema_version": 1, "event": "call", "phase": "result",
             "call_id": "call-1", "sequence": 2, "op": "confirm_trade",
             "args": {"rev": 10, "action_id": "act-unc", "item_id": 101,
                      "side": "sell"},
             "is_write": True, "dispatch_status": "dispatched",
             "response_status": "received",
             "execution_status": "uncertain", "result": uncertain},
        ])
        trace = trace_recorder.export_trace(self.path)
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))

        forged = copy.deepcopy(trace)
        forged["steps"][0]["claims"] = ["trade_completed"]
        violations = trace_validator.validate_payload(forged)
        self.assertTrue(errors_of(violations))
        self.assertIn("TRADE_EVIDENCE_MISSING", codes_of(violations))

    def test_unresolved_intent_fails_export_closed(self):
        write_jsonl(self.path, [crash_intent()])
        with self.assertRaises(trace_recorder.TraceExportError):
            trace_recorder.export_trace(self.path)

    def test_tampered_recorded_result_is_rejected_at_export(self):
        recorder = record_clean_run(self, self.path)
        recorder.close()
        lines = read_lines(self.path)
        target = None
        for index, line in enumerate(lines):
            row = json.loads(line)
            if (row.get("phase") == "result"
                    and row.get("op") == "confirm_trade"):
                row["result"]["cash_after"] = 99999
                lines[index] = json.dumps(row, sort_keys=True)
                target = index
                break
        self.assertIsNotNone(target)
        with open(self.path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")
        with self.assertRaises(trace_recorder.TraceExportError):
            trace_recorder.export_trace(self.path)

    def test_missing_recording_fails_export_closed(self):
        missing = os.path.join(self._tmp.name, "does-not-exist.jsonl")
        with self.assertRaises(trace_recorder.TraceExportError):
            trace_recorder.export_trace(missing)

    def test_script_form_cli_entrypoint_exports(self):
        recorder = record_clean_run(self, self.path)
        recorder.close()
        output = os.path.join(self._tmp.name, "script-trace.json")
        script = os.path.join(AGENT_DIR, "trace_recorder.py")
        completed = subprocess.run(
            [sys.executable, script, "export", self.path,
             "--output", output],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.assertEqual(0, completed.returncode, completed.stderr)
        with open(output, "r", encoding="utf-8") as handle:
            trace = json.load(handle)
        self.assertEqual(
            getattr(trace_validator, "TRACE_SCHEMA_VERSION", 1),
            trace["schema_version"])
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))

    def test_cli_export_writes_a_validator_clean_trace(self):
        recorder = record_clean_run(self, self.path)
        recorder.close()
        output = os.path.join(self._tmp.name, "run-trace.json")
        code = trace_recorder.main(["export", self.path, "--output", output])
        self.assertEqual(0, code)
        with open(output, "r", encoding="utf-8") as handle:
            trace = json.load(handle)
        self.assertEqual(
            getattr(trace_validator, "TRACE_SCHEMA_VERSION", 1),
            trace["schema_version"])
        self.assertEqual(5, len(trace["steps"]))
        self.assertEqual([], errors_of(trace_validator.validate_payload(trace)))

        bad = trace_recorder.main(["export", os.path.join(self._tmp.name,
                                                          "missing.jsonl")])
        self.assertEqual(2, bad)


if __name__ == "__main__":
    unittest.main()
