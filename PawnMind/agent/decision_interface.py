"""R5-C3: minimal LLM decision interface + the trustworthy executor.

The decision model may only *propose*; it never owns idempotency, revisions or
the permission gate. This module separates the four layers the handoff requires:

* decision validation -- what a model is allowed to say at all,
* execution -- attach ``rev``/``action_id``, dispatch exactly one call, read
  back the real state,
* recovery -- stale revision, rejected, running, uncertain: query, never
  blind-retry,
* budget -- actions, consecutive rejections, consecutive uncertainty and wall
  clock; reaching a limit is a safe stop that keeps the trace.

It is deliberately offline and stdlib-only. It speaks to anything with the
``BridgeClient`` shape (``get_state``/``action``/``get_operation``/
``wait_operation``), including ``agent.trace_recorder.TraceRecorder``, so the
same loop works against the fake bridge and a live one. It never decides
what to do: the caller (an LLM host) supplies one :class:`Decision` per step.
"""

import inspect
import json
import os
import time
import uuid

__all__ = [
    "Decision",
    "DecisionError",
    "Budget",
    "BudgetExceeded",
    "StepStatus",
    "StepResult",
    "Executor",
    "classify_response",
    "DEFAULT_LIMITS",
]

HERE = os.path.dirname(os.path.abspath(__file__))
POLICY_PATH = os.path.join(HERE, "policy", "tool_policy.json")

#: Hard ceiling used when a model smuggles these into tool arguments.
#: ``rev`` and ``action_id`` belong to the executor; hidden-field probes are
#: never a legal tool argument (AGENTS.md: hidden answers stay hidden).
FORBIDDEN_ARG_KEYS = frozenset(
    {
        "action_id",
        "rev",
        "include_hidden_appraisal",
        "appraisal_answer",
        "hidden_identity",
        "client_hidden_identity",
        "real_water_composition",
        "water_composition",
        "add_credit",
        "grant_item",
    }
)

#: Default pilot budgets (the handoff asks for per-run declared limits).
DEFAULT_LIMITS = {
    "max_actions": 300,
    "max_consecutive_rejections": 5,
    "max_consecutive_uncertain": 3,
    "max_wall_seconds": 90 * 60,
}


class DecisionError(ValueError):
    """The model produced a decision the execution layer must refuse."""


class BudgetExceeded(RuntimeError):
    def __init__(self, code, detail=None):
        self.code = code
        self.detail = detail
        RuntimeError.__init__(self, "%s: %s" % (code, detail or ""))


class StepStatus(object):
    COMPLETED = "completed"
    REJECTED = "rejected"
    RUNNING = "running"
    UNCERTAIN = "uncertain"
    UNKNOWN = "unknown"
    STOPPED = "stopped"
    BLOCKED = "blocked"


def _known_tool_names():
    """Tool names from the canonical policy (the same JSON the bridge uses)."""
    try:
        with open(POLICY_PATH, "r", encoding="utf-8") as handle:
            policy = json.load(handle)
        return set(policy.get("tools", {}).keys())
    except (IOError, ValueError):
        return set()


_KNOWN_TOOLS = None


def known_tools():
    global _KNOWN_TOOLS
    if _KNOWN_TOOLS is None:
        _KNOWN_TOOLS = _known_tool_names()
    return _KNOWN_TOOLS


def _non_empty_string(value, field):
    if not isinstance(value, str) or not value.strip():
        raise DecisionError("%s must be a non-empty string" % field)
    return value.strip()


def _check_args(args):
    if args is None:
        return {}
    if not isinstance(args, dict):
        raise DecisionError("proposed_action.args must be an object")
    for key, value in args.items():
        if not isinstance(key, str):
            raise DecisionError("proposed_action.args keys must be strings")
        if key in FORBIDDEN_ARG_KEYS:
            raise DecisionError(
                "proposed_action.args may not set %r: the executor owns it / it"
                " is a hidden-field probe" % key
            )
        if isinstance(value, dict) or isinstance(value, (list, tuple, set)):
            raise DecisionError("proposed_action.args[%r] must be a JSON scalar" % key)
    return dict(args)


class Decision(object):
    """One model proposal. Only the documented handoff fields are accepted."""

    def __init__(self, goal, tool, args, reason, observation_refs=None,
                 expected_observation="", stop_if="", raw=None):
        self.goal = goal
        self.tool = tool
        self.args = args
        self.reason = reason
        self.observation_refs = observation_refs or []
        self.expected_observation = expected_observation
        self.stop_if = stop_if
        self.raw = raw

    @classmethod
    def from_dict(cls, payload):
        if not isinstance(payload, dict):
            raise DecisionError("decision must be an object")
        goal = _non_empty_string(payload.get("goal"), "goal")
        reason = _non_empty_string(payload.get("reason"), "reason")
        action = payload.get("proposed_action")
        if not isinstance(action, dict):
            raise DecisionError("proposed_action must be an object")
        tool = _non_empty_string(action.get("tool"), "proposed_action.tool")
        if tool not in known_tools():
            raise DecisionError("proposed_action.tool %r is not a PawnMind tool" % tool)
        args = _check_args(action.get("args"))
        refs = payload.get("observation_refs") or []
        if not isinstance(refs, list) or any(not isinstance(r, str) for r in refs):
            raise DecisionError("observation_refs must be a list of strings")
        return cls(
            goal=goal,
            tool=tool,
            args=args,
            reason=reason,
            observation_refs=list(refs),
            expected_observation=str(payload.get("expected_observation") or ""),
            stop_if=str(payload.get("stop_if") or ""),
            raw=payload,
        )


class Budget(object):
    """Per-run hard limits. Reaching one is a safe stop, never a retry loop."""

    def __init__(self, max_actions=300, max_consecutive_rejections=5,
                 max_consecutive_uncertain=3, max_wall_seconds=90 * 60,
                 clock=None, max_tokens=None):
        self.max_actions = int(max_actions)
        self.max_consecutive_rejections = int(max_consecutive_rejections)
        self.max_consecutive_uncertain = int(max_consecutive_uncertain)
        self.max_wall_seconds = int(max_wall_seconds)
        self.max_tokens = max_tokens
        self.clock = clock or time.time
        self.started = self.clock()
        self.actions = 0
        self.consecutive_rejections = 0
        self.consecutive_uncertain = 0
        self.tokens = 0

    @classmethod
    def from_limits(cls, limits, **overrides):
        merged = dict(DEFAULT_LIMITS)
        merged.update(limits or {})
        merged.update(overrides)
        return cls(**merged)

    # ----------------------------------------------------------- ticking
    def check_clock(self):
        elapsed = self.clock() - self.started
        if elapsed > self.max_wall_seconds:
            raise BudgetExceeded(
                "max_wall_seconds", "%d seconds elapsed" % int(elapsed))

    def tick_action(self):
        self.check_clock()
        self.actions += 1
        if self.actions > self.max_actions:
            raise BudgetExceeded("max_actions", "%d actions" % self.actions)

    def tick_success(self):
        self.consecutive_rejections = 0
        self.consecutive_uncertain = 0

    def tick_rejection(self):
        self.consecutive_rejections += 1
        self.consecutive_uncertain = 0
        if self.consecutive_rejections > self.max_consecutive_rejections:
            raise BudgetExceeded(
                "max_consecutive_rejections", "%d in a row" % self.consecutive_rejections)

    def tick_uncertain(self):
        self.consecutive_uncertain += 1
        self.consecutive_rejections = 0
        if self.consecutive_uncertain > self.max_consecutive_uncertain:
            raise BudgetExceeded(
                "max_consecutive_uncertain", "%d in a row" % self.consecutive_uncertain)

    def tick_run(self):
        # A pending (running) step is progress, not a rejection: reset the
        # rejection streak but never the uncertainty streak silently.
        self.consecutive_rejections = 0

    def note_tokens(self, count):
        if self.max_tokens is None or not count:
            return
        self.tokens += int(count)
        if self.tokens > self.max_tokens:
            raise BudgetExceeded("max_tokens", "%d tokens" % self.tokens)


def classify_response(response):
    """Pure mapping from a bridge response to a step status. Never optimistic."""
    if not isinstance(response, dict):
        return StepStatus.UNKNOWN
    status = response.get("execution_status")
    ok = response.get("ok") is True
    if status == "completed" and ok:
        return StepStatus.COMPLETED
    if status in ("prepared", "running"):
        return StepStatus.RUNNING
    if status == "uncertain":
        return StepStatus.UNCERTAIN
    if status == "rejected":
        return StepStatus.REJECTED
    if status == "failed":
        return StepStatus.REJECTED
    if ok and not status:
        # ok:true without a completed execution axis is not a success claim.
        return StepStatus.RUNNING
    if response.get("error"):
        return StepStatus.REJECTED
    return StepStatus.UNKNOWN


class StepResult(object):
    def __init__(self, status, decision=None, request=None, response=None,
                 observation=None, stop=False, stop_reason=None, hint=None):
        self.status = status
        self.decision = decision
        self.request = request
        self.response = response
        self.observation = observation
        self.stop = stop
        self.stop_reason = stop_reason
        self.hint = hint

    def __repr__(self):
        return "StepResult(%r, stop=%r, %r)" % (self.status, self.stop, self.stop_reason)


def client_takes_kwargs(client):
    """Whether ``client.action(op, **args)`` is this client's calling convention.

    Decided once from the signature (R5.1/F1). Guessing per call by catching
    ``TypeError`` was unsound: a TypeError raised *after* the client dispatched
    replayed the call, and the replay itself raised.

    Fail-open to the documented keyword convention when a signature cannot be
    read (a C callable), because that is what `BridgeClient` and `TraceRecorder`
    both implement.
    """
    try:
        signature = inspect.signature(client.action)
    except (TypeError, ValueError):
        return True

    for parameter in signature.parameters.values():
        if parameter.kind is inspect.Parameter.VAR_KEYWORD:
            return True
    return "action_id" in signature.parameters


def tool_purity(tool):
    """'pure' tools cannot change the game; everything else is treated as a write.

    Unknown tools fall back to 'write' so a missing policy entry can never widen
    what may run while a write is pending (R5.1/F2).
    """
    try:
        import sys

        agent_dir = os.path.dirname(os.path.abspath(__file__))
        if agent_dir not in sys.path:
            sys.path.insert(0, agent_dir)
        import tool_policy

        policy = tool_policy.load_policy()
        tools = policy.get("tools") or policy
        entry = tools.get(tool) or {}
        return entry.get("purity") or "write"
    except Exception:
        return "write"


class Executor(object):
    """One decision in, one verified outcome out. The model never dispatches."""

    PLAY_MODE = "play"
    TEST_MODE = "test"

    def __init__(self, client, allowed_tools, budget=None, mode="test",
                 clock=None):
        self.client = client
        self._kwargs_call = client_takes_kwargs(client)
        # None is the explicit "unrestricted, the host declares the surface"
        # opt-in. An empty iterable means deny-all: an empty declared surface
        # must never silently become a fail-open executor (W-C3-1).
        self.allowed_tools = None if allowed_tools is None else set(allowed_tools)
        self.budget = budget or Budget()
        self.mode = mode
        self.clock = clock or time.time
        self.rev = None
        self.stopped = False
        self.stop_reason = None
        # R5.1/F2: a write that came back RUNNING is still in flight. The
        # barrier lives here, not only in TraceRecorder, because a host may
        # wrap a bare BridgeClient.
        self.pending_operation = None

    # ----------------------------------------------------------- policy
    @staticmethod
    def play_allowlist_from_policy():
        """The Play surface is empty until a live session earns each tool."""
        import sys

        agent_dir = os.path.dirname(os.path.abspath(__file__))
        if agent_dir not in sys.path:
            sys.path.insert(0, agent_dir)
        import tool_policy

        policy = tool_policy.load_policy()
        return sorted(tool_policy.play_allowlist(policy))

    # ----------------------------------------------------------- reading
    def observe(self):
        return self.client.get_state()

    def _refresh_rev(self, state):
        if isinstance(state, dict) and isinstance(state.get("rev"), int):
            self.rev = state["rev"]
        return self.rev

    # ----------------------------------------------------------- executing
    def _surface_refusal(self, decision):
        if self.mode == self.PLAY_MODE:
            if decision.tool not in self.play_allowlist_from_policy():
                return (
                    "blocked_by_play_policy",
                    "tool %r has no live evidence and is not on the play allowlist"
                    % decision.tool,
                )
        if decision.tool not in known_tools():
            return ("tool_not_allowed", "tool %r is not a PawnMind tool" % decision.tool)
        if self.allowed_tools is not None and decision.tool not in self.allowed_tools:
            return ("tool_not_allowed", "tool %r is not in the declared pilot surface" % decision.tool)
        return None

    def execute(self, decision):
        if isinstance(decision, dict):
            try:
                decision = Decision.from_dict(decision)
            except DecisionError as error:
                return StepResult(StepStatus.REJECTED, stop=True,
                                  stop_reason="invalid_decision: %s" % error)

        if self.stopped:
            return StepResult(StepStatus.STOPPED, decision=decision, stop=True,
                              stop_reason=self.stop_reason)

        # W-C3-2: a host may construct Decision directly, so validate its
        # arguments at execution time too (escaped quotes in one field must not
        # let a model set rev/action_id or probe hidden fields).
        try:
            decision.args = _check_args(decision.args)
        except DecisionError as error:
            self.stopped = True
            self.stop_reason = "invalid_decision: %s" % error
            return StepResult(StepStatus.REJECTED, decision=decision, stop=True,
                              stop_reason=self.stop_reason)

        refusal = self._surface_refusal(decision)
        if refusal is not None:
            code, detail = refusal
            self.stopped = True
            self.stop_reason = detail
            return StepResult(StepStatus.BLOCKED, decision=decision, stop=True,
                              stop_reason=detail, hint=code)

        if self.pending_operation is not None and tool_purity(decision.tool) != "pure":
            try:
                self.budget.tick_rejection()
            except BudgetExceeded as error:
                return self._stop(decision, error)
            pending = self.pending_operation.get("id")
            return StepResult(
                StepStatus.BLOCKED, decision=decision,
                hint="query get_operation(%r) first; a write is still pending"
                     % (pending,),
                stop_reason="pending_operation %r" % (pending,),
            )

        try:
            self.budget.tick_action()
        except BudgetExceeded as error:
            return self._stop(decision, error)

        if self.rev is None:
            state = self.observe()
            if not isinstance(state, dict):
                return self._stop(decision, BudgetExceeded("state_unreadable", "get_state failed"))
            self._refresh_rev(state)

        action_id = uuid.uuid4().hex
        request = {"op": decision.tool, "rev": self.rev, "action_id": action_id}
        request.update(decision.args)

        response = self._act(decision.tool, action_id, self.rev, decision.args)
        status = classify_response(response)

        if status == StepStatus.REJECTED and self._error_of(response) == "stale_rev":
            # A stale revision is a pre-dispatch refusal: refresh once and replay
            # the SAME action id (idempotent), never a new id.
            state = self.observe()
            self._refresh_rev(state)
            request["rev"] = self.rev
            response = self._act(decision.tool, action_id, self.rev, decision.args)
            status = classify_response(response)
            if status == StepStatus.REJECTED and self._error_of(response) == "stale_rev":
                return self._stop(
                    decision,
                    BudgetExceeded("stale_rev", "stale revision twice; the state is not"
                                               " converging, stop instead of looping"),
                    request=request, response=response)

        if status == StepStatus.COMPLETED:
            self.budget.tick_success()
            self._release_pending(decision)
            observation = self.observe()
            self._refresh_rev(observation)
            return StepResult(StepStatus.COMPLETED, decision=decision, request=request,
                              response=response, observation=observation)

        if status == StepStatus.RUNNING:
            self.budget.tick_run()
            operation_id = response.get("operation_id") if isinstance(response, dict) else None
            if operation_id:
                self.pending_operation = {"id": operation_id, "tool": decision.tool}
            return StepResult(
                StepStatus.RUNNING, decision=decision, request=request, response=response,
                hint="call get_operation/wait_operation for %r; never re-dispatch"
                     % (operation_id,),
            )

        if status == StepStatus.UNCERTAIN:
            try:
                self.budget.tick_uncertain()
            except BudgetExceeded as error:
                return self._stop(decision, error, request=request, response=response)
            operation_id = response.get("operation_id") if isinstance(response, dict) else None
            if operation_id:
                # Query only. The original action id is the only safe replay key.
                self.client.get_operation(operation_id)
            self.stopped = True
            self.stop_reason = "uncertain operation %r is unresolved: query it and ask a human" \
                               % (operation_id,)
            return StepResult(StepStatus.UNCERTAIN, decision=decision, request=request,
                              response=response, stop=True, stop_reason=self.stop_reason)

        if status == StepStatus.REJECTED:
            self._release_pending(decision)
            try:
                self.budget.tick_rejection()
            except BudgetExceeded as error:
                return self._stop(decision, error, request=request, response=response)
            return StepResult(StepStatus.REJECTED, decision=decision, request=request,
                              response=response,
                              hint="read state and choose a different legal action;"
                                   " do not replay this one")

        # unknown / failed response shape
        return self._stop(decision, BudgetExceeded("unreadable_response", "the bridge"
                                                   " response carries no trustable status"),
                          request=request, response=response)

    def _act(self, tool, action_id, rev, args):
        """One decision, one call.

        A transport failure means "the dispatch state is unknown", not "nothing
        happened": the answer is UNCERTAIN with ``dispatch_status`` unknown, so
        the executor stops and asks for the original operation instead of
        replaying a write that may already have run (R5.1/F1).
        """
        try:
            if self._kwargs_call:
                return self.client.action(tool, rev=rev, action_id=action_id, **args)
            payload = {"rev": rev, "action_id": action_id}
            payload.update(args)
            return self.client.action(tool, payload)
        except Exception as error:  # transport failure: never a success
            return {
                "ok": False,
                "op": tool,
                "error": "transport_error",
                "execution_status": "uncertain",
                "dispatch_status": "unknown",
                "dispatched": None,
                "detail": str(error)[:200],
            }

    def _release_pending(self, decision):
        """A terminal readback of the pending operation lifts the barrier.

        Only get_operation can do it: an unrelated tool finishing says nothing
        about the write that is still in flight.
        """
        if self.pending_operation is None or decision.tool != "get_operation":
            return
        requested = decision.args.get("operation_id")
        if requested is None or requested == self.pending_operation.get("id"):
            self.pending_operation = None

    @staticmethod
    def _error_of(response):
        if not isinstance(response, dict):
            return None
        return response.get("error")

    def _stop(self, decision, error, request=None, response=None):
        self.stopped = True
        detail = "%s: %s" % (error.code, error.detail or "")
        self.stop_reason = detail
        return StepResult(StepStatus.STOPPED, decision=decision, request=request,
                          response=response, stop=True, stop_reason=detail)
