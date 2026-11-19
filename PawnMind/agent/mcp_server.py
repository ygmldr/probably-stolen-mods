#!/usr/bin/env python3
"""PawnMind MCP server (stdio).

M1 scaffold: exposes the read-only state surface plus ping/wait_for_change.
Gameplay actions are added after the bridge action layer lands.

Install:
  python -m venv .venv
  .venv/Scripts/python.exe -m pip install mcp

Run:
  python mcp_server.py --config "<...>/ModData/pawnmind/bridge.conf"
  python mcp_server.py --config "..." --tools recording
  python mcp_server.py --config "..." --probe
"""

import argparse
import functools
import os
import sys
import threading
import time
import uuid
from typing import Annotated

from pydantic import Field, StrictInt, StrictStr

try:  # mcp 2.x renamed FastMCP to MCPServer
    from mcp.server.mcpserver import MCPServer
except ImportError:  # mcp 1.x
    from mcp.server.fastmcp import FastMCP as MCPServer

# The host template launches this file as `python -m agent.mcp_server` from the
# PawnMind root, where only that root is on sys.path. `pawnmind_client` lives
# next to this file, so make the module directory importable there too.
_MODULE_DIR = os.path.dirname(os.path.abspath(__file__))
if _MODULE_DIR not in sys.path:
    sys.path.insert(0, _MODULE_DIR)

from pawnmind_client import BridgeClient, BridgeError

TOOL_SET_FULL = "full"
TOOL_SET_RECORDING = "recording"
# R5.3/A3: the surface an autonomous pilot may use. Reads are every pure tool the
# recording surface already carries; writes are the Day-1 core set only. The
# player-legality audit (2026-11-19) asked for an explicit, short, verified
# allowlist instead of "whatever the recording set happens to contain".
TOOL_SET_PILOT = "pilot"
TOOL_SET_CHOICES = (TOOL_SET_FULL, TOOL_SET_RECORDING, TOOL_SET_PILOT)

PILOT_WRITES = frozenset({
    # store front, day cycle
    "open_shop", "call_next_customer", "end_day", "sleep", "close_windows",
    "dismiss_tutorial_hints",
    # dialogue
    "advance_dialogue", "choose_dialogue",
    # negotiation and the one staging primitive
    "stage_negotiation_item", "confirm_trade", "dismiss_current_client",
    "answer_prompt",
    # stocking (A1 hardened move_item; post-readback still an open item)
    "move_item",
    # starting the recorded run goes through the native new-game menu
    "open_new_game_menu", "new_game_continue", "new_game_next_page",
    "select_new_game_option", "focus_new_game_selected_perk",
    "blur_new_game_option", "confirm_new_game",
})


def pilot_reads():
    """Every pure tool on the recording surface, from the policy (single source)."""
    try:
        import os
        import sys

        agent_dir = os.path.dirname(os.path.abspath(__file__))
        if agent_dir not in sys.path:
            sys.path.insert(0, agent_dir)
        import tool_policy

        policy = tool_policy.load_policy()
        tools = policy.get("tools") or policy
        return frozenset(
            name for name in RECORDING_TOOL_NAMES
            if (tools.get(name) or {}).get("purity") == "pure")
    except Exception:
        # Fail closed: without the policy there is no read surface to grant.
        return frozenset()



#: Recording mode registers exactly this set; every other tool stays
#: unregistered. `demo_agent.ALLOWED_OPS` must stay a subset;
#: `test_mcp_tools_mode.py` pins that relation.
# Tools a human player could equivalently use during the recording run. This
# is a whitelist, not a blacklist: a tool added to this server later does NOT
# silently join the recording surface -- it must be listed here on purpose.
#
# Excluded on purpose (see tests/test_mcp_tools_mode.py):
#   act             generic any-op entry point; bypasses every typed tool,
#                   the rev/action_id gate and the recording slot policy
#   press           invokes an arbitrary UI control; same bypass problem
#   get_ui_snapshot control enumerator that only exists to feed press
#   wait_until      host-side polling helper, not a game action (duplicates
#                   wait_for_events / wait_for_change)
#   wine_add_water  injects pure water into a bottle; no player action does
#                   this, so it would be a shortcut/cheat
#   new_game        hardcodes APPRENTICE + skipIntro=true, so it cannot express
#                   the player's own scenario/difficulty/perk choice; the menu
#                   flow (get_new_game_options .. confirm_new_game) is the only
#                   start path that preserves that choice
RECORDING_TOOL_NAMES = frozenset({
    "activate_item",
    "advance_dialogue",
    "analyze_item",
    "answer_prompt",
    "arrest_wanted",
    "bargain",
    "bargain_preview",
    "barter_accept",
    "barter_status",
    "blur_new_game_option",
    "call",
    "call_next_customer",
    "capture_screenshot",
    "choose_dialogue",
    "close_windows",
    "commissary_buy",
    "confirm_new_game",
    "confirm_trade",
    "contraband_status",
    "deliver_request",
    "dismiss_current_client",
    "dismiss_tutorial_hints",
    "end_call",
    "end_day",
    "events_status",
    "expedition_status",
    "focus_new_game_selected_perk",
    "get_action_history",
    "get_calendar",
    "get_clientele",
    "get_day_report",
    "get_item",
    "get_layout",
    "get_legal_actions",
    "get_new_game_options",
    "get_operation",
    "get_pending_prompt",
    "get_recording_save_status",
    "get_run_summary",
    "get_state",
    "go_out",
    "health_status",
    "husbandry_status",
    "inspect_ticket",
    "list_analyzers",
    "list_books",
    "list_contacts",
    "list_containers",
    "list_factions",
    "list_goods",
    "list_interactions",
    "list_inventory",
    "list_labels",
    "list_locations",
    "list_machines",
    "list_marks",
    "list_requests",
    "list_screenshots",
    "list_services",
    "list_signs",
    "list_tickets",
    "list_upgrades",
    "list_wine",
    "load_game",
    "load_recording_game",
    "lottery_status",
    "machine_collect",
    "machine_grow_status",
    "machine_insert",
    "machine_status",
    "memory_bootstrap",
    "memory_get",
    "memory_retrieve",
    "memory_status",
    "move_item",
    "new_game_continue",
    "new_game_next_page",
    "open_new_game_menu",
    "open_shop",
    "phone_advance",
    "phone_choose",
    "phone_state",
    "ping",
    "read_book",
    "read_item",
    "redeem_ticket",
    "report_client",
    "return_home",
    "scavenge",
    "security_status",
    "select_new_game_option",
    "set_pen_mode",
    "sleep",
    "stage_negotiation_item",
    "start_fermentation",
    "toggle_item",
    "toggle_service",
    "use_tool",
    "wait_for_change",
    "wait_for_events",
    "wait_operation",
    "wanted_status",
    "wine_add_ingredient",
    "wine_status",
})
# R5.3/A3: the pilot surface is the curated writes plus the policy-pure reads.
PILOT_TOOL_NAMES = frozenset(PILOT_WRITES) | pilot_reads()


CALENDAR_MIN_DAYS = 1
CALENDAR_MAX_DAYS = 14

MEMORY_MAX_LIMIT = 50
MEMORY_MAX_TOKEN_BUDGET = 100000
MEMORY_MAX_QUERY_CHARS = 500

#: D2 wait_operation bounds (milliseconds). The poll happens on the Python side;
#: it never blocks the Unity main thread.
WAIT_OPERATION_MAX_MS = 10000
WAIT_OPERATION_DEFAULT_MS = 2000

NEW_GAME_OPTION_KINDS = ("scenario", "perk", "difficulty")
NEW_GAME_PAGE_ROWS = ("available", "selected")
NEW_GAME_PAGE_DIRECTIONS = (-1, 1)

# The JSON schemas advertise the closed value sets. The tool bodies enforce
# them as well so Python callers that bypass MCP/Pydantic still fail closed
# with a stable `invalid_argument` result instead of reaching the bridge.
NewGameOptionKind = Annotated[
    str,
    Field(json_schema_extra={"enum": list(NEW_GAME_OPTION_KINDS)}),
]
NewGamePageRow = Annotated[
    str,
    Field(json_schema_extra={"enum": list(NEW_GAME_PAGE_ROWS)}),
]
NewGamePageDirection = Annotated[
    StrictInt,
    Field(json_schema_extra={"enum": list(NEW_GAME_PAGE_DIRECTIONS)}),
]
NewGameRev = StrictInt
NewGameId = StrictStr
RecordingRev = StrictInt


def _invalid_argument(field, allowed=None):
    result = {"ok": False, "error": "invalid_argument", "field": field}
    if allowed is not None:
        result["allowed"] = list(allowed)
    return result


def _check_choice(field, value, allowed, expected_type):
    if type(value) is not expected_type or value not in allowed:
        return _invalid_argument(field, allowed)
    return None


def _check_rev(rev):
    if type(rev) is not int or rev < 0:
        return _invalid_argument("rev")
    return None


def _check_operation_id(operation_id):
    if type(operation_id) is not str or not operation_id.strip():
        return _invalid_argument("operation_id")
    if len(operation_id) > 128:
        return _invalid_argument("operation_id")
    return None


def _check_wait_timeout(timeout_ms):
    if type(timeout_ms) is not int or timeout_ms < 0 or timeout_ms > WAIT_OPERATION_MAX_MS:
        return _invalid_argument("timeout_ms")
    return None


def _check_days(days):
    if (type(days) is not int
            or days < CALENDAR_MIN_DAYS
            or days > CALENDAR_MAX_DAYS):
        return _invalid_argument(
            "days", range(CALENDAR_MIN_DAYS, CALENDAR_MAX_DAYS + 1))
    return None


def _check_id(identifier):
    if type(identifier) is not str or not identifier.strip():
        return _invalid_argument("id")
    return None


def _legal_action_payload(state):
    """Split a snapshot into legal vs candidate actions, tolerating old payloads.

    The C# snapshot only emits ``legal_actions`` rows whose native guards already
    accepted. ``candidate_actions`` (schema 1.1, B module) carries actions that
    still need a native check, e.g. the four bargain directions while the panel is
    open. Older snapshots have neither section; they must degrade to empty lists,
    never raise, and never be interpreted as "everything is legal".
    """
    if not isinstance(state, dict):
        return {"rev": -1, "legal_actions": [], "candidate_actions": []}

    legal = state.get("legal_actions")
    candidates = state.get("candidate_actions")
    return {
        "rev": state.get("rev", -1),
        "legal_actions": legal if isinstance(legal, list) else [],
        "candidate_actions": candidates if isinstance(candidates, list) else [],
    }


def _first_error(*errors):
    for error in errors:
        if error is not None:
            return error
    return None


_MEMORY_API = None


def _memory_api():
    """Import the memory domain lazily so `--tools full` never needs the DB."""
    global _MEMORY_API
    if _MEMORY_API is None:
        try:
            from .memory.memories import (
                MemoryNotFound,
                MemoryStore,
                MemoryUnavailable,
                MemoryValidationError,
            )
        except ImportError:  # `python agent/mcp_server.py` form
            from memory.memories import (
                MemoryNotFound,
                MemoryStore,
                MemoryUnavailable,
                MemoryValidationError,
            )
        _MEMORY_API = (MemoryStore, MemoryNotFound, MemoryUnavailable,
                       MemoryValidationError)
    return _MEMORY_API


class _MemoryStoreProvider(object):
    """Open ``memories.db`` on first use, never for ``tools/list``.

    Tests start the server without ``--memories-db`` to enumerate tools; an
    eager open would create ``PawnMind/play/memory/memories.db`` as a side
    effect.  Lazy opening also keeps a memory-only server usable while the
    game/bridge is down.
    """

    def __init__(self, path, events_db=None):
        self.path = path
        self.events_db = events_db
        self._store = None
        self._lock = threading.Lock()

    def get(self):
        with self._lock:
            if self._store is None:
                MemoryStore = _memory_api()[0]
                self._store = MemoryStore(self.path,
                                          events_db=self.events_db)
            return self._store

    def close(self):
        with self._lock:
            if self._store is not None:
                try:
                    self._store.close()
                finally:
                    self._store = None


def _check_memory_limit(limit):
    if type(limit) is not int or limit < 1 or limit > MEMORY_MAX_LIMIT:
        return _invalid_argument("limit")
    return None


def _check_token_budget(token_budget):
    if (type(token_budget) is not int or token_budget < 1
            or token_budget > MEMORY_MAX_TOKEN_BUDGET):
        return _invalid_argument("token_budget")
    return None


def _memory_scope(item):
    return {
        "game_id": item.get("game_id"),
        "game_version": item.get("game_version"),
        "save_id": item.get("save_id"),
        "run_id": item.get("run_id"),
        "timeline_id": item.get("timeline_id"),
    }


def _memory_conditions(item):
    extra = item.get("scope_extra") or {}
    conditions = extra.get("conditions") or {}
    return conditions if isinstance(conditions, dict) else {}


def _memory_entities(item):
    extra = item.get("scope_extra") or {}
    entities = extra.get("entities") or []
    return entities if isinstance(entities, list) else []


def _bootstrap_payload(item):
    return {
        "memory_id": item["memory_id"],
        "kind": item["kind"],
        "text": item["text"],
        "status": item["status"],
        "confidence": item["confidence"],
        "why": item.get("why", ""),
        "evidence_count": item.get("evidence_count", 0),
        "contradict_count": item.get("contradict_count", 0),
        "scope": _memory_scope(item),
    }


def _retrieve_why(item):
    return ("%s match confidence=%.2f evidence=%d contradict=%d"
            % (item.get("recall_reason", "keyword"), item.get("confidence", 0.0),
               item.get("evidence_count", 0),
               item.get("contradict_count", 0)))


def _retrieve_payload(item):
    return {
        "memory_id": item["memory_id"],
        "kind": item["kind"],
        "text": item["text"],
        "status": item["status"],
        "confidence": item["confidence"],
        "recall_reason": item.get("recall_reason"),
        "matched_terms": item.get("matched_terms", []),
        "conditions": _memory_conditions(item),
        "entities": _memory_entities(item),
        "evidence_count": item.get("evidence_count", 0),
        "contradict_count": item.get("contradict_count", 0),
        "why": _retrieve_why(item),
    }


#: Tools served locally (memory) never depend on the game bridge, so a mode
#: filter must not hide them.
LOCAL_TOOL_NAMES = frozenset({"memory_bootstrap", "memory_get", "memory_retrieve",
                              "memory_status"})


def _guard_tool(function, mode_guard):
    """Wrap one tool so a stale surface refuses instead of running.

    ``functools.wraps`` keeps ``__wrapped__``/``__annotations__`` intact, so the
    MCP schema is still derived from the real signature.
    """

    @functools.wraps(function)
    def wrapper(*args, **kwargs):
        refusal = mode_guard.check()
        if refusal is not None:
            return refusal
        return function(*args, **kwargs)

    return wrapper


def build_server(client, tool_set=TOOL_SET_FULL, memory_provider=None,
                 allowed_tools=None, mode_guard=None):
    if tool_set not in TOOL_SET_CHOICES:
        raise ValueError("unknown tool set: %r" % (tool_set,))

    server = MCPServer(
        "pawnmind",
        instructions=(
            "You are the shopkeeper of a deep-space pawnshop. "
            "Call get_state before every decision. Actions are single-step: "
            "do one thing, then read the state again to verify the outcome. "
            "Never invent item ids, prices or customer names."
        ),
    )

    def tool(*args, **kwargs):
        """Register a tool only when the selected surface allows it."""

        def decorator(function):
            name = function.__name__
            if tool_set == TOOL_SET_RECORDING and name not in RECORDING_TOOL_NAMES:
                return function
            if tool_set == TOOL_SET_PILOT and name not in PILOT_TOOL_NAMES:
                return function
            # F5: expose only what the current run mode can actually execute. The
            # C# gate stays the final authority; this only stops a model from
            # seeing tools the bridge would refuse anyway.
            if (allowed_tools is not None and name not in LOCAL_TOOL_NAMES
                    and name not in allowed_tools):
                return function

            # F5b: a start-up surface is a promise. Refuse the call when the
            # bridge has since moved to another mode instead of quietly serving a
            # tool the bridge would now reject.
            guarded = function
            if mode_guard is not None and name not in LOCAL_TOOL_NAMES:
                guarded = _guard_tool(function, mode_guard)
            return server.tool(*args, **kwargs)(guarded)

        return decorator

    def with_memory(callback):
        """Run a local memory query, mapping domain errors to stable results."""
        if memory_provider is None:
            return {"ok": False, "error": "memory_unavailable",
                    "detail": "no memories database configured"}
        _, MemoryNotFound, MemoryUnavailable, MemoryValidationError = (
            _memory_api())
        try:
            store = (memory_provider.get()
                     if hasattr(memory_provider, "get") else memory_provider)
            return callback(store)
        except MemoryNotFound as exc:
            return {"ok": False, "error": "not_found", "detail": str(exc)}
        except MemoryUnavailable as exc:
            return {"ok": False, "error": "memory_unavailable",
                    "detail": str(exc)}
        except MemoryValidationError as exc:
            return {"ok": False, "error": "invalid_argument",
                    "detail": str(exc)}

    @tool()
    def get_state() -> dict:
        """Return the current shop snapshot: cash, rent, customer and negotiated item. legal_actions contains only actions whose native guards accepted; candidate_actions lists plausible actions that still need a native check (for example bargain directions while the panel is open) and must not be treated as legal."""
        return client.get_state()

    @tool()
    def memory_bootstrap(limit: int = 8, scope: dict | None = None) -> dict:
        """Read-only (no rev): verified, unexpired memories for session start, highest confidence/usage first. Its query is local and safe while the game write gate is locked. scope is the caller's identity (game_version/save_id/run_id/timeline_id); without it only version/global experience is returned, so another run's private facts never leak. Each item has memory_id/kind/text/confidence/why/evidence_count/contradict_count."""
        error = _check_memory_limit(limit)
        if error is not None:
            return error
        if scope is not None and not isinstance(scope, dict):
            return _invalid_argument("scope")

        def run(store):
            items = store.bootstrap(limit=limit, scope=scope)
            return {"ok": True, "count": len(items), "scope": scope,
                    "memories": [_bootstrap_payload(item) for item in items]}

        return with_memory(run)

    @tool()
    def memory_retrieve(query: str, limit: int = 5, scope: dict | None = None,
                        state: dict | None = None,
                        token_budget: int = 900) -> dict:
        """Read-only (no rev): recall verified rules for the current situation using FTS5, keywords and declared entities. Hidden statuses (proposed/candidate/conflict/deprecated/superseded), expired rows and other scopes are hard-filtered. state is the current game snapshot (for example {"in_game": true, "rent": {"days_left": 1}}); a conditional rule is never returned when its condition is missing or false. Each item carries recall_reason (fulltext/keyword/entity), evidence_count and contradict_count. token_budget keeps the highest-priority warning and drops the rest."""
        error = _check_memory_limit(limit)
        if error is not None:
            return error
        error = _check_token_budget(token_budget)
        if error is not None:
            return error
        if not isinstance(query, str) or not query.strip():
            return _invalid_argument("query")
        if len(query) > MEMORY_MAX_QUERY_CHARS:
            return _invalid_argument("query")
        if scope is not None and not isinstance(scope, dict):
            return _invalid_argument("scope")
        if state is not None and not isinstance(state, dict):
            return _invalid_argument("state")

        def run(store):
            result = store.retrieve(
                query, limit=limit, scope=scope, state=state,
                token_budget=token_budget)
            memories = [_retrieve_payload(item)
                        for item in result["items"]]
            return {
                "ok": True,
                "query": query,
                "count": len(memories),
                "memories": memories,
                "candidates": result["candidates"],
                "state_filtered": result["state_filtered"],
                "token_estimate": result["token_estimate"],
                "token_budget": result["token_budget"],
                "truncated": result["truncated"],
                "dropped": result["dropped"],
            }

        return with_memory(run)

    @tool()
    def memory_get(memory_id: str) -> dict:
        """Read-only (no rev): one memory with its evidence events and full revision history. Evidence rows keep event_id, evidence_grade, evidence_role (support/contradict/context) and added_at; when the events DB is configured each row also carries the event metadata. Use it to audit why a rule exists before trusting it."""
        if type(memory_id) is not str or not memory_id.strip():
            return _invalid_argument("memory_id")

        def run(store):
            memory = store.get_memory(memory_id)
            event_ids = [row["event_id"] for row in memory["evidence"]]
            details = store.event_details(event_ids)
            for row in memory["evidence"]:
                if row["event_id"] in details:
                    row["event"] = details[row["event_id"]]
            return {"ok": True, "memory": memory,
                    "revisions": store.revisions(memory_id)}

        return with_memory(run)

    @tool()
    def memory_status() -> dict:
        """Read-only (no rev): counts, status/kind distribution, schema version and file paths for the memories database and the separate events database. If the events DB is not configured or missing the events section degrades to configured=false with a reason instead of failing."""
        def run(store):
            return {"ok": True, "memories": store.status(),
                    "events": store.events_status()}

        return with_memory(run)

    @tool()
    def get_legal_actions() -> dict:
        """Return the actions that are legal right now (state-derived, includes args) plus candidate_actions. legal_actions rows were accepted by the native guards; candidate actions (availability=unknown, requires=native_guard) are plausible but unproven - execute one only through its write tool and treat an unavailable/refused result as normal, not as a bug. Old snapshots without candidate_actions degrade to an empty list."""
        return _legal_action_payload(client.get_state())

    @tool()
    def get_operation(operation_id: str) -> dict:
        """Read-only (no rev): two-axis status of one write action, {ok, known, operation_id, op, execution_status, delivery_status, dispatch_status, dispatched, run_change_status, run_change_confirmed, completion_evidence, error, rev, result_rev}. ok:true only when execution_status=completed. dispatch_status is not_dispatched|dispatched|unknown; unknown means the native call may have run, so never retry it with a new action_id (dispatched is then null). run_change_status is present for load/new-game actions: requested (accepted, run identity not confirmed yet), confirmed (the switch is proven, old evidence evicted) or unknown. completion_evidence states what the verifier observed when it promoted the operation. Use it after a lost response or an uncertain/pending write; unknown_operation means the in-process record is gone (mod restart or run change) - re-read get_state instead of replaying with a new action_id."""
        error = _check_operation_id(operation_id)
        if error is not None:
            return error
        return client.get_operation(operation_id)

    @tool()
    def wait_operation(operation_id: str, timeout_ms: int = WAIT_OPERATION_DEFAULT_MS) -> dict:
        """Read-only (no rev): bounded poll of get_operation for up to timeout_ms (0..10000, default 2000). Returns the terminal operation payload (completed/rejected/failed) or, on timeout, ok:false + status=pending + execution_status + operation_id + next - never a fake success. The poll runs in this Python process; the Unity main thread is not blocked. Do not start a new action id for the same op while it is pending."""
        error = _first_error(_check_operation_id(operation_id),
                             _check_wait_timeout(timeout_ms))
        if error is not None:
            return error
        return client.wait_operation(operation_id, timeout_ms)

    @tool()
    def get_run_summary() -> dict:
        """Compact run summary: day, shop state, cash, rent, inventory, current client."""
        state = client.get_state()
        return state.get("run_summary", {})

    @tool()
    def get_action_history(limit: int = 20) -> list:
        """Recent actions with their args, ok flag and revision."""
        return client.action("get_action_history", limit=limit)

    @tool()
    def capture_screenshot(reason: str = "manual") -> dict:
        """Capture the current game frame as evidence (JPEG, short side 768) and return its path relative to ModData/pawnmind/screenshots/. Use it right before stopping on a hard failure, or when the screen disagrees with what the bridge reports. Server-side throttling applies; returns screenshot_unavailable when capture is off."""
        return client.action("capture_screenshot", reason=reason)

    @tool()
    def get_pending_prompt() -> dict:
        """Read the native confirmation modal that currently owns the input, if any: {ok, prompt:{kind, target, body, confirm_text, cancel_text, confirm_enabled, cancel_enabled, single_button, source_op}|null}. kind is 'confirmation' (generic) or 'negotiation_confirm' (negotiation-tablet confirm panel); target names the native window, so answer by identity never by button index. Read-only, no rev. Two modals open at once => prompt_ambiguous. Call it whenever state.prompt is not null or a write returns confirmation_required, and read the text before deciding."""
        return client.action("get_pending_prompt")

    @tool()
    def close_windows(rev: StrictInt) -> dict:
        """Close the note/book/contract/deed panels, hide floating windows, and dismiss the two adapter-less overlays (tutorial control-hint overlay; dialogue/typewriter settings dialog) through their own native buttons, so the shop is visible again. Use it right after read_book/read_item/analyze_item and before ending the day; 'closed' may contain 'overlays'. Returns what was closed and what (if anything) is still open. One shot: if the screen is still dirty it reports window_readback_mismatch and you must stop, not retry; never fall back to press."""
        if isinstance(rev, bool) or not isinstance(rev, int) or rev < 0:
            return {"ok": False, "op": "close_windows", "error": "invalid_argument",
                    "field": "rev"}
        return client.action("close_windows", rev=rev)

    @tool()
    def answer_prompt(
        choice: Annotated[str, Field(
            description="'confirm' or 'cancel'.",
            json_schema_extra={"enum": ["confirm", "cancel"]})],
        rev: StrictInt,
    ) -> dict:
        """Answer exactly one option of the modal read by get_pending_prompt (routed by kind/target to the native callback; never a guessed button index). choice is 'confirm' or 'cancel'. Success requires prompt_closed:true plus the post-answer state (incl. shop_state), so 'the panel closed' and 'the confirmed action happened' stay separate facts. Unknown/ambiguous modal => prompt_ambiguous (never clicked); a changed modal => prompt_changed with dispatched:false; a disabled option => choice_unavailable. Destructive confirmations default to 'cancel' unless you understand the consequence; always read get_pending_prompt first and re-read state afterwards."""
        # Validate here instead of in the JSON schema: a schema-level enum makes
        # the SDK reject with free text, while the model needs a structured
        # error it can act on (and the bridge must never see the bad call).
        if not isinstance(choice, str) or choice not in ("confirm", "cancel"):
            return {"ok": False, "op": "answer_prompt", "error": "invalid_argument",
                    "field": "choice", "allowed": ["confirm", "cancel"]}
        if isinstance(rev, bool) or not isinstance(rev, int) or rev < 0:
            return {"ok": False, "op": "answer_prompt", "error": "invalid_argument",
                    "field": "rev"}
        return client.action("answer_prompt", choice=choice, rev=rev)

    @tool()
    def list_screenshots(limit: int = 20) -> dict:
        """List recent evidence captures, newest first, with total_count/total_bytes/budget_bytes/dropped/budget_exhausted. Read-only."""
        return client.action("list_screenshots", limit=limit)

    @tool()
    def act(action: str, args: dict, rev: int, action_id: str = "") -> dict:
        """Generic entry point: run one legal action by name with its args."""
        return client.action("act", action=action, args=args, rev=rev, action_id=action_id or None)

    @tool()
    def wait_until(condition: str, timeout_s: float = 20.0, since_rev: int = -1) -> dict:
        """Block until a state condition holds: rev_changed, client_present, client_arrived, can_call_next, dialogue_active, dialogue_choice, shop_open, shop_closed, negotiation_open."""
        deadline = time.time() + max(1.0, min(timeout_s, 60.0))
        last = None
        while time.time() < deadline:
            last = client.get_state()
            if _matches(last, condition, since_rev):
                return {"ok": True, "state": last}
            time.sleep(0.5)
        return {"ok": False, "error": "timeout", "state": last}

    @tool()
    def get_ui_snapshot(rev: int) -> dict:
        """List active UI controls (tablet buttons and plain buttons) with stable ids."""
        return client.action("get_ui_snapshot", rev=rev)

    @tool()
    def press(control_id: str, rev: int) -> dict:
        """Invoke a control from the last get_ui_snapshot by id (for windows without a typed adapter)."""
        return client.action("press", control_id=control_id, rev=rev)

    @tool()
    def list_goods(scope: str = "all") -> dict:
        """List goods on the front/back counters: scope=client (non-owned), sellable (owned+client wants), all."""
        return client.action("list_goods", scope=scope)

    @tool()
    def list_locations() -> dict:
        """List discovered night locations (dumping ground / commissary / Dr Jackson) and availability."""
        return client.action("list_locations")

    @tool()
    def go_out(location: str, rev: int) -> dict:
        """Leave the shop and travel to a night location."""
        return client.action("go_out", location=location, rev=rev)

    @tool()
    def scavenge(rev: int) -> dict:
        """Scavenge at the dumping ground; returns gained items and remaining attempts."""
        return client.action("scavenge", rev=rev)

    @tool()
    def return_home(rev: int) -> dict:
        """Return from a night location to the shop."""
        return client.action("return_home", rev=rev)

    @tool()
    def buy_wholesale(rev: int) -> dict:
        """Buy the current client's whole wholesale offer (requires isOfferWholesale)."""
        return client.action("buy_wholesale", rev=rev)

    @tool()
    def commissary_buy(identifier: str, rev: int) -> dict:
        """Buy one item from the supply station (commissary) by identifier."""
        return client.action("commissary_buy", identifier=identifier, rev=rev)

    @tool()
    def list_books() -> dict:
        """List owned/unlocked books (vanilla BOOK_TAG and paper GUIDE_PAPER_TAG)."""
        return client.action("list_books")

    @tool()
    def read_book(book_id: str, page: int = -1) -> dict:
        """Read an owned book by item id or book id; returns pages and optionally one page."""
        return client.action("read_book", book_id=book_id, page=page)

    @tool()
    def read_item(item_id: int) -> dict:
        """Read one item's visible details plus book/document flags."""
        return client.action("read_item", item_id=item_id)

    @tool()
    def list_interactions(item_id: int) -> dict:
        """List available interactions for one item: activate/toggle/target."""
        return client.action("list_interactions", item_id=item_id)

    @tool()
    def activate_item(item_id: int, rev: int) -> dict:
        """Activate an owned item (books, lottery tickets, scanners)."""
        return client.action("activate_item", item_id=item_id, rev=rev)

    @tool()
    def toggle_item(item_id: int, rev: int) -> dict:
        """Toggle an owned item (gene scanner, machine switches)."""
        return client.action("toggle_item", item_id=item_id, rev=rev)

    @tool()
    def use_tool(tool_id: int, target_id: int, rev: int) -> dict:
        """Use an owned tool on a target item (screwdriver, UV, scanner, marking pen)."""
        return client.action("use_tool", tool_id=tool_id, target_id=target_id, rev=rev)

    @tool()
    def contraband_status() -> dict:
        """Owned contraband/stolen items with levels and risk flags."""
        return client.action("contraband_status")

    @tool()
    def security_status() -> dict:
        """Security evidence, crimes, case officer and current inspection item."""
        return client.action("security_status")

    @tool()
    def list_services() -> dict:
        """Nightly services (insurance, janitorial, power, informant) and toggles."""
        return client.action("list_services")

    @tool()
    def toggle_service(id: str, on: bool, rev: int) -> dict:
        """Toggle a nightly service on/off."""
        return client.action("toggle_service", id=id, on=on, rev=rev)

    @tool()
    def husbandry_status() -> dict:
        """Owned animals/fish with health/dead state."""
        return client.action("husbandry_status")

    @tool()
    def dispatch_expedition(hireling_id: str, location_id: str, rev: int) -> dict:
        """Send a hireling to an unlocked expedition location."""
        return client.action("dispatch_expedition", hireling_id=hireling_id, location_id=location_id, rev=rev)

    @tool()
    def advance_expedition(expedition_id: str, rev: int) -> dict:
        """Advance an ongoing expedition one step."""
        return client.action("advance_expedition", expedition_id=expedition_id, rev=rev)

    @tool()
    def expedition_status() -> dict:
        """Expedition unlock, locations, hirelings and ongoing runs."""
        return client.action("expedition_status")

    @tool()
    def events_status() -> dict:
        """Active and queued store events."""
        return client.action("events_status")

    @tool()
    def get_day_report() -> dict:
        """Read this morning's report lines: power/electricity, insurance, security, storefront cleaning, rent warnings and the after-hours "opened late, missed a customer" line. Read-only, no rev. Call it after waking up (after sleep + wait_for_events) before deciding the day. `source` says which path was used: structured night_logs, the visible morning panel, or the bridge cache. `readable:false` with `reason:"morning_pending"` means the async day transition has not finished; wait for the day to change and read again instead of sleeping twice."""
        return client.action("get_day_report")

    @tool()
    def get_calendar(days: int = 7) -> dict:
        """Read the store calendar for the next `days` days (default 7, max 14; it may cross the week boundary). Each day lists the active/queued events for that day plus `rent_due`; `rent` gives the next rent day, the amount and whether it falls in the current week. Day numbers come from the live day counter, not from guesses. Read-only, no rev. Use it at the start of a run to plan around rent day, inspections and threats."""
        error = _check_days(days)
        if error is not None:
            return error
        return client.action("get_calendar", days=days)

    @tool()
    def get_clientele() -> dict:
        """What kind of customers the display case attracts: showcase types, the discerning-shopper chance and cooldown, the predicted (client, chance %, item) rows shown by the clientele panel, and the number of random normal customers. Read-only, no rev. Use it to decide what to put in the showcase and whether to expect a rich buyer before opening. The reading is cached for the rest of the game day (cached=true), so repeated calls return the same snapshot instead of re-rolling the game's RNG."""
        return client.action("get_clientele")

    @tool()
    def dismiss_tutorial_hints(rev: StrictInt) -> dict:
        """Hide the tutorial hint bubbles only: turns PlayerStore.tutorialEnable off once, closes the visible hints and hides the tutorial group. It does not unlock services, skip the intro, or change items, story clients, toggles or endless mode. Requires rev. Returns changed/tutorial_enabled/hints_hidden; re-read get_state.tutorial_hints afterwards. A tutorial_hints_readback_mismatch means stop, do not retry."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("dismiss_tutorial_hints", rev=rev)

    @tool()
    def list_signs() -> dict:
        """Owned marketing signs."""
        return client.action("list_signs")

    @tool()
    def deliver_request(id: str, rev: int) -> dict:
        """Deliver a fillable StoreRequest and claim rewards."""
        return client.action("deliver_request", id=id, rev=rev)

    @tool()
    def report_client(rev: int) -> dict:
        """Report the current customer (any reportable client; is_wanted tells the risk)."""
        return client.action("report_client", rev=rev)

    @tool()
    def barter_accept(rev: int) -> dict:
        """Accept the current client's barter offer when acceptable."""
        return client.action("barter_accept", rev=rev)

    @tool()
    def arrest_wanted(id: str, rev: int) -> dict:
        """Report/arrest a wanted person by id."""
        return client.action("arrest_wanted", id=id, rev=rev)

    @tool()
    def list_factions() -> dict:
        """Faction reputation amounts, influence and every reputation perk with threshold, unlock state, remaining points and direction (positive thresholds unlock by gaining reputation, negative ones by losing it)."""
        return client.action("list_factions")

    @tool()
    def health_status() -> dict:
        """Player wounds, luck, addiction timers."""
        return client.action("health_status")

    @tool()
    def list_requests() -> dict:
        """Active customer/faction requests and rewards."""
        return client.action("list_requests")

    @tool()
    def wanted_status() -> dict:
        """Wanted-person visit state and wanted list."""
        return client.action("wanted_status")

    @tool()
    def barter_status() -> dict:
        """Current client's barter offer and conditions."""
        return client.action("barter_status")

    @tool()
    def list_wine() -> dict:
        """List owned wine containers/bottles and finished state."""
        return client.action("list_wine")

    @tool()
    def wine_status(item_id: int) -> dict:
        """Wine bottle fermentation/quality details."""
        return client.action("wine_status", item_id=item_id)

    @tool()
    def wine_add_water(item_id: int, rev: StrictInt, ml: int = 750) -> dict:
        """Add water to a wine bottle (test/gameplay helper)."""
        return client.action("wine_add_water", item_id=item_id, ml=ml, rev=rev)

    @tool()
    def wine_add_ingredient(bottle_id: int, item_id: int, rev: int) -> dict:
        """Move a berry/yeast into a wine bottle's ingredient grid."""
        return client.action("wine_add_ingredient", bottle_id=bottle_id, item_id=item_id, rev=rev)

    @tool()
    def start_fermentation(item_id: int, rev: int) -> dict:
        """Try to start fermentation on a wine bottle."""
        return client.action("start_fermentation", item_id=item_id, rev=rev)

    @tool()
    def list_machines() -> dict:
        """List owned standard machines with state, progress and slots."""
        return client.action("list_machines")

    @tool()
    def machine_grow_status(machine_id: int) -> dict:
        """Hydroponic grow status: power/water/seed/progress."""
        return client.action("machine_grow_status", machine_id=machine_id)

    @tool()
    def machine_status(machine_id: int) -> dict:
        """Show one machine's slots and contents."""
        return client.action("machine_status", machine_id=machine_id)

    @tool()
    def machine_insert(machine_id: int, slot: int, item_id: int, rev: int) -> dict:
        """Insert an inventory item into a machine slot."""
        return client.action("machine_insert", machine_id=machine_id, slot=slot, item_id=item_id, rev=rev)

    @tool()
    def machine_collect(machine_id: int, slot: int, rev: int) -> dict:
        """Move items from a machine slot back to the backpack."""
        return client.action("machine_collect", machine_id=machine_id, slot=slot, rev=rev)

    @tool()
    def list_upgrades() -> dict:
        """List network/store upgrades with cost/unlock/prerequisite state."""
        return client.action("list_upgrades")

    @tool()
    def buy_upgrade(id: str, rev: int) -> dict:
        """Buy a network upgrade by id (also unlocks supplier phone contacts)."""
        return client.action("buy_upgrade", id=id, rev=rev)

    @tool()
    def lottery_status() -> dict:
        """Lottery unlock state, edition, day and prize table."""
        return client.action("lottery_status")

    @tool()
    def list_tickets() -> dict:
        """List owned lottery tickets with number/edition/matches/prize."""
        return client.action("list_tickets")

    @tool()
    def inspect_ticket(item_id: int) -> dict:
        """Inspect one lottery ticket."""
        return client.action("inspect_ticket", item_id=item_id)

    @tool()
    def redeem_ticket(item_id: int, rev: int) -> dict:
        """Redeem a winning lottery ticket."""
        return client.action("redeem_ticket", item_id=item_id, rev=rev)

    @tool()
    def list_contacts(include_hidden: bool = False) -> dict:
        """List phone contacts, state, cooldown and spend.

        include_hidden only expands contacts the phone book has not shown yet
        when the bridge runs with allow_test_items=true (a debug profile) and
        recording is not armed. Production and recording sessions return
        hidden_contacts_forbidden and stay on the visible contact set.
        """
        return client.action("list_contacts", include_hidden=include_hidden)

    @tool()
    def call(number: int, rev: int) -> dict:
        """Open the phone and dial a contact."""
        return client.action("call", number=number, rev=rev)

    @tool()
    def phone_state() -> dict:
        """Current phone call state and dialogue."""
        return client.action("phone_state")

    @tool()
    def phone_advance(rev: int) -> dict:
        """Advance the phone dialogue by one step."""
        return client.action("phone_advance", rev=rev)

    @tool()
    def phone_choose(index: int, rev: int) -> dict:
        """Choose a phone dialogue option by index."""
        return client.action("phone_choose", index=index, rev=rev)

    @tool()
    def end_call(rev: int) -> dict:
        """End the current phone call."""
        return client.action("end_call", rev=rev)

    @tool()
    def list_labels(item_id: int) -> dict:
        """List editable label categories and conditions for an item."""
        return client.action("list_labels", item_id=item_id)

    @tool()
    def set_label(item_id: int, category: str, condition_id: str, rev: int) -> dict:
        """Set one editable label condition on an item."""
        return client.action("set_label", item_id=item_id, category=category, condition_id=condition_id, rev=rev)

    @tool()
    def list_analyzers() -> dict:
        """List owned analyzers (water strip/aquascan, chem scanner, plant analyzer, gene scanner)."""
        return client.action("list_analyzers")

    @tool()
    def analyze_item(analyzer_id: int, target_id: int, rev: int) -> dict:
        """Run an analyzer on a target and return its visible result fields."""
        return client.action("analyze_item", analyzer_id=analyzer_id, target_id=target_id, rev=rev)

    @tool()
    def set_pen_mode(pen_id: int, mode: int, rev: int) -> dict:
        """Set marking pen mode: 0 red, 1 purple, 2 green, 3 blue, 4 orange, 5 eraser."""
        return client.action("set_pen_mode", pen_id=pen_id, mode=mode, rev=rev)

    @tool()
    def list_marks() -> dict:
        """List marked fish/animals with their color."""
        return client.action("list_marks")

    @tool()
    def list_containers() -> dict:
        """List player containers (main/front/back/showcase/hidden/afterhour/trash/sold) with counts."""
        return client.action("list_containers")

    @tool()
    def list_inventory(container: str = "main", search: str = "", page: int = 1, page_size: int = 20) -> dict:
        """Paginated inventory for one container; search matches item name or identifier."""
        return client.action("list_inventory", container=container, search=search, page=page, page_size=page_size)

    @tool()
    def move_item(item_id: int, target: str, rev: int, x: int = -1, y: int = -1,
                  rotate: bool = False, turns: int = 0) -> dict:
        """Move one item; omit x/y to auto-place. turns=0..3 rotates 0/90/180/270 degrees."""
        return client.action("move_item", item_id=item_id, target=target, x=x, y=y,
                             rotate=rotate, turns=turns, rev=rev)

    @tool()
    def get_layout(container: str = "main") -> dict:
        """Return the grid layout (width/height, rows of item ids) and item positions for a container."""
        return client.action("get_layout", container=container)

    @tool()
    def get_item(item_id: int) -> dict:
        """Full visible details for one item id: container, value, count, lock, size, descriptions."""
        return client.action("get_item", item_id=item_id)

    @tool()
    def ping() -> dict:
        """Round-trip check used to verify the in-game bridge is alive."""
        return client.action("ping")

    @tool()
    def open_shop(rev: int) -> dict:
        """Open the shop shutter. Re-read get_state afterwards to confirm."""
        return client.action("open_shop", rev=rev)

    @tool()
    def call_next_customer(rev: int) -> dict:
        """Call the next customer to the counter. Re-read get_state afterwards."""
        return client.action("call_next_customer", rev=rev)

    @tool()
    def advance_dialogue(rev: int) -> dict:
        """Advance the current non-choice dialogue line. Re-read get_state afterwards."""
        return client.action("advance_dialogue", rev=rev)

    @tool()
    def choose_dialogue(index: int, rev: int) -> dict:
        """Pick a dialogue choice by index (0-based). Re-read get_state afterwards."""
        return client.action("choose_dialogue", index=index, rev=rev)

    @tool()
    def end_day(rev: int, dismiss_waiting: bool = False) -> dict:
        """Close the shop for the day. Refused with cannot_end_day while an important client (the landlord) is queued, and with customers_waiting when ordinary customers are still queued: the game asks before throwing them out, so pass dismiss_waiting=true only when you deliberately give up on them. Re-read get_state afterwards."""
        return client.action("end_day", rev=rev, dismiss_waiting=dismiss_waiting)

    @tool()
    def bargain_preview(direction: str, percent: int) -> dict:
        """Preview one bargain: success/leave/refuse chance, price before/after, reputation estimate."""
        return client.action("bargain_preview", direction=direction, percent=percent)

    @tool()
    def bargain(direction: str, percent: int, rev: int) -> dict:
        """One bargain offer: direction is sell_discount|sell_markup|buy_markup|buy_discount; percent is 10|25|50|75."""
        return client.action("bargain", direction=direction, percent=percent, rev=rev)

    @tool()
    def get_new_game_options() -> dict:
        """Read the current native new-game menu: visible options, current selection, rules and pagination. Read-only; no rev or id."""
        return client.action("get_new_game_options")

    @tool()
    def open_new_game_menu(rev: NewGameRev) -> dict:
        """Open the native new-game menu (one UI step). Re-read get_new_game_options afterwards."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("open_new_game_menu", rev=rev)

    @tool()
    def select_new_game_option(kind: NewGameOptionKind, id: NewGameId, rev: NewGameRev) -> dict:
        """Select one scenario/perk/difficulty card in the open menu (one native click). Re-read get_new_game_options afterwards to verify the actual selection."""
        error = _first_error(
            _check_choice("kind", kind, NEW_GAME_OPTION_KINDS, str),
            _check_id(id),
            _check_rev(rev),
        )
        if error is not None:
            return error
        return client.action("select_new_game_option", kind=kind, id=id, rev=rev)

    @tool()
    def new_game_next_page(row: NewGamePageRow, direction: NewGamePageDirection, rev: NewGameRev) -> dict:
        """Turn one new-game page: row=available|selected, direction=-1 (previous) or 1 (next). Re-read get_new_game_options afterwards."""
        error = _first_error(
            _check_choice("row", row, NEW_GAME_PAGE_ROWS, str),
            _check_choice("direction", direction, NEW_GAME_PAGE_DIRECTIONS, int),
            _check_rev(rev),
        )
        if error is not None:
            return error
        return client.action("new_game_next_page", row=row, direction=direction, rev=rev)

    @tool()
    def focus_new_game_selected_perk(id: NewGameId, rev: NewGameRev) -> dict:
        """Move native focus/hover onto one already-selected perk; this does not select or unselect it. Re-read get_new_game_options afterwards. Fails closed if the bridge cannot focus that card."""
        error = _first_error(_check_id(id), _check_rev(rev))
        if error is not None:
            return error
        return client.action("focus_new_game_selected_perk", id=id, rev=rev)

    @tool()
    def blur_new_game_option(id: NewGameId, rev: NewGameRev) -> dict:
        """Trigger one native pointer-exit on the currently hovered perk card; this does not change the selection. Use it after focus_new_game_selected_perk returns hover_conflict (for example a same-id available card kept the hover). Single step; re-read get_new_game_options afterwards."""
        error = _first_error(_check_id(id), _check_rev(rev))
        if error is not None:
            return error
        return client.action("blur_new_game_option", id=id, rev=rev)

    @tool()
    def new_game_continue(rev: NewGameRev) -> dict:
        """Continue one intermediate new-game page. Does not confirm the first-run prompt or press difficulty Start; re-read get_new_game_options afterwards."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("new_game_continue", rev=rev)

    @tool()
    def get_recording_save_status() -> dict:
        """Read-only status for the dedicated recording slot. Recording is locked by default. Returns only the configured/bound slot and its binding fingerprint (slot, run id, run number, start type, start scenario); it never lists other slots, previews or paths. No rev; never writes, saves or starts a game."""
        return client.action("get_recording_save_status")

    @tool()
    def save_recording_game(rev: RecordingRev) -> dict:
        """Save once through the native save. Recording is locked by default; this only works after the dedicated recording slot is bound, and it only saves that bound slot (no slot parameter). Re-read get_recording_save_status afterwards."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("save_recording_game", rev=rev)

    @tool()
    def load_recording_game(rev: RecordingRev) -> dict:
        """Load the bound dedicated recording slot once. Recording is locked by default; only available after that slot is bound, and it can only load the bound slot (no slot parameter). Re-read get_recording_save_status afterwards."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("load_recording_game", rev=rev)

    @tool()
    def confirm_new_game(rev: RecordingRev) -> dict:
        """Grant exactly one native new-game write permission for the dedicated recording slot. Recording is locked by default; requires an armed bindable slot. The native Start triggers the game's InitialSave (a real disk write); when the first-run confirm prompt is visible, this op also clicks its native yes once. It does not retry or pick a slot. Re-read get_recording_save_status and get_new_game_options afterwards."""
        error = _check_rev(rev)
        if error is not None:
            return error
        return client.action("confirm_new_game", rev=rev)

    @tool()
    def new_game(rev: int) -> dict:
        """Start a fresh apprentice run from the main menu. Never touches existing save slots."""
        return client.action("new_game", rev=rev)

    @tool()
    def load_game(slot: int, rev: int) -> dict:
        """Load a save slot from the main menu (slot ids are listed in get_state.save_slots)."""
        return client.action("load_game", slot=slot, rev=rev)

    @tool()
    def sleep(rev: int, dismiss_waiting: bool = False) -> dict:
        """Sleep into the next morning: settles night services, autosaves and advances the day. Only legal after the shop is CLOSED for the day (call end_day first): otherwise not_closed. It never skips the landlord: cannot_end_day while an important client waits, customers_waiting unless dismiss_waiting=true."""
        return client.action("sleep", rev=rev, dismiss_waiting=dismiss_waiting)

    @tool()
    def save_game(rev: int) -> dict:
        """Save the current run. Re-read get_state afterwards; saving is synchronous."""
        return client.action("save_game", rev=rev)

    @tool()
    def dismiss_current_client(rev: StrictInt) -> dict:
        """Send the current client away through the game's own dismissal path. Escape hatch for a client you cannot serve (for example a seller who arrived with nothing on the table, so there is no trade to accept): while a client is present, call_next_customer and end_day both refuse. Refused with dialogue_active while a dialogue is running (finish it with advance_dialogue first)."""
        return client.action("dismiss_current_client", rev=rev)

    @tool()
    def confirm_trade(item_id: int, side: str, rev: int) -> dict:
        """Buy (side=buy) or sell (side=sell) the item staged on the negotiation table by pressing the game's own accept button. Every known silent native refusal branch is pre-checked (trade_locked, intent_undefined, business_complete, item_not_staged, barter_not_acceptable, cannot_afford, gun_order_not_accepted, cannot_buy_from_table, client_refuses_item) and returns dispatched=false without clicking. ok:true only when execution_status=completed (business_complete observed for the same client); a silent native return is uncertain - query get_operation, do not retry with a new action_id. The closing dialogue is a separate workflow: call advance_dialogue next."""
        return client.action("confirm_trade", item_id=item_id, side=side, rev=rev)

    @tool()
    def stage_negotiation_item(item_id: int, rev: int) -> dict:
        """Stage one item on the weighted/back negotiation counter for the current client, through the same native placement path a player drag uses (no direct assignment of currentNegociatedItem, ownership, intent or price). Pre-checks the current client, the item container/ownership, the current staged item, the negotiation UI and revision and refuses with a stable error (dispatched=false) when a trustworthy field is missing (no_client, client_unreadable, item_not_found, item_sold, item_unreachable, not_owned, already_owned, intent_undefined, modal_open, negotiation_unreadable, back_counter_full). Returns staged_item_id_before, staged_item_id_after, negotiation_open and client_identity_unchanged: completed only when staged_item_id_after equals item_id, the item sits on the weighted counter, the tablet is open and the client identity is unchanged. A silent native return or a mismatched readback is uncertain, never ok:true - read get_state.negotiated_item and do not retry the same item with a new action_id (a different item is allowed); confirm_trade is the next step only after completed."""
        return client.action("stage_negotiation_item", item_id=item_id, rev=rev)

    @tool()
    def wait_for_events(since_rev: int = -1, timeout_ms: int = 25000) -> dict:
        """Block until the game revision changes; returns the fresh state (long-poll, no busy loop)."""
        return client.wait_events(since_rev, timeout_ms)

    @tool()
    def wait_for_change(since_rev: int, timeout_s: float = 20.0) -> dict:
        """Block until the shop revision changes (customer arrives, dialogue advances)."""
        deadline = time.time() + max(1.0, min(timeout_s, 60.0))
        last = None
        while time.time() < deadline:
            last = client.get_state()
            if last.get("rev", -1) != since_rev:
                return last
            time.sleep(0.5)
        return {"ok": False, "error": "timeout", "state": last}

    return server


def _matches(state, condition, since_rev):
    if condition == "rev_changed":
        return state.get("rev", -1) != since_rev
    if condition == "client_present":
        return state.get("client") is not None
    if condition == "client_arrived":
        return bool(state.get("client_arrived"))
    if condition == "can_call_next":
        return bool(state.get("can_call_next"))
    if condition == "dialogue_active":
        dialogue = state.get("dialogue") or {}
        return bool(dialogue) and not dialogue.get("ended")
    if condition == "dialogue_choice":
        dialogue = state.get("dialogue") or {}
        return bool(dialogue.get("choice"))
    if condition == "shop_open":
        return state.get("shop_state") == "OPEN"
    if condition == "shop_closed":
        return state.get("shop_state") == "CLOSED"
    if condition == "negotiation_open":
        return bool(state.get("negotiation_open"))
    raise ValueError("unknown condition: " + condition)


def probe(client):
    print("health:", client.health())
    print("state: ", client.get_state())
    print("ping:  ", client.action("ping"))


def _recording_slot_from_env():
    """Optional ``recording_slot`` annotation for trace events.

    The dedicated recording slot is normally only known after the native
    binding, which happens later than MCP start-up. An operator who already
    knows it can pin ``PAWNMIND_RECORDING_SLOT``; the value is copied onto the
    trace events for audit only and grants no permission by itself.
    """
    raw = os.environ.get("PAWNMIND_RECORDING_SLOT", "")
    try:
        slot = int(raw)
    except (TypeError, ValueError):
        return None
    return slot if slot >= 0 else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=None, help="path to bridge.conf")
    parser.add_argument(
        "--mode",
        choices=("auto", "bootstrap", "play", "test"),
        default=os.environ.get("PAWNMIND_MODE", "auto"),
        help=(
            "register only the tools the bridge run mode can execute: bootstrap, "
            "play (live-evidenced only), test (no filtering). auto follows /health. "
            "An unreachable, unknown or mismatched bridge locks the surface down to "
            "the local memory tools instead of exposing the unfiltered test set"
        ),
    )
    parser.add_argument(
        "--tools",
        choices=TOOL_SET_CHOICES,
        default=TOOL_SET_FULL,
        help=(
            "tool surface to register: full (all 117 tools, default) or "
            "recording (107 player-equivalent tools; act/press/debug shortcuts, "
            "save_game and the hardcoded new_game are left out)"
        ),
    )
    parser.add_argument("--probe", action="store_true", help="check the bridge and exit")
    parser.add_argument(
        "--memory",
        choices=("off", "on"),
        default=os.environ.get("PAWNMIND_MEMORY", "off"),
        help="record every tool call into the local event store (P1)",
    )
    parser.add_argument(
        "--memory-db",
        default=os.environ.get("PAWNMIND_MEMORY_DB", None),
        help="path to memory.db (default: <PawnMind>/play/memory/memory.db)",
    )
    parser.add_argument(
        "--memories-db",
        default=os.environ.get("PAWNMIND_MEMORIES_DB", None),
        help="path to the separate memories.db (default: "
             "<PawnMind>/play/memory/memories.db)",
    )
    parser.add_argument(
        "--trace-recording",
        default=None,
        metavar="PATH",
        help=(
            "append a trustworthy JSONL call recording (one event before and "
            "one after every MCP call) to PATH. The server refuses to start if "
            "the recording cannot be opened: a run must never silently lose its "
            "evidence. --probe exits before the recorder starts"
        ),
    )
    args = parser.parse_args()

    # The bridge reports the policy build it was compiled with; a mismatch is
    # refused before any write leaves the MCP server.
    expected_policy_sha256 = None
    try:
        import tool_policy as _tool_policy
        expected_policy_sha256 = _tool_policy.policy_sha256(_tool_policy.load_policy())
    except Exception:  # a missing policy file must not stop read-only use
        expected_policy_sha256 = None

    try:
        client = BridgeClient(args.config, expected_policy_sha256=expected_policy_sha256)
    except (BridgeError, OSError) as error:
        print("pawnmind: " + str(error), file=sys.stderr)
        return 2

    if args.probe:
        probe(client)
        return 0

    recorder = None
    memory_events_db = args.memory_db
    if args.memory == "on":
        # Works both as `python -m agent.mcp_server` (package form) and as
        # `python agent/mcp_server.py` (script form, where relative imports fail).
        try:
            from .memory.recorder import RecordingClient
            from .memory.store import EventStore, MemoryUnavailable
        except ImportError:
            from memory.recorder import RecordingClient
            from memory.store import EventStore, MemoryUnavailable

        db_path = args.memory_db or _default_memory_db()
        memory_events_db = db_path
        try:
            store = EventStore(db_path)
            version = "unknown"
            try:
                version = str(client.health().get("game_version") or "unknown")
            except Exception:
                pass
            session = store.start_session(
                agent_session_id=str(uuid.uuid4()),
                game_version=version,
                game_mvid=None,
                instance_id=os.environ.get("PAWNMIND_INSTANCE_ID", "local-01"),
            )
            recorder = RecordingClient(client, store, session)
            client = recorder
            print("pawnmind: recording history to %s" % db_path, file=sys.stderr)
        except MemoryUnavailable as error:
            print("pawnmind: memory unavailable, refusing to run: %s" % error,
                  file=sys.stderr)
            return 4

    # R5/C2: the trustworthy call layer. Wrapped after the memory recorder so
    # every MCP tool call produces exactly one intent/result event pair. When
    # requested it is mandatory: an unopenable recording is a start-up failure,
    # never a silent run without evidence (fail closed).
    trace_recorder = None
    if args.trace_recording:
        try:
            from .trace_recorder import TraceRecorder, TraceRecorderError
        except ImportError:
            try:
                from trace_recorder import TraceRecorder, TraceRecorderError
            except ImportError as error:
                print("pawnmind: trace recording unavailable, refusing to run"
                      " unrecorded: %s" % error, file=sys.stderr)
                return 5
        try:
            trace_recorder = TraceRecorder(
                client,
                args.trace_recording,
                actor="mcp",
                model_id=os.environ.get("PAWNMIND_MODEL_ID"),
                policy_sha256=expected_policy_sha256,
                tool_surface=args.tools,
                host_version=args.mode,
                recording_slot=_recording_slot_from_env(),
            )
        except (TraceRecorderError, OSError) as error:
            print("pawnmind: cannot open trace recording %s: %s; refusing to"
                  " run without a trustworthy recording"
                  % (args.trace_recording, error), file=sys.stderr)
            return 5
        client = trace_recorder
        print("pawnmind: recording MCP call trace to %s"
              % args.trace_recording, file=sys.stderr)

    memory_provider = _MemoryStoreProvider(
        args.memories_db or _default_memories_db(),
        events_db=memory_events_db)
    try:
        mode, reason = _resolve_mode(client, args.mode)
        allowed = allowed_tools_for_mode(mode)
        if mode == "locked":
            print("pawnmind: tool surface locked (%s); only the local memory tools "
                  "are registered" % reason, file=sys.stderr)
        build_server(client, args.tools,
                     memory_provider=memory_provider,
                     allowed_tools=allowed,
                     mode_guard=_ModeGuard(client, mode)).run()
    finally:
        memory_provider.close()
        if recorder is not None:
            recorder.store.close()
        if trace_recorder is not None:
            trace_recorder.close()
    return 0


#: Run modes the bridge can report and the MCP server can honour.
BRIDGE_MODES = ("bootstrap", "play", "test")

#: The fail-closed surface: no game tool at all, only the local memory tools.
LOCKED_MODE = "locked"


def allowed_tools_for_mode(mode, policy=None):
    """Tools a run mode may expose.

    ``None`` means "no filtering" and is reserved for the explicitly requested
    test surface. Every unresolved state maps to ``locked`` -> an empty set, so a
    missing bridge can never expose the full 117-tool surface (R4/F5b).
    """
    import tool_policy

    if mode == LOCKED_MODE:
        return set()

    policy = policy or tool_policy.load_policy()
    if mode == "play":
        # Only reviewed live evidence counts: never the candidate allowlist.
        return set(tool_policy.play_allowlist(policy))
    if mode == "bootstrap":
        return set(tool_policy.allowlist(policy, mode))
    if mode == "test":
        return None
    raise ValueError("unknown mode: %r" % (mode,))


def _bridge_mode(client):
    """The mode the bridge reports, or ``None`` when it cannot be trusted."""
    try:
        health = client.health() or {}
    except Exception:
        return None
    mode = health.get("run_mode")
    return mode if mode in BRIDGE_MODES + (LOCKED_MODE,) else None


def _resolve_mode(client, requested):
    """Resolve the mode and explain it; unresolved always means ``locked``.

    R4/F5b: the test surface is unfiltered, so it may only be enabled by an
    explicit ``--mode test`` *and* a bridge that really reports ``test``. An
    unreachable bridge, a missing/invalid ``run_mode`` and any requested/reported
    mismatch all lock the surface down.
    """
    reported = _bridge_mode(client)

    if requested == "auto":
        if reported is None:
            return LOCKED_MODE, "the bridge did not report a usable run_mode"
        return reported, "bridge run_mode=%s" % reported

    if reported is None:
        return LOCKED_MODE, "the bridge did not report a usable run_mode"
    if reported != requested:
        return LOCKED_MODE, "requested %s but the bridge reports %s" % (requested, reported)
    return requested, "requested %s and the bridge agrees" % requested


def _detect_mode(client, requested):
    """Backwards-compatible wrapper around :func:`_resolve_mode`."""
    return _resolve_mode(client, requested)[0]


class _ModeGuard:
    """Refuses calls once the bridge no longer matches the registered surface.

    The MCP tool registry is built once at start-up. The C# gate stays the final
    authority, but a long-running session must not keep offering tools the bridge
    has since locked (R4/F5-05). The health fingerprint is cached briefly so a
    normal call does not pay for an extra HTTP round trip every time.
    """

    RECHECK_SECONDS = 2.0

    def __init__(self, client, mode, interval=None):
        self.client = client
        self.mode = mode
        # Tests shrink the window; production keeps the short cache.
        try:
            configured = float(os.environ.get("PAWNMIND_MODE_RECHECK_SECONDS", ""))
        except ValueError:
            configured = None
        if configured is None or configured < 0:
            configured = self.RECHECK_SECONDS if interval is None else interval
        self.interval = configured
        self._checked_at = 0.0
        self._refusal = None

    def check(self):
        now = time.monotonic()
        if now - self._checked_at < self.interval:
            return self._refusal
        self._checked_at = now
        self._refusal = self._evaluate()
        return self._refusal

    def _evaluate(self):
        try:
            reported = _bridge_mode(self.client)
        except Exception:
            return None
        if reported is None:
            # The bridge is gone or unreadable: the call itself will fail.
            return None
        if reported == self.mode:
            return None
        return {
            "ok": False,
            "error": "mode_changed",
            "detail": (
                "the bridge run_mode is now %r but this MCP server registered the "
                "%r surface; restart the MCP server before continuing" % (reported, self.mode)
            ),
        }


def _default_memory_db():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(root, "play", "memory", "memory.db")


def _default_memories_db():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(root, "play", "memory", "memories.db")


if __name__ == "__main__":
    sys.exit(main())
