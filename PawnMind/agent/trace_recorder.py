"""Trustworthy append-only JSONL call recorder + trace exporter (R5-C2).

Purpose
-------
The R5 LLM pilot must be judged from the *call layer*, never from what the
model says it did.  This module wraps a duck-typed bridge client (the object
the MCP layer holds: ``action(op, **args)``, ``get_state()``,
``get_operation(operation_id)``, ``wait_operation(operation_id,
timeout_ms)``, ...) and appends one JSON object per line to a recording file:

* ``phase="intent"`` **before** dispatch: run identity, actor/model/skill/
  policy/tool-surface context, the exact request ``op``/``args``, the observed
  revision, and ``request_status="issued"`` / ``dispatch_status="pending"``;
* ``phase="result"`` **after** dispatch: the exact bridge response bytes
  (``result``), the two-axis statuses (``execution_status`` /
  ``delivery_status``), ``dispatch_status``, ``response_status``, redacted
  error, ``duration_ms`` and state references.

It also journals ``phase="recovery"`` decisions, ``phase="stop"`` reasons and
``phase="intervention"`` manual interventions.  Everything is offline and
stdlib only; nothing here opens a socket, touches the game or a save.

Secrets
-------
The recording must never contain the bridge bearer token or auth headers.
Values under secret-looking keys (``token``, ``authorization``, ``api_key``,
``password``, ...) are replaced with ``"[redacted]"``, every known secret
value (the wrapped client's ``token`` plus explicit ``secrets=``) is scrubbed
from every recorded string, the result returned to the caller is never
mutated, and each serialized line is re-checked by
:func:`assert_no_known_secrets` before it is written (a leaking line raises
:class:`TraceRecorderError` instead of being persisted).

Durability policy
-----------------
* The file is opened in append mode (``"a"``); old bytes are never rewritten.
* Every event is one line of canonical JSON (``sort_keys``, tight separators,
  UTF-8, no embedded newlines) so a line is self-contained.
* Every line is written, ``flush()``-ed and ``os.fsync()``-ed before the MCP
  call proceeds.  The cost is acceptable for agent traffic and means a crash
  can lose at most the event currently being written.
* If a previous process died mid-line, the recorder appends a single ``"\\n"``
  separator before writing its own events (append-only repair) so the
  truncated fragment stays visible as one corrupt line.
* Reading tolerates corruption: :func:`load_events` skips blank/corrupt/
  truncated lines and returns structured warnings; recovery and export never
  crash on them.  The *exporter* fails closed when a recorded call has no
  complete response and when a recorded checksum no longer matches.

Recovery decision table
-----------------------
On startup the recorder scans the existing recording.  It never auto-replays
anything: an unresolved write is only allowed to be resolved by a real
``get_operation(<operation_id>)`` / ``wait_operation`` query for that id, or
by an idempotent replay with the *original* ``action_id``.  Reads and
operation queries stay allowed; other writes are refused locally.

==========================  =========================================  ============
recorded tail state         recovery path                              decision
==========================  =========================================  ============
intent without result       operation id query, else original          needs_operation_query
                            action_id replay
result dispatch unknown /   operation id query, else original          needs_operation_query
transport error (lost)      action_id replay
result ``prepared``/``running``  operation id query / wait_operation   needs_operation_query
result ``uncertain``        operation id query, else original          needs_operation_query
                            action_id replay
no operation id *and* no    none: fail closed, UNKNOWN, stop          unknown
action id available
query answers ``unknown_operation``  outcome is unprovable, UNKNOWN,   unknown
                            stop
terminal completed/rejected/       nothing to recover                  clean
failed, or a duplicate-call
refusal (operation_pending)
==========================  =========================================  ============

``snapshot_startup()`` writes one ``recovery_scan`` event recording the
decision (plus any tolerated corruption warnings) whenever the file already
had content, so the decision itself is auditable.

Export contract
---------------
:func:`export_trace` rebuilds ``{"schema_version": <validator schema>,
"steps": [{"tool", "args", "result"}, ...]}`` from the recorded intent/result
bytes in file order.  Nothing else can become a step: model text, host
annotations or caller claims stored next to an event are never copied.  The
export verifies the recorded ``args_sha256``/``result_sha256`` checksums
(tamper/accidental-edit detection) and raises :class:`TraceExportError` if any
recorded call lacks a complete recorded response, so an incomplete run cannot
be laundered into a clean trace.  A ``"recorder"`` metadata block carries the
corruption warnings, run ids, interventions and stop reasons for auditors;
the validator ignores unknown top-level keys.

CLI::

    python agent/trace_recorder.py export recording.jsonl --output trace.json
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys
import threading
import time
import uuid
from dataclasses import dataclass, field

#: Event schema of the JSONL recorder (independent of the *trace* schema).
TRACE_RECORDER_SCHEMA_VERSION = 1

#: Fallback if ``agent/trace_validator.py`` cannot be imported (e.g. while it
#: is being edited): the legacy trace schema.  The exporter reads the live
#: constant via ``getattr(trace_validator, "TRACE_SCHEMA_VERSION", ...)``.
LEGACY_TRACE_SCHEMA_FALLBACK = 1

REDACTED = "[redacted]"

#: Explicit phases; an intent event is a legal partial event (no response yet).
EVENT_PHASES = ("intent", "result", "recovery", "stop", "intervention")

#: request / response / dispatch axes (vocabulary kept local on purpose: the
#: recorder must not depend on the concurrently-edited validator module).
REQUEST_ISSUED = "issued"
REQUEST_BLOCKED = "blocked"
RESPONSE_RECEIVED = "received"
RESPONSE_TRANSPORT_ERROR = "transport_error"
RESPONSE_INVALID = "invalid_response"
RESPONSE_MISSING = "missing"
DISPATCH_PENDING = "pending"
DISPATCH_DISPATCHED = "dispatched"
DISPATCH_NOT_DISPATCHED = "not_dispatched"
DISPATCH_UNKNOWN = "unknown"

#: Frozen D2 operation axes (mirrors src/Core/OperationStatus + the validator).
EXECUTION_STATUSES = frozenset({
    "prepared", "running", "completed", "rejected", "failed", "uncertain",
})
DELIVERY_STATUSES = frozenset({
    "pending_delivery", "delivered", "undelivered",
})
PENDING_EXECUTION_STATUSES = frozenset({"prepared", "running"})
TERMINAL_EXECUTION_STATUSES = frozenset({"completed", "rejected", "failed"})

#: Error codes after which a write definitely may not have completed, but the
#: caller cannot tell whether it started; treat as uncertain, never success.
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

#: Refusals that mean "a duplicate native call was already in flight": the
#: write was not dispatched and starts no operation of its own.
REFUSED_BY_PENDING_ERRORS = frozenset({
    "operation_pending",
    "run_change_pending",
})

#: Calls that are operation *queries* and may therefore resolve recovery.
OPERATION_QUERY_OPS = frozenset({"get_operation", "wait_operation"})

#: A wildcard listing may reveal an operation but is never a real query of one
#: operation id, so it must never resolve a recovery entry.
OPERATION_LIST_WILDCARD = "*"

RECOVERY_PENDING_ERROR = "recovery_pending"
RECOVERY_UNKNOWN_ERROR = "recovery_unknown"

#: Keys whose values are secrets and must be redacted, case-insensitively.
_SECRET_KEY_RE = re.compile(
    r"(token|secret|password|passwd|authorization|api[_-]?key|access[_-]?key|"
    r"private[_-]?key|bearer|cookie|credential)",
    re.IGNORECASE,
)

_STATE_KEYS = ("in_game", "day")
_STATE_RENT_KEY = "rent"

_EVENT_CORE_FIELDS = (
    "schema_version", "event", "phase", "run_id", "sequence", "wall_time",
    "monotonic_elapsed_ms", "call_id", "actor", "model_id", "prompt_sha256",
    "skill_sha256", "host_version", "op", "args", "args_sha256",
    "observed_rev", "action_id", "operation_id", "policy_sha256",
    "tool_surface", "recording_slot", "is_write", "request_status",
    "response_status", "execution_status", "dispatch_status",
    "delivery_status", "evidence", "result", "result_sha256",
    "state_before_ref", "state_after_ref", "error", "human_intervention",
    "duration_ms",
)

# Read-only classification: single source of truth shared with the memory
# recorder (agent/memory/classification.py). If it cannot be imported we fail
# closed and treat every action() call as a write.
try:  # script form: `python agent/trace_recorder.py` (agent/ on sys.path)
    from memory import classification as _classification
except ImportError:  # pragma: no cover - package form fallback
    try:
        from .memory import classification as _classification
    except ImportError:
        _classification = None


class TraceRecorderError(RuntimeError):
    """The recorder cannot safely record/dispatch a call (fail closed)."""


class TraceExportError(TraceRecorderError):
    """A recording cannot be exported as a trustworthy trace."""


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------

def _is_plain_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def _str_or_none(value):
    return value if isinstance(value, str) and value else None


def _canonical_json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False, default=str)


def _sha256_canonical(value):
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def _normalise_execution_status(raw):
    return raw if isinstance(raw, str) and raw in EXECUTION_STATUSES else None


def _normalise_delivery_status(raw):
    return raw if isinstance(raw, str) and raw in DELIVERY_STATUSES else None


def _error_is_uncertain(error):
    return isinstance(error, str) and error in UNCERTAIN_ERROR_CODES


def _is_state_payload(payload):
    """A full bridge snapshot (the thing state_before/after_ref points at)."""
    if not isinstance(payload, dict):
        return False
    if "in_game" in payload:
        return True
    return ("day" in payload and _STATE_RENT_KEY in payload)


def _is_secret_key(key):
    return isinstance(key, str) and bool(_SECRET_KEY_RE.search(key))


def _scrub_text(text, known_secrets):
    for secret in known_secrets:
        if secret and secret in text:
            text = text.replace(secret, REDACTED)
    return text


def _redact(value, known_secrets):
    """Return a JSON-safe copy of ``value`` with secrets removed.

    Never mutates the input: the caller keeps the real bridge payload.
    """
    if isinstance(value, dict):
        redacted = {}
        for key, inner in value.items():
            name = key if isinstance(key, str) else str(key)
            if _is_secret_key(name):
                redacted[name] = REDACTED
            else:
                redacted[name] = _redact(inner, known_secrets)
        return redacted
    if isinstance(value, (list, tuple)):
        return [_redact(item, known_secrets) for item in value]
    if isinstance(value, str):
        return _scrub_text(value, known_secrets)
    if value is None or isinstance(value, (bool, int, float)):
        return value
    return _scrub_text(str(value), known_secrets)


def assert_no_known_secrets(text, secrets):
    """Raise :class:`TraceRecorderError` if a known secret is in ``text``.

    Last line of defence after redaction: a JSONL line containing a known
    bridge token is never persisted.
    """
    for secret in secrets or ():
        if isinstance(secret, str) and secret and secret in text:
            raise TraceRecorderError(
                "refusing to write a recording line that contains a known"
                " secret (redaction failed)")
    return text


# ---------------------------------------------------------------------------
# tolerant JSONL reader
# ---------------------------------------------------------------------------

def load_events(path):
    """Read a JSONL recording, tolerating corrupted lines.

    Returns ``(events, warnings)`` where ``events`` are the parsed JSON
    objects in file order and ``warnings`` are structured dicts
    ``{"line", "reason", "detail"}`` with reason one of ``truncated`` (last
    line without a trailing newline and unparsable), ``invalid_json``,
    ``not_object``.  A blank line is skipped silently.  An unreadable file
    raises :class:`TraceRecorderError` (fail closed); a missing file is an
    empty recording.
    """
    if not os.path.exists(path):
        return [], []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            text = handle.read()
    except OSError as error:
        raise TraceRecorderError(
            "cannot read recording %r: %s" % (path, error))

    lines = text.splitlines()
    last_content = -1
    for index, line in enumerate(lines):
        if line.strip():
            last_content = index

    events = []
    warnings = []
    for index, line in enumerate(lines):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except (ValueError, TypeError) as error:
            truncated = (index == last_content
                         and not text.endswith("\n"))
            warnings.append({
                "line": index + 1,
                "reason": "truncated" if truncated else "invalid_json",
                "detail": str(error)[:200],
            })
            continue
        if not isinstance(value, dict):
            warnings.append({
                "line": index + 1,
                "reason": "not_object",
                "detail": type(value).__name__,
            })
            continue
        events.append(value)
    return events, warnings


# ---------------------------------------------------------------------------
# recovery analysis
# ---------------------------------------------------------------------------

@dataclass
class RecoveryEntry:
    """One unresolved write found while scanning a recording."""

    call_id: str
    op: str
    action_id: str = None
    operation_id: str = None
    reason: str = "intent_without_result"
    dispatch_status: str = DISPATCH_UNKNOWN
    execution_status: str = None
    can_query_operation: bool = False
    can_replay_same_action_id: bool = False
    status: str = "pending"          # pending | resolved | unknown
    resolution: str = None           # operation_query | same_action_id

    def to_dict(self):
        return {
            "call_id": self.call_id,
            "op": self.op,
            "action_id": self.action_id,
            "operation_id": self.operation_id,
            "reason": self.reason,
            "dispatch_status": self.dispatch_status,
            "execution_status": self.execution_status,
            "can_query_operation": self.can_query_operation,
            "can_replay_same_action_id": self.can_replay_same_action_id,
            "status": self.status,
            "resolution": self.resolution,
        }


@dataclass
class RecoveryReport:
    """Result of scanning a recording; also the recorder's live decision."""

    status: str = "clean"            # clean | needs_operation_query | unknown
    entries: list = field(default_factory=list)
    warnings: list = field(default_factory=list)

    @property
    def pending_entries(self):
        return [entry for entry in self.entries if entry.status == "pending"]

    @property
    def unknown_entries(self):
        return [entry for entry in self.entries if entry.status == "unknown"]

    @property
    def stop_required(self):
        return self.status == "unknown"

    def refresh(self):
        if any(entry.status == "unknown" for entry in self.entries):
            self.status = "unknown"
        elif any(entry.status == "pending" for entry in self.entries):
            self.status = "needs_operation_query"
        else:
            self.status = "clean"
        return self.status

    def to_dict(self):
        return {
            "status": self.status,
            "stop_required": self.stop_required,
            "entries": [entry.to_dict() for entry in self.entries],
            "warnings": list(self.warnings),
        }


def _resolution_verdict(payload):
    """Judge a query/replay response: ``"unknown"``, ``"terminal"`` or None."""
    if not isinstance(payload, dict):
        return None
    if payload.get("known") is False or payload.get("error") == "unknown_operation":
        return "unknown"
    status = _normalise_execution_status(payload.get("execution_status"))
    if status in TERMINAL_EXECUTION_STATUSES:
        return "terminal"
    return None


def _resolution_from_record(entry, record):
    """Return ``(verdict, resolution)`` if ``record`` is a legitimate recovery.

    Only a real ``get_operation``/``wait_operation`` of the *same* operation id
    or an idempotent replay with the *original* action_id counts; a
    ``get_operation("*")`` listing never does.
    """
    result_event = record.get("result")
    if result_event is None:
        return None
    intent = record["intent"]
    payload = result_event.get("result")
    if not isinstance(payload, dict):
        return None
    op = intent.get("op")
    if op in OPERATION_QUERY_OPS:
        query_args = intent.get("args")
        query_id = None
        if isinstance(query_args, dict):
            query_id = _str_or_none(query_args.get("operation_id"))
        if query_id is None or query_id == OPERATION_LIST_WILDCARD:
            return None
        if entry.operation_id is None or query_id != entry.operation_id:
            return None
        verdict = _resolution_verdict(payload)
        return None if verdict is None else (verdict, "operation_query")
    if (entry.action_id
            and op == entry.op
            and _str_or_none(result_event.get("action_id")) == entry.action_id):
        verdict = _resolution_verdict(payload)
        return None if verdict is None else (verdict, "same_action_id")
    return None


def _entry_for_record(record):
    """Classify one recorded write call as unresolved (or return ``None``)."""
    intent = record["intent"]
    result_event = record.get("result")
    op = str(intent.get("op") or "?")
    action_id = _str_or_none(intent.get("action_id"))
    operation_id = _str_or_none(intent.get("operation_id"))
    if result_event is None:
        entry = RecoveryEntry(
            call_id=record["call_id"], op=op, action_id=action_id,
            operation_id=operation_id, reason="intent_without_result",
            dispatch_status=str(intent.get("dispatch_status")
                                or DISPATCH_PENDING),
            can_query_operation=bool(operation_id),
            can_replay_same_action_id=bool(action_id),
        )
        if not entry.can_query_operation and not entry.can_replay_same_action_id:
            entry.status = "unknown"
        return entry

    payload_recorded = result_event.get("result")
    payload = payload_recorded if isinstance(payload_recorded, dict) else {}
    action_id = action_id or _str_or_none(result_event.get("action_id"))
    operation_id = (operation_id
                    or _str_or_none(result_event.get("operation_id"))
                    or _str_or_none(payload.get("operation_id")))
    status = (_normalise_execution_status(result_event.get("execution_status"))
              or _normalise_execution_status(payload.get("execution_status")))
    response_status = result_event.get("response_status")
    dispatch_status = str(result_event.get("dispatch_status")
                          or DISPATCH_UNKNOWN)
    error = payload.get("error")

    if response_status in (RESPONSE_TRANSPORT_ERROR, RESPONSE_MISSING):
        reason = "response_lost"
    elif response_status == RESPONSE_INVALID:
        reason = "invalid_response"
    elif status in TERMINAL_EXECUTION_STATUSES:
        return None                      # nothing to recover
    elif status in PENDING_EXECUTION_STATUSES:
        reason = "non_terminal_operation"
    elif status == "uncertain":
        reason = "uncertain_operation"
    elif _error_is_uncertain(error):
        reason = "uncertain_operation"
    elif (error in REFUSED_BY_PENDING_ERRORS
          and (payload.get("blocking_operation_id") is not None
               or payload.get("blocking_same_call") is True)):
        return None                      # duplicate-call refusal: not dispatched
    elif payload.get("ok") is False:
        return None                      # definitive pre-dispatch refusal
    elif payload.get("ok") is True and operation_id is None:
        reason = "unverified_response"
    else:
        return None

    entry = RecoveryEntry(
        call_id=record["call_id"], op=op, action_id=action_id,
        operation_id=operation_id, reason=reason,
        dispatch_status=dispatch_status, execution_status=status,
        can_query_operation=bool(operation_id),
        can_replay_same_action_id=bool(action_id),
    )
    if not entry.can_query_operation and not entry.can_replay_same_action_id:
        entry.status = "unknown"
    return entry


def analyze_events(events):
    """Scan recorded events and return a :class:`RecoveryReport`.

    Pure function (no I/O): the recorder calls it on startup and tests call it
    directly.  It never executes or replays anything.
    """
    warnings = []
    records = []
    by_call = {}
    for position, event in enumerate(events):
        if not isinstance(event, dict):
            warnings.append({"line": position + 1, "reason": "not_object"})
            continue
        phase = event.get("phase")
        call_id = event.get("call_id")
        if phase == "intent":
            if not isinstance(call_id, str) or not call_id:
                warnings.append({"line": position + 1,
                                 "reason": "intent_without_call_id"})
                continue
            if call_id in by_call:
                warnings.append({"line": position + 1,
                                 "reason": "duplicate_intent",
                                 "call_id": call_id})
                continue
            record = {"call_id": call_id, "intent": event, "result": None,
                      "order": len(records)}
            records.append(record)
            by_call[call_id] = record
        elif phase == "result":
            if not isinstance(call_id, str) or call_id not in by_call:
                warnings.append({"line": position + 1,
                                 "reason": "result_without_intent",
                                 "call_id": call_id})
                continue
            record = by_call[call_id]
            if record["result"] is not None:
                warnings.append({"line": position + 1,
                                 "reason": "duplicate_result",
                                 "call_id": call_id})
                continue
            record["result"] = event

    entries = []
    for record in records:
        if record["intent"].get("is_write") is not True:
            continue
        entry = _entry_for_record(record)
        if entry is not None:
            entries.append(entry)

    # Apply legitimate resolutions found later in the file.
    for entry in entries:
        if entry.status != "pending":
            continue
        start = by_call[entry.call_id]["order"] + 1
        for record in records[start:]:
            resolution = _resolution_from_record(entry, record)
            if resolution is None:
                continue
            verdict, resolution_name = resolution
            if verdict == "unknown":
                entry.status = "unknown"
                entry.resolution = None
            else:
                entry.status = "resolved"
                entry.resolution = resolution_name
            break

    report = RecoveryReport(entries=entries, warnings=warnings)
    report.refresh()
    return report


# ---------------------------------------------------------------------------
# trace exporter
# ---------------------------------------------------------------------------

def _current_trace_schema_version():
    """Read the live trace schema version from ``agent/trace_validator.py``."""
    try:
        import trace_validator  # script form / tests (agent/ on sys.path)
        return getattr(trace_validator, "TRACE_SCHEMA_VERSION",
                       LEGACY_TRACE_SCHEMA_FALLBACK)
    except Exception:
        pass
    try:
        from . import trace_validator as validator_module  # package form
        return getattr(validator_module, "TRACE_SCHEMA_VERSION",
                       LEGACY_TRACE_SCHEMA_FALLBACK)
    except Exception:
        return LEGACY_TRACE_SCHEMA_FALLBACK


def _verify_recorded_checksum(event, payload_key, sha_key, call_id):
    expected = event.get(sha_key)
    if expected is None:
        return  # legacy/hand-written event: nothing to verify
    if not isinstance(expected, str):
        raise TraceExportError(
            "recorded %s checksum for call %r is not a string"
            % (payload_key, call_id))
    actual = _sha256_canonical(event.get(payload_key))
    if actual != expected:
        raise TraceExportError(
            "recorded %s bytes for call %r were modified after recording"
            " (checksum mismatch); refusing to export an untrustworthy trace"
            % (payload_key, call_id))


def _recorder_metadata(path, events, warnings):
    run_ids = []
    for event in events:
        run_id = event.get("run_id")
        if isinstance(run_id, str) and run_id and run_id not in run_ids:
            run_ids.append(run_id)

    interventions = []
    stops = []
    recovery_decisions = []
    for event in events:
        phase = event.get("phase")
        if phase == "intervention":
            interventions.append({
                "sequence": event.get("sequence"),
                "actor": event.get("actor"),
                "kind": event.get("intervention_kind"),
                "detail": event.get("intervention_detail"),
            })
        elif phase == "stop":
            stops.append({
                "sequence": event.get("sequence"),
                "reason": event.get("stop_reason"),
            })
        elif phase == "recovery":
            recovery = event.get("recovery")
            decision = recovery.get("decision") if isinstance(recovery, dict) else None
            recovery_decisions.append({
                "sequence": event.get("sequence"),
                "decision": decision,
            })

    return {
        "schema_version": TRACE_RECORDER_SCHEMA_VERSION,
        "source": os.path.basename(path),
        "event_count": len(events),
        "warnings": list(warnings),
        "run_ids": run_ids,
        "human_interventions": interventions,
        "stop_reasons": stops,
        "recovery_decisions": recovery_decisions,
    }


def export_trace(recording_path):
    """Export a recording as a trace payload ``agent/trace_validator.py`` accepts.

    Steps are rebuilt *only* from the recorded intent (``op``/``args``) and
    result (``result``) bytes, in file order.  Model text, comments, claims or
    annotations stored next to an event are never copied into a step.

    Raises :class:`TraceExportError` when the recording is missing, structurally
    inconsistent, has a call without a complete recorded response (crash /
    response lost), or when a recorded payload was modified after recording.
    """
    path = os.path.abspath(recording_path)
    if not os.path.exists(path):
        raise TraceExportError("recording not found: %s" % path)
    events, warnings = load_events(path)

    calls = {}
    order = []
    for position, event in enumerate(events):
        phase = event.get("phase")
        call_id = event.get("call_id")
        if phase == "intent":
            if not isinstance(call_id, str) or not call_id:
                raise TraceExportError(
                    "event #%d is an intent without a call_id"
                    % (position + 1))
            if call_id in calls:
                raise TraceExportError(
                    "duplicate intent for call_id %r" % call_id)
            calls[call_id] = {"intent": event, "result": None}
            order.append(call_id)
        elif phase == "result":
            if not isinstance(call_id, str) or call_id not in calls:
                raise TraceExportError(
                    "event #%d is a result without a recorded intent"
                    % (position + 1))
            if calls[call_id]["result"] is not None:
                raise TraceExportError(
                    "duplicate result for call_id %r" % call_id)
            calls[call_id]["result"] = event

    steps = []
    unresolved = []
    for call_id in order:
        record = calls[call_id]
        intent = record["intent"]
        result_event = record["result"]
        if result_event is None:
            unresolved.append(
                (call_id, "no recorded result (process stopped after intent)"))
            continue
        if result_event.get("response_status") != RESPONSE_RECEIVED:
            unresolved.append(
                (call_id, "no recorded response bytes (%r)"
                 % (result_event.get("response_status"),)))
            continue
        payload = result_event.get("result")
        if not isinstance(payload, dict):
            unresolved.append((call_id, "recorded result is not an object"))
            continue
        _verify_recorded_checksum(intent, "args", "args_sha256", call_id)
        _verify_recorded_checksum(result_event, "result", "result_sha256",
                                  call_id)
        tool = intent.get("op")
        args = intent.get("args")
        if not isinstance(tool, str) or not tool:
            raise TraceExportError(
                "intent for call %r has no tool name" % call_id)
        if not isinstance(args, dict):
            raise TraceExportError(
                "intent for call %r has non-object args" % call_id)
        steps.append({"tool": tool, "args": args, "result": payload})

    if unresolved:
        call_id, reason = unresolved[0]
        raise TraceExportError(
            "recording is not exportable: %d recorded call(s) have no"
            " complete response (first: call_id=%r, reason: %s)"
            % (len(unresolved), call_id, reason))

    return {
        "schema_version": _current_trace_schema_version(),
        "steps": steps,
        "recorder": _recorder_metadata(path, events, warnings),
    }


def _write_output(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def main(argv=None):
    """CLI: ``export <recording.jsonl> [--output trace.json]``."""
    parser = argparse.ArgumentParser(
        prog="trace_recorder",
        description="Export a PawnMind append-only JSONL call recording as a"
                    " JSON trace accepted by agent/trace_validator.py"
                    " (offline, stdlib only).")
    subparsers = parser.add_subparsers(dest="command")
    export_parser = subparsers.add_parser(
        "export", help="export a recording as a validator trace")
    export_parser.add_argument("recording_path",
                               help="path to the append-only JSONL recording")
    export_parser.add_argument("--output", "-o", default=None,
                               help="write the trace JSON here (default: stdout;"
                                    " when set, a JSON summary is printed)")
    args = parser.parse_args(argv)

    if args.command != "export":
        parser.print_usage(sys.stderr)
        return 2

    try:
        trace = export_trace(args.recording_path)
    except (TraceRecorderError, OSError) as error:
        print(json.dumps({"ok": False, "error": "export_failed",
                          "detail": str(error)}, ensure_ascii=False))
        return 2

    text = json.dumps(trace, sort_keys=True, ensure_ascii=False,
                      indent=2) + "\n"
    if args.output:
        try:
            _write_output(args.output, text)
        except OSError as error:
            print(json.dumps({"ok": False, "error": "write_failed",
                              "detail": str(error)}, ensure_ascii=False))
            return 2
        print(json.dumps({
            "ok": True,
            "output": args.output,
            "schema_version": trace["schema_version"],
            "steps": len(trace["steps"]),
            "warnings": len(trace["recorder"]["warnings"]),
        }, ensure_ascii=False))
    else:
        sys.stdout.write(text)
    return 0


# ---------------------------------------------------------------------------
# the recorder
# ---------------------------------------------------------------------------

class TraceRecorder:
    """Append-only recorder wrapper around a duck-typed bridge client.

    Every protocol call (``action``, ``get_state``, ``get_operation``,
    ``wait_operation``, ``wait_events``) is journaled as an intent event
    before dispatch and a result event after it.  The wrapped object is never
    modified and the caller's argument dict/result object is never mutated.

    ``health()`` and unknown attributes are passed through: ``health`` is the
    internal run-mode probe of the MCP server, not an MCP tool call, and must
    not flood the recording with its 2-second refresh.

    Args:
        inner: bridge client (duck-typed; e.g. ``BridgeClient`` or
            ``memory.recorder.RecordingClient``).
        recording_path: JSONL file to append to (created if missing; parent
            directory created on demand).
        run_id: stable id for this recorder session (default: random hex).
        actor: who is speaking through this layer (default ``"agent"``).
        model_id, prompt_sha256, skill_sha256, host_version, policy_sha256,
            tool_surface, recording_slot: run context copied onto every event.
        secrets: extra literal secret values to scrub (the client's ``token``
            attribute is scrubbed automatically).
        wall_clock / monotonic_clock: injectable clocks (testing).
        strict_recovery: when True (default) other writes are refused locally
            while a recovered operation is unresolved (fail closed).
    """

    def __init__(self, inner, recording_path, *, run_id=None, actor="agent",
                 model_id=None, prompt_sha256=None, skill_sha256=None,
                 host_version=None, policy_sha256=None, tool_surface=None,
                 recording_slot=None, secrets=None, wall_clock=None,
                 monotonic_clock=None, strict_recovery=True):
        self._inner = inner
        self.recording_path = os.path.abspath(recording_path)
        self.run_id = run_id or uuid.uuid4().hex
        self.actor = actor
        self.model_id = model_id
        self.prompt_sha256 = prompt_sha256
        self.skill_sha256 = skill_sha256
        self.host_version = host_version
        self.policy_sha256 = policy_sha256
        self.tool_surface = tool_surface
        self.recording_slot = recording_slot
        self.strict_recovery = bool(strict_recovery)

        self._wall_clock = wall_clock or time.time
        self._monotonic_clock = monotonic_clock or time.monotonic
        self._t0 = self._monotonic_clock()
        self._lock = threading.RLock()
        self._closed = False
        self._sequence = 0
        self._state_ref = None
        self._seen_action_ids = {}
        self._known_secrets = self._collect_secrets(inner, secrets)

        prior_events, load_warnings = load_events(self.recording_path)
        next_sequence = 1
        for event in prior_events:
            sequence = event.get("sequence")
            if _is_plain_int(sequence) and sequence >= next_sequence:
                next_sequence = sequence + 1
        self._sequence = next_sequence
        self.recovery = analyze_events(prior_events)
        if load_warnings:
            self.recovery.warnings = (list(load_warnings)
                                      + list(self.recovery.warnings))
            self.recovery.refresh()

        self._repair_trailing_newline()
        self._handle = open(self.recording_path, "a",
                            encoding="utf-8", newline="\n")
        if prior_events or load_warnings:
            self._record(
                "recovery", "recovery_scan",
                recovery={
                    "decision": self.recovery.status,
                    "pending": [entry.to_dict()
                                for entry in self.recovery.pending_entries],
                    "warnings": list(load_warnings),
                })

    # -- lifecycle -------------------------------------------------------

    @staticmethod
    def _collect_secrets(inner, secrets):
        candidates = []
        try:
            token = getattr(inner, "token", None)
        except Exception:
            token = None
        if isinstance(token, str) and token:
            candidates.append(token)
        for secret in secrets or ():
            if isinstance(secret, str) and secret:
                candidates.append(secret)
        # de-duplicate, stable order
        known = []
        for secret in candidates:
            if secret not in known:
                known.append(secret)
        return tuple(known)

    def _ensure_open(self):
        if self._closed:
            raise TraceRecorderError(
                "trace recorder is closed; refusing an unrecordable call")

    def close(self):
        """Flush, fsync and close the recording (idempotent)."""
        with self._lock:
            if getattr(self, "_closed", True):
                return
            self._closed = True
            handle = getattr(self, "_handle", None)
            if handle is None:
                return
            try:
                handle.flush()
                os.fsync(handle.fileno())
            except (OSError, ValueError):
                pass
            finally:
                handle.close()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()
        return False

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        inner = self.__dict__.get("_inner")
        if inner is None:
            raise AttributeError(name)
        return getattr(inner, name)

    # -- event writing ---------------------------------------------------

    def _wall_time(self):
        stamp = datetime.datetime.fromtimestamp(
            self._wall_clock(), tz=datetime.timezone.utc)
        return (stamp.strftime("%Y-%m-%dT%H:%M:%S.")
                + "%03dZ" % (stamp.microsecond // 1000))

    def _elapsed_ms(self):
        return max(0, int(round((self._monotonic_clock() - self._t0) * 1000)))

    def _repair_trailing_newline(self):
        """Append-only repair: never merge a new event into a partial line."""
        if not os.path.exists(self.recording_path):
            return
        try:
            if os.path.getsize(self.recording_path) <= 0:
                return
            with open(self.recording_path, "rb") as handle:
                handle.seek(-1, os.SEEK_END)
                last = handle.read(1)
        except OSError:
            return
        if last != b"\n":
            with open(self.recording_path, "ab") as handle:
                handle.write(b"\n")

    def _record(self, phase, event, **fields):
        """Build, redact, serialize, fsync and return one JSONL event."""
        with self._lock:
            self._ensure_open()
            row = {name: None for name in _EVENT_CORE_FIELDS}
            row.update({
                "schema_version": TRACE_RECORDER_SCHEMA_VERSION,
                "event": event,
                "phase": phase,
                "run_id": self.run_id,
                "sequence": self._sequence,
                "wall_time": self._wall_time(),
                "monotonic_elapsed_ms": self._elapsed_ms(),
                "actor": self.actor,
                "model_id": self.model_id,
                "prompt_sha256": self.prompt_sha256,
                "skill_sha256": self.skill_sha256,
                "host_version": self.host_version,
                "policy_sha256": self.policy_sha256,
                "tool_surface": self.tool_surface,
                "recording_slot": self.recording_slot,
                "human_intervention": False,
                "duplicate_action_id": False,
                "duplicate_of_call_id": None,
                "recovery_replay": False,
            })
            row.update(fields)
            self._sequence += 1

            for key in ("args", "result", "evidence", "error", "recovery",
                        "pending", "warnings", "intervention_detail"):
                if row.get(key) is not None:
                    row[key] = _redact(row[key], self._known_secrets)
            if isinstance(row.get("args"), dict):
                row["args_sha256"] = _sha256_canonical(row["args"])
            if row.get("result") is not None:
                row["result_sha256"] = _sha256_canonical(row["result"])

            line = json.dumps(row, sort_keys=True, separators=(",", ":"),
                              ensure_ascii=False)
            assert_no_known_secrets(line, self._known_secrets)
            self._handle.write(line + "\n")
            self._handle.flush()
            os.fsync(self._handle.fileno())
            return row

    # -- call framing ----------------------------------------------------

    @staticmethod
    def _is_write_op(op):
        if _classification is not None:
            try:
                return not _classification.is_read_only(op)
            except Exception:  # pragma: no cover - classification bug
                return True
        # Fail closed: unknown operation means "assume it mutates state".
        return True

    def _begin_call(self, op, args, *, is_write, action_id=None,
                    recovery_replay=False):
        with self._lock:
            call_args = dict(args)
            if action_id is None:
                action_id = call_args.get("action_id")
            call = {
                "call_id": "call-" + uuid.uuid4().hex,
                "op": op,
                "args": call_args,
                "is_write": bool(is_write),
                "action_id": _str_or_none(action_id),
                "operation_id": _str_or_none(call_args.get("operation_id")),
                "observed_rev": (call_args.get("rev")
                                 if _is_plain_int(call_args.get("rev"))
                                 else None),
                "state_before_ref": self._state_ref,
                "started_mono": self._monotonic_clock(),
                "recovery_replay": bool(recovery_replay),
            }
            duplicate = False
            duplicate_of = None
            if call["is_write"] and call["action_id"]:
                previous = self._seen_action_ids.get(call["action_id"])
                if previous is not None:
                    duplicate = True
                    duplicate_of = previous["call_id"]
                else:
                    self._seen_action_ids[call["action_id"]] = {
                        "call_id": call["call_id"], "op": op}
            self._record(
                "intent", "call",
                call_id=call["call_id"], op=op, args=call_args,
                is_write=call["is_write"], action_id=call["action_id"],
                operation_id=call["operation_id"],
                observed_rev=call["observed_rev"],
                request_status=REQUEST_ISSUED,
                dispatch_status=DISPATCH_PENDING,
                state_before_ref=call["state_before_ref"],
                duplicate_action_id=duplicate,
                duplicate_of_call_id=duplicate_of,
                recovery_replay=call["recovery_replay"],
            )
            return call

    def _duration_ms(self, call):
        return max(0, int(round(
            (self._monotonic_clock() - call["started_mono"]) * 1000)))

    def _finish_call(self, call, result):
        payload = result if isinstance(result, dict) else None
        duration = self._duration_ms(call)
        state_after = self._state_ref
        if payload is not None and _is_state_payload(payload):
            state_after = "call:%s#sha256:%s" % (
                call["call_id"], _sha256_canonical(payload))
            self._state_ref = state_after
        action_id = call["action_id"]
        operation_id = call["operation_id"]
        execution_status = None
        delivery_status = None
        error = None
        evidence = None
        if payload is not None:
            action_id = action_id or _str_or_none(payload.get("action_id"))
            operation_id = (operation_id
                            or _str_or_none(payload.get("operation_id")))
            execution_status = _normalise_execution_status(
                payload.get("execution_status"))
            delivery_status = _normalise_delivery_status(
                payload.get("delivery_status"))
            error = payload.get("error")
            recorded_evidence = payload.get("evidence")
            if isinstance(recorded_evidence, (dict, list)):
                evidence = recorded_evidence
        self._record(
            "result", "call",
            call_id=call["call_id"], op=call["op"], args=call["args"],
            is_write=call["is_write"], action_id=action_id,
            operation_id=operation_id, observed_rev=call["observed_rev"],
            request_status=REQUEST_ISSUED,
            response_status=(RESPONSE_RECEIVED if payload is not None
                             else RESPONSE_INVALID),
            dispatch_status=DISPATCH_DISPATCHED,
            execution_status=execution_status,
            delivery_status=delivery_status, evidence=evidence,
            result=payload, error=error, duration_ms=duration,
            state_before_ref=call["state_before_ref"],
            state_after_ref=state_after,
            recovery_replay=call["recovery_replay"],
        )
        self._after_result(call, payload)

    def _finish_transport_error(self, call, error):
        self._record(
            "result", "call",
            call_id=call["call_id"], op=call["op"], args=call["args"],
            is_write=call["is_write"], action_id=call["action_id"],
            operation_id=call["operation_id"],
            observed_rev=call["observed_rev"],
            request_status=REQUEST_ISSUED,
            response_status=RESPONSE_TRANSPORT_ERROR,
            dispatch_status=DISPATCH_UNKNOWN,
            error={"type": type(error).__name__, "message": str(error)},
            duration_ms=self._duration_ms(call),
            state_before_ref=call["state_before_ref"],
            state_after_ref=self._state_ref,
            recovery_replay=call["recovery_replay"],
        )
        if call["is_write"]:
            self._register_pending(call, reason="response_lost",
                                   dispatch_status=DISPATCH_UNKNOWN)

    def _after_result(self, call, payload):
        op = call["op"]
        if op in OPERATION_QUERY_OPS:
            query_args = call["args"]
            operation_id = query_args.get("operation_id")
            self._note_operation_query(operation_id, payload)
            return
        if call["is_write"]:
            self._note_write_recovery(call, payload)

    # -- recovery bookkeeping -------------------------------------------

    def _matching_pending(self, op, action_id):
        if not action_id:
            return None
        for entry in self.recovery.entries:
            if (entry.status == "pending"
                    and entry.op == op
                    and entry.action_id == action_id):
                return entry
        return None

    def _record_recovery_update(self, entry, decision, resolution=None):
        recovery = {
            "decision": decision,
            "report_status": self.recovery.status,
            "entry": entry.to_dict(),
        }
        if resolution is not None:
            recovery["resolution"] = resolution
        return self._record("recovery", "recovery_update",
                            recovery=recovery)

    def _record_stop(self, reason, detail=None, op=None, args=None,
                     recovery=None):
        return self._record(
            "stop", "stop",
            stop_reason=reason, op=op, args=args,
            error={"code": reason, "detail": detail},
            recovery=recovery,
        )

    def _apply_resolution(self, entry, payload, resolution):
        """Consume one legitimate query/replay response for ``entry``."""
        if not isinstance(payload, dict):
            return
        verdict = _resolution_verdict(payload)
        if verdict == "unknown":
            entry.status = "unknown"
            entry.resolution = None
            self.recovery.refresh()
            self._record_recovery_update(entry, decision="unknown")
            self._record_stop(
                "operation_not_queryable",
                detail=("the bridge no longer knows operation %r; the outcome"
                        " is UNKNOWN and the run must stop" % (entry.operation_id,)),
                recovery={"decision": self.recovery.status,
                          "entry": entry.to_dict()})
            return
        entry.operation_id = (entry.operation_id
                              or _str_or_none(payload.get("operation_id")))
        normalised = _normalise_execution_status(
            payload.get("execution_status"))
        entry.execution_status = normalised or entry.execution_status
        if verdict == "terminal":
            entry.status = "resolved"
            entry.resolution = resolution
            self.recovery.refresh()
            self._record_recovery_update(entry, decision="resolved",
                                         resolution=resolution)
            return
        # no proof yet: stay pending, never dispatch on our own
        self.recovery.refresh()

    def _note_operation_query(self, operation_id, payload):
        if not isinstance(operation_id, str) or not operation_id.strip():
            return
        operation_id = operation_id.strip()
        if operation_id == OPERATION_LIST_WILDCARD:
            return  # a listing is never a real query of one operation id
        if not isinstance(payload, dict):
            return
        for entry in list(self.recovery.entries):
            if entry.status != "pending" or entry.operation_id is None:
                continue
            if entry.operation_id != operation_id:
                continue
            self._apply_resolution(entry, payload,
                                   resolution="operation_query")

    def _note_write_recovery(self, call, payload):
        matched = self._matching_pending(call["op"], call["action_id"])
        if matched is not None:
            self._apply_resolution(matched, payload,
                                   resolution="same_action_id")
            return
        self._maybe_register_uncertain(call, payload)

    def _register_pending(self, call, reason, dispatch_status,
                          execution_status=None, operation_id=None):
        entry = RecoveryEntry(
            call_id=call["call_id"], op=call["op"],
            action_id=call["action_id"],
            operation_id=operation_id or call["operation_id"],
            reason=reason, dispatch_status=dispatch_status,
            execution_status=execution_status,
            can_query_operation=bool(operation_id
                                     or call["operation_id"]),
            can_replay_same_action_id=bool(call["action_id"]),
        )
        if not entry.can_query_operation and not entry.can_replay_same_action_id:
            entry.status = "unknown"
        self.recovery.entries.append(entry)
        self.recovery.refresh()
        self._record_recovery_update(entry, decision=self.recovery.status)
        if entry.status == "unknown":
            self._record_stop(
                "no_operation_reference",
                detail=("no operation_id and no action_id is available for a"
                        " safe recovery query; the outcome is UNKNOWN and the"
                        " run must stop"),
                recovery={"decision": self.recovery.status,
                          "entry": entry.to_dict()})
        return entry

    def _maybe_register_uncertain(self, call, payload):
        """Register a live unresolved write result (fail closed from now on)."""
        if not isinstance(payload, dict):
            self._register_pending(call, reason="invalid_response",
                                   dispatch_status=DISPATCH_UNKNOWN)
            return
        error = payload.get("error")
        if (error in REFUSED_BY_PENDING_ERRORS
                and (payload.get("blocking_operation_id") is not None
                     or payload.get("blocking_same_call") is True)):
            return  # duplicate-call refusal: not dispatched, nothing pending
        status = _normalise_execution_status(payload.get("execution_status"))
        operation_id = (_str_or_none(payload.get("operation_id"))
                        or call.get("operation_id"))
        dispatched = payload.get("dispatched")
        dispatch_status = (DISPATCH_DISPATCHED if dispatched is True
                           else DISPATCH_UNKNOWN)
        if status in TERMINAL_EXECUTION_STATUSES:
            return
        if status in PENDING_EXECUTION_STATUSES:
            reason = "non_terminal_operation"
        elif status == "uncertain" or _error_is_uncertain(error):
            reason = "uncertain_operation"
        elif payload.get("ok") is True and operation_id is None:
            reason = "unverified_response"
        elif payload.get("ok") is False:
            return  # definitive refusal: terminal, nothing to recover
        else:
            reason = "unverified_response"
        self._register_pending(call, reason=reason,
                               dispatch_status=dispatch_status,
                               execution_status=status,
                               operation_id=operation_id)

    # -- write guard -----------------------------------------------------

    def _write_refusal(self, op, action_id):
        if not self.strict_recovery:
            return None
        if self.recovery.unknown_entries or self.recovery.status == "unknown":
            return {
                "ok": False,
                "error": RECOVERY_UNKNOWN_ERROR,
                "detail": ("an earlier operation has an UNKNOWN outcome and the"
                           " recording cannot prove it safe; refusing to"
                           " dispatch %r. Stop the run and inspect the"
                           " recording" % (op,)),
                "recovery": self.recovery.to_dict(),
            }
        if not self.recovery.pending_entries:
            return None
        if self._matching_pending(op, action_id) is not None:
            return None  # same-action-id idempotent replay is the recovery
        return {
            "ok": False,
            "error": RECOVERY_PENDING_ERROR,
            "detail": ("write %r refused: an earlier operation is still"
                       " unresolved; only get_operation/wait_operation reads"
                       " or an idempotent replay with the original action_id"
                       " are allowed (never a new action id)" % (op,)),
            "recovery": self.recovery.to_dict(),
        }

    # -- recorded protocol methods --------------------------------------

    def get_state(self):
        """Recorded ``get_state`` (read; updates ``state_after_ref``)."""
        self._ensure_open()
        call = self._begin_call("get_state", {}, is_write=False)
        try:
            result = self._inner.get_state()
        except Exception as error:
            self._finish_transport_error(call, error)
            raise
        self._finish_call(call, result)
        return result

    def get_operation(self, operation_id):
        """Recorded ``get_operation`` (read; may resolve recovery by id)."""
        self._ensure_open()
        call = self._begin_call("get_operation",
                                {"operation_id": operation_id},
                                is_write=False)
        try:
            result = self._inner.get_operation(operation_id)
        except Exception as error:
            self._finish_transport_error(call, error)
            raise
        self._finish_call(call, result)
        return result

    def wait_operation(self, operation_id, timeout_ms=2000):
        """Recorded ``wait_operation`` (read; may resolve recovery by id)."""
        self._ensure_open()
        call = self._begin_call(
            "wait_operation",
            {"operation_id": operation_id, "timeout_ms": timeout_ms},
            is_write=False)
        try:
            result = self._inner.wait_operation(operation_id,
                                                timeout_ms=timeout_ms)
        except Exception as error:
            self._finish_transport_error(call, error)
            raise
        self._finish_call(call, result)
        return result

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        """Recorded ``wait_events`` (read)."""
        self._ensure_open()
        call = self._begin_call(
            "wait_events",
            {"since_rev": since_rev, "timeout_ms": timeout_ms},
            is_write=False)
        try:
            result = self._inner.wait_events(since_rev=since_rev,
                                             timeout_ms=timeout_ms)
        except Exception as error:
            self._finish_transport_error(call, error)
            raise
        self._finish_call(call, result)
        return result

    def action(self, op, **args):
        """Recorded write/read action; injects a stable ``action_id`` for writes.

        For writes without a caller-provided ``action_id`` the recorder mints
        one *before* dispatch so a crash leaves a replayable idempotency key in
        the intent event.  The dispatch itself is the caller's single call: the
        recorder never duplicates or replays it on its own.
        """
        self._ensure_open()
        call_args = dict(args)
        is_write = self._is_write_op(op)
        action_id = None
        recovery_replay = False
        if is_write:
            action_id = _str_or_none(call_args.get("action_id"))
            if action_id is None:
                action_id = str(uuid.uuid4())
                call_args["action_id"] = action_id
            refusal = self._write_refusal(op, action_id)
            if refusal is not None:
                self._record_stop(
                    refusal["error"], detail=refusal.get("detail"),
                    op=op, args=call_args,
                    recovery=refusal.get("recovery"))
                return refusal
            recovery_replay = (self._matching_pending(op, action_id)
                               is not None)
        call = self._begin_call(op, call_args, is_write=is_write,
                                action_id=action_id,
                                recovery_replay=recovery_replay)
        try:
            result = self._inner.action(op, **call_args)
        except Exception as error:
            self._finish_transport_error(call, error)
            raise
        self._finish_call(call, result)
        return result

    # -- journal helpers -------------------------------------------------

    def health(self):
        """Pass-through run-mode probe (not an MCP call; not recorded)."""
        return self._inner.health()

    def record_intervention(self, kind, detail=None, actor="human"):
        """Journal a manual/human intervention (outside the tool surface)."""
        self._ensure_open()
        return self._record(
            "intervention", "intervention",
            actor=actor, human_intervention=True,
            intervention_kind=kind, intervention_detail=detail,
        )

    def record_stop(self, reason, detail=None):
        """Journal an explicit stop reason (budget, unsafe state, crash, ...)."""
        self._ensure_open()
        return self._record_stop(reason, detail=detail)


if __name__ == "__main__":
    raise SystemExit(main())
