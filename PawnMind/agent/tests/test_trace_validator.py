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


def write(tool, rev=5, action_id="act-1", args=None, result=None, **extra):
    merged = dict(args or {})
    merged.setdefault("rev", rev)
    merged.setdefault("action_id", action_id)
    if result is None:
        result = write_result(tool, action_id=action_id)
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
        trace = [
            write("confirm_trade", action_id="act-1",
                  result=write_result("confirm_trade", action_id="act-1")),
        ]
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
                  result=write_result("confirm_trade", action_id="act-1")),
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

    def test_cli_counts_warnings_separately(self):
        path = self._write_temp([get_state(day=1)])
        exit_code, payload = self._run(path)
        self.assertEqual(0, exit_code)
        self.assertEqual({"error": 0, "warning": 0}, payload["counts"])


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
