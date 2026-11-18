"""Offline trace validator for PawnMind agent runs (stdlib only).

Purpose
-------
A PawnMind agent run is a JSON trace: an ordered list of MCP tool calls with
their exact arguments, the exact bridge result, and optional annotations from
the caller (claims, model text, dependencies). This module checks one trace
against the Operation protocol invariants agreed on 2026-11-17 and revised in
R5/A4 (2026-11-19: per-trade evidence, calibrated pending conflict relation,
run-change field) and R5/B1 (2026-11-19: ``stage_negotiation_item`` joins
``confirm_trade`` as a call-scoped operation) and reports *structured*
violations (``code`` / ``message`` / ``step_index``).

It is deliberately offline and self-contained:

* it never imports ``agent/mcp_server.py`` (stdio server), ``agent/pawnmind_client.py``
  (HTTP client) or any game module;
* it never opens a socket, never starts a game, never touches a save;
* it is pure Python so it can run in CI, in the DSH host, or next to the fake
  game without side effects.

Trace format
------------
::

    trace := {"schema_version": 2, "steps": list[step]}   # canonical (R5)
           | list[step] | {"steps": list[step]}           # legacy (schema v1)
    step  := {
        "tool": str,              # real MCP tool name
        "args": dict,             # tool arguments; writes carry rev/action_id
        "result": dict,           # exact MCP result payload
        "rev": int,               # optional; state revision seen by the caller
        "claims": list[str],      # optional; what the caller says happened
        "claim_text": str,        # optional; natural language, never evidence
        "status_source": str,     # optional; "operation" | "model_text" | ...
        "depends_on": list[int],  # optional; step indexes this step relies on
    }

Schema versions
---------------
``schema_version: 2`` is the current trace schema (R5/A4) and selects the v2
per-trade evidence rule 17. ``schema_version: 1`` (or a bare step list /
object without a version, the pre-R5 format) is *legacy* and is validated
with the v1 trade-evidence rule (``business_complete``). It is never silently
reinterpreted: when an unversioned trace contains a ``confirm_trade`` whose
verdict differs between v1 and v2, a ``TRACE_SCHEMA_VERSION_MISSING`` warning
is emitted so the trace must be re-exported with ``schema_version: 2`` before
it can be judged under the current semantics. An unsupported version is an
error (``TRACE_SCHEMA_VERSION_UNSUPPORTED``) and the scan stops: fail closed,
never guess. (Rule 3's calibrated conflict relation is not version-gated: it
mirrors what the bridge has always allowed, so it applies to every schema.)

Recognised claim codes for the first-week flow: ``trade_completed``,
``operation_completed``, ``rent_paid`` / ``paid_rent``, ``day_changed`` /
``slept`` / ``day_advanced``, ``saved`` / ``save_completed``.

Rules enforced (error unless stated otherwise)
----------------------------------------------
1. ``UNCERTAIN_RETRY_WITH_NEW_ACTION_ID`` -- an operation whose
   ``execution_status`` is ``uncertain`` may only be retried with the *same*
   ``action_id``. Retrying with a new ``action_id`` risks double execution.
2. ``MISSING_OPERATION_QUERY`` (warning) -- the retry above happened without a
   prior ``get_operation`` / ``wait_operation`` query; this is a blind retry.
3. ``CONFLICTING_WRITE_WHILE_PENDING`` -- a write issued while an operation
   is ``prepared``/``running`` (or ``wait_operation`` timed out as pending) is
   classified by an explicit conflict relation: ``same_op`` (the same native
   call, or the same non-call-scoped op with any arguments; the call-scoped
   ops are ``confirm_trade`` and ``stage_negotiation_item``, mirroring
   ``Core.PendingScope`` -- same item+args retry is a conflict, a different
   item is independent), ``same_item`` (both writes name the same item),
   ``run_changing`` (a run switch is in
   flight, or one was requested but not confirmed), ``modal_resolution`` (a
   dialogue/prompt write that resolves the blocking modal; allowed when the
   pending op is not itself a dialogue/prompt op), ``independent`` (a
   call-scoped trade on provably different items; allowed), and ``unknown``
   (insufficient evidence to prove independence; always an ERROR -- never
   "allow everything"). Reads are always allowed. Conflicts carry the
   ``relation`` and ``reason`` in ``details``.
4. ``OK_WITHOUT_COMPLETED_EXECUTION`` -- ``ok:true`` only ever means
   ``execution_status == "completed"``.
5. ``MISSING_OPERATION_EVIDENCE`` (warning) -- write responses without
   ``operation_id`` / ``execution_status`` cannot prove anything (legacy or
   drifted schema).
6. ``MODEL_SELF_REPORT_IS_NOT_EVIDENCE`` -- model text (``claim_text``) or a
   ``status_source`` of ``model_text`` is never proof of success.
7. ``STATUS_NOT_FROM_OPERATION`` -- ``execution_status`` attached to a plain
   read (not a write response, not ``get_operation`` / ``wait_operation``) does
   not come from the operation state machine.
8. ``INVALID_EXECUTION_STATUS`` / ``INVALID_DELIVERY_STATUS`` /
   ``MISSING_DELIVERY_STATUS`` (last one warning) -- unknown or missing axis
   values.
9. ``RENT_EVIDENCE_MISSING`` / ``RENT_EVIDENCE_INCONSISTENT`` -- a rent-payment
   claim needs an externally verifiable receipt (``last_rent_payment`` /
   ``rent_receipt`` / ``settlement_event`` with id+amount) in the write
   response, or a later state read whose ``rent.rent_paid_total`` /
   ``rent_weeks_paid`` increased. The model saying "rent paid" is not evidence.
10. ``DAY_CHANGE_EVIDENCE_MISSING`` -- a day-change claim needs a readable
    ``readback.day_advanced`` (or an equivalent later state read showing a
    higher ``day``). A day that did not move is not a successful transition.
11. ``SAVE_EVIDENCE_MISSING`` -- ``saved``/``save_completed`` claims need the
    sleep save axis (``save_evidence: "save_completed"`` / ``save_completed:
    true``) or file evidence (``save_file`` / ``save_path`` / ``file_written``
    etc.). ``save_evidence: "save_unknown"`` can never be claimed as saved, and
    a new ``last_save_time`` alone is not proof.
12. ``REJECTED_WITH_SUCCESS_FIELDS`` / ``REJECTED_CLAIMED_AS_SUCCESS`` -- a
    rejected/failed step may not carry success fields or be presented as a
    success.
13. ``REJECTED_DEPENDENCY_TREATED_AS_SUCCESS`` -- a step whose ``depends_on``
    points at a rejected/failed step treats the rejection as success.
14. ``ACTION_ID_REUSED_FOR_DIFFERENT_OPERATION`` -- an ``action_id`` is an
    idempotency key; it may not be reused for a different op/args.
15. ``UNRESOLVED_UNCERTAIN_OPERATION`` / ``UNRESOLVED_PENDING_OPERATION``
    (warnings) -- the trace ends while an operation is still unresolved.
16. ``INVALID_TRACE_STEP`` -- malformed step (not a dict, missing tool, wrong
    args/result type).
17. ``TRADE_EVIDENCE_MISSING`` (schema v2) -- a ``confirm_trade`` presented as
    completed (``ok:true`` / ``execution_status:"completed"`` / a
    ``trade_completed`` claim) needs *per-trade* evidence from the same client:
    ``client_unchanged == true`` AND (``items_transferred == true`` OR
    ``cash_moved == true``; a cash-only delta is a valid purchase and a
    zero-cash item move is a valid barter), with the ``evidence`` object
    corroborating those axes (``called:true``, ``client_unchanged:true`` and
    the same items/cash movement). ``business_complete`` / ``visit_complete``
    are auxiliary attribution for the whole visit and can neither prove nor
    replace this trade; a visit-complete report with no item or cash movement
    is a false success and is flagged. The legacy v1 rule (schema v1 only)
    instead required ``business_complete:true`` + ``client_unchanged:true`` +
    the ``evidence`` object.
18. ``END_DAY_EVIDENCE_MISMATCH`` -- ``end_day`` may report ``completed`` only
    when ``shop_closed_after:true``; still-OPEN is ``running``/``pending``.
19. ``REJECTED_WITH_DISPATCHED_TRUE`` / ``COMPLETED_WITHOUT_DISPATCH`` /
    ``REDELIVERED_WITHOUT_UNDELIVERED`` (warning) / ``INVALID_DISPATCHED``
    (warning) -- the ``dispatched`` and ``delivery_status`` fields must agree
    with the execution axis; a re-delivery keeps ``delivery_status:
    "undelivered"``.
20. ``WAIT_OPERATION_TIMEOUT_MISSING_NEXT`` -- a ``wait_operation`` timeout must
    carry ``operation_id`` and ``next`` instead of looking like a dead end or a
    fake success.
21. ``TRACE_SCHEMA_VERSION_UNSUPPORTED`` -- the payload declares a
    ``schema_version`` other than 1 or 2; the scan stops without interpreting
    any step (fail closed).
22. ``TRACE_SCHEMA_VERSION_MISSING`` (warning) -- a legacy (unversioned) trace
    contains a ``confirm_trade`` whose verdict differs between the v1 and v2
    trade rules; re-export it with ``schema_version: 2`` instead of letting it
    be silently reinterpreted.
23. ``INVALID_RUN_CHANGE_FIELD`` -- the additive ``run_change`` response field
    (R5/A1) must be ``"issued"`` or ``"not_issued"`` when present. A first-run
    prompt confirmation (``prompt_confirmed:true``) is never a run-changing
    native start, so it does not register a run change even when a pre-A1
    trace still carries ``run_change_status:"requested"`` on it.

Frozen D/C schema (HEAD ``f09314e``, policy sha ``e0da5a24``) is described in
``skills/pawnmind-first-week/SKILL.md`` §13 and the benchmark spec; R5/A4
revised rules 3 and 17 and added 21-23 (trace schema ``2`` encodes them).

CLI
---
::

    python agent/trace_validator.py trace.json
    # exit 0: no errors (warnings allowed), 1: violations, 2: bad input
"""

import argparse
import json
import re
from dataclasses import dataclass, field

VALIDATOR_VERSION = "1.3.0"

#: Current trace schema. v2 = R5/A4 per-trade evidence + calibrated pending
#: conflict relation. No ``schema_version`` (bare list / object) means legacy v1.
TRACE_SCHEMA_VERSION = 2
LEGACY_TRACE_SCHEMA_VERSION = 1
SUPPORTED_TRACE_SCHEMA_VERSIONS = (LEGACY_TRACE_SCHEMA_VERSION,
                                   TRACE_SCHEMA_VERSION)

__all__ = [
    "DELIVERY_STATUSES",
    "EXECUTION_STATUSES",
    "LEGACY_TRACE_SCHEMA_VERSION",
    "PENDING_CONFLICT_RELATIONS",
    "TRACE_SCHEMA_VERSION",
    "TraceFormatError",
    "VALIDATOR_VERSION",
    "Violation",
    "classify_pending_relation",
    "load_trace",
    "main",
    "summarize",
    "validate_payload",
    "validate_trace",
]

# ---------------------------------------------------------------------------
# protocol vocabulary (frozen 2026-11-17 contract)
# ---------------------------------------------------------------------------

EXECUTION_STATUSES = frozenset({
    "prepared", "running", "completed", "rejected", "failed", "uncertain",
})
DELIVERY_STATUSES = frozenset({
    "pending_delivery", "delivered", "undelivered",
})
PENDING_STATUSES = frozenset({"prepared", "running"})
TERMINAL_STATUSES = frozenset({"completed", "rejected", "failed"})
DEFINITIVE_REJECTION_STATUSES = frozenset({"rejected", "failed"})

#: Relation classes of a write issued while another operation is pending.
#: Only ``independent`` and ``modal_resolution`` are allowed; the rest are
#: conflicts, and ``unknown`` is the conservative default when the trace does
#: not carry enough evidence to prove independence.
PENDING_CONFLICT_RELATIONS = frozenset({
    "same_op",
    "same_item",
    "run_changing",
    "modal_resolution",
    "independent",
    "unknown",
})
ALLOWED_PENDING_RELATIONS = frozenset({"independent", "modal_resolution"})

#: Operations that switch the run/timeline (mirrors Core/RunChangeOps). A write
#: that issues one of these is the only kind that may start a native run change.
RUN_CHANGE_OPS = frozenset({
    "new_game",
    "load_game",
    "load_recording_game",
    "confirm_new_game",
})

#: The ``run_change`` response field added in R5/A1. ``not_issued`` is the
#: authoritative fact that a confirmation/refusal did not start a run switch.
RUN_CHANGE_ISSUED = "issued"
RUN_CHANGE_NOT_ISSUED = "not_issued"
VALID_RUN_CHANGE_FIELDS = frozenset({RUN_CHANGE_ISSUED, RUN_CHANGE_NOT_ISSUED})

#: Ops the bridge scopes by arguments when blocking a duplicate native call
#: (mirrors Core/PendingScope): a second call with different arguments is a
#: different native call, everything else is blocked by op name. R5/B1 added
#: ``stage_negotiation_item`` (same item+args retry conflicts, a different item
#: is a different native call and therefore independent).
CALL_SCOPED_OPS = frozenset({"confirm_trade", "stage_negotiation_item"})

#: Writes that consume/resolve a native dialogue, prompt or modal input. They
#: are allowed while an unrelated operation is pending (that is what unblocks a
#: stuck workflow) but two of them contending for one input is a conflict.
MODAL_RESOLUTION_OPS = frozenset({
    "advance_dialogue",
    "choose_dialogue",
    "answer_prompt",
    "close_windows",
})

#: Argument keys that name one concrete item; shared ids mean a shared target.
ITEM_ARG_KEYS = ("item_id", "item")

#: Refusals that mean "a duplicate native call was already in flight". The
#: write was not dispatched and starts no operation of its own.
REFUSED_BY_PENDING_ERRORS = frozenset({
    "operation_pending",
    "run_change_pending",
})

#: ``wait_operation`` may surface a short ``status`` alias; map it onto the
#: execution axis instead of inventing a seventh status.
STATUS_ALIASES = {"pending": "running"}

OPERATION_TOOLS = frozenset({"get_operation", "wait_operation"})
READ_STATE_TOOLS = frozenset({"get_state", "get_run_summary"})

#: Error codes after which a write definitely did not complete, but the
#: caller cannot tell whether it *started*; treat as ``uncertain``.
UNCERTAIN_ERROR_CODES = frozenset({
    "outcome_unknown",
    "uncertain",
    "unknown",
    "timeout",
    "wait_timeout",
    "wait_failed",
    "bridge_unavailable",
    "transport_error",
    "dispatch_unknown",
    "dispatch_failed_unknown",
    "previous_response_lost",
    "outcome_unverified",
    "client_changed",
})

RENT_CLAIM_CODES = frozenset({"rent_paid", "paid_rent", "rent_settled"})
DAY_CLAIM_CODES = frozenset({"day_changed", "slept", "day_advanced"})
SAVE_CLAIM_CODES = frozenset({"saved", "save_completed"})
GENERIC_SUCCESS_CLAIM_CODES = frozenset({
    "trade_completed", "operation_completed", "completed", "success",
    "succeeded",
})

RENT_RECEIPT_KEYS = (
    "last_rent_payment",
    "rent_receipt",
    "settlement_event",
    "rent_payment",
)
SAVE_FILE_EVIDENCE_FIELDS = (
    "save_file",
    "saved_file",
    "save_path",
    "file_written",
    "bytes_written",
)
SAVE_EVIDENCE_COMPLETED = "save_completed"
SAVE_EVIDENCE_UNKNOWN = "save_unknown"
SUCCESS_TEXT_RE = re.compile(
    r"(?:succeed(?:ed|s)?|success(?:ful(?:ly)?)?|completed?|paid|settled|"
    r"sold|bought|done)",
    re.IGNORECASE,
)
RENT_TEXT_RE = re.compile(
    r"(?:rent).{0,40}(?:paid|pay|settl|success|done|complete)|"
    r"(?:paid|pay|settl).{0,40}(?:rent)",
    re.IGNORECASE,
)


class TraceFormatError(ValueError):
    """The payload is not a trace at all (list / {"steps": list})."""


@dataclass(frozen=True)
class Violation:
    """One structured protocol violation."""

    code: str
    message: str
    step_index: int
    severity: str = "error"
    details: dict = field(default_factory=dict)

    def to_dict(self):
        return {
            "code": self.code,
            "message": self.message,
            "step_index": self.step_index,
            "severity": self.severity,
            "details": dict(self.details),
        }


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------

def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False, default=str)


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def _numeric(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _normalize_status(raw):
    """Return (status | None, invalid_raw | None)."""
    if raw is None:
        return None, None
    if not isinstance(raw, str):
        return None, raw
    if raw in EXECUTION_STATUSES:
        return raw, None
    if raw in STATUS_ALIASES:
        return STATUS_ALIASES[raw], None
    return None, raw


def _step_tool(step):
    tool = step.get("tool")
    return tool if isinstance(tool, str) and tool.strip() else None


def _step_args(step):
    args = step.get("args")
    return args if isinstance(args, dict) else None


def _step_result(step):
    result = step.get("result")
    return result if isinstance(result, dict) else None


def _declared_schema_version(payload):
    """Return ``(version, declared)`` for a trace payload.

    A bare step list or an object without ``schema_version`` is legacy v1;
    ``declared`` is False in that case so the validator can warn when the
    trade verdict would differ under the current rule.
    """
    if isinstance(payload, dict) and "schema_version" in payload:
        return payload.get("schema_version"), True
    return LEGACY_TRACE_SCHEMA_VERSION, False


def _valid_schema_version(raw):
    return _is_int(raw) and raw in SUPPORTED_TRACE_SCHEMA_VERSIONS


def _business_signature(tool, args):
    """op + args identity used to detect retries (rev/action_id excluded)."""
    filtered = {key: value for key, value in args.items()
                if key not in ("action_id", "rev")}
    return tool, _canonical(filtered)


def _effective_op(tool, args):
    """Resolve the generic ``act`` wrapper to the inner op it carries."""
    if tool == "act":
        inner = args.get("action")
        if isinstance(inner, str) and inner.strip():
            return inner
    return tool


def _item_ids(args):
    """The concrete item identities named by one write's arguments."""
    ids = set()
    for key in ITEM_ARG_KEYS:
        value = args.get(key)
        if _is_int(value):
            ids.add((key, value))
        elif isinstance(value, str) and value.strip():
            ids.add((key, value))
    return frozenset(ids)


def classify_pending_relation(pending, write):
    """Classify one write issued while another operation is pending.

    ``pending``/``write`` are dicts with at least ``tool`` and ``args`` (a
    write may also carry ``rev``/``action_id``). Returns
    ``(relation, conflicting, reason)`` where ``relation`` is one of
    ``PENDING_CONFLICT_RELATIONS``. Only ``independent`` (a call-scoped trade on
    provably different items) and ``modal_resolution`` (a dialogue/prompt write
    that can unblock an unrelated pending op) are allowed; insufficient
    evidence is always ``unknown`` and conflicts, never "allow everything".

    An exact idempotent replay (same action id, args and rev) is handled by the
    caller before this classifier is consulted.
    """
    pending_args = pending.get("args")
    if not isinstance(pending_args, dict):
        pending_args = {}
    write_args = write.get("args")
    if not isinstance(write_args, dict):
        write_args = {}
    pending_tool = pending.get("tool") or "?"
    write_tool = write.get("tool") or "?"
    pending_op = _effective_op(pending_tool, pending_args)
    write_op = _effective_op(write_tool, write_args)

    if pending_op in RUN_CHANGE_OPS or write_op in RUN_CHANGE_OPS:
        return "run_changing", True, "run_change_in_flight"
    if write_op in MODAL_RESOLUTION_OPS:
        if pending_op in MODAL_RESOLUTION_OPS:
            return "modal_resolution", True, "dialogue_input_shared"
        return "modal_resolution", False, "resolves_blocking_modal"
    if pending_op in MODAL_RESOLUTION_OPS:
        return "unknown", True, "pending_modal_input"

    pending_items = _item_ids(pending_args)
    write_items = _item_ids(write_args)
    if pending_items & write_items:
        return "same_item", True, "same_item"
    if write_tool == pending_tool:
        if (_business_signature(write_tool, write_args)
                == _business_signature(pending_tool, pending_args)):
            if (pending.get("action_id") is not None
                    and pending.get("action_id") == write.get("action_id")
                    and pending.get("rev") != write.get("rev")):
                return "same_op", True, "different_rev"
            return "same_op", True, "different_action_id"
        if pending_tool in CALL_SCOPED_OPS:
            if pending_items and write_items:
                return "independent", False, "different_item_trade"
            return "unknown", True, "cannot_prove_different_item"
        return "same_op", True, "same_op_native_call"
    if pending_items or write_items:
        return "unknown", True, "item_scope_unproven"
    return "unknown", True, "different_operation_unproven"


def _looks_like_success_text(text):
    return bool(isinstance(text, str) and SUCCESS_TEXT_RE.search(text))


def _cash_movement(result):
    """Did this trade move cash? ``True``/``False`` when readable, else None.

    ``cash_moved`` is the canonical R5 field; ``cash_changed`` is the R4
    compatibility alias and the before/after pair is the last fallback.
    """
    moved = result.get("cash_moved")
    if isinstance(moved, bool):
        return moved
    changed = result.get("cash_changed")
    if isinstance(changed, bool):
        return changed
    before = result.get("cash_before")
    after = result.get("cash_after")
    if _numeric(before) and _numeric(after):
        return before != after
    return None


def _evidence_cash_movement(evidence):
    moved = evidence.get("cash_moved")
    if isinstance(moved, bool):
        return moved
    before = evidence.get("cash_before")
    after = evidence.get("cash_after")
    if _numeric(before) and _numeric(after):
        return before != after
    return None


def _trade_evidence_v1(result):
    """Legacy (schema v1) per-trade evidence problems.

    Returns a list of ``{"reason": ..., "message": ...}`` dicts; empty means
    the completion is backed by the old rule (whole-visit evidence).
    """
    business = result.get("business_complete")
    unchanged = result.get("client_unchanged")
    evidence = result.get("evidence")
    if business is not True or unchanged is not True:
        return [{
            "reason": "business_or_client_not_confirmed",
            "message": "confirm_trade is presented as completed but"
                       " business_complete=%r / client_unchanged=%r; both are"
                       " required by the legacy v1 rule"
                       % (business, unchanged),
            "business_complete": business,
            "client_unchanged": unchanged,
        }]
    if not isinstance(evidence, dict) or evidence.get("business_complete") is not True:
        return [{
            "reason": "evidence_object_missing",
            "message": "confirm_trade reports business_complete but the"
                       " 'evidence' object is missing or does not confirm it",
        }]
    return []


def _trade_evidence_v2(result):
    """R5 per-trade evidence problems (schema v2).

    Two different facts must not be mixed: ``business_complete`` /
    ``visit_complete`` describe the whole visit, while this trade needs the
    same client plus an item transfer OR a cash movement, corroborated by the
    ``evidence`` object. Returns a list of ``{"reason", "message"}`` dicts.
    """
    problems = []
    unchanged = result.get("client_unchanged")
    items = result.get("items_transferred")
    cash = _cash_movement(result)
    evidence = result.get("evidence")

    if unchanged is not True:
        problems.append({
            "reason": "client_not_unchanged",
            "message": "confirm_trade is presented as completed but"
                       " client_unchanged=%r; another client's state may not"
                       " back this trade" % (unchanged,),
        })
    if items is not True and cash is not True:
        problems.append({
            "reason": "no_trade_effect",
            "message": "confirm_trade is presented as completed but"
                       " items_transferred=%r / cash_moved=%r: the whole-visit"
                       " business_complete/visit_complete attribution cannot"
                       " prove this trade; a partial trade needs an item"
                       " transfer or a cash movement"
                       % (items, result.get("cash_moved")),
        })
    if not isinstance(evidence, dict):
        problems.append({
            "reason": "evidence_object_missing",
            "message": "confirm_trade completion has no 'evidence' object to"
                       " corroborate the per-trade facts",
        })
    else:
        if evidence.get("called") is not True:
            problems.append({
                "reason": "evidence_not_called",
                "message": "confirm_trade 'evidence.called' is not true; a"
                           " trade that was not dispatched cannot be completed",
            })
        if evidence.get("client_unchanged") is not True:
            problems.append({
                "reason": "evidence_client_mismatch",
                "message": "confirm_trade 'evidence.client_unchanged' is not"
                           " true; the evidence object contradicts the"
                           " completion",
            })
        if items is True and evidence.get("items_transferred") is not True:
            problems.append({
                "reason": "evidence_items_mismatch",
                "message": "confirm_trade claims items_transferred:true but"
                           " 'evidence.items_transferred' is %r"
                           % (evidence.get("items_transferred"),),
            })
        if cash is True and _evidence_cash_movement(evidence) is False:
            problems.append({
                "reason": "evidence_cash_mismatch",
                "message": "confirm_trade claims cash movement but"
                           " 'evidence' reports cash_before == cash_after",
            })
    return problems


def _find_rent_receipts(result):
    """Receipt-like objects carried by an MCP result payload."""
    receipts = []
    for key in RENT_RECEIPT_KEYS:
        value = result.get(key)
        if isinstance(value, dict) and value:
            receipts.append(value)
    rent = result.get("rent")
    if isinstance(rent, dict):
        payment = rent.get("last_payment")
        if isinstance(payment, dict) and payment:
            receipts.append(payment)
    return receipts


def _receipt_problem(receipt):
    """Classify a receipt as ok / invalid / inconsistent.

    Returns ``(verdict, reason)`` where verdict is one of ``ok``, ``invalid``,
    ``inconsistent``.
    """
    if not isinstance(receipt, dict):
        return "invalid", "receipt_invalid"
    receipt_id = None
    for key in ("id", "receipt_id", "settlement_id", "event_id"):
        value = receipt.get(key)
        if isinstance(value, str) and value.strip():
            receipt_id = value
            break
    amount = receipt.get("amount")
    if receipt_id is None or not _numeric(amount) or amount <= 0:
        return "invalid", "receipt_invalid"
    if receipt.get("detection_complete") is False:
        return "inconsistent", "receipt_incomplete"

    cash_delta = receipt.get("cash_delta")
    cash_before = receipt.get("cash_before")
    cash_after = receipt.get("cash_after")
    if _numeric(cash_delta) and cash_delta > 0:
        return "inconsistent", "receipt_cash_delta_positive"
    if _numeric(cash_delta) and _numeric(cash_before) and _numeric(cash_after):
        if cash_before - cash_after != -cash_delta:
            return "inconsistent", "receipt_cash_mismatch"
    days_after = receipt.get("rent_days_left_after")
    if _numeric(days_after) and days_after <= 0:
        return "inconsistent", "receipt_days_not_reset"
    return "ok", None


def _state_payload(result):
    """Extract a full state snapshot from a read result (direct or nested).

    ``get_run_summary`` also carries ``day`` but not the nested ``rent``
    projection; it is deliberately not accepted as rent evidence.
    """
    if not isinstance(result, dict):
        return None
    if "in_game" in result:
        return result
    if "day" in result and "rent" in result:
        return result
    nested = result.get("state")
    if isinstance(nested, dict):
        return nested
    return None


def _state_rent(state):
    rent = state.get("rent") if isinstance(state, dict) else None
    return rent if isinstance(rent, dict) else None


def _rent_counter(rent, key):
    value = rent.get(key) if isinstance(rent, dict) else None
    return value if _numeric(value) else 0


# ---------------------------------------------------------------------------
# scan state
# ---------------------------------------------------------------------------

@dataclass
class _Operation:
    key: str
    tool: str
    signature: tuple
    rev: object
    action_id: object
    operation_id: object
    execution: object
    delivery: object
    last_index: int
    args: dict = field(default_factory=dict)


class _ScanState:
    def __init__(self, schema_version=LEGACY_TRACE_SCHEMA_VERSION,
                 schema_version_declared=False):
        self.violations = []
        self.operations = {}
        self.pending = {}
        self.uncertain = {}
        self.by_action_id = {}
        self.query_since = {}
        self.states = []          # (step_index, state dict)
        self.claims = []          # (step_index, step, claim set, claim_text)
        self.rejected_steps = set()
        self.last_index = -1
        self.schema_version = schema_version
        self.schema_version_declared = schema_version_declared
        self.unconfirmed_run_change = None

    # -- helpers ---------------------------------------------------------
    def add(self, code, message, step_index, severity="error", **details):
        self.violations.append(Violation(code, message, step_index, severity,
                                         details))

    def _record_for(self, action_id, operation_id):
        keys = []
        if operation_id is not None:
            for key, record in self.operations.items():
                if record.operation_id == operation_id:
                    keys.append(key)
        if action_id is not None:
            for key, record in self.operations.items():
                if record.action_id == action_id:
                    keys.append(key)
        return keys

    # -- per-step observation -------------------------------------------
    def observe(self, index, step):
        self.last_index = index
        tool = _step_tool(step)
        args = _step_args(step)
        result = _step_result(step)

        if tool is None:
            self.add("INVALID_TRACE_STEP", "step has no string 'tool' field",
                     index, severity="error")
            return
        if args is None:
            self.add("INVALID_TRACE_STEP",
                     "step %r has a non-dict 'args' field" % tool, index)
            args = {}
        if result is None:
            self.add("INVALID_TRACE_STEP",
                     "step %r has a non-dict 'result' field" % tool, index)
            result = {}

        if tool in OPERATION_TOOLS:
            self._observe_operation_query(index, tool, args, result)
            return

        if self._is_write(tool, args, result, step):
            self._observe_write(index, step, tool, args, result)
            return

        self._observe_read(index, tool, step, result)

    @staticmethod
    def _is_write(tool, args, result, step):
        if tool in READ_STATE_TOOLS:
            return False
        if "rev" in args or "action_id" in args:
            return True
        if "dispatched" in result:
            return True
        if "execution_status" in result:
            return True
        return False

    def _observe_read(self, index, tool, step, result):
        status, invalid_raw = _normalize_status(result.get("execution_status"))
        if invalid_raw is not None:
            self.add("INVALID_EXECUTION_STATUS",
                     "read step carries unknown execution_status %r"
                     % (invalid_raw,), index)
        if status is not None:
            self.add(
                "STATUS_NOT_FROM_OPERATION",
                "execution_status on read tool %r is not operation evidence;"
                " only GET /operation (via get_operation/wait_operation) and"
                " write responses may report the two axes" % tool,
                index)
        payload = _state_payload(result)
        if payload is not None:
            self.states.append((index, payload))
        self._collect_claims(index, step)

    # -- write handling --------------------------------------------------
    def _observe_write(self, index, step, tool, args, result):
        action_id = result.get("action_id") or args.get("action_id")
        operation_id = result.get("operation_id")
        rev = args.get("rev")
        if rev is None:
            rev = step.get("rev")
        signature = _business_signature(tool, args)

        if action_id is None:
            self.add("MISSING_ACTION_ID",
                     "write %r has no action_id; the idempotency key is"
                     " missing from the trace" % tool, index)
        if action_id is not None:
            previous = self.by_action_id.get(action_id)
            if previous is not None and (previous.tool, previous.signature) != (
                    tool, signature):
                self.add(
                    "ACTION_ID_REUSED_FOR_DIFFERENT_OPERATION",
                    "action_id %r was already used for %r %r"
                    % (action_id, previous.tool, previous.signature[1]),
                    index,
                    action_id=action_id,
                    previous_tool=previous.tool,
                    current_tool=tool)

        # A duplicate-call refusal (operation_pending / run_change_pending) was
        # not dispatched: its status/dispatched fields describe the *blocking*
        # operation, and it starts no operation of its own. Judge the blind-retry
        # attempt, never the blocker's axis.
        refused_by_pending = (
            result.get("error") in REFUSED_BY_PENDING_ERRORS
            and (result.get("blocking_operation_id") is not None
                 or result.get("blocking_same_call") is True))

        status, invalid_raw = _normalize_status(result.get("execution_status"))
        delivery = result.get("delivery_status")
        if invalid_raw is not None:
            self.add("INVALID_EXECUTION_STATUS",
                     "write %r returned unknown execution_status %r"
                     % (tool, invalid_raw), index)
        if delivery is not None and delivery not in DELIVERY_STATUSES:
            self.add("INVALID_DELIVERY_STATUS",
                     "write %r returned unknown delivery_status %r"
                     % (tool, delivery), index)
        if status is not None and delivery is None:
            self.add("MISSING_DELIVERY_STATUS",
                     "write %r reports execution_status %r but no"
                     " delivery_status axis" % (tool, status),
                     index, severity="warning")

        # Frozen D contract: `dispatched` must agree with the execution axis,
        # and a re-delivered cached response keeps the undelivered axis.
        dispatched = None if refused_by_pending else result.get("dispatched")
        if dispatched is not None and not isinstance(dispatched, bool):
            self.add("INVALID_DISPATCHED",
                     "write %r returned non-boolean dispatched=%r"
                     % (tool, dispatched), index, severity="warning")
        if status == "rejected" and dispatched is True:
            self.add("REJECTED_WITH_DISPATCHED_TRUE",
                     "write %r is rejected but reports dispatched:true; a"
                     " pre-dispatch refusal must not claim dispatch" % tool,
                     index)
        if status == "completed" and dispatched is False:
            self.add("COMPLETED_WITHOUT_DISPATCH",
                     "write %r is completed but reports dispatched:false"
                     % tool, index)
        if (result.get("redelivered") is True
                and delivery != "undelivered"):
            self.add("REDELIVERED_WITHOUT_UNDELIVERED",
                     "write %r reports redelivered:true but"
                     " delivery_status=%r; a cached re-delivery must keep the"
                     " undelivered axis" % (tool, delivery),
                     index, severity="warning")

        # Rule 3 (R5/A4): classify the conflict relation instead of treating
        # every write while pending as the same violation.
        conflict = self._pending_write_conflict(tool, args, action_id, rev,
                                                signature)
        if conflict is not None:
            message, details = conflict
            self.add("CONFLICTING_WRITE_WHILE_PENDING", message, index,
                     **details)

        # Rules 1/2: uncertain retries must reuse the original action_id.
        for key, uncertain in list(self.uncertain.items()):
            if uncertain.signature != signature:
                continue
            if action_id is not None and uncertain.action_id == action_id:
                continue  # correct recovery: idempotent replay
            blind = self.query_since.get(key) is None
            self.add(
                "UNCERTAIN_RETRY_WITH_NEW_ACTION_ID",
                "operation %r is uncertain but %r was retried with a new"
                " action_id %r; reuse the original action_id or query"
                " get_operation/wait_operation with the original"
                " operation_id" % (key, tool, action_id),
                index,
                operation=key,
                original_action_id=uncertain.action_id,
                retried_action_id=action_id,
                blind=blind)
            if blind:
                self.add(
                    "MISSING_OPERATION_QUERY",
                    "no get_operation/wait_operation query was recorded"
                    " between the uncertain result and the retry of %r"
                    % tool,
                    index, severity="warning", operation=key)
            break

        ok = result.get("ok")
        claims = self._claim_set(step)
        self._check_status_claims(index, step, result, status, operation_id)
        if ok is True and status is not None and status != "completed":
            self.add(
                "OK_WITHOUT_COMPLETED_EXECUTION",
                "write %r returned ok:true with execution_status=%r;"
                " ok:true only means completed" % (tool, status),
                index, execution_status=status)
        if ok is True and status is None and operation_id is None:
            self.add(
                "MISSING_OPERATION_EVIDENCE",
                "write %r returned ok:true without operation_id or"
                " execution_status; the completion claim cannot be proven"
                % tool, index, severity="warning")

        # Infer the execution axis when a response omits it (legacy shape or a
        # generic refusal). A `pending:true` refusal is a running operation and
        # must never be misread as a rejection.
        execution = status
        error = result.get("error")
        if refused_by_pending:
            execution = "rejected"
        elif execution is None:
            if result.get("pending") is True:
                execution = "running"
            elif ok is False:
                if error in UNCERTAIN_ERROR_CODES:
                    execution = "uncertain"
                elif result.get("dispatched") is True:
                    execution = "failed"
                else:
                    execution = "rejected"
            elif error in UNCERTAIN_ERROR_CODES:
                execution = "uncertain"

        # D3: end_day is completed only when the readback shows the shop CLOSED.
        if (tool == "end_day" and execution == "completed"
                and result.get("shop_closed_after") is not True):
            self.add(
                "END_DAY_EVIDENCE_MISMATCH",
                "end_day reports completed but shop_closed_after=%r;"
                " completion requires the shop to be CLOSED"
                % (result.get("shop_closed_after"),), index)

        # C (R5/A4): a whole-visit completion is not a single-trade fact.
        self._check_trade_evidence(index, tool, result, ok, execution, claims)

        rejected = (execution in DEFINITIVE_REJECTION_STATUSES
                    or (ok is False and execution == "completed")
                    or (ok is False and status is None
                        and execution == "rejected"))
        if rejected:
            success_fields = (
                status == "completed"
                or result.get("completed") is True
                or result.get("claimed_success") is True
            )
            if success_fields:
                self.add(
                    "REJECTED_WITH_SUCCESS_FIELDS",
                    "step %r is rejected/failed but carries success fields"
                    % tool, index)
            if claims & GENERIC_SUCCESS_CLAIM_CODES:
                self.add(
                    "REJECTED_CLAIMED_AS_SUCCESS",
                    "rejected step %r claims %s; a refusal may not be"
                    " reported as success" % (tool, sorted(claims)),
                    index, claims=sorted(claims))
            claim_text = step.get("claim_text")
            if _looks_like_success_text(claim_text):
                self.add(
                    "REJECTED_CLAIMED_AS_SUCCESS",
                    "rejected step %r is described as success in model text"
                    % tool, index)
            self.rejected_steps.add(index)

        # Rule 13: dependencies on rejected steps.
        for dependency in self._dependency_indexes(step):
            if dependency in self.rejected_steps:
                self.add(
                    "REJECTED_DEPENDENCY_TREATED_AS_SUCCESS",
                    "step %r depends on rejected/failed step %d as if it"
                    " had succeeded" % (tool, dependency),
                    index, depends_on=dependency)

        if not refused_by_pending:
            self._track_run_change(index, tool, args, result, action_id,
                                   operation_id, signature, rev)
            # Store / update the operation record. A duplicate-call refusal is
            # terminal and starts nothing, so it must not create a phantom
            # pending operation (that would poison later conflict checks).
            key = action_id
            if key is None:
                key = "opid:%s" % operation_id
            record = _Operation(
                key=key, tool=tool, signature=signature, rev=rev,
                action_id=action_id, operation_id=operation_id,
                execution=execution, delivery=delivery, last_index=index,
                args=dict(args))
            self.operations[key] = record
            if action_id is not None:
                self.by_action_id[action_id] = record
            self._apply_operation_state(record)
        self._collect_claims(index, step)

    def _pending_write_conflict(self, tool, args, action_id, rev, signature):
        """Rule 3: return ``(message, details)`` for a conflicting write."""
        for pending in list(self.pending.values()):
            same_attempt = (
                action_id is not None
                and pending.action_id == action_id
                and pending.signature == signature
                and pending.rev == rev)
            if same_attempt:
                continue
            relation, conflicting, reason = classify_pending_relation(
                {"tool": pending.tool, "args": pending.args,
                 "rev": pending.rev, "action_id": pending.action_id},
                {"tool": tool, "args": args, "rev": rev,
                 "action_id": action_id})
            if not conflicting:
                continue
            return (
                "write %r issued while operation %r is pending"
                " (relation=%s, reason=%s); only reads, a modal-resolution"
                " write or an idempotent replay of the same action_id are"
                " allowed" % (tool, pending.key, relation, reason),
                {"pending_operation": pending.key,
                 "pending_op": pending.tool,
                 "relation": relation,
                 "reason": reason})
        unconfirmed = self.unconfirmed_run_change
        if unconfirmed is not None:
            replay = (
                action_id is not None
                and unconfirmed.get("action_id") == action_id
                and unconfirmed.get("signature") == signature
                and unconfirmed.get("rev") == rev)
            if not replay:
                return (
                    "write %r issued while a run change is requested but not"
                    " confirmed (op %r from step %r); only reads and"
                    " get_operation/wait_operation queries are safe until"
                    " run_change_status=confirmed"
                    % (tool, unconfirmed.get("tool"),
                       unconfirmed.get("step_index")),
                    {"pending_operation": unconfirmed.get("key"),
                     "pending_op": unconfirmed.get("tool"),
                     "relation": "run_changing",
                     "reason": "run_change_unconfirmed"})
        return None

    def _track_run_change(self, index, tool, args, result, action_id,
                          operation_id, signature, rev):
        """Track a run change that was requested but is not confirmed yet.

        R5/A1: ``run_change`` is the authoritative additive field. A first-run
        prompt confirmation (``prompt_confirmed:true``) is a UI operation, not
        a native run start, so it never registers a run change -- even when a
        pre-A1 bridge left a stale ``run_change_status:"requested"`` on it.
        """
        raw = result.get("run_change")
        if raw is not None and raw not in VALID_RUN_CHANGE_FIELDS:
            self.add(
                "INVALID_RUN_CHANGE_FIELD",
                "write %r returned run_change=%r; expected %r or %r"
                % (tool, raw, RUN_CHANGE_ISSUED, RUN_CHANGE_NOT_ISSUED),
                index, run_change=raw)
        if raw == RUN_CHANGE_ISSUED:
            run_change = True
        elif raw == RUN_CHANGE_NOT_ISSUED:
            run_change = False
        else:
            run_change = (_effective_op(tool, args) in RUN_CHANGE_OPS
                          and result.get("prompt_confirmed") is not True)
        status = result.get("run_change_status")
        confirmed = result.get("run_change_confirmed")
        if confirmed is True or status == "confirmed":
            self.unconfirmed_run_change = None
            return
        if not run_change:
            return
        if status in ("requested", "unknown") or confirmed is False:
            self.unconfirmed_run_change = {
                "key": action_id or operation_id,
                "tool": tool,
                "signature": signature,
                "action_id": action_id,
                "rev": rev,
                "step_index": index,
                "status": status,
            }

    def _check_trade_evidence(self, index, tool, result, ok, execution, claims):
        """Rule 17: per-trade evidence, versioned by the trace schema.

        v1 (legacy only) judged a completion by ``business_complete``; v2
        judges it by this trade's own item/cash movement. A legacy trace whose
        verdict differs under the current rule gets a
        ``TRACE_SCHEMA_VERSION_MISSING`` warning so it is never silently
        reinterpreted.
        """
        if tool != "confirm_trade":
            return
        if not (ok is True or execution == "completed"
                or "trade_completed" in claims):
            return
        v1_problems = _trade_evidence_v1(result)
        v2_problems = _trade_evidence_v2(result)
        problems = v1_problems if self.schema_version == 1 else v2_problems
        for problem in problems:
            details = dict(problem)
            reason = details.pop("reason")
            message = details.pop("message")
            self.add("TRADE_EVIDENCE_MISSING", message, index,
                     reason=reason, **details)
        if (self.schema_version < TRACE_SCHEMA_VERSION
                and not self.schema_version_declared
                and bool(v1_problems) != bool(v2_problems)):
            self.add(
                "TRACE_SCHEMA_VERSION_MISSING",
                "this legacy (unversioned) trace contains a confirm_trade"
                " whose verdict differs under the current schema %d trade"
                " rule; re-export it with schema_version:%d instead of"
                " reading it under the old rule"
                % (TRACE_SCHEMA_VERSION, TRACE_SCHEMA_VERSION),
                index, severity="warning",
                suggested_schema_version=TRACE_SCHEMA_VERSION,
                rule="trade_evidence")

    def _check_status_claims(self, index, step, result, status, operation_id):
        claim_text = step.get("claim_text")
        source = step.get("status_source")
        if source is None:
            source = result.get("status_source")
        if source in ("model_text", "model", "claim", "self_report"):
            self.add(
                "MODEL_SELF_REPORT_IS_NOT_EVIDENCE",
                "status_source=%r; only bridge operation state is evidence"
                % (source,), index)
            return
        claims = self._claim_set(step)
        has_evidence = status is not None or operation_id is not None
        if not has_evidence and (claims & GENERIC_SUCCESS_CLAIM_CODES
                                 or _looks_like_success_text(claim_text)):
            self.add(
                "MODEL_SELF_REPORT_IS_NOT_EVIDENCE",
                "success is claimed from model text/annotation without any"
                " operation_id or execution_status evidence",
                index)
        elif has_evidence and claims & GENERIC_SUCCESS_CLAIM_CODES:
            if status is not None and status != "completed":
                self.add(
                    "MODEL_SELF_REPORT_IS_NOT_EVIDENCE",
                    "success is claimed while execution_status=%r; model"
                    " text cannot override the operation axis" % status,
                    index)
        elif has_evidence and _looks_like_success_text(claim_text):
            if status is not None and status != "completed":
                self.add(
                    "MODEL_SELF_REPORT_IS_NOT_EVIDENCE",
                    "model text describes success while execution_status=%r"
                    % status, index)

    @staticmethod
    def _claim_set(step):
        raw = step.get("claims")
        if isinstance(raw, str):
            raw = [raw]
        if not isinstance(raw, (list, tuple)):
            return set()
        return {item for item in raw if isinstance(item, str)}

    @staticmethod
    def _dependency_indexes(step):
        indexes = []
        raw = step.get("depends_on")
        if isinstance(raw, int) and not isinstance(raw, bool):
            indexes.append(raw)
        elif isinstance(raw, (list, tuple)):
            indexes.extend(item for item in raw if _is_int(item))
        assumed = step.get("assumes_success_of")
        if _is_int(assumed):
            indexes.append(assumed)
        return indexes

    def _collect_claims(self, index, step):
        claims = self._claim_set(step)
        claim_text = step.get("claim_text")
        if claims or isinstance(claim_text, str):
            self.claims.append((index, step, claims,
                                claim_text if isinstance(claim_text, str)
                                else ""))

    # -- operation query handling ---------------------------------------
    def _observe_operation_query(self, index, tool, args, result):
        operation_id = result.get("operation_id") or args.get("operation_id")
        action_id = result.get("action_id") or args.get("action_id")
        status, invalid_raw = _normalize_status(result.get("execution_status"))
        short = result.get("status")
        if status is None and short is not None:
            status, invalid_raw = _normalize_status(short)
        delivery = result.get("delivery_status")
        if invalid_raw is not None:
            self.add("INVALID_EXECUTION_STATUS",
                     "%s returned unknown status %r" % (tool, invalid_raw),
                     index)
        if delivery is not None and delivery not in DELIVERY_STATUSES:
            self.add("INVALID_DELIVERY_STATUS",
                     "%s returned unknown delivery_status %r"
                     % (tool, delivery), index)
        if delivery is None and status is not None:
            self.add("MISSING_DELIVERY_STATUS",
                     "%s reports status %r without delivery_status"
                     % (tool, status), index, severity="warning")

        if (result.get("ok") is True and status is not None
                and status != "completed"):
            self.add(
                "OK_WITHOUT_COMPLETED_EXECUTION",
                "%s returned ok:true with execution_status=%r; ok:true only"
                " means completed" % (tool, status), index)
        if (tool == "wait_operation" and result.get("ok") is False
                and result.get("status") == "pending"
                and (operation_id is None or not result.get("next"))):
            self.add(
                "WAIT_OPERATION_TIMEOUT_MISSING_NEXT",
                "wait_operation timed out without operation_id/next; a"
                " timeout must never look like a success or a dead end",
                index, operation_id=operation_id)

        keys = self._record_for(action_id, operation_id)
        # Unknown ids (lost/restarted bridge or run change) are reported via
        # error=="unknown_operation", known:false and/or process_restarted.
        unknown_operation = (result.get("error") == "unknown_operation"
                             or result.get("known") is False
                             or result.get("process_restarted") is True)
        resolved = (unknown_operation
                    or status in TERMINAL_STATUSES)
        if (unknown_operation
                or result.get("run_change_confirmed") is True
                or result.get("run_change_status") == "confirmed"):
            self.unconfirmed_run_change = None
        if unknown_operation:
            # A restarted bridge lost the in-memory record: the previous
            # operation is definitively unobservable, never auto-replayed.
            for key in keys:
                self.pending.pop(key, None)
                self.uncertain.pop(key, None)
            return
        for key in keys:
            self.query_since[key] = index
            record = self.operations.get(key)
            if record is not None and status is not None:
                record.execution = status
                record.delivery = delivery
                record.last_index = index
            if resolved:
                self.pending.pop(key, None)
                self.uncertain.pop(key, None)
        if status is None and not keys:
            return
        if operation_id is None and status is not None and not keys:
            self.add("UNKNOWN_OPERATION_QUERY",
                     "%s reports a status without an operation_id" % tool,
                     index, severity="warning")

    # -- operation state transitions ------------------------------------
    def _apply_operation_state(self, record):
        key = record.key
        execution = record.execution
        if execution in PENDING_STATUSES:
            self.pending[key] = record
            return
        self.pending.pop(key, None)
        if execution == "uncertain":
            self.uncertain[key] = record
        else:
            self.uncertain.pop(key, None)

    # -- deferred claim evidence ----------------------------------------
    def finish(self):
        for index, step, claims, claim_text in self.claims:
            tool = _step_tool(step) or "?"
            result = _step_result(step) or {}
            rent_claim = bool(claims & RENT_CLAIM_CODES)
            if not rent_claim and RENT_TEXT_RE.search(claim_text or ""):
                rent_claim = True
            if rent_claim:
                self._check_rent_claim(index, tool, step, result, claims,
                                       claim_text)
            if claims & DAY_CLAIM_CODES:
                self._check_day_claim(index, tool, step, result)
            if claims & SAVE_CLAIM_CODES:
                self._check_save_claim(index, tool, result)

        for key, record in self.uncertain.items():
            self.add(
                "UNRESOLVED_UNCERTAIN_OPERATION",
                "trace ends with operation %r still uncertain; query"
                " get_operation/wait_operation (never retry with a new"
                " action_id)" % key,
                self.last_index, severity="warning", operation=key)
        for key, record in self.pending.items():
            self.add(
                "UNRESOLVED_PENDING_OPERATION",
                "trace ends while operation %r is still pending" % key,
                self.last_index, severity="warning", operation=key)

        self.violations.sort(key=lambda row: (row.step_index, row.code))
        return self.violations

    def _last_state_before(self, index):
        for state_index, state in reversed(self.states):
            if state_index < index:
                return state
        return None

    def _first_state_after(self, index):
        for state_index, state in self.states:
            if state_index > index:
                return state
        return None

    @staticmethod
    def _operation_status_for(index, result):
        status, _ = _normalize_status(result.get("execution_status"))
        return status

    def _check_rent_claim(self, index, tool, step, result, claims, claim_text):
        exec_status = self._operation_status_for(index, result)
        receipts = _find_rent_receipts(result)
        verdicts = [(_receipt_problem(receipt)) for receipt in receipts]
        valid_receipts = [receipt for receipt, (verdict, _)
                          in zip(receipts, verdicts) if verdict == "ok"]

        baseline = self._last_state_before(index)
        after = self._first_state_after(index)
        baseline_rent = _state_rent(baseline) if baseline is not None else None
        after_rent = _state_rent(after) if after is not None else None

        if valid_receipts:
            return
        if exec_status in PENDING_STATUSES or exec_status == "uncertain":
            self.add(
                "RENT_EVIDENCE_MISSING",
                "rent payment claimed before the operation reached a"
                " terminal status (execution_status=%r)" % exec_status,
                index, reason="operation_not_terminal", tool=tool)
            return
        if after_rent is not None:
            missed_after = _rent_counter(after_rent, "rent_weeks_missed")
            missed_before = (_rent_counter(baseline_rent, "rent_weeks_missed")
                             if baseline_rent is not None else 0)
            if missed_after > missed_before:
                self.add(
                    "RENT_EVIDENCE_INCONSISTENT",
                    "rent is claimed as paid but rent_weeks_missed increased"
                    " from %r to %r; a missed week cannot be a paid week"
                    % (missed_before, missed_after),
                    index, tool=tool)
                return
            paid_increase = False
            for key in ("rent_paid_total", "rent_weeks_paid"):
                before = (_rent_counter(baseline_rent, key)
                          if baseline_rent is not None else 0)
                if _rent_counter(after_rent, key) > before:
                    paid_increase = True
            payment = after_rent.get("last_payment")
            if paid_increase or (isinstance(payment, dict)
                                 and _receipt_problem(payment)[0] == "ok"):
                return
        if any(verdict == "inconsistent" for verdict, _ in verdicts):
            self.add(
                "RENT_EVIDENCE_INCONSISTENT",
                "rent receipt is present but internally inconsistent;"
                " the payment cannot be verified", index, tool=tool)
            return
        if not claims and RENT_TEXT_RE.search(claim_text or ""):
            reason = "self_report_only"
        elif receipts:
            reason = "receipt_invalid"
        else:
            reason = "receipt_missing"
        self.add(
            "RENT_EVIDENCE_MISSING",
            "rent payment is claimed but no externally verifiable settlement"
            " evidence exists (need last_rent_payment/rent_receipt/"
            "settlement_event with id+amount, or a later state read showing"
            " rent_paid_total/rent_weeks_paid increased); model text is not"
            " evidence",
            index, reason=reason, tool=tool)

    def _check_day_claim(self, index, tool, step, result):
        exec_status = self._operation_status_for(index, result)
        readback = result.get("readback")
        if isinstance(readback, dict) and readback.get("readable") is not False:
            if readback.get("day_advanced") is True:
                return
            before = readback.get("day_before")
            after = readback.get("day_after")
            if _numeric(before) and _numeric(after) and after > before:
                return
        if result.get("day_changed") is True:
            return
        # A2: the read that observes the higher day is itself external evidence.
        # This is how an async sleep converges: not by poll counting, but by an
        # actual state read showing day_before < day_after.
        own_payload = _state_payload(result)
        if own_payload is not None:
            own_baseline = self._last_state_before(index)
            before_day = (own_baseline.get("day")
                          if isinstance(own_baseline, dict) else None)
            own_day = own_payload.get("day")
            if _numeric(before_day) and _numeric(own_day) and own_day > before_day:
                return
        if exec_status in PENDING_STATUSES or exec_status == "uncertain":
            self.add(
                "DAY_CHANGE_EVIDENCE_MISSING",
                "day change claimed while execution_status=%r; wait for the"
                " operation to complete before claiming the new day"
                % exec_status,
                index, reason="operation_not_terminal", tool=tool)
            return
        baseline = self._last_state_before(index)
        after = self._first_state_after(index)
        before_day = baseline.get("day") if isinstance(baseline, dict) else None
        after_day = after.get("day") if isinstance(after, dict) else None
        if _numeric(before_day) and _numeric(after_day) and after_day > before_day:
            return
        if isinstance(readback, dict) and readback.get("readable") is not False:
            reason = "day_not_advanced"
        elif isinstance(readback, dict):
            reason = "readback_unavailable"
        else:
            reason = "readback_missing"
        self.add(
            "DAY_CHANGE_EVIDENCE_MISSING",
            "day change is claimed but no external evidence exists (readable"
            " readback with day_advanced=true, or a later state read showing"
            " a higher day)",
            index, reason=reason, tool=tool)

    def _check_save_claim(self, index, tool, result):
        save_evidence = result.get("save_evidence")
        if (result.get("save_unknown") is True
                or save_evidence == SAVE_EVIDENCE_UNKNOWN):
            self.add(
                "SAVE_EVIDENCE_MISSING",
                "the save result is explicitly save_unknown; it may not be"
                " claimed as saved", index, reason="save_unknown", tool=tool)
            return
        if (result.get("save_completed") is True
                or save_evidence == SAVE_EVIDENCE_COMPLETED):
            return
        for field_name in SAVE_FILE_EVIDENCE_FIELDS:
            value = result.get(field_name)
            if field_name == "bytes_written" and _numeric(value) and value > 0:
                return
            if value is True and field_name == "file_written":
                return
            if isinstance(value, str) and value.strip():
                return
        if result.get("saved") is not True:
            reason = "save_not_reported"
        else:
            reason = "file_evidence_missing"
        self.add(
            "SAVE_EVIDENCE_MISSING",
            "save success is claimed without file evidence (save_file/"
            "save_path/file_written/bytes_written); a last_save_time change"
            " alone is not proof -- report save_unknown instead",
            index, reason=reason, tool=tool)


# ---------------------------------------------------------------------------
# public API
# ---------------------------------------------------------------------------

def validate_trace(trace):
    """Validate one trace (list of step dicts) and return violations."""
    return validate_payload(trace)


def validate_payload(payload):
    """Validate a trace payload: a versioned object, list or ``{"steps": ...}``."""
    if isinstance(payload, dict):
        steps = payload.get("steps")
        if not isinstance(steps, list):
            raise TraceFormatError(
                "trace object must contain a 'steps' list")
    elif isinstance(payload, list):
        steps = payload
    else:
        raise TraceFormatError(
            "trace must be a list of steps or an object with 'steps'")

    schema_version, declared = _declared_schema_version(payload)
    if declared and not _valid_schema_version(schema_version):
        return [Violation(
            "TRACE_SCHEMA_VERSION_UNSUPPORTED",
            "trace declares schema_version=%r; this validator supports"
            " schema_version %s (a missing version means the legacy"
            " schema %d); refusing to interpret the trace under a guessed"
            " rule"
            % (schema_version,
               " or ".join(str(v) for v in SUPPORTED_TRACE_SCHEMA_VERSIONS),
               LEGACY_TRACE_SCHEMA_VERSION),
            -1, details={"declared_schema_version": schema_version})]

    state = _ScanState(schema_version=schema_version,
                       schema_version_declared=declared)
    for index, raw in enumerate(steps):
        if not isinstance(raw, dict):
            state.add("INVALID_TRACE_STEP",
                      "step #%d is not an object (got %s)"
                      % (index, type(raw).__name__), index)
            continue
        state.observe(index, raw)
    return state.finish()


def summarize(violations):
    """Stable, JSON-serializable summary of a validation result."""
    error_count = sum(1 for row in violations if row.severity == "error")
    warning_count = sum(1 for row in violations if row.severity == "warning")
    return {
        "validator_version": VALIDATOR_VERSION,
        "ok": error_count == 0,
        "counts": {"error": error_count, "warning": warning_count},
        "codes": sorted({row.code for row in violations}),
    }


def load_trace(path):
    """Read a JSON trace from disk; raises OSError/ValueError on failure."""
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main(argv=None):
    """CLI entry point: validate a trace file, print JSON, return exit code."""
    parser = argparse.ArgumentParser(
        prog="trace_validator",
        description="Validate a PawnMind agent trace against the operation"
                    " protocol (offline, stdlib only).")
    parser.add_argument("trace_path", help="path to a JSON trace")
    args = parser.parse_args(argv)

    try:
        payload = load_trace(args.trace_path)
        violations = validate_payload(payload)
    except (OSError, ValueError) as exc:
        print(json.dumps({
            "ok": False,
            "validator_version": VALIDATOR_VERSION,
            "error": "unreadable_trace",
            "detail": str(exc),
        }, ensure_ascii=False))
        return 2

    summary = summarize(violations)
    report = dict(summary)
    # The schema version is part of the result: an unversioned trace is judged
    # by the legacy rule and must never be mistaken for a current one.
    report["trace_schema_version"] = _declared_schema_version(payload)[0]
    report["violations"] = [row.to_dict() for row in violations]
    print(json.dumps(report, ensure_ascii=False, sort_keys=True))
    return 0 if summary["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
