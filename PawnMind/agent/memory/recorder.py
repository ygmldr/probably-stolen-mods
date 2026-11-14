"""Recording wrapper around ``BridgeClient``.

Implements the P1 contract from docs/ADR-001-memory-architecture.md:

* ADR-008: classification is semantic (see ``classification.py``), never by HTTP
  method.
* ADR-009: ``sleep`` reports a *request*; completion requires an observed day
  advance. A second ``sleep`` while one is pending, or after an unresolved one,
  is refused locally.
* ADR-010: the intent row is persisted **before** dispatch. If history cannot be
  recorded, the action is not sent.
* Unknown outcomes are recorded once and never retried.
"""

import sys
import time
import uuid

from . import classification
from .store import (
    EVENT_EXTERNAL,
    EVENT_INTENT,
    EVENT_OBSERVATION,
    EVENT_RESULT,
    EVENT_SLEEP_COMPLETED,
    EVENT_SLEEP_UNRESOLVED,
    STATUS_FAILED,
    STATUS_REJECTED,
    STATUS_SUCCESS,
    STATUS_UNKNOWN,
    MemoryUnavailable,
)

SLEEP_PENDING_ERROR = "sleep_pending"
SLEEP_BLOCKED_ERROR = "sleep_unresolved"


def _now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


class RecordingClient(object):
    """Delegates to a real bridge client while persisting an auditable history."""

    def __init__(self, inner, store, session, sleep_observation_limit=3,
                 verbose=False):
        self.inner = inner
        self.store = store
        self.session = session
        self.timeline_id = session.timeline_id
        self.sleep_observation_limit = sleep_observation_limit
        self.lost_events = 0
        self.verbose = verbose

        self._last_day = None
        self._last_snapshot_id = None
        self._sleep_pending_day = None
        self._sleep_observations = 0
        self._sleep_blocked = False
        self._pending_load = False
        self._closed = False

        # Recover anything the previous process left incomplete.
        try:
            recovered = self.store.reconcile_pending(self.timeline_id)
            if recovered and self.verbose:
                print("memory: reconciled %d pending action(s) as unknown"
                      % recovered, file=sys.stderr)
        except MemoryUnavailable:
            self._lost()

    # ----------------------------------------------------------- pass-through

    @property
    def port(self):
        return getattr(self.inner, "port", None)

    @property
    def token(self):
        return getattr(self.inner, "token", None)

    def health(self):
        return self.inner.health()

    def close(self):
        self._closed = True

    # ----------------------------------------------------------------- reads

    def get_state(self):
        state = self.inner.get_state()
        self._observe(state, tool_name="get_state")
        return state

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        result = self.inner.wait_events(since_rev=since_rev, timeout_ms=timeout_ms)
        if isinstance(result, dict) and isinstance(result.get("state"), dict):
            self._observe(result["state"], tool_name="wait_for_events")
        return result

    # ---------------------------------------------------------------- writes

    def action(self, op, **args):
        if classification.is_read_only(op):
            result = self.inner.action(op, **args)
            self._record_observation(op, args, result)
            if op == "get_recording_save_status":
                self._observe_status(result)
            return result

        if classification.is_async_completion(op) and op == "sleep":
            refusal = self._sleep_refusal()
            if refusal is not None:
                return refusal

        action_id = args.get("action_id") or str(uuid.uuid4())
        args["action_id"] = action_id
        game_day = self._last_day
        rev = args.get("rev")

        # ADR-010: persist the intent first; refuse to act if that fails.
        event_id = self.store.append_intent(
            self.timeline_id, tool_name=op, args=args, action_id=action_id,
            rev=rev, game_day=game_day,
            before_snapshot_id=self._last_snapshot_id)

        try:
            result = self.inner.action(op, **args)
        except MemoryUnavailable:
            raise
        except Exception as exc:
            self._complete(event_id, STATUS_UNKNOWN,
                           {"error": "outcome_unknown", "detail": str(exc)},
                           capture_quality="partial")
            raise

        status = self._status_of(result)
        try:
            self._complete(event_id, status, result, capture_quality="partial")
        except MemoryUnavailable:
            self.lost_events += 1

        if status == STATUS_SUCCESS:
            if op == "sleep":
                self._sleep_pending_day = game_day
                self._sleep_observations = 0
            elif op in classification.LOAD_OPS:
                self._pending_load = True
            elif op == "get_recording_save_status":
                self._observe_status(result)
        return result

    # -------------------------------------------------------------- internals

    def _complete(self, event_id, status, result, capture_quality="complete"):
        try:
            self.store.append_result(event_id, status=status, result=result,
                                     capture_quality=capture_quality)
        except MemoryUnavailable:
            self.lost_events += 1
            if self.verbose:
                print("memory: result not recorded for %s" % event_id,
                      file=sys.stderr)

    def _record_observation(self, op, args, result):
        try:
            self.store.append_event(
                self.timeline_id, event_type=EVENT_OBSERVATION, tool_name=op,
                result_status=self._status_of(result), args=args, result=result,
                capture_quality="complete")
        except MemoryUnavailable:
            self.lost_events += 1

    def _status_of(self, result):
        if not isinstance(result, dict):
            return STATUS_UNKNOWN
        if result.get("ok") is True:
            return STATUS_SUCCESS
        if result.get("unknown") is True or result.get("error") == "outcome_unknown":
            return STATUS_UNKNOWN
        if result.get("ok") is False:
            return STATUS_REJECTED
        return STATUS_FAILED

    def _observe(self, state, tool_name):
        if not isinstance(state, dict):
            return
        day = state.get("day")
        if isinstance(day, int):
            self._last_day = day

        has_success_before = self._had_success_since_last_snapshot()
        try:
            observation = self.store.observe_state(self.timeline_id, state)
        except MemoryUnavailable:
            self.lost_events += 1
            return

        first_observation = self._last_snapshot_id is None
        if observation.changed or first_observation:
            self._last_snapshot_id = observation.snapshot_id
            self._record_state_observation(tool_name, observation,
                                            state.get("rev"))
            if observation.delta:
                self._maybe_external_change(observation, has_success_before)

        self._resolve_sleep(day, observation)

    def _record_state_observation(self, tool_name, observation, rev=None):
        """Only changed states are logged, otherwise polling would flood history."""
        try:
            self.store.append_event(
                self.timeline_id, event_type=EVENT_OBSERVATION,
                tool_name=tool_name, result_status=STATUS_SUCCESS,
                delta=observation.delta, capture_quality="complete",
                game_day=self._last_day, rev=rev,
                after_snapshot_id=observation.snapshot_id)
        except MemoryUnavailable:
            self.lost_events += 1

    def _had_success_since_last_snapshot(self):
        try:
            recent = self.store.history(self.timeline_id, limit=20)
        except MemoryUnavailable:
            return True
        for row in reversed(recent):
            if row["event_type"] != EVENT_RESULT:
                continue
            return row["result_status"] == STATUS_SUCCESS
        return False

    def _maybe_external_change(self, observation, has_success_before):
        if has_success_before:
            return
        try:
            self.store.append_event(
                self.timeline_id, event_type=EVENT_EXTERNAL,
                tool_name="state_observation", result_status=STATUS_SUCCESS,
                delta=observation.delta, capture_quality="partial",
                game_day=self._last_day,
                after_snapshot_id=observation.snapshot_id)
        except MemoryUnavailable:
            self.lost_events += 1

    def _resolve_sleep(self, day, observation):
        if self._sleep_pending_day is None or not isinstance(day, int):
            return
        if day > self._sleep_pending_day:
            try:
                self.store.append_event(
                    self.timeline_id, event_type=EVENT_SLEEP_COMPLETED,
                    tool_name="sleep", result_status=STATUS_SUCCESS,
                    game_day=day, delta=observation.delta,
                    capture_quality="complete",
                    after_snapshot_id=observation.snapshot_id)
            except MemoryUnavailable:
                self.lost_events += 1
            self._sleep_pending_day = None
            self._sleep_observations = 0
            self._sleep_blocked = False
            return

        self._sleep_observations += 1
        if self._sleep_observations < self.sleep_observation_limit:
            return
        try:
            self.store.append_event(
                self.timeline_id, event_type=EVENT_SLEEP_UNRESOLVED,
                tool_name="sleep", result_status=STATUS_UNKNOWN,
                delta=observation.delta, capture_quality="partial",
                game_day=day, after_snapshot_id=observation.snapshot_id)
        except MemoryUnavailable:
            self.lost_events += 1
        self._sleep_pending_day = None
        self._sleep_observations = 0
        self._sleep_blocked = True

    def _sleep_refusal(self):
        if self._sleep_pending_day is not None:
            return {"ok": False, "error": SLEEP_PENDING_ERROR,
                    "detail": "sleep already requested; wait for the day to advance"}
        if self._sleep_blocked:
            return {"ok": False, "error": SLEEP_BLOCKED_ERROR,
                    "detail": "previous sleep never resolved; verify manually"}
        return None

    def _observe_status(self, result):
        if not isinstance(result, dict) or result.get("bound") is not True:
            return
        binding = result.get("binding") or {}
        run_id = binding.get("run_id")
        slot = binding.get("slot")
        run_number = binding.get("run_number")
        if not run_id or not isinstance(slot, int):
            return

        try:
            current = self.store.get_timeline(self.timeline_id)
        except MemoryUnavailable:
            return

        reason = None
        if current["run_id"] is None:
            try:
                self.store.attach_identity(self.timeline_id, run_id=run_id,
                                           slot=slot, run_number=run_number)
            except MemoryUnavailable:
                self.lost_events += 1
            return

        if self._pending_load:
            reason = "rollback"
            self._pending_load = False
        elif current["run_id"] != run_id:
            reason = "external_replace"
        elif current["slot"] != slot:
            reason = "new_game"

        if reason is None:
            return

        try:
            self.timeline_id = self.store.fork_timeline(
                self.timeline_id, run_id=run_id, slot=slot,
                run_number=run_number, reason=reason)
            self._last_snapshot_id = None
        except MemoryUnavailable:
            self.lost_events += 1

    def _lost(self):
        self.lost_events += 1
