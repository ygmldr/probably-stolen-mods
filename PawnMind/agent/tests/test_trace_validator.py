"""TDD tests for the offline trace validator (module E, Nightly Batch A).

These tests pin the *shape* of the violation contract before the validator
exists. The trace format under test is documented in
``agent/trace_validator.py``::

    trace := list[step]
    step  := {
        "tool":  str,                 # real MCP tool name
        "args":  dict,                # tool arguments (writes carry rev/action_id)
        "result": dict,               # exact MCP result payload
        "rev":   int | None,          # optional step-level revision
        "claims": list[str],          # optional: what the caller claims happened
        "claim_text": str,            # optional: natural language, never evidence
        "status_source": str,         # optional: "operation" | "model_text" | ...
        "depends_on": list[int],      # optional: step indexes this step relies on
    }

Only the standard library is used; the validator must not import the stdio
server or the HTTP client.
"""

import contextlib
import io
import json
import os
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

import trace_validator  # noqa: E402


# ---------------------------------------------------------------------------
# trace construction helpers
# ---------------------------------------------------------------------------

def step(tool, args=None, result=None, rev=None, **extra):
    row = {"tool": tool, "args": dict(args or {})}
    if result is not None:
        row["result"] = result
    if rev is not None:
        row["rev"] = rev
    row.update(extra)
    return row


def state(day=1, cash=500, rent_paid_total=0, rent_weeks_paid=0,
          rent_weeks_missed=0, last_payment=None, rev=None):
    rent = {
        "readable": True,
        "error": None,
        "due": 100,
        "rent_value": 100,
        "due_days_left": 7,
        "days_left": 7,
        "can_pay": cash >= 100,
        "rent_paid_total": rent_paid_total,
        "rent_weeks_paid": rent_weeks_paid,
        "rent_weeks_missed": rent_weeks_missed,
        "cash": cash,
        "day": day,
        "last_payment": last_payment,
        "receipt_error": None,
    }
    return {
        "in_game": True,
        "day": day,
        "shop_state": "OPEN",
        "cash": cash,
        "client": None,
        "legal_actions": [],
        "candidate_actions": [],
        "rent": rent,
    }


def get_state(day=1, cash=500, rev=None, **rent_kwargs):
    return step("get_state", result=state(day=day, cash=cash, **rent_kwargs), rev=rev)


def write_result(tool, action_id=None, operation_id="op-1",
                 execution_status="completed", delivery_status="delivered",
                 ok=True, error=None, **extra):
    payload = {"ok": ok, "op": tool}
    if action_id is not None:
        payload["action_id"] = action_id
    if ok or execution_status is not None:
        if operation_id is not None:
            payload["operation_id"] = operation_id
        if execution_status is not None:
            payload["execution_status"] = execution_status
        if delivery_status is not None:
            payload["delivery_status"] = delivery_status
    if error is not None:
        payload["error"] = error
    payload.update(extra)
    return payload


def _trade_success_extra(item_id=101):
    return {
        "dispatched": True,
        "business_complete": True,
        "client_unchanged": True,
        "items_transferred": True,
        "cash_before": 500,
        "cash_after": 480,
        "cash_changed": True,
        "staged_item_id": item_id,
        "client_intent": "BUY",
        "evidence": {
            "called": True,
            "error": None,
            "business_complete": True,
            "client_unchanged": True,
            "items_transferred": True,
            "cash_before": 500,
            "cash_after": 480,
            "staged_item_id_after": item_id,
        },
    }


def write(tool, rev=5, action_id="act-1", args=None, result=None, **extra):
    merged = dict(args or {})
    merged.setdefault("rev", rev)
    merged.setdefault("action_id", action_id)
    if result is None:
        result = write_result(tool, action_id=action_id)
        # D/C frozen contract: a completed trade/end_day carries evidence.
        if tool == "confirm_trade" and result.get("execution_status") == "completed":
            result.update(_trade_success_extra(merged.get("item_id", 101)))
        elif tool == "end_day" and result.get("execution_status") == "completed":
            result.update({"dispatched": True, "shop_closed_after": True})
    return step(tool, args=merged, result=result, rev=rev, **extra)


def operation(tool, operation_id, execution_status, action_id=None,
              delivery_status="delivered", ok=None, error=None, **extra):
    result = {"op": tool, "operation_id": operation_id,
              "execution_status": execution_status,
              "delivery_status": delivery_status}
    if action_id is not None:
        result["action_id"] = action_id
    if ok is not None:
        result["ok"] = ok
    if error is not None:
        result["error"] = error
    result.update(extra)
    return step("get_operation", args={"operation_id": operation_id},
                result=result)


def codes(violations):
    return [row.code for row in violations]


def errors(violations):
    return [row for row in violations if row.severity == "error"]


def _valid_receipt(**overrides):
    receipt = {
        "id": "rent-0001",
        "amount": 100,
        "cash_before": 500,
        "cash_after": 400,
        "cash_delta": -100,
        "rent_days_left_before": 0,
        "rent_days_left_after": 7,
        "paid_at_rev": 12,
        "source": "dialogue_choice",
        "day": 8,
        "weeks_paid_delta": 1,
        "rent_weeks_paid": 1,
        "rent_weeks_missed": 0,
        "detection_complete": True,
    }
    receipt.update(overrides)
    return receipt


def _valid_day_readback(day_before=1, day_after=2):
    return {
        "readable": True,
        "error": None,
        "day_before": day_before,
        "day_after": day_after,
        "rent_days_left_before": 7,
        "rent_days_left_after": 6,
        "cash_before": 500,
        "cash_after": 500,
        "day_advanced": day_after > day_before,
        "changed": True,
    }


# ---------------------------------------------------------------------------
# happy path
# ---------------------------------------------------------------------------

class ValidTraceTests(unittest.TestCase):
    def test_happy_path_first_week_has_no_errors(self):
        trace = [
            get_state(day=1, cash=500),
            write("confirm_trade", rev=5, action_id="act-trade",
                  args={"item_id": 101, "side": "buy"}),
            get_state(day=1, cash=480),
            write("end_day", rev=6, action_id="act-end"),
            write("sleep", rev=7, action_id="act-sleep",
                  result=write_result("sleep", action_id="act-sleep",
                                      operation_id="op-sleep",
                                      readback=_valid_day_readback(1, 2),
                                      saved=False, last_save_time="2026-01-01T00:10:00"),
                  claims=["day_changed"]),
            get_state(day=2, cash=480),
            operation("sleep", "op-sleep", "completed"),
            write("choose_dialogue", rev=8, action_id="act-rent",
                  args={"index": 0},
                  result=write_result(
                      "choose_dialogue", action_id="act-rent",
                      operation_id="op-rent", dialogue_changed=True,
                      last_rent_payment=_valid_receipt()),
                  claims=["rent_paid"]),
            get_state(day=2, cash=400, rent_paid_total=100, rent_weeks_paid=1,
                      last_payment=_valid_receipt()),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_completed_replay_with_same_action_id_is_allowed(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"}),
            write("confirm_trade", action_id="act-1", rev=5,
                  args={"item_id": 101, "side": "buy"}),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_delivered_axis_and_completed_status_are_required_on_success(self):
        trace = [write("confirm_trade", action_id="act-1")]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_empty_trace_is_valid(self):
        self.assertEqual([], trace_validator.validate_trace([]))


# ---------------------------------------------------------------------------
# uncertain / retry rules
# ---------------------------------------------------------------------------

class UncertainRetryTests(unittest.TestCase):
    def _uncertain_first(self):
        return write(
            "confirm_trade", action_id="act-1",
            args={"item_id": 101, "side": "buy"},
            result=write_result("confirm_trade", action_id="act-1",
                                operation_id="op-1", ok=False,
                                execution_status="uncertain",
                                error="outcome_unknown"),
        )

    def test_new_action_id_retry_of_uncertain_is_rejected(self):
        trace = [
            self._uncertain_first(),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("UNCERTAIN_RETRY_WITH_NEW_ACTION_ID", codes(violations))
        self.assertIn("MISSING_OPERATION_QUERY", codes(violations))

    def test_new_action_id_retry_after_query_still_rejected(self):
        trace = [
            self._uncertain_first(),
            operation("confirm_trade", "op-1", "uncertain",
                      action_id="act-1", ok=False),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("UNCERTAIN_RETRY_WITH_NEW_ACTION_ID", codes(violations))
        self.assertNotIn("MISSING_OPERATION_QUERY", codes(violations))

    def test_new_action_id_allowed_after_unknown_operation_query(self):
        trace = [
            self._uncertain_first(),
            step("get_operation", args={"operation_id": "op-1"},
                 result={"ok": False, "error": "unknown_operation",
                         "operation_id": "op-1"}),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_new_action_id_allowed_after_known_false_query(self):
        # The D2 contract may report a lost operation as known:false without
        # the error string; it is still definitively unobservable.
        trace = [
            self._uncertain_first(),
            step("get_operation", args={"operation_id": "op-1"},
                 result={"ok": False, "known": False, "operation_id": "op-1"}),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_same_action_id_replay_is_the_correct_recovery(self):
        trace = [
            self._uncertain_first(),
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=dict(write_result("confirm_trade", action_id="act-1"),
                              **_trade_success_extra(101))),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_definitive_rejection_then_retry_with_new_id_is_allowed(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=write_result("confirm_trade", action_id="act-1",
                                      ok=False, execution_status="rejected",
                                      error="cannot_afford")),
            get_state(day=1, cash=600),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertNotIn("UNCERTAIN_RETRY_WITH_NEW_ACTION_ID", codes(violations))

    def test_action_id_reused_for_different_operation(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"}),
            write("end_day", action_id="act-1"),
        ]
        self.assertIn("ACTION_ID_REUSED_FOR_DIFFERENT_OPERATION",
                      codes(trace_validator.validate_trace(trace)))

    def test_trace_ending_with_uncertain_operation_is_flagged(self):
        trace = [
            self._uncertain_first(),
            get_state(day=1, cash=500),
        ]
        self.assertIn("UNRESOLVED_UNCERTAIN_OPERATION",
                      codes(trace_validator.validate_trace(trace)))


# ---------------------------------------------------------------------------
# pending conflicts
# ---------------------------------------------------------------------------

class PendingConflictTests(unittest.TestCase):
    def _pending_first(self):
        return write("confirm_trade", action_id="act-1",
                     result=write_result("confirm_trade", action_id="act-1",
                                         operation_id="op-1",
                                         execution_status="running",
                                         delivery_status="delivered", ok=False))

    def test_conflicting_write_while_pending_is_rejected(self):
        trace = [self._pending_first(), write("end_day", action_id="act-2")]
        self.assertIn("CONFLICTING_WRITE_WHILE_PENDING",
                      codes(trace_validator.validate_trace(trace)))

    def test_same_operation_new_action_id_while_pending_is_rejected(self):
        trace = [
            self._pending_first(),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        self.assertIn("CONFLICTING_WRITE_WHILE_PENDING",
                      codes(trace_validator.validate_trace(trace)))

    def test_different_rev_write_while_pending_is_rejected(self):
        trace = [
            self._pending_first(),
            write("confirm_trade", action_id="act-1", rev=6,
                  args={"item_id": 101, "side": "buy"}),
        ]
        # A replay of the same action_id is allowed by the bridge; a different
        # rev is still a conflicting write while the operation is pending.
        violations = trace_validator.validate_trace(trace)
        self.assertIn("CONFLICTING_WRITE_WHILE_PENDING", codes(violations))

    def test_reads_are_allowed_while_pending(self):
        trace = [
            self._pending_first(),
            get_state(day=1, cash=500),
            step("get_operation", args={"operation_id": "op-1"},
                 result={"ok": False, "status": "pending",
                         "operation_id": "op-1", "execution_status": "running"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_terminal_query_clears_pending(self):
        trace = [
            self._pending_first(),
            operation("confirm_trade", "op-1", "completed", action_id="act-1"),
            write("end_day", action_id="act-2"),
        ]
        self.assertNotIn("CONFLICTING_WRITE_WHILE_PENDING",
                         codes(trace_validator.validate_trace(trace)))

    def test_wait_operation_timeout_pending_keeps_operation_pending(self):
        trace = [
            self._pending_first(),
            step("wait_operation",
                 args={"operation_id": "op-1", "timeout_ms": 1000},
                 result={"ok": False, "status": "pending",
                         "operation_id": "op-1", "execution_status": "running",
                         "next": "call get_operation again"}),
            write("end_day", action_id="act-2"),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("CONFLICTING_WRITE_WHILE_PENDING", codes(violations))
        self.assertNotIn("OK_WITHOUT_COMPLETED_EXECUTION", codes(violations))


# ---------------------------------------------------------------------------
# status provenance: bridge operation state vs model text
# ---------------------------------------------------------------------------

class StatusProvenanceTests(unittest.TestCase):
    def test_ok_true_requires_completed_execution_status(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1",
                                      execution_status="running", ok=True)),
        ]
        self.assertIn("OK_WITHOUT_COMPLETED_EXECUTION",
                      codes(trace_validator.validate_trace(trace)))

    def test_claim_without_operation_evidence_is_rejected(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  result={"ok": True, "op": "confirm_trade"},
                  claims=["trade_completed"]),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("MODEL_SELF_REPORT_IS_NOT_EVIDENCE", codes(violations))
        self.assertIn("MISSING_OPERATION_EVIDENCE", codes(violations))

    def test_model_text_only_success_claim_is_rejected(self):
        trace = [
            write("choose_dialogue", action_id="act-1",
                  args={"index": 0},
                  result={"ok": True, "op": "choose_dialogue"},
                  claim_text="I successfully paid the rent."),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("MODEL_SELF_REPORT_IS_NOT_EVIDENCE", codes(violations))

    def test_status_source_model_text_is_rejected_even_with_status_field(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1"),
                  status_source="model_text"),
        ]
        self.assertIn("MODEL_SELF_REPORT_IS_NOT_EVIDENCE",
                      codes(trace_validator.validate_trace(trace)))

    def test_operation_status_attached_to_read_is_not_evidence(self):
        trace = [
            get_state(day=1, cash=500),
            step("get_state", result={"ok": True, "in_game": True,
                                      "execution_status": "completed"}),
        ]
        self.assertIn("STATUS_NOT_FROM_OPERATION",
                      codes(trace_validator.validate_trace(trace)))

    def test_missing_delivery_axis_is_a_warning(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1",
                                      delivery_status=None)),
        ]
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations
                if row.code == "MISSING_DELIVERY_STATUS"]
        self.assertEqual(1, len(rows))
        self.assertEqual("warning", rows[0].severity)

    def test_unknown_execution_status_is_rejected(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1",
                                      execution_status="done")),
        ]
        self.assertIn("INVALID_EXECUTION_STATUS",
                      codes(trace_validator.validate_trace(trace)))


# ---------------------------------------------------------------------------
# rent evidence
# ---------------------------------------------------------------------------

class RentEvidenceTests(unittest.TestCase):
    def _rent_write(self, result, claims=("rent_paid",), claim_text=None):
        extra = {"claims": list(claims)}
        if claim_text is not None:
            extra["claim_text"] = claim_text
        return write("choose_dialogue", action_id="act-rent",
                     args={"index": 0}, result=result, **extra)

    def test_receipt_in_the_write_response_is_evidence(self):
        trace = [
            get_state(day=8, cash=500, rent_weeks_paid=0),
            self._rent_write(write_result(
                "choose_dialogue", action_id="act-rent",
                operation_id="op-rent",
                last_rent_payment=_valid_receipt())),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_rent_paid_total_increase_is_state_evidence(self):
        trace = [
            get_state(day=8, cash=500, rent_weeks_paid=0),
            self._rent_write(write_result("choose_dialogue",
                                          action_id="act-rent",
                                          operation_id="op-rent")),
            get_state(day=8, cash=400, rent_paid_total=100,
                      rent_weeks_paid=1, last_payment=_valid_receipt()),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_rent_claim_without_receipt_is_rejected(self):
        trace = [
            get_state(day=8, cash=500, rent_weeks_paid=0),
            self._rent_write(write_result("choose_dialogue",
                                          action_id="act-rent",
                                          operation_id="op-rent")),
            get_state(day=8, cash=500, rent_weeks_paid=0),
        ]
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations if row.code == "RENT_EVIDENCE_MISSING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("receipt_missing", rows[0].details.get("reason"))

    def test_model_self_report_rent_success_is_not_evidence(self):
        trace = [
            get_state(day=8, cash=500),
            self._rent_write(write_result("choose_dialogue",
                                          action_id="act-rent",
                                          operation_id="op-rent"),
                             claims=(), claim_text="Rent paid. Week secured."),
        ]
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations if row.code == "RENT_EVIDENCE_MISSING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("self_report_only", rows[0].details.get("reason"))

    def test_incomplete_receipt_is_not_evidence(self):
        trace = [
            get_state(day=8, cash=500),
            self._rent_write(write_result(
                "choose_dialogue", action_id="act-rent",
                operation_id="op-rent",
                last_rent_payment=_valid_receipt(id="", amount=0))),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("RENT_EVIDENCE_MISSING", codes(violations))

    def test_receipt_with_inconsistent_cash_delta_is_rejected(self):
        trace = [
            get_state(day=8, cash=500),
            self._rent_write(write_result(
                "choose_dialogue", action_id="act-rent",
                operation_id="op-rent",
                last_rent_payment=_valid_receipt(cash_delta=-50))),
        ]
        self.assertIn("RENT_EVIDENCE_INCONSISTENT",
                      codes(trace_validator.validate_trace(trace)))

    def test_missed_week_counter_increase_invalidates_rent_claim(self):
        trace = [
            get_state(day=8, cash=500, rent_weeks_missed=0),
            self._rent_write(write_result("choose_dialogue",
                                          action_id="act-rent",
                                          operation_id="op-rent")),
            get_state(day=8, cash=400, rent_paid_total=100,
                      rent_weeks_paid=1, rent_weeks_missed=1,
                      last_payment=_valid_receipt()),
        ]
        self.assertIn("RENT_EVIDENCE_INCONSISTENT",
                      codes(trace_validator.validate_trace(trace)))

    def test_no_rent_claim_is_not_checked(self):
        trace = [
            get_state(day=1, cash=500),
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"}),
        ]
        self.assertEqual([], trace_validator.validate_trace(trace))


# ---------------------------------------------------------------------------
# day change and save evidence
# ---------------------------------------------------------------------------

class DayChangeSaveTests(unittest.TestCase):
    def _sleep(self, result, claims=("day_changed",)):
        return write("sleep", action_id="act-sleep", result=result,
                     claims=list(claims))

    def test_day_change_with_readback_is_evidence(self):
        trace = [
            get_state(day=1),
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep",
                                     readback=_valid_day_readback(1, 2))),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_day_change_from_next_state_read_is_evidence(self):
        trace = [
            get_state(day=1),
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep")),
            get_state(day=2),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_day_change_claim_without_readback_is_rejected(self):
        trace = [
            get_state(day=1),
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep")),
            get_state(day=1),
        ]
        self.assertIn("DAY_CHANGE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))

    def test_day_not_advanced_readback_is_rejected(self):
        trace = [
            get_state(day=1),
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep",
                                     readback=_valid_day_readback(1, 1))),
        ]
        self.assertIn("DAY_CHANGE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))

    def test_day_claim_on_observing_read_after_async_sleep_is_evidence(self):
        # A2: a running sleep is inconclusive; convergence is proven by a later
        # read or query, not by counting polls. Claiming day_changed on the read
        # that actually observes the higher day is backed by external evidence.
        trace = v2([
            get_state(day=1),
            write("sleep", action_id="act-sleep",
                  result=dict(write_result("sleep", action_id="act-sleep",
                                           operation_id="op-sleep", ok=False,
                                           execution_status="running"),
                              pending=True, dispatched=True,
                              day_changed=False,
                              readback=_valid_day_readback(1, 1))),
            get_state(day=1),
            step("get_operation", args={"operation_id": "op-sleep"},
                 result={"ok": False, "status": "pending",
                         "operation_id": "op-sleep",
                         "execution_status": "running",
                         "delivery_status": "delivered",
                         "next": "call get_operation again"}),
            step("get_state", result=state(day=2),
                 claims=["day_changed"]),
            operation("sleep", "op-sleep", "completed"),
        ])
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))
        self.assertNotIn("DAY_CHANGE_EVIDENCE_MISSING", codes(violations))

    def test_premature_day_claim_while_sleep_running_stays_flagged(self):
        trace = v2([
            get_state(day=1),
            write("sleep", action_id="act-sleep",
                  result=dict(write_result("sleep", action_id="act-sleep",
                                           operation_id="op-sleep", ok=False,
                                           execution_status="running"),
                              pending=True, dispatched=True,
                              day_changed=False,
                              readback=_valid_day_readback(1, 1)),
                  claims=["day_changed"]),
            get_state(day=2),
        ])
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations
                if row.code == "DAY_CHANGE_EVIDENCE_MISSING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("operation_not_terminal", rows[0].details.get("reason"))

    def test_save_success_without_file_evidence_is_rejected(self):
        trace = [
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep",
                                     readback=_valid_day_readback(1, 2),
                                     saved=True,
                                     last_save_time="2026-01-01T00:10:00"),
                       claims=("day_changed", "saved")),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("SAVE_EVIDENCE_MISSING", codes(violations))

    def test_save_success_with_file_evidence_is_accepted(self):
        trace = [
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep",
                                     readback=_valid_day_readback(1, 2),
                                     saved=True,
                                     save_file="slot-17.sav",
                                     last_save_time="2026-01-01T00:10:00"),
                       claims=("day_changed", "saved")),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_save_unknown_must_not_be_claimed_as_saved(self):
        trace = [
            self._sleep(write_result("sleep", action_id="act-sleep",
                                     operation_id="op-sleep",
                                     readback=_valid_day_readback(1, 2),
                                     saved=False, save_unknown=True),
                       claims=("day_changed", "saved")),
        ]
        self.assertIn("SAVE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))


# ---------------------------------------------------------------------------
# rejection path must never be treated as success
# ---------------------------------------------------------------------------

class RejectedPathTests(unittest.TestCase):
    def test_rejected_step_with_completed_status_contradicts_itself(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  result={"ok": False, "op": "confirm_trade",
                          "execution_status": "completed",
                          "operation_id": "op-1"}),
        ]
        self.assertIn("REJECTED_WITH_SUCCESS_FIELDS",
                      codes(trace_validator.validate_trace(trace)))

    def test_rejected_step_claiming_success_is_rejected(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=write_result("confirm_trade", action_id="act-1",
                                      ok=False, execution_status="rejected",
                                      error="cannot_afford"),
                  claims=["trade_completed"]),
        ]
        self.assertIn("REJECTED_CLAIMED_AS_SUCCESS",
                      codes(trace_validator.validate_trace(trace)))

    def test_dependency_on_rejected_step_is_rejected(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=write_result("confirm_trade", action_id="act-1",
                                      ok=False, execution_status="rejected",
                                      error="cannot_afford")),
            step("get_state", result=state(day=1, cash=500)),
            write("end_day", action_id="act-2", depends_on=[0]),
        ]
        self.assertIn("REJECTED_DEPENDENCY_TREATED_AS_SUCCESS",
                      codes(trace_validator.validate_trace(trace)))

    def test_recovery_after_rejection_is_not_flagged(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=write_result("confirm_trade", action_id="act-1",
                                      ok=False, execution_status="rejected",
                                      error="cannot_afford")),
            get_state(day=1, cash=700),
            write("confirm_trade", action_id="act-2",
                  args={"item_id": 101, "side": "buy"}),
        ]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))


# ---------------------------------------------------------------------------
# frozen D/C protocol fields (trade / end_day / sleep / get_operation)
# ---------------------------------------------------------------------------

class FrozenProtocolTests(unittest.TestCase):
    def test_trade_completion_requires_business_evidence(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  result={"ok": True, "op": "confirm_trade",
                          "operation_id": "op-1",
                          "execution_status": "completed",
                          "delivery_status": "delivered",
                          "dispatched": True}),
        ]
        self.assertIn("TRADE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))

    def test_trade_completion_with_frozen_evidence_is_clean(self):
        result = dict(write_result("confirm_trade", action_id="act-1"),
                      **_trade_success_extra())
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result=result, claims=["trade_completed"]),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_silent_trade_return_is_inferred_uncertain(self):
        first = write(
            "confirm_trade", action_id="act-1",
            args={"item_id": 101, "side": "buy"},
            result={"ok": False, "op": "confirm_trade",
                    "error": "outcome_unverified", "dispatched": True})
        retry = write("confirm_trade", action_id="act-2",
                      args={"item_id": 101, "side": "buy"})
        violations = trace_validator.validate_trace([first, retry])
        self.assertIn("UNCERTAIN_RETRY_WITH_NEW_ACTION_ID",
                      codes(violations))

    def test_trade_rejection_with_dispatched_false_is_clean(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  args={"item_id": 101, "side": "buy"},
                  result={"ok": False, "op": "confirm_trade",
                          "error": "trade_locked", "dispatched": False,
                          "execution_status": "rejected",
                          "delivery_status": "delivered"}),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_end_day_completion_requires_shop_closed_after(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1",
                                      shop_closed_after=False)),
        ]
        self.assertIn("END_DAY_EVIDENCE_MISMATCH",
                      codes(trace_validator.validate_trace(trace)))

    def test_end_day_with_shop_closed_after_true_is_clean(self):
        trace = [
            write("end_day", action_id="act-1",
                  result=write_result("end_day", action_id="act-1",
                                      dispatched=True,
                                      shop_closed_after=True)),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_end_day_pending_is_not_treated_as_rejection(self):
        pending = write(
            "end_day", action_id="act-1",
            result={"ok": False, "op": "end_day",
                    "execution_status": "running",
                    "delivery_status": "delivered",
                    "dispatched": True, "pending": True,
                    "error": "shop_still_open"})
        trace = [pending,
                 write("confirm_trade", action_id="act-2", depends_on=[0])]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("CONFLICTING_WRITE_WHILE_PENDING", codes(violations))
        self.assertNotIn("REJECTED_DEPENDENCY_TREATED_AS_SUCCESS",
                         codes(violations))

    def test_sleep_save_completed_evidence_is_accepted(self):
        result = dict(
            write_result("sleep", action_id="act-sleep",
                         readback=_valid_day_readback(1, 2)),
            dispatched=True, day_changed=True,
            save_evidence="save_completed", save_completed=True,
            save_unknown=False, last_save_time="2026-01-01T00:10:00")
        trace = [
            get_state(day=1),
            write("sleep", action_id="act-sleep", result=result,
                  claims=["day_changed", "saved"]),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_sleep_save_unknown_claim_is_rejected(self):
        result = dict(
            write_result("sleep", action_id="act-sleep",
                         readback=_valid_day_readback(1, 2)),
            dispatched=True, day_changed=True,
            save_evidence="save_unknown", save_completed=False,
            save_unknown=True, last_save_time="2026-01-01T00:10:00")
        trace = [
            get_state(day=1),
            write("sleep", action_id="act-sleep", result=result,
                  claims=["day_changed", "saved"]),
        ]
        self.assertIn("SAVE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))

    def test_sleep_day_unchanged_running_is_not_a_success(self):
        result = dict(
            write_result("sleep", action_id="act-sleep",
                         ok=False, execution_status="running",
                         readback=_valid_day_readback(1, 1)),
            dispatched=True, pending=True, day_changed=False,
            save_evidence="save_unknown", save_completed=False,
            save_unknown=True)
        trace = [
            get_state(day=1),
            write("sleep", action_id="act-sleep", result=result,
                  claims=["day_changed"]),
        ]
        self.assertIn("DAY_CHANGE_EVIDENCE_MISSING",
                      codes(trace_validator.validate_trace(trace)))

    def test_wait_operation_timeout_requires_operation_id_and_next(self):
        trace = [
            step("wait_operation",
                 args={"operation_id": "op-1", "timeout_ms": 100},
                 result={"ok": False, "status": "pending",
                         "operation_id": "op-1",
                         "execution_status": "running",
                         "delivery_status": "delivered"}),
        ]
        self.assertIn("WAIT_OPERATION_TIMEOUT_MISSING_NEXT",
                      codes(trace_validator.validate_trace(trace)))

    def test_wait_operation_timeout_with_next_is_not_a_failure(self):
        trace = [
            step("wait_operation",
                 args={"operation_id": "op-1", "timeout_ms": 100},
                 result={"ok": False, "status": "pending",
                         "operation_id": "op-1",
                         "execution_status": "running",
                         "delivery_status": "delivered",
                         "next": "call get_operation again"}),
        ]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_operation_query_ok_true_requires_completed(self):
        trace = [
            step("get_operation", args={"operation_id": "op-1"},
                 result={"ok": True, "known": True,
                         "operation_id": "op-1", "op": "end_day",
                         "execution_status": "running",
                         "delivery_status": "delivered"}),
        ]
        self.assertIn("OK_WITHOUT_COMPLETED_EXECUTION",
                      codes(trace_validator.validate_trace(trace)))

    def test_unknown_operation_with_process_restarted_allows_retry(self):
        first = write(
            "confirm_trade", action_id="act-1",
            args={"item_id": 101, "side": "buy"},
            result=write_result("confirm_trade", action_id="act-1",
                                operation_id="op-1", ok=False,
                                execution_status="uncertain",
                                error="outcome_unknown"))
        query = step("get_operation", args={"operation_id": "op-1"},
                     result={"ok": False, "known": False,
                             "operation_id": "op-1",
                             "error": "unknown_operation",
                             "process_restarted": True})
        retry = write("confirm_trade", action_id="act-2",
                      args={"item_id": 101, "side": "buy"})
        violations = trace_validator.validate_trace([first, query, retry])
        self.assertEqual([], errors(violations), codes(violations))

    def test_redelivered_requires_undelivered_delivery(self):
        result = dict(write_result("confirm_trade", action_id="act-1"),
                      **_trade_success_extra())
        result["redelivered"] = True
        trace = [write("confirm_trade", action_id="act-1",
                       args={"item_id": 101, "side": "buy"},
                       result=result)]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("REDELIVERED_WITHOUT_UNDELIVERED", codes(violations))

    def test_redelivered_undelivered_is_clean(self):
        result = dict(
            write_result("confirm_trade", action_id="act-1",
                         delivery_status="undelivered"),
            **_trade_success_extra())
        result["redelivered"] = True
        trace = [write("confirm_trade", action_id="act-1",
                       args={"item_id": 101, "side": "buy"},
                       result=result)]
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_rejected_step_with_dispatched_true_is_a_contradiction(self):
        trace = [
            write("confirm_trade", action_id="act-1",
                  result={"ok": False, "op": "confirm_trade",
                          "error": "trade_locked", "dispatched": True,
                          "execution_status": "rejected",
                          "delivery_status": "delivered"}),
        ]
        self.assertIn("REJECTED_WITH_DISPATCHED_TRUE",
                      codes(trace_validator.validate_trace(trace)))

    def test_completed_step_with_dispatched_false_is_a_contradiction(self):
        result = dict(write_result("end_day", action_id="act-1",
                                   dispatched=False,
                                   shop_closed_after=True))
        trace = [write("end_day", action_id="act-1", result=result)]
        self.assertIn("COMPLETED_WITHOUT_DISPATCH",
                      codes(trace_validator.validate_trace(trace)))


# ---------------------------------------------------------------------------
# R5/A4: trace schema version policy and the v2 per-trade evidence rule
# ---------------------------------------------------------------------------

SCHEMA_V1 = 1
SCHEMA_V2 = 2
_DEFAULT = object()


def v1(trace):
    return {"schema_version": SCHEMA_V1, "steps": trace}


def v2(trace):
    return {"schema_version": SCHEMA_V2, "steps": trace}


def trade_v2_result(item_id=101, action_id="act-1", operation_id="op-1",
                    business_complete=False, items_transferred=True,
                    cash_moved=True, cash_before=500, cash_after=480,
                    client_unchanged=True, evidence=_DEFAULT):
    """A real R5 ``confirm_trade`` completion (same field contract as A3)."""
    payload = write_result("confirm_trade", action_id=action_id,
                           operation_id=operation_id)
    payload.update({
        "dispatched": True,
        "business_complete": business_complete,
        "visit_complete": business_complete,
        "client_unchanged": client_unchanged,
        "items_transferred": items_transferred,
        "cash_moved": cash_moved,
        "cash_changed": cash_before != cash_after,
        "cash_before": cash_before,
        "cash_after": cash_after,
        "staged_item_id": item_id,
        "client_intent": "SELL",
    })
    if evidence is _DEFAULT:
        payload["evidence"] = {
            "called": True,
            "error": None,
            "business_complete": business_complete,
            "client_unchanged": client_unchanged,
            "items_transferred": items_transferred,
            "cash_moved": cash_moved,
            "cash_before": cash_before,
            "cash_after": cash_after,
            "staged_item_id_after": item_id,
        }
    elif evidence is not None:
        payload["evidence"] = evidence
    return payload


class TraceSchemaVersionTests(unittest.TestCase):
    def test_v2_payload_is_accepted(self):
        self.assertEqual([], trace_validator.validate_trace(v2([get_state(day=1)])))

    def test_unsupported_schema_version_is_rejected(self):
        violations = trace_validator.validate_trace(
            {"schema_version": 3, "steps": [get_state(day=1)]})
        self.assertEqual(["TRACE_SCHEMA_VERSION_UNSUPPORTED"], codes(violations))
        self.assertEqual("error", violations[0].severity)

    def test_non_integer_schema_version_is_rejected(self):
        violations = trace_validator.validate_trace(
            {"schema_version": "two", "steps": [get_state(day=1)]})
        self.assertEqual(["TRACE_SCHEMA_VERSION_UNSUPPORTED"], codes(violations))

    def test_legacy_partial_trade_is_validated_with_v1_rule_and_warns(self):
        # The old rule required business_complete:true, so an unversioned R4
        # partial trade is an error AND the operator is told the trace is
        # legacy instead of being silently reinterpreted under v2 semantics.
        trace = [write("confirm_trade", action_id="act-1",
                       args={"item_id": 101, "side": "buy"},
                       result=trade_v2_result())]
        violations = trace_validator.validate_trace(trace)
        self.assertIn("TRADE_EVIDENCE_MISSING", codes(violations))
        rows = [row for row in violations
                if row.code == "TRACE_SCHEMA_VERSION_MISSING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("warning", rows[0].severity)
        self.assertEqual(SCHEMA_V2, rows[0].details.get("suggested_schema_version"))

    def test_legacy_visit_complete_only_is_not_silently_clean(self):
        # v1 accepts business_complete-only; v2 rejects it. A legacy trace is
        # still judged by v1 but the verdict difference is surfaced as a
        # warning, never hidden.
        result = trade_v2_result(business_complete=True, items_transferred=False,
                                 cash_moved=False, cash_before=500,
                                 cash_after=500)
        trace = [write("confirm_trade", action_id="act-1",
                       args={"item_id": 101, "side": "buy"}, result=result)]
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))
        self.assertIn("TRACE_SCHEMA_VERSION_MISSING", codes(violations))

    def test_explicit_v1_trace_uses_the_old_rule_without_warning(self):
        trace = v1([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=trade_v2_result())])
        violations = trace_validator.validate_trace(trace)
        self.assertIn("TRADE_EVIDENCE_MISSING", codes(violations))
        self.assertNotIn("TRACE_SCHEMA_VERSION_MISSING", codes(violations))


class TradeEvidenceV2Tests(unittest.TestCase):
    def test_partial_trade_without_business_complete_is_clean(self):
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=trade_v2_result(),
                          claims=["trade_completed"])])
        violations = trace_validator.validate_trace(trace)
        self.assertNotIn("TRADE_EVIDENCE_MISSING", codes(violations))
        self.assertEqual([], errors(violations), codes(violations))

    def test_visit_complete_without_trade_effect_is_flagged(self):
        result = trade_v2_result(business_complete=True, items_transferred=False,
                                 cash_moved=False, cash_before=500,
                                 cash_after=500)
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=result)])
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations
                if row.code == "TRADE_EVIDENCE_MISSING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("no_trade_effect", rows[0].details.get("reason"))

    def test_cash_only_trade_is_clean(self):
        # A purchase keeps the negotiated pointer on the bought item, so the
        # cash delta alone proves that trade.
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=trade_v2_result(items_transferred=False))])
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_item_only_barter_is_clean(self):
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "sell"},
                          result=trade_v2_result(items_transferred=True,
                                                 cash_moved=False,
                                                 cash_before=500,
                                                 cash_after=500))])
        self.assertEqual([], errors(trace_validator.validate_trace(trace)))

    def test_trade_without_client_unchanged_is_flagged(self):
        result = trade_v2_result(client_unchanged=False)
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=result)])
        violations = trace_validator.validate_trace(trace)
        reasons = {row.details.get("reason") for row in violations
                   if row.code == "TRADE_EVIDENCE_MISSING"}
        self.assertIn("client_not_unchanged", reasons)

    def test_trade_without_evidence_object_is_flagged(self):
        result = trade_v2_result(evidence=None)
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=result)])
        violations = trace_validator.validate_trace(trace)
        reasons = {row.details.get("reason") for row in violations
                   if row.code == "TRADE_EVIDENCE_MISSING"}
        self.assertIn("evidence_object_missing", reasons)

    def test_inconsistent_evidence_object_is_flagged(self):
        result = trade_v2_result()
        result["evidence"] = dict(result["evidence"],
                                  items_transferred=False)
        trace = v2([write("confirm_trade", action_id="act-1",
                          args={"item_id": 101, "side": "buy"},
                          result=result)])
        violations = trace_validator.validate_trace(trace)
        reasons = {row.details.get("reason") for row in violations
                   if row.code == "TRADE_EVIDENCE_MISSING"}
        self.assertIn("evidence_items_mismatch", reasons)


# ---------------------------------------------------------------------------
# R5/A4: calibrated pending-conflict relation and run-change tracking
# ---------------------------------------------------------------------------

def pending_trade(item_id=101, action_id="act-1", operation_id="op-1",
                  rev=5, tool="confirm_trade", side="buy", **extra):
    result = dict(
        write_result(tool, action_id=action_id, operation_id=operation_id,
                     ok=False, execution_status="running"),
        pending=True, dispatched=True)
    result.update(extra)
    args = {"item_id": item_id, "side": side} if tool == "confirm_trade" else {}
    return write(tool, rev=rev, action_id=action_id, args=args, result=result)


def pending_end_day(action_id="act-end", operation_id="op-end", rev=5):
    result = dict(
        write_result("end_day", action_id=action_id,
                     operation_id=operation_id, ok=False,
                     execution_status="running"),
        pending=True, dispatched=True, error="shop_still_open")
    return write("end_day", rev=rev, action_id=action_id, result=result)


def completed_trade(item_id=101, action_id="act-2", operation_id="op-2",
                    rev=6, business_complete=False):
    return write(
        "confirm_trade", rev=rev, action_id=action_id,
        args={"item_id": item_id, "side": "buy"},
        result=trade_v2_result(item_id=item_id, action_id=action_id,
                               operation_id=operation_id,
                               business_complete=business_complete))


def resolve(operation_id="op-1", tool="confirm_trade", action_id="act-1",
            execution_status="completed", **extra):
    return operation(tool, operation_id, execution_status,
                     action_id=action_id, **extra)


class ConflictRelationUnitTests(unittest.TestCase):
    """The relation classifier is pure and is tested directly."""

    def _relation(self, pending, write_step):
        return trace_validator.classify_pending_relation(pending, write_step)

    @staticmethod
    def _op(tool, args, rev=5, action_id="act-1"):
        return {"tool": tool, "args": dict(args), "rev": rev,
                "action_id": action_id}

    def test_different_item_trade_is_independent(self):
        relation, conflicting, _ = self._relation(
            self._op("confirm_trade", {"item_id": 101, "side": "buy"}),
            self._op("confirm_trade", {"item_id": 202, "side": "buy"},
                     rev=6, action_id="act-2"))
        self.assertEqual("independent", relation)
        self.assertFalse(conflicting)

    def test_same_item_is_a_conflict(self):
        relation, conflicting, _ = self._relation(
            self._op("confirm_trade", {"item_id": 101, "side": "buy"}),
            self._op("move_item", {"item_id": 101, "target": "main"},
                     rev=6, action_id="act-2"))
        self.assertEqual("same_item", relation)
        self.assertTrue(conflicting)

    def test_same_op_is_a_conflict(self):
        relation, conflicting, _ = self._relation(
            self._op("end_day", {"dismiss_waiting": False}),
            self._op("end_day", {"dismiss_waiting": True},
                     rev=6, action_id="act-2"))
        self.assertEqual("same_op", relation)
        self.assertTrue(conflicting)

    def test_modal_resolution_while_other_op_is_pending_is_allowed(self):
        relation, conflicting, _ = self._relation(
            self._op("end_day", {}),
            self._op("answer_prompt", {"choice": "confirm"},
                     rev=6, action_id="act-2"))
        self.assertEqual("modal_resolution", relation)
        self.assertFalse(conflicting)

    def test_two_dialogue_writes_are_a_modal_conflict(self):
        relation, conflicting, _ = self._relation(
            self._op("choose_dialogue", {"index": 0}),
            self._op("advance_dialogue", {}, rev=6, action_id="act-2"))
        self.assertEqual("modal_resolution", relation)
        self.assertTrue(conflicting)

    def test_insufficient_evidence_is_unknown_not_allowed(self):
        relation, conflicting, _ = self._relation(
            self._op("confirm_trade", {"item_id": 101, "side": "buy"}),
            self._op("end_day", {}, rev=6, action_id="act-2"))
        self.assertEqual("unknown", relation)
        self.assertTrue(conflicting)

    def test_call_scoped_staging_different_item_is_independent(self):
        # R5-B1: Core/PendingScope now covers stage_negotiation_item, so the
        # validator must allow a different-item staging call while one is
        # pending instead of treating it as the same native call.
        relation, conflicting, _ = self._relation(
            self._op("stage_negotiation_item", {"item_id": 101}),
            self._op("stage_negotiation_item", {"item_id": 202},
                     rev=6, action_id="act-2"))
        self.assertEqual("independent", relation)
        self.assertFalse(conflicting)

    def test_call_scoped_staging_same_item_is_a_conflict(self):
        relation, conflicting, _ = self._relation(
            self._op("stage_negotiation_item", {"item_id": 101}),
            self._op("stage_negotiation_item", {"item_id": 101},
                     rev=6, action_id="act-2"))
        self.assertEqual("same_item", relation)
        self.assertTrue(conflicting)

    def test_run_change_in_flight_is_run_changing(self):
        relation, conflicting, _ = self._relation(
            self._op("load_game", {}),
            self._op("answer_prompt", {"choice": "confirm"},
                     rev=6, action_id="act-2"))
        self.assertEqual("run_changing", relation)


class CalibratedPendingTests(unittest.TestCase):
    def test_different_item_trade_while_pending_is_allowed(self):
        trace = v2([
            pending_trade(item_id=101),
            completed_trade(item_id=202, rev=6),
            resolve(),
        ])
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))
        self.assertNotIn("CONFLICTING_WRITE_WHILE_PENDING", codes(violations))

    def test_same_item_write_while_pending_is_a_same_item_conflict(self):
        trace = v2([
            pending_trade(item_id=101),
            completed_trade(item_id=101, rev=6),
            resolve(),
        ])
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations
                if row.code == "CONFLICTING_WRITE_WHILE_PENDING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("same_item", rows[0].details.get("relation"))

    def test_insufficient_evidence_write_while_pending_is_unknown(self):
        trace = v2([
            pending_trade(item_id=101),
            write("end_day", rev=6, action_id="act-2",
                  result=write_result("end_day", action_id="act-2",
                                      shop_closed_after=True)),
            resolve(),
        ])
        violations = trace_validator.validate_trace(trace)
        rows = [row for row in violations
                if row.code == "CONFLICTING_WRITE_WHILE_PENDING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("unknown", rows[0].details.get("relation"))

    def test_modal_resolution_while_end_day_pending_is_allowed(self):
        trace = v2([
            pending_end_day(),
            write("answer_prompt", rev=6, action_id="act-answer",
                  result=write_result("answer_prompt",
                                      action_id="act-answer",
                                      operation_id="op-answer")),
            resolve(operation_id="op-end", tool="end_day",
                    action_id="act-end"),
        ])
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_dialogue_continuation_while_trade_pending_is_allowed(self):
        trace = v2([
            pending_trade(item_id=101),
            write("advance_dialogue", rev=6, action_id="act-dialogue",
                  result=write_result("advance_dialogue",
                                      action_id="act-dialogue",
                                      operation_id="op-dialogue")),
            resolve(),
        ])
        violations = trace_validator.validate_trace(trace)
        self.assertEqual([], errors(violations), codes(violations))

    def test_duplicate_refusal_does_not_create_a_phantom_operation(self):
        duplicate = write(
            "confirm_trade", rev=6, action_id="act-2",
            args={"item_id": 101, "side": "buy"},
            result={"ok": False, "op": "confirm_trade",
                    "error": "operation_pending",
                    "blocking_operation_id": "op-1",
                    "blocking_same_call": True,
                    "execution_status": "running",
                    "delivery_status": "delivered", "dispatched": True,
                    "next": "call get_operation for the blocking id"})
        trace = v2([pending_trade(item_id=101), duplicate, resolve()])
        violations = trace_validator.validate_trace(trace)
        self.assertEqual(["CONFLICTING_WRITE_WHILE_PENDING"],
                         sorted(set(codes(violations))))

    def test_run_change_pending_refusal_is_a_run_changing_conflict(self):
        first = write(
            "load_game", rev=3, action_id="act-load",
            result=dict(write_result("load_game", action_id="act-load",
                                     operation_id="op-load",
                                     dispatched=True),
                        run_change="issued",
                        run_change_status="requested",
                        run_change_confirmed=False))
        retry = write(
            "load_game", rev=3, action_id="act-load-2",
            result={"ok": False, "op": "load_game",
                    "error": "run_change_pending",
                    "blocking_operation_id": "op-load",
                    "run_change_status": "requested",
                    "run_change_confirmed": False,
                    "next": "call get_operation for the blocking id"})
        confirmed = operation("load_game", "op-load", "completed",
                              action_id="act-load",
                              run_change_status="confirmed",
                              run_change_confirmed=True)
        violations = trace_validator.validate_trace(v2([first, retry,
                                                        confirmed]))
        rows = [row for row in violations
                if row.code == "CONFLICTING_WRITE_WHILE_PENDING"]
        self.assertEqual(1, len(rows))
        self.assertEqual("run_changing", rows[0].details.get("relation"))

    def test_prompt_confirmation_is_not_a_run_change(self):
        prompt = write(
            "confirm_new_game", rev=3, action_id="act-prompt",
            result=dict(write_result("confirm_new_game",
                                     action_id="act-prompt",
                                     operation_id="op-prompt",
                                     dispatched=True),
                        prompt_confirmed=True,
                        prompt_id="first_run",
                        run_change="not_issued"))
        start = write(
            "confirm_new_game", rev=4, action_id="act-start",
            result=dict(write_result("confirm_new_game",
                                     action_id="act-start",
                                     operation_id="op-start",
                                     dispatched=True),
                        run_change="issued",
                        run_change_status="confirmed",
                        run_change_confirmed=True))
        violations = trace_validator.validate_trace(
            v2([prompt, start, get_state(day=1)]))
        self.assertEqual([], errors(violations), codes(violations))

    def test_stale_prompt_run_change_status_is_not_a_run_change(self):
        # Traces recorded before A1 may carry run_change_status=requested on a
        # first-run prompt confirmation. prompt_confirmed is authoritative:
        # that was a UI confirmation, never a native run start.
        prompt = write(
            "confirm_new_game", rev=3, action_id="act-prompt",
            result=dict(write_result("confirm_new_game",
                                     action_id="act-prompt",
                                     operation_id="op-prompt",
                                     dispatched=True),
                        prompt_confirmed=True,
                        prompt_id="first_run",
                        run_change_status="requested",
                        run_change_confirmed=False))
        start = write(
            "confirm_new_game", rev=4, action_id="act-start",
            result=dict(write_result("confirm_new_game",
                                     action_id="act-start",
                                     operation_id="op-start",
                                     dispatched=True),
                        run_change_status="confirmed",
                        run_change_confirmed=True))
        violations = trace_validator.validate_trace(
            v2([prompt, start, get_state(day=1)]))
        self.assertEqual([], errors(violations), codes(violations))

    def test_invalid_run_change_field_is_flagged(self):
        write_step = write(
            "confirm_new_game", rev=3, action_id="act-prompt",
            result=dict(write_result("confirm_new_game",
                                     action_id="act-prompt",
                                     operation_id="op-prompt",
                                     dispatched=True),
                        run_change="maybe"))
        violations = trace_validator.validate_trace(v2([write_step]))
        self.assertIn("INVALID_RUN_CHANGE_FIELD", codes(violations))


# ---------------------------------------------------------------------------
# malformed trace / structure
# ---------------------------------------------------------------------------

class MalformedTraceTests(unittest.TestCase):
    def test_payload_object_with_steps_is_accepted(self):
        trace = {"steps": [get_state(day=1)]}
        self.assertEqual([], trace_validator.validate_payload(trace))

    def test_payload_object_without_steps_raises(self):
        with self.assertRaises(trace_validator.TraceFormatError):
            trace_validator.validate_payload({"nope": 1})

    def test_scalar_payload_raises(self):
        with self.assertRaises(trace_validator.TraceFormatError):
            trace_validator.validate_payload("not a trace")

    def test_non_dict_step_is_a_violation(self):
        violations = trace_validator.validate_payload([42])
        self.assertIn("INVALID_TRACE_STEP", codes(violations))

    def test_step_without_tool_is_a_violation(self):
        violations = trace_validator.validate_payload(
            [{"args": {}, "result": {}}])
        self.assertIn("INVALID_TRACE_STEP", codes(violations))

    def test_step_with_non_dict_args_is_a_violation(self):
        violations = trace_validator.validate_payload(
            [{"tool": "get_state", "args": "nope", "result": {}}])
        self.assertIn("INVALID_TRACE_STEP", codes(violations))

    def test_step_with_non_dict_result_is_a_violation(self):
        violations = trace_validator.validate_payload(
            [{"tool": "get_state", "args": {}, "result": "nope"}])
        self.assertIn("INVALID_TRACE_STEP", codes(violations))

    def test_violations_are_structured(self):
        violations = trace_validator.validate_payload(
            [{"tool": "confirm_trade", "args": {"rev": 5}, "result": {}}])
        self.assertTrue(violations)
        row = violations[0]
        payload = row.to_dict()
        self.assertEqual({"code", "message", "step_index", "severity", "details"},
                         set(payload))
        self.assertIsInstance(payload["code"], str)
        self.assertIsInstance(payload["message"], str)
        self.assertIsInstance(payload["step_index"], int)
        self.assertIn(payload["severity"], ("error", "warning"))


# ---------------------------------------------------------------------------
# CLI behaviour
# ---------------------------------------------------------------------------

class CliTests(unittest.TestCase):
    def _write_temp(self, payload):
        handle, path = tempfile.mkstemp(suffix=".json")
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            json.dump(payload, stream)
        self.addCleanup(os.unlink, path)
        return path

    def _run(self, path):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            exit_code = trace_validator.main([path])
        return exit_code, json.loads(output.getvalue())

    def test_cli_clean_trace_exits_zero(self):
        path = self._write_temp([get_state(day=1)])
        exit_code, payload = self._run(path)
        self.assertEqual(0, exit_code)
        self.assertTrue(payload["ok"])
        self.assertEqual([], payload["violations"])

    def test_cli_violation_exits_one(self):
        path = self._write_temp([{"tool": "nope"}])
        exit_code, payload = self._run(path)
        self.assertEqual(1, exit_code)
        self.assertFalse(payload["ok"])
        self.assertTrue(payload["violations"])

    def test_cli_bad_json_exits_two(self):
        handle, path = tempfile.mkstemp(suffix=".json")
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            stream.write("{not json")
        self.addCleanup(os.unlink, path)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            exit_code = trace_validator.main([path])
        self.assertEqual(2, exit_code)
        self.assertFalse(json.loads(output.getvalue())["ok"])

    def test_cli_missing_file_exits_two(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            exit_code = trace_validator.main(["/nonexistent/trace.json"])
        self.assertEqual(2, exit_code)

    def test_cli_reports_declared_schema_version(self):
        path = self._write_temp(v2([get_state(day=1)]))
        exit_code, payload = self._run(path)
        self.assertEqual(0, exit_code)
        self.assertEqual(SCHEMA_V2, payload["trace_schema_version"])

    def test_cli_reports_legacy_trace_schema_version(self):
        path = self._write_temp([get_state(day=1)])
        exit_code, payload = self._run(path)
        self.assertEqual(0, exit_code)
        self.assertEqual(SCHEMA_V1, payload["trace_schema_version"])

    def test_cli_counts_warnings_separately(self):
        path = self._write_temp([get_state(day=1)])
        exit_code, payload = self._run(path)
        self.assertEqual(0, exit_code)
        self.assertEqual({"error": 0, "warning": 0}, payload["counts"])


# ---------------------------------------------------------------------------
# independent JSON fixtures (agent/tests/fixtures)
# ---------------------------------------------------------------------------

FIXTURES_DIR = os.path.join(os.path.dirname(__file__), "fixtures")


def load_fixture(name):
    with open(os.path.join(FIXTURES_DIR, name), encoding="utf-8") as handle:
        return json.load(handle)


class TraceFixtureTests(unittest.TestCase):
    """Positive/negative traces for the R5-A4 protocol changes."""

    EXPECTED_CODES = {
        "trade_partial_ok.json": [],
        "trade_visit_complete_no_effect_invalid.json": [
            "TRADE_EVIDENCE_MISSING"],
        "pending_same_item_retry_conflict.json": [
            "CONFLICTING_WRITE_WHILE_PENDING"],
        "pending_different_item_ok.json": [],
        "prompt_confirm_then_real_start_ok.json": [],
        "sleep_async_converges_ok.json": [],
        "pending_stage_same_item_retry_conflict.json": [
            "CONFLICTING_WRITE_WHILE_PENDING"],
        "pending_stage_different_item_ok.json": [],
    }

    def test_fixtures_are_schema_version_2(self):
        for name in self.EXPECTED_CODES:
            payload = load_fixture(name)
            self.assertEqual(SCHEMA_V2, payload.get("schema_version"), name)

    def test_fixtures_validate_to_the_exact_code_set(self):
        for name, expected in self.EXPECTED_CODES.items():
            with self.subTest(fixture=name):
                violations = trace_validator.validate_trace(load_fixture(name))
                self.assertEqual(sorted(expected),
                                 sorted({row.code for row in violations}))

    def test_negative_fixtures_are_errors(self):
        for name in ("trade_visit_complete_no_effect_invalid.json",
                     "pending_same_item_retry_conflict.json"):
            with self.subTest(fixture=name):
                violations = trace_validator.validate_trace(load_fixture(name))
                self.assertEqual(1, len(errors(violations)), codes(violations))

    def test_fixture_cli_exit_codes_and_schema_version(self):
        for name, expected in self.EXPECTED_CODES.items():
            with self.subTest(fixture=name):
                path = os.path.join(FIXTURES_DIR, name)
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    exit_code = trace_validator.main([path])
                self.assertEqual(1 if expected else 0, exit_code, name)
                report = json.loads(output.getvalue())
                self.assertEqual(SCHEMA_V2, report["trace_schema_version"], name)


# ---------------------------------------------------------------------------
# module hygiene
# ---------------------------------------------------------------------------

class ModuleHygieneTests(unittest.TestCase):
    def test_validator_does_not_import_the_stdio_server_or_client(self):
        with open(trace_validator.__file__, encoding="utf-8") as handle:
            source = handle.read()
        self.assertNotIn("import mcp_server", source)
        self.assertNotIn("from mcp_server", source)
        self.assertNotIn("import pawnmind_client", source)
        self.assertNotIn("from pawnmind_client", source)

    def test_summarize_reports_counts(self):
        violations = trace_validator.validate_payload([42])
        summary = trace_validator.summarize(violations)
        self.assertEqual({"error": 1, "warning": 0}, summary["counts"])
        self.assertEqual(trace_validator.VALIDATOR_VERSION,
                         summary["validator_version"])


if __name__ == "__main__":
    unittest.main()
