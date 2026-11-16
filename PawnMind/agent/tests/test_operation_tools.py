"""D2 module (Python half): bounded operation polling.

``wait_operation`` must never turn a timeout into a fake success: a still-running
operation returns ``ok: false`` with the operation id and an explicit next step.
The polling loop is pure enough to test with a fake clock, so no HTTP server and
no game process is needed.
"""

import os
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

from pawnmind_client import (  # noqa: E402
    TERMINAL_EXECUTION_STATUSES,
    poll_operation,
)
import mcp_server  # noqa: E402


class FakeClock:
    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.now += seconds


def _running(operation_id="op-1"):
    return {
        "ok": False,
        "known": True,
        "operation_id": operation_id,
        "op": "end_day",
        "execution_status": "running",
        "delivery_status": "delivered",
    }


def _completed(operation_id="op-1"):
    return {
        "ok": True,
        "known": True,
        "operation_id": operation_id,
        "op": "end_day",
        "execution_status": "completed",
        "delivery_status": "delivered",
    }


class PollOperationTests(unittest.TestCase):
    def test_terminal_statuses_are_the_three_cannot_continue_states(self):
        self.assertEqual(
            {"completed", "rejected", "failed"},
            set(TERMINAL_EXECUTION_STATUSES))

    def test_pending_then_completed_returns_the_completion(self):
        clock = FakeClock()
        responses = [_running(), _running(), _completed()]

        def fetch(operation_id):
            return responses.pop(0)

        result = poll_operation(fetch, "op-1", 1000, clock=clock,
                                sleep=clock.sleep)
        self.assertTrue(result["ok"])
        self.assertEqual("completed", result["execution_status"])
        self.assertEqual(0, len(responses))
        self.assertEqual(2, len(clock.sleeps), "bounded polling with sleeps")

    def test_timeout_returns_pending_not_success(self):
        clock = FakeClock()
        result = poll_operation(lambda operation_id: _running(), "op-1", 250,
                                clock=clock, sleep=clock.sleep)
        self.assertFalse(result["ok"], "a timeout is never a success")
        self.assertTrue(result["timed_out"])
        self.assertEqual("op-1", result["operation_id"])
        self.assertEqual("running", result["execution_status"])
        self.assertEqual("pending", result["status"])
        self.assertTrue(result["next"], "the caller is told what to do next")
        self.assertLessEqual(clock.now, 250 + 100)  # bounded, one poll interval slack

    def test_unknown_operation_returns_immediately(self):
        clock = FakeClock()
        calls = []

        def fetch(operation_id):
            calls.append(operation_id)
            return {"ok": False, "known": False, "error": "unknown_operation",
                    "operation_id": operation_id}

        result = poll_operation(fetch, "op-x", 5000, clock=clock, sleep=clock.sleep)
        self.assertFalse(result["ok"])
        self.assertEqual("unknown_operation", result["error"])
        self.assertEqual(1, len(calls), "no point polling an unknown id")
        self.assertEqual(0, len(clock.sleeps))

    def test_rejected_is_terminal(self):
        clock = FakeClock()
        result = poll_operation(
            lambda operation_id: {"ok": False, "known": True,
                                  "execution_status": "rejected",
                                  "delivery_status": "delivered"},
            "op-1", 5000, clock=clock, sleep=clock.sleep)
        self.assertFalse(result["ok"])
        self.assertEqual("rejected", result["execution_status"])
        self.assertFalse(result.get("timed_out", False))

    def test_fetch_error_is_reported_as_a_structured_failure(self):
        clock = FakeClock()

        def fetch(operation_id):
            raise RuntimeError("bridge gone")

        result = poll_operation(fetch, "op-1", 500, clock=clock, sleep=clock.sleep)
        self.assertFalse(result["ok"])
        self.assertIn("error", result)
        self.assertEqual(0, len(clock.sleeps), "a transport failure is not retried forever")


class ToolSurfaceTests(unittest.TestCase):
    def test_recording_surface_contains_the_operation_tools(self):
        for name in ("get_operation", "wait_operation"):
            self.assertIn(name, mcp_server.RECORDING_TOOL_NAMES)

    def test_policy_allows_operation_tools_without_live_evidence(self):
        import tool_policy
        policy = tool_policy.load_policy()
        for name in ("get_operation", "wait_operation"):
            entry = policy["tools"][name]
            self.assertEqual("allow", entry["decision"], name)
            self.assertEqual("pure", entry["purity"], name)
            self.assertTrue(entry["modes"]["test"], name)
            self.assertEqual([], entry.get("live_evidence", []),
                             "no fake live evidence for %s" % name)
            self.assertEqual([], tool_policy.play_allowlist(policy),
                             "play stays closed globally")

    def test_operation_tools_are_not_snapshot_readers(self):
        import tool_policy
        self.assertNotIn("get_operation", tool_policy.SNAPSHOT_READERS)
        self.assertNotIn("wait_operation", tool_policy.SNAPSHOT_READERS)


if __name__ == "__main__":
    unittest.main()
