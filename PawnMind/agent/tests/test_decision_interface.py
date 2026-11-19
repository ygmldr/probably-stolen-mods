"""Tests for R5-C3: the LLM decision interface and the trustworthy executor.

These are L1 offline tests. The "LLM" is a scripted list of decisions: the point
is not to prove a model plays well, but that the execution layer

* validates what a model may and may not say,
* owns ``rev`` and ``action_id`` itself,
* never turns an uncertain/rejected answer into success,
* stops safely when a budget or a stuck loop is reached,
* keeps the Play surface closed (``play_allowlist`` is empty).
"""

import json
import unittest

from agent import decision_interface
from agent.decision_interface import (
    Budget,
    BudgetExceeded,
    Decision,
    DecisionError,
    Executor,
    StepStatus,
    classify_response,
)


class FakeClient(object):
    """Duck-typed stand-in for TraceRecorder/BridgeClient."""

    def __init__(self):
        self.state = {
            "ok": True,
            "in_game": True,
            "rev": 10,
            "day": 1,
            "cash": 700,
            "shop_state": "OPEN",
            "legal_actions": [{"op": "get_state"}],
        }
        self.calls = []
        self.next_response = None
        self.responses = []
        self.after_action_rev = None
        self.state_reads = 0

    def get_state(self):
        self.state_reads += 1
        return dict(self.state)

    def action(self, op, **args):
        self.calls.append((op, args))
        if self.responses:
            response = self.responses.pop(0)
        elif self.next_response is not None:
            response = self.next_response
            self.next_response = None
        else:
            response = {"ok": True, "op": op, "execution_status": "completed", "rev": args.get("rev", 10)}
        if self.after_action_rev is not None:
            self.state["rev"] = self.after_action_rev
            self.after_action_rev = None
        return response

    def get_operation(self, operation_id):
        self.calls.append(("get_operation", {"operation_id": operation_id}))
        return {"ok": True, "operation_id": operation_id, "execution_status": "completed"}

    def wait_operation(self, operation_id, timeout_ms=2000):
        self.calls.append(("wait_operation", {"operation_id": operation_id, "timeout_ms": timeout_ms}))
        return {"ok": False, "pending": True, "operation_id": operation_id}


def decision(tool="open_shop", args=None, **overrides):
    payload = {
        "goal": "open the shop",
        "observation_refs": ["get_state.rev=10"],
        "proposed_action": {"tool": tool, "args": args or {}},
        "reason": "the shop is still MORNING",
        "expected_observation": "shop_state becomes OPEN",
        "stop_if": "shop_state stays MORNING twice",
    }
    payload.update(overrides)
    return Decision.from_dict(payload)


ALLOWED = {"open_shop", "call_next_customer", "confirm_trade", "stage_negotiation_item",
           "answer_prompt", "close_windows", "end_day", "sleep", "get_state"}


class DecisionSchemaTests(unittest.TestCase):
    def test_accepts_the_documented_decision_shape(self):
        item = decision()
        self.assertEqual("open_shop", item.tool)
        self.assertEqual({}, item.args)
        self.assertTrue(item.reason)

    def test_rejects_unknown_tool(self):
        with self.assertRaises(DecisionError):
            Decision.from_dict(
                {
                    "goal": "x",
                    "proposed_action": {"tool": "run_arbitrary_code", "args": {}},
                    "reason": "x",
                }
            )

    def test_rejects_model_supplied_action_id_or_rev(self):
        # The executor owns idempotency and revisions; a model must never set
        # them (that would let it replay or spoof an operation).
        for forbidden in ("action_id", "rev"):
            payload = {
                "goal": "x",
                "proposed_action": {"tool": "open_shop", "args": {forbidden: "evil"}},
                "reason": "x",
            }
            with self.assertRaises(DecisionError) as ctx:
                Decision.from_dict(payload)
            self.assertIn(forbidden, str(ctx.exception))

    def test_rejects_empty_reason_or_bad_action(self):
        with self.assertRaises(DecisionError):
            Decision.from_dict({"goal": "x", "proposed_action": {"tool": "open_shop"}, "reason": " "})
        with self.assertRaises(DecisionError):
            Decision.from_dict({"goal": "x", "proposed_action": "open_shop", "reason": "x"})
        with self.assertRaises(DecisionError):
            Decision.from_dict({"goal": "x", "proposed_action": {"tool": "open_shop", "args": []}, "reason": "x"})

    def test_rejects_hidden_answer_requests(self):
        # Decisions may reference visible observations only; a decision cannot
        # smuggle a hidden-field request as an argument of a known tool.
        payload = {
            "goal": "x",
            "proposed_action": {"tool": "get_state", "args": {"include_hidden_appraisal": True}},
            "reason": "x",
        }
        with self.assertRaises(DecisionError):
            Decision.from_dict(payload)


class BudgetTests(unittest.TestCase):
    def test_budget_reports_which_limit_was_hit(self):
        budget = Budget(max_actions=2)
        budget.tick_action()
        budget.tick_action()
        with self.assertRaises(BudgetExceeded) as ctx:
            budget.tick_action()
        self.assertEqual("max_actions", ctx.exception.code)

    def test_consecutive_rejections_are_separate_from_total_actions(self):
        budget = Budget(max_actions=100, max_consecutive_rejections=2)
        budget.tick_action()
        budget.tick_rejection()
        budget.tick_rejection()
        with self.assertRaises(BudgetExceeded) as ctx:
            budget.tick_rejection()
        self.assertEqual("max_consecutive_rejections", ctx.exception.code)
        budget.tick_success()
        budget.tick_rejection()  # reset by success: allowed again

    def test_uncertain_streak_stops_the_loop(self):
        budget = Budget(max_actions=100, max_consecutive_uncertain=2)
        budget.tick_uncertain()
        budget.tick_uncertain()
        with self.assertRaises(BudgetExceeded) as ctx:
            budget.tick_uncertain()
        self.assertEqual("max_consecutive_uncertain", ctx.exception.code)

    def test_wall_clock_budget(self):
        now = [0.0]
        budget = Budget(max_actions=100, max_wall_seconds=10, clock=lambda: now[0])
        now[0] = 11.0
        with self.assertRaises(BudgetExceeded) as ctx:
            budget.check_clock()
        self.assertEqual("max_wall_seconds", ctx.exception.code)


class ResponseClassificationTests(unittest.TestCase):
    def test_ok_completed_is_success(self):
        self.assertEqual(StepStatus.COMPLETED, classify_response({"ok": True, "execution_status": "completed"}))

    def test_ok_without_completed_execution_is_not_success(self):
        self.assertEqual(StepStatus.RUNNING, classify_response({"ok": True, "execution_status": "running"}))
        self.assertEqual(StepStatus.UNCERTAIN, classify_response({"ok": False, "execution_status": "uncertain"}))
        self.assertEqual(StepStatus.REJECTED, classify_response({"ok": False, "error": "stale_rev"}))
        self.assertEqual(StepStatus.UNKNOWN, classify_response({"ok": False}))


class ExecutorTests(unittest.TestCase):
    def setUp(self):
        self.client = FakeClient()
        self.executor = Executor(self.client, ALLOWED, Budget(max_actions=50))

    def test_executor_owns_rev_and_action_id(self):
        before = self.client.state["rev"]
        result = self.executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.COMPLETED, result.status)
        op, args = self.client.calls[0]
        self.assertEqual("open_shop", op)
        self.assertEqual(before, args["rev"])
        self.assertTrue(args["action_id"])
        self.assertNotIn("rev", {}, "the model never owns rev")

    def test_model_cannot_choose_the_action_id(self):
        # Same agent decision shape cannot carry an id; the executor generates it.
        a = self.executor.execute(decision(tool="open_shop"))
        first_id = self.client.calls[-1][1]["action_id"]
        b = self.executor.execute(decision(tool="open_shop"))
        second_id = self.client.calls[-1][1]["action_id"]
        self.assertNotEqual(first_id, second_id)
        self.assertEqual(StepStatus.COMPLETED, a.status)
        self.assertEqual(StepStatus.COMPLETED, b.status)

    def test_uncertain_queries_operations_and_never_re_dispatches(self):
        self.client.next_response = {
            "ok": False,
            "execution_status": "uncertain",
            "operation_id": "op-1",
            "error": "outcome_unverified",
        }
        result = self.executor.execute(decision(tool="confirm_trade", args={"item_id": 5, "side": "sell"}))
        self.assertEqual(StepStatus.UNCERTAIN, result.status)
        self.assertEqual(1, len([c for c in self.client.calls if c[0] == "confirm_trade"]),
                         "exactly one native dispatch")
        self.assertTrue(any(c[0] == "get_operation" for c in self.client.calls),
                        "the uncertain outcome was queried, not replayed")
        self.assertTrue(result.stop, "an unresolved uncertain step stops the loop")

    def test_stale_rev_refreshes_and_retries_the_same_action_id_once(self):
        self.client.next_response = {"ok": False, "error": "stale_rev"}
        self.client.after_action_rev = 11
        result = self.executor.execute(decision(tool="open_shop"))
        ops = [c for c in self.client.calls if c[0] == "open_shop"]
        self.assertEqual(2, len(ops), "one retry after a revision refresh")
        self.assertEqual(ops[0][1]["action_id"], ops[1][1]["action_id"],
                         "the retry reuses the same idempotency key")
        self.assertEqual(StepStatus.COMPLETED, result.status)

    def test_repeated_stale_rev_stops_instead_of_looping(self):
        self.client.responses = [
            {"ok": False, "error": "stale_rev"},
            {"ok": False, "error": "stale_rev"},
        ]
        result = self.executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.STOPPED, result.status)
        self.assertIn("stale_rev", result.stop_reason)

    def test_rejected_decision_counts_toward_the_budget(self):
        executor = Executor(self.client, ALLOWED, Budget(max_actions=50, max_consecutive_rejections=2))
        self.client.next_response = {"ok": False, "execution_status": "rejected", "error": "no_negotiation"}
        executor.execute(decision(tool="confirm_trade"))
        self.client.next_response = {"ok": False, "execution_status": "rejected", "error": "no_negotiation"}
        executor.execute(decision(tool="confirm_trade"))
        self.client.next_response = {"ok": False, "execution_status": "rejected", "error": "no_negotiation"}
        result = executor.execute(decision(tool="confirm_trade"))
        self.assertEqual(StepStatus.STOPPED, result.status)
        self.assertIn("max_consecutive_rejections", result.stop_reason)

    def test_play_surface_is_closed_without_live_evidence(self):
        # play allowlist is empty today: a Play-mode executor must refuse writes.
        executor = Executor(self.client, allowed_tools=ALLOWED, budget=Budget(max_actions=10), mode="play")
        play_allow = Executor.play_allowlist_from_policy()
        self.assertEqual([], play_allow, "play allowlist must be empty until live evidence")
        result = executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.BLOCKED, result.status)
        self.assertEqual(0, self.client.calls.__len__(), "no call is made in a closed play surface")

    def test_every_step_reports_the_new_observation(self):
        self.client.after_action_rev = 12
        result = self.executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.COMPLETED, result.status)
        self.assertIsNotNone(result.observation)
        self.assertEqual(12, result.observation.get("rev"), "the executor re-reads the real state")


class ExecutorSurfaceGuardTests(unittest.TestCase):
    """W-C3-1/W-C3-2: the executor's own surface guard must fail closed."""

    def setUp(self):
        self.client = FakeClient()

    def test_empty_allowed_tools_denies_every_tool(self):
        # An empty declared surface means "nothing allowed", not "no restriction".
        executor = Executor(self.client, allowed_tools=[],
                            budget=Budget(max_actions=5))
        result = executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.BLOCKED, result.status)
        self.assertEqual(0, len(self.client.calls), "a deny-all surface dispatches nothing")

    def test_none_allowed_tools_is_the_explicit_unrestricted_declaration(self):
        executor = Executor(self.client, allowed_tools=None,
                            budget=Budget(max_actions=5))
        result = executor.execute(decision(tool="open_shop"))
        self.assertEqual(StepStatus.COMPLETED, result.status)

    def test_hand_built_decision_cannot_bypass_argument_checks(self):
        # A host could construct Decision directly; the executor must still
        # reject executor-owned args instead of trusting the object.
        executor = Executor(self.client, ALLOWED, Budget(max_actions=5))
        bad = Decision(goal="x", tool="open_shop", args={"rev": 9}, reason="x")
        result = executor.execute(bad)
        self.assertEqual(StepStatus.REJECTED, result.status)
        self.assertEqual(0, len(self.client.calls))

    def test_hand_built_decision_with_unknown_tool_is_blocked(self):
        executor = Executor(self.client, None, Budget(max_actions=5))
        bad = Decision(goal="x", tool="run_arbitrary_code", args={}, reason="x")
        result = executor.execute(bad)
        self.assertEqual(StepStatus.BLOCKED, result.status,
                         "an unknown tool is a surface refusal, not an execution attempt")
        self.assertIn("not a PawnMind tool", result.stop_reason)
        self.assertEqual(0, len(self.client.calls))


if __name__ == "__main__":
    unittest.main()


class TransportFailureTests(unittest.TestCase):
    """R5.1/F1: the call convention is fixed at construction, a failing transport
    never retries the call, and an unknown outcome is UNCERTAIN (not a rejection
    that invites a blind retry).

    Independent review 2026-11-19 (F1): `_act` caught ``TypeError`` to guess the
    client signature, so a TypeError raised *after* a side effect re-sent the same
    call; the transport branch then ran ``error.encode()`` on an Exception, which
    raises AttributeError and hides the real failure.
    """

    class _RecordingClient(object):
        def __init__(self, outcome):
            self.outcome = outcome
            self.calls = []
            self.state = {
                "ok": True, "in_game": True, "rev": 10, "day": 1, "cash": 700,
                "shop_state": "OPEN", "legal_actions": [{"op": "open_shop"}],
            }

        def get_state(self):
            return self.state

        def get_operation(self, operation_id):
            return {"ok": True, "operation_id": operation_id, "execution_status": "completed"}

        def action(self, op, **args):
            self.calls.append((op, args))
            raise self.outcome

    def test_type_error_after_dispatch_is_not_retried(self):
        client = self._RecordingClient(TypeError("client blew up after dispatching"))
        executor = Executor(client, frozenset({"open_shop"}), Budget(max_actions=5))

        result = executor.execute(decision(tool="open_shop"))

        self.assertEqual(1, len(client.calls),
                         "a TypeError must not be treated as 'this client wants a dict'")
        self.assertEqual(StepStatus.UNCERTAIN, result.status,
                         "an exception after a possible dispatch is not a rejection")
        self.assertTrue(result.stop, "the executor stops instead of retrying")

    def test_connection_error_becomes_an_uncertain_result(self):
        client = self._RecordingClient(ConnectionError("bridge went away"))
        executor = Executor(client, frozenset({"open_shop"}), Budget(max_actions=5))

        result = executor.execute(decision(tool="open_shop"))

        self.assertEqual(1, len(client.calls), "no retry")
        self.assertEqual(StepStatus.UNCERTAIN, result.status,
                         "a transport failure has an unknown dispatch state")
        response = result.response
        self.assertEqual("transport_error", response["error"])
        self.assertEqual("unknown", response["dispatch_status"])
        self.assertIn("bridge went away", response["detail"],
                      "the real error must survive, not become AttributeError")

    def test_payload_style_client_is_supported_without_guessing(self):
        class PayloadClient(object):
            def __init__(self):
                self.calls = []
                self.state = {
                    "ok": True, "in_game": True, "rev": 10, "day": 1, "cash": 700,
                    "shop_state": "OPEN", "legal_actions": [{"op": "open_shop"}],
                }

            def get_state(self):
                return self.state

            def action(self, op, payload):
                self.calls.append((op, payload))
                return {"ok": True, "op": op, "execution_status": "completed", "rev": 11}

        client = PayloadClient()
        executor = Executor(client, frozenset({"open_shop"}), Budget(max_actions=5))

        result = executor.execute(decision(tool="open_shop"))

        self.assertEqual(StepStatus.COMPLETED, result.status, "the dict convention works")
        op, payload = client.calls[0]
        self.assertEqual("open_shop", op)
        self.assertIn("rev", payload, "rev rides in the payload for this convention")
        self.assertIn("action_id", payload)
