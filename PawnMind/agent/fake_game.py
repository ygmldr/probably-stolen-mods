"""Offline fake game bridge for the S6B demo — **TEST DOUBLE ONLY**.

This module speaks the same HTTP shape as the real `PawnMind` bridge
(127.0.0.1 + Bearer token + `/health`, `/state`, `/action`, `rev` checks and
`action_id` idempotency) but it is **not** the game and it is **not** an
adapter for it:

* it never loads Unity, the game assembly, a save file or the network;
* its menu, economy and rent numbers are canned test data;
* it deliberately implements only the subset the S6B path needs, with a
  simplified trading model (no customer negotiation, no bargaining);
* it must never be used for a real recording. The real bridge is a C# mod
  inside the game and is verified in S8.

Semantics mirrored from the C# side (names of ops/error codes/JSON keys are
taken from `src/Bridge/*` so the demo can be re-pointed at the real bridge):

* REV discipline: every write needs an ``int`` ``rev`` equal to the current
  revision, otherwise ``bad_rev`` / ``missing_rev`` / ``stale_rev``.
* ACTION_ID discipline: every write needs a non-empty ``action_id``; a replay
  with the same id and arguments returns the recorded first response, a replay
  with different arguments returns ``action_conflict``.
* New-game menu: ``closed -> specialization -> perks -> difficulty`` with
  visible-page options, hidden/off-page/locked/unknown rejections, page turns,
  the S3B hover dance (``ambiguous_target`` / ``hover_conflict``) and
  ``difficulty_start_forbidden`` for ``new_game_continue`` on the last page.
* Recording slot: ``disabled`` (not configured), ``armed`` (not bound) and
  ``bound``; ``confirm_new_game`` performs the one native start + InitialSave
  and binds the configured slot, a slot-allocation mismatch fails closed.
* First week: ``confirm_trade`` (atomic cash/inventory, ``cannot_afford``),
  landlord ``choose_dialogue`` (``choice_disabled`` when the cash is short),
  ``end_day`` (closes the shop, does *not* advance the day), ``sleep``
  (advances the day, decrements the rent countdown, auto-saves, readback).
* Receipts: one ``last_payment`` per payment, reused verbatim on later reads;
  a controlled load resets the live receipt tracker like the C# bridge does.

R5 contract mirror (L1-fake, scenarios in ``tests/test_fake_scenarios.py``):

* ``stage_negotiation_item`` mirrors ``Bridge/NegotiationStagingService``: the
  response carries ``staged_item_id_before/after``, ``negotiation_open``,
  ``client_identity_unchanged``, ``item_on_weighted_counter``,
  ``native_primitive`` and nested ``evidence``; a pre-dispatch guard refusal is
  ``dispatched:false``/``rejected`` and a silent native return is
  ``uncertain``/``outcome_unverified`` (never ``ok:true``). Item id ``0`` means
  "no staged item", exactly like the C# adapter.
* When a customer is at the counter (a host-side fixture injects it),
  ``confirm_trade`` mirrors ``Bridge/TradeService``: ok:true only with
  per-trade evidence (``items_transferred`` or ``cash_moved``) plus
  ``client_unchanged``; the client-budget gate refuses
  ``client_budget_insufficient`` before the native click. Without a customer
  the fake keeps its older simplified store trade model (used by the S6B
  contract tests).
* ``get_pending_prompt``/``answer_prompt`` mirror ``Bridge/PromptService``: the
  typed ``negotiation_confirm``/``negotiation_tablet_confirm`` panel, a
  ``prompt_closed`` + ``state.shop_state`` readback, and ``no_prompt`` with
  zero native clicks when nothing is open.
* ``sleep`` can run the asynchronous model (``async_sleep=True``): the dispatch
  is ``running``/``pending``, a missing day change is inconclusive, a later
  observed day advance promotes the operation to ``completed`` (completion
  evidence ``source:"day_readback"``) and the 10s observation window degrades
  to ``sleep_unconfirmed``/``uncertain`` without ever reporting
  ``failed``/``day_not_changed``.
* Every write is tracked in-process and queryable at ``GET /operation?id=``
  (including ``"*"`` with ``count``/``total_non_terminal``/``truncated``) with
  the C# two-axis envelope (``operation_id``/``execution_status``/
  ``delivery_status``/``dispatch_status``/``dispatched``) and the call-scoped
  duplicate refusal ``operation_pending`` (<- ``confirm_trade`` and
  ``stage_negotiation_item`` share one fingerprint scope).

The R5 behaviours are deterministic: the clock is simulated (``advance_clock``)
and every async transition is driven by a host hook (``finish_sleep``), never by
the wall clock.

Host-only helper (never an MCP tool): :meth:`FakeGame.return_to_menu` models
the human returning the game to the main menu before a controlled load.
"""

import copy
import json
import secrets
import threading
import urllib.parse
from datetime import datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"

MENU_PAGE_SIZE = 4
SELECTED_PERK_PAGE_SIZE = 10
MAX_PERK_POINTS = 3
BASE_PERK_COUNT = 4

START_DAY = 1
START_CASH = 25
RENT_DUE = 100
RENT_PERIOD_DAYS = 7
DEFAULT_RECORDING_SLOT = 17

# Canned /health identity. Tests may edit `FakeGame.health_payload` to exercise
# locked gates or malformed health payloads; they never point at the real game.
FAKE_GAME_VERSION = "VERSION 100-Rev13"
FAKE_ASSEMBLY_MVID = "911508a6-e0eb-4cc0-91e8-e14b16ea28d7"


def _policy_sha256():
    """The fake bridge mirrors the real /health policy fingerprint.

    A real build reports the policy it was compiled from; the Python client
    refuses writes when that fingerprint is missing (fail closed), so the test
    double must report the canonical policy hash too.
    """
    try:
        import tool_policy
        return tool_policy.policy_sha256(tool_policy.load_policy())
    except Exception:  # keep the fake importable if the policy file is absent
        return None


FAKE_POLICY_SHA256 = _policy_sha256()


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


SCENARIOS = (
    {"id": "fence", "display_name": "Fence",
     "description": "Move goods with no questions asked."},
    {"id": "scavenger", "display_name": "Scavenger",
     "description": "You know where the good junk hides."},
    {"id": "courier", "display_name": "Courier",
     "description": "Fast hands, faster feet."},
    {"id": "prospector", "display_name": "Prospector",
     "description": "Paid to dig, not to ask.", "enabled": False,
     "lock_reason": "locked:requires_completion"},
    {"id": "medic", "display_name": "Medic",
     "description": "Patch up what the vacuum chews."},
    {"id": "pilot", "display_name": "Pilot",
     "description": "Fly anything with a seat."},
)
HIDDEN_SCENARIOS = (
    {"id": "smuggler", "display_name": "Smuggler",
     "description": "Never shown by the native menu."},
)

PERKS = (
    {"id": "tough", "display_name": "Tough", "cost": 2,
     "description": "Shrug off the first bad deal."},
    {"id": "saving", "display_name": "Saving", "cost": 1,
     "description": "A little extra cash every week."},
    {"id": "charming", "display_name": "Charming", "cost": 2,
     "description": "Customers open up faster."},
    {"id": "lucky", "display_name": "Lucky", "cost": 1,
     "description": "Sometimes the junk is worth more."},
    {"id": "nightowl", "display_name": "Night Owl", "cost": 1,
     "description": "You lose less sleep."},
    {"id": "handy", "display_name": "Handy", "cost": 1,
     "description": "Repairs cost less."},
)

DIFFICULTIES = (
    {"id": "apprentice", "display_name": "Apprentice",
     "description": "The shop starts small and forgiving."},
    {"id": "journeyman", "display_name": "Journeyman",
     "description": "Tighter margins, fewer mistakes allowed."},
    {"id": "master", "display_name": "Master",
     "description": "Locked until the shop has a history.", "enabled": False,
     "lock_reason": "locked:requires_completion"},
)

# One day's market: a cheap flip and a crate that needs real capital.
OFFERS = (
    {"identifier": "scrap_core", "name": "Scrap Core", "buy": 12, "sell": 22},
    {"identifier": "cargo_crate", "name": "Cargo Crate", "buy": 60, "sell": 110},
)

READ_ONLY_OPS = frozenset({
    "get_new_game_options",
    "get_pending_prompt",
    "get_recording_save_status",
    "list_goods",
    "list_inventory",
})
WRITE_OPS = frozenset({
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_next_page",
    "focus_new_game_selected_perk",
    "blur_new_game_option",
    "new_game_continue",
    "confirm_new_game",
    "save_recording_game",
    "load_recording_game",
    "confirm_trade",
    "choose_dialogue",
    "answer_prompt",
    "end_day",
    "sleep",
    "stage_negotiation_item",
})

# R5-C4: mirrors RecordingSaveService.SleepObservationWindowMs (A2). A sleep
# whose day has not been observed to change within this window is *uncertain*,
# never failed: the native blackscreen transition is asynchronous.
SLEEP_OBSERVATION_WINDOW_MS = 10000

# R5-B1: stable refusal code -> human detail, mirrored from
# Bridge/NegotiationStagingService.DetailFor. The fake never invents a success
# field for a pre-dispatch refusal.
STAGE_REFUSAL_DETAILS = {
    "not_in_game": "no_player_store",
    "no_client": "no_current_client",
    "client_unreadable": "client_intent_unreadable",
    "staging_read_failed": "staged_item_unreadable",
    "negotiation_unreadable": "negotiation_ui_unwired",
    "modal_open": "blocking_modal_open",
    "item_not_found": "item_not_found",
    "item_sold": "item_in_sold_container",
    "item_unreadable": "item_state_unreadable",
    "item_unreachable": "item_container_not_reachable",
    "not_owned": "client_buys_an_unowned_item",
    "already_owned": "client_sells_an_owned_item",
    "intent_undefined": "client_intent_undefined",
    "back_counter_unreadable": "weighted_counter_unreadable",
    "back_counter_full": "weighted_counter_rejects_item",
}

# Core/PendingScope.IsCallScoped: these two ops share a fingerprint so a
# same-item retry while one attempt is non-terminal is blocked, while a
# different item/side stays an independent native call.
CALL_SCOPED_OPS = frozenset({"confirm_trade", "stage_negotiation_item"})

# Containers a player can drag from while negotiating (Core/StagingContainers).
PLAYER_REACHABLE_CONTAINERS = frozenset(
    {"main", "front", "back", "showcase", "hidden", "trash"})

NEGOTIATION_UNCERTAIN_NEXT = (
    "the staging attempt ran but its outcome is not proven: do not retry"
    " the same item with a new action_id; read get_state.negotiated_item and"
    " decide, or stage a different item")

SLEEP_PENDING_NEXT = ("the day transition is still running; call"
                      " get_operation or wait_operation, do not re-dispatch")

OPERATION_PENDING_NEXT = (
    "call get_operation or wait_operation for the blocking id;"
    " do not start a new action id for the same op")


PHASE_FOR_KIND = {
    "scenario": "specialization",
    "perk": "perks",
    "difficulty": "difficulty",
}


class FakeGame:
    """A loopback HTTP stand-in for the in-game bridge (tests/demos only)."""

    def __init__(self, recording_mode="off", recording_slot=None,
                 next_free_slot=None, token=None, start_cash=START_CASH,
                 rent=RENT_DUE, async_sleep=False, clock=None):
        if recording_mode not in ("off", "armed"):
            raise ValueError("recording_mode must be 'off' or 'armed' (a real "
                             "'bound' state is reached through confirm_new_game)")
        if recording_mode == "armed" and recording_slot is None:
            recording_slot = DEFAULT_RECORDING_SLOT
        self.recording_mode = recording_mode
        self.recording_slot = recording_slot
        self.next_free_slot = (recording_slot if next_free_slot is None
                               else next_free_slot)
        self.token = token or secrets.token_hex(16)
        self.start_cash = int(start_cash)
        self.rent = int(rent)
        # R5-C4: opt-in asynchronous sleep model. The wall clock is never used
        # by tests: `clock` (callable, milliseconds) or `advance_clock` drives
        # the observation window.
        self.async_sleep = bool(async_sleep)
        self._clock = clock
        self.requests = []
        self.health_payload = {
            "ok": True,
            "name": "pawnmind",
            "fake": True,
            "mode": "fake_game",
            # R4/F5b: the MCP surface now requires the bridge to report a run
            # mode. The offline double is a test-mode bridge.
            "run_mode": "test",
            "note": "offline test double; not the real game",
            "game_version": FAKE_GAME_VERSION,
            "game_assembly_mvid": FAKE_ASSEMBLY_MVID,
            "expected_game_version": FAKE_GAME_VERSION,
            "expected_assembly_mvid": FAKE_ASSEMBLY_MVID,
            "write_gate": "open",
            "write_gate_reason": None,
            "policy_schema": "2",
            "policy_sha256": FAKE_POLICY_SHA256,
            "latest": "UNVERIFIED",
            "latest_verified": False,
        }

        self._lock = threading.RLock()
        self._httpd = None
        self._thread = None
        self._cache = {}
        self._run_seq = 0
        self.item_seq = 0
        self._reset()

    # ------------------------------------------------------------------ setup

    def _reset(self):
        self.rev = 0
        self.in_game = False
        self.menu_phase = "closed"
        self.page = 0
        self.selected_page = 0
        self.selected_scenario = None
        self.selected_perks = []
        self.selected_difficulty = None
        self.hovered = None
        self.day = 0
        self.cash = 0
        self.shop_state = "CLOSED"
        self.can_end_day = False
        self.rent_days_left = 0
        self.rent_paid_total = 0
        self.rent_weeks_paid = 0
        self.rent_weeks_missed = 0
        self.last_payment = None
        self.dialogue = None
        self.inventory = []
        self.offers = []
        self.bound = False
        self.binding = None
        self.save = None
        self.last_save_time = None
        # R5 contract state ---------------------------------------------------
        # The current counter customer (host fixture), the pointer to the item
        # on the weighted/back counter (0 = nothing staged, like the C#
        # adapter's StagedItemId), the typed modal slot and the native click
        # counters the fail-closed scenarios assert on.
        self.client = None
        self.staged_item_id = 0
        self.negotiation_open = False
        self.modal = None
        self.native_clicks = {"stage_negotiation_item": 0, "confirm_trade": 0,
                              "answer_prompt": 0}
        # In-process operation records: action_id -> two-axis record. Never
        # persisted; the real bridge is also in-process only.
        self._operations = {}
        self._pending_sleep = None
        self._faults = {}
        self._fake_clock_ms = 0

    def start(self):
        self._httpd = ThreadingHTTPServer((HOST, 0), _FakeGameHandler)
        self._httpd.fake = self
        self._thread = threading.Thread(
            target=self._httpd.serve_forever,
            kwargs={"poll_interval": 0.02},
            daemon=True,
        )
        self._thread.start()
        return self

    def stop(self):
        if self._httpd is not None:
            self._httpd.shutdown()
            self._httpd.server_close()
            self._httpd = None
        if self._thread is not None:
            self._thread.join(timeout=5)
            self._thread = None

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.stop()
        return False

    @property
    def host(self):
        return HOST

    @property
    def port(self):
        if self._httpd is None:
            raise RuntimeError("fake game is not running")
        return self._httpd.server_address[1]

    @property
    def url(self):
        return "http://%s:%d" % (HOST, self.port)

    def write_config(self, path):
        """Write a bridge.conf that the real MCP server/client can read."""
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("port=%d\ntoken=%s\n" % (self.port, self.token))
        return path

    # ------------------------------------------------------------- inspection

    def state(self):
        with self._lock:
            return copy.deepcopy(self._state_payload())

    def snapshot(self):
        with self._lock:
            return {
                "in_game": self.in_game,
                "day": self.day,
                "cash": self.cash,
                "shop_state": self.shop_state,
                "rent_days_left": self.rent_days_left,
                "rent_paid_total": self.rent_paid_total,
                "rent_weeks_paid": self.rent_weeks_paid,
                "rent_weeks_missed": self.rent_weeks_missed,
                "last_payment": copy.deepcopy(self.last_payment),
                "dialogue_active": bool(self.dialogue and self.dialogue.get("available")),
                "bound": self.bound,
                "configured_slot": self.recording_slot,
                "save_slot": (self.binding or {}).get("slot"),
                "binding": copy.deepcopy(self.binding),
                "last_save_time": self.last_save_time,
                "save": copy.deepcopy(self.save),
                "rev": self.rev,
                # R5 contract inspection fields ------------------------------
                "client": self._client_payload(),
                "negotiation_open": self._negotiation_is_open(),
                "staged_item_id": self.staged_item_id or 0,
                "inventory_ids": [item["id"] for item in self.inventory],
                "prompt": self._prompt_object(),
                "native_clicks": copy.deepcopy(self.native_clicks),
                "async_sleep": self.async_sleep,
                "sleep_pending": copy.deepcopy(self._pending_sleep),
                "operations": {
                    action_id: copy.deepcopy(record)
                    for action_id, record in self._operations.items()
                },
            }

    # ------------------------------------------------------------ host action

    def return_to_menu(self):
        """Host/human step: stand at the main menu.

        This is the S8 runbook step where the human sends the game back to the
        menu before the AI calls the controlled load. It is intentionally not
        an HTTP op: the real bridge cannot be driven back to the menu by the
        model, and this fake must not pretend otherwise.
        """
        with self._lock:
            if not self.in_game:
                return
            self.in_game = False
            self.shop_state = "CLOSED"
            self.can_end_day = False
            self.dialogue = None
            self._clear_negotiation()
            self.menu_phase = "closed"
            self.rev += 1

    # ------------------------------------------------- R5 host-only fixtures
    #
    # The following helpers model game-world facts a player can cause but the
    # *model* must never fabricate: a customer walking up, the shop stock the
    # player already owns, the native day-end confirm panel, the asynchronous
    # blackscreen day transition and the native clock. They are host-only (like
    # return_to_menu) and are never exposed as HTTP ops.

    def _now_ms(self):
        try:
            if self._clock is not None:
                return int(self._clock())
        except Exception:
            pass
        return self._fake_clock_ms

    def advance_clock(self, milliseconds):
        """Advance the injectable fake clock (the observation window fixture)."""
        with self._lock:
            self._fake_clock_ms += int(milliseconds)
            return self._fake_clock_ms

    def stock_item(self, name, value, container="main", identifier=None):
        """Fixture step: the player already owns this item on the shop floor."""
        with self._lock:
            self.item_seq += 1
            item = {
                "id": 5000 + self.item_seq,
                "identifier": identifier or name.lower().replace(" ", "_"),
                "name": name,
                "count": 1,
                "value": int(value),
                "sell": int(value),
                "container": container,
                "owned": True,
            }
            self.inventory.append(item)
            self.rev += 1
            return copy.deepcopy(item)

    def arrive_customer(self, name="Customer", intent="BUY", budget=200,
                        uses_budget=True, purchases=1, items=None,
                        business_complete=False):
        """Fixture step: a customer stands at the counter to negotiate."""
        with self._lock:
            self.client = {
                "name": name,
                "intent": (intent or "").upper(),
                "budget": int(budget),
                "uses_budget": bool(uses_budget),
                "purchases_left": int(purchases),
                "business_complete": bool(business_complete),
                "items": list(items or []),
            }
            self.staged_item_id = 0
            self.negotiation_open = True
            self.rev += 1
            return copy.deepcopy(self.client)

    def clear_customer(self):
        """Fixture step: the customer leaves the counter."""
        with self._lock:
            self._clear_negotiation()
            self.rev += 1

    def raise_negotiation_confirm(self, body, confirm_text="Confirm",
                                  cancel_text="Cancel", confirm_enabled=True,
                                  cancel_enabled=True, single_button=False):
        """Fixture step: the native day-end flow opened the tablet confirm."""
        with self._lock:
            self.modal = {
                "kind": "negotiation_confirm",
                "target": "negotiation_tablet_confirm",
                "body": body,
                "confirm_text": confirm_text,
                "cancel_text": cancel_text,
                "single_button": bool(single_button),
                "confirm_enabled": bool(confirm_enabled),
                "cancel_enabled": bool(cancel_enabled),
                "source_op": None,
            }
            self.rev += 1
            return copy.deepcopy(self.modal)

    def dismiss_modal(self):
        """Fixture step: the native panel closed outside the bridge."""
        with self._lock:
            self.modal = None
            self.rev += 1

    def finish_sleep(self):
        """Host step: the native async blackscreen finally advances the day.

        Returns True when a pending sleep was applied. Applying the advance is
        observable game state: the next get_operation read promotes the running
        (or timed-out uncertain) sleep operation to completed.
        """
        with self._lock:
            if self._pending_sleep is None:
                return False
            before_day = self.day
            self._pending_sleep = None
            self.day += 1
            self.rent_days_left = max(0, self.rent_days_left - 1)
            self.shop_state = "OPEN"
            self.can_end_day = True
            if (self.rent_days_left == 0
                    and not (self.dialogue and self.dialogue.get("available"))):
                self._landlord_arrives()
            self.offers = self._offers_for(self.day)
            self.rev += 1
            self._write_save()
            return self.day > before_day

    def inject_native_fault(self, op, detail="native_not_called", count=1):
        """Fault injection: the next native calls of `op` return silently.

        Mirrors a native call that returns without doing anything (the bridge
        can only report uncertain). Never used to fabricate a success.
        """
        with self._lock:
            self._faults.setdefault(op, []).append([detail, int(count)])

    def _take_native_fault(self, op):
        faults = self._faults.get(op)
        if not faults:
            return None
        detail, count = faults[0]
        if count <= 1:
            faults.pop(0)
        else:
            faults[0][1] = count - 1
        return detail

    def _clear_negotiation(self):
        self.client = None
        self.staged_item_id = 0
        self.negotiation_open = False

    # ------------------------------------------------------ game projections

    def _intent(self):
        return (self.client or {}).get("intent", "")

    def _negotiation_is_open(self):
        return bool(self.client is not None and self.negotiation_open)

    def _find_owned_item(self, item_id):
        return next((row for row in self.inventory if row["id"] == item_id),
                    None)

    def _find_client_item(self, item_id):
        if self.client is None:
            return None
        return next((row for row in self.client["items"]
                     if row["id"] == item_id), None)

    def _staged_item(self):
        if not self.staged_item_id:
            return None
        return (self._find_owned_item(self.staged_item_id)
                or self._find_client_item(self.staged_item_id))

    def _item_value(self, item):
        if item is None:
            return 0
        return int(item.get("value", item.get("sell", 0)))

    @staticmethod
    def _describe_item(item):
        """Project an item exactly like StateSnapshot.DescribeItem (no id)."""
        if item is None:
            return None
        return {
            "name": item.get("name"),
            "identifier": item.get("identifier"),
            "count": item.get("count", 1),
            "value": int(item.get("value", item.get("sell", 0))),
        }

    def _client_payload(self):
        client = self.client
        if client is None:
            return None
        # Mirrors Core/ClientSnapshotProjection: player-visible client fields
        # only (never the hidden wallet).
        return {
            "name": client["name"],
            "intent": client["intent"],
            "uses_budget": client["uses_budget"],
            "impatience": 0,
            "is_wanted": False,
            "report_reward": 0,
            "reported": False,
            "wanted_poster_id": None,
            "bargain_available": False,
            "faction": None,
            "budget": int(client["budget"]),
        }

    def _prompt_object(self):
        """Project the open modal exactly like Bridge/PromptJson.PromptObject."""
        modal = self.modal
        if modal is None:
            return None
        single = bool(modal.get("single_button", False))
        return {
            "kind": modal.get("kind", "confirmation"),
            "target": modal.get("target", "generic_confirmation"),
            "body": modal.get("body") or "",
            "confirm_text": modal.get("confirm_text") or "",
            "cancel_text": modal.get("cancel_text") or "",
            "single_button": single,
            "confirm_enabled": bool(modal.get("confirm_enabled", True)),
            "cancel_enabled": (bool(modal.get("cancel_enabled", True))
                               and not single),
            "source_op": modal.get("source_op"),
        }

    # ------------------------------------------------------------- HTTP layer

    def _remember(self, record):
        with self._lock:
            self.requests.append(record)

    def _handle_http(self, handler, method):
        path = urllib.parse.urlsplit(handler.path).path
        authorization = handler.headers.get("Authorization")
        authorized = authorization == "Bearer " + self.token
        record = {
            "method": method,
            "path": path,
            "authorization": authorization,
            "authorized": authorized,
            "body": None,
        }

        if path == "/health":
            with self._lock:
                payload = copy.deepcopy(self.health_payload)
            record["status"] = 200
            self._remember(record)
            self._send(handler, 200, payload)
            return

        if path not in ("/state", "/action", "/operation"):
            record["status"] = 404
            self._remember(record)
            self._send(handler, 404, {"ok": False, "error": "not_found"})
            return

        if not authorized:
            record["status"] = 401
            self._remember(record)
            self._send(handler, 401, {"ok": False, "error": "unauthorized"})
            return

        if path == "/state":
            if method != "GET":
                record["status"] = 405
                self._remember(record)
                self._send(handler, 405, {"ok": False, "error": "method_not_allowed"})
                return
            with self._lock:
                payload = self._state_payload()
            record["status"] = 200
            self._remember(record)
            self._send(handler, 200, payload)
            return

        if path == "/operation":
            if method != "GET":
                record["status"] = 405
                self._remember(record)
                self._send(handler, 405, {"ok": False, "error": "method_not_allowed"})
                return
            query = urllib.parse.parse_qs(
                urllib.parse.urlsplit(handler.path).query)
            operation_id = (query.get("id") or [None])[0]
            record["operation_id"] = operation_id
            with self._lock:
                payload = self._operation_payload(operation_id)
            record["status"] = 200
            self._remember(record)
            self._send(handler, 200, payload)
            return

        if method != "POST":
            record["status"] = 405
            self._remember(record)
            self._send(handler, 405, {"ok": False, "error": "method_not_allowed"})
            return

        length = int(handler.headers.get("Content-Length") or 0)
        raw = handler.rfile.read(length) if length else b""
        try:
            body = json.loads(raw.decode("utf-8") or "{}")
            if not isinstance(body, dict):
                raise ValueError("not an object")
        except (ValueError, UnicodeDecodeError):
            record["status"] = 200
            self._remember(record)
            with self._lock:
                self._send(handler, 200, {"ok": False, "error": "bad_json",
                                          "rev": self.rev})
            return

        record["body"] = body
        record["op"] = body.get("op")
        with self._lock:
            payload = self._handle_action(body)
        record["status"] = 200
        self._remember(record)
        self._send(handler, 200, payload)

    @staticmethod
    def _send(handler, status, payload):
        data = json.dumps(payload, sort_keys=True).encode("utf-8")
        handler.send_response(status)
        handler.send_header("Content-Type", "application/json")
        handler.send_header("Content-Length", str(len(data)))
        handler.end_headers()
        handler.wfile.write(data)

    # ----------------------------------------------------------- action layer

    def _handle_action(self, body):
        op = body.get("op")
        if not isinstance(op, str) or not op.strip():
            return {"ok": False, "error": "missing_op", "rev": self.rev}
        op = op.strip()

        if op == "ping":
            return {"ok": True, "pong": True, "rev": self.rev}

        if op in READ_ONLY_OPS:
            payload = self._read(op, body)
            payload.setdefault("ok", True)
            payload["rev"] = self.rev
            return payload

        action_id = body.get("action_id")
        if not isinstance(action_id, str) or not action_id.strip():
            return {"ok": False, "error": "missing_action_id", "rev": self.rev}

        rev = body.get("rev")
        if rev is None:
            return {"ok": False, "error": "missing_rev", "rev": self.rev}
        if isinstance(rev, bool) or not isinstance(rev, int) or rev < 0:
            return {"ok": False, "error": "bad_rev", "rev": self.rev}

        fingerprint = self._fingerprint(op, body)
        cached = self._cache.get(action_id)
        if cached is not None:
            if cached[0] != fingerprint:
                return {"ok": False, "error": "action_conflict", "rev": self.rev}
            record = self._operations.get(action_id)
            if record is None:
                return copy.deepcopy(cached[1])
            return self._decorate(cached[1], record)

        if rev != self.rev:
            return {"ok": False, "error": "stale_rev", "rev": self.rev}
        if op not in WRITE_OPS:
            return {"ok": False, "op": op, "error": "unknown_op", "rev": self.rev}

        # D4 / R5-B1: while the same native call is still non-terminal, a
        # second action id would issue a duplicate native call. Call-scoped ops
        # (confirm_trade / stage_negotiation_item) compare the fingerprint, so a
        # different item/side stays available; every other op blocks by op name.
        call_scoped = op in CALL_SCOPED_OPS
        pending = self._find_pending(op, fingerprint if call_scoped else None)
        if pending is not None:
            return self._operation_pending_payload(op, pending)

        raw = self._write(op, body)
        if not isinstance(raw, dict):
            raw = {"ok": False, "op": op, "error": "bad_response"}
        raw["op"] = op
        raw["rev"] = self.rev

        execution, dispatch = self._classify(raw)
        record = {
            "operation_id": action_id,
            "op": op,
            "fingerprint": fingerprint,
            "execution_status": execution,
            "delivery_status": "delivered",
            "dispatch_status": dispatch,
            "rev": rev,
            "result_rev": self.rev,
            "error": raw.get("error"),
            "completion_evidence": None,
            "sleep": None,
        }
        if execution == "running" and op == "sleep":
            readback = raw.get("readback") or {}
            record["sleep"] = {
                "day_before": int(readback.get("day_before", self.day)),
                "issued_at_ms": self._now_ms(),
                "timed_out": False,
            }
        self._operations[action_id] = record
        self._cache[action_id] = (fingerprint, copy.deepcopy(raw))
        return self._decorate(raw, record)

    @staticmethod
    def _fingerprint(op, body):
        args = {key: value for key, value in body.items()
                if key not in ("action_id", "rev")}
        return (op, json.dumps(args, sort_keys=True, default=str))

    # ------------------------------------------------- D2 operation envelope
    #
    # Mirrors Bridge/ActionRouter: every write gets a two-axis record and the
    # cached response is decorated with the record facts (never re-derived from
    # the body). GET /operation?id= exposes it, with the same fail-closed rules.

    @staticmethod
    def _classify(raw):
        """Mirror Core/ExecutionStatus.Classify + Bridge/ActionResult dispatch."""
        ok = raw.get("ok") is True
        pending = raw.get("pending") is True
        uncertain = raw.get("uncertain") is True or raw.get("unknown") is True
        claim = raw.get("dispatched")
        if claim is True:
            dispatch = "dispatched"
        elif claim is False:
            dispatch = "not_dispatched"
        else:
            dispatch = "unknown"

        if pending:
            return "running", dispatch
        if uncertain:
            return "uncertain", dispatch
        if ok:
            return "completed", dispatch
        if dispatch == "not_dispatched":
            return "rejected", dispatch
        if dispatch == "dispatched":
            return "failed", dispatch
        return "uncertain", dispatch

    @staticmethod
    def _dispatched_json(dispatch):
        if dispatch == "dispatched":
            return True
        if dispatch == "not_dispatched":
            return False
        return None

    @staticmethod
    def _is_terminal(execution):
        return execution in ("completed", "rejected", "failed")

    def _decorate(self, raw, record):
        """Mirror Bridge/ActionRouter.DecorateResponse for one response body."""
        body = copy.deepcopy(raw)
        completed = record["execution_status"] == "completed"

        # A promoted operation keeps its dispatch-time body verbatim and exposes
        # only the current provable facts at the top level.
        if completed and (body.get("pending") is True
                          or body.get("uncertain") is True
                          or body.get("unknown") is True):
            initial = copy.deepcopy(body)
            initial.pop("initial_result", None)
            body = {
                "op": initial.get("op"),
                "initial_result": initial,
                "initial_result_note": (
                    "the dispatch-time response; its readback describes the"
                    " moment of the call, not the moment it completed"),
            }

        body["ok"] = completed
        body["operation_id"] = record["operation_id"]
        body["execution_status"] = record["execution_status"]
        body["delivery_status"] = record["delivery_status"]
        body["dispatch_status"] = record["dispatch_status"]
        body["dispatched"] = self._dispatched_json(record["dispatch_status"])
        evidence = record.get("completion_evidence")
        if completed and evidence:
            body["completion_evidence"] = copy.deepcopy(evidence)
        return body

    def _find_pending(self, op, fingerprint):
        for record in self._operations.values():
            if record["op"] != op:
                continue
            if fingerprint is not None and record["fingerprint"] != fingerprint:
                continue
            # "unknown" may have run: it blocks a second native call just like a
            # confirmed dispatch. Only a provably-not-dispatched call is safe.
            if record["dispatch_status"] == "not_dispatched":
                continue
            if self._is_terminal(record["execution_status"]):
                continue
            if record.get("_claimed_not_issued"):
                continue
            return copy.deepcopy(record)
        return None

    def _operation_pending_payload(self, op, pending):
        return {
            "ok": False,
            "error": "operation_pending",
            "op": op,
            "rev": self.rev,
            "blocking_operation_id": pending["operation_id"],
            "blocking_same_call": True,
            "execution_status": pending["execution_status"],
            "delivery_status": pending["delivery_status"],
            "dispatch_status": pending["dispatch_status"],
            "dispatched": self._dispatched_json(pending["dispatch_status"]),
            "next": OPERATION_PENDING_NEXT,
        }

    def _operation_payload(self, operation_id):
        if operation_id == "*":
            return self._list_operations_payload()

        if (not isinstance(operation_id, str)
                or operation_id.strip() == ""
                or len(operation_id) > 128
                or any(ord(ch) < 0x20 for ch in operation_id)):
            return {"ok": False, "known": False,
                    "error": "bad_operation_id"}

        record = self._operations.get(operation_id)
        if record is None:
            payload = {
                "ok": False,
                "known": False,
                "operation_id": operation_id,
                "error": "unknown_operation",
                "next": ("do not replay this id or start a new action id for"
                         " the same op; re-read get_state and decide again"),
            }
            return payload

        record = self._verify_operation(record)
        payload = {
            "ok": record["execution_status"] == "completed",
            "known": True,
            "operation_id": record["operation_id"],
            "op": record["op"],
            "execution_status": record["execution_status"],
            "delivery_status": record["delivery_status"],
            "dispatch_status": record["dispatch_status"],
            "dispatched": self._dispatched_json(record["dispatch_status"]),
            "error": record.get("error"),
            "rev": record["rev"],
            "result_rev": record["result_rev"],
        }
        if (record["execution_status"] == "completed"
                and record.get("completion_evidence")):
            payload["completion_evidence"] = copy.deepcopy(
                record["completion_evidence"])
        if not self._is_terminal(record["execution_status"]):
            payload["next"] = (
                "the outcome is unknown: do not retry with a new action id;"
                " read state and decide"
                if record["execution_status"] == "uncertain"
                else "still running: call wait_operation or read state again;"
                     " do not re-dispatch")
        return payload

    def _list_operations_payload(self):
        non_terminal = [record for record in self._operations.values()
                        if not self._is_terminal(record["execution_status"])]
        total = len(non_terminal)
        rows = []
        for record in list(reversed(non_terminal))[:20]:
            rows.append({
                "operation_id": record["operation_id"],
                "op": record["op"],
                "execution_status": record["execution_status"],
                "delivery_status": record["delivery_status"],
                "dispatch_status": record["dispatch_status"],
                "dispatched": self._dispatched_json(record["dispatch_status"]),
                "rev": record["rev"],
                "result_rev": record["result_rev"],
                "error": record.get("error"),
            })
        payload = {
            "ok": True,
            "operations": rows,
            "count": len(rows),
            "total_non_terminal": total,
            "truncated": total > len(rows),
            "next": ("call get_operation with one of these ids; a sleep or"
                     " save that never finished must be resolved before"
                     " retrying it"),
        }
        if payload["truncated"]:
            payload["truncation_note"] = (
                "only the newest %d non-terminal operations are listed; older"
                " ones can still be resolved by get_operation with their known id"
                % len(rows))
        return payload

    def _verify_operation(self, record):
        """Mirror the D2 readback verifier: only the sleep op has one here."""
        if record["op"] != "sleep" or self._is_terminal(record["execution_status"]):
            return record
        sleep = record.get("sleep")
        if not sleep:
            return record

        if self.day > sleep["day_before"]:
            record["execution_status"] = "completed"
            record["error"] = None
            record["result_rev"] = self.rev
            record["completion_evidence"] = {
                "source": "day_readback",
                "observed_rev": self.rev,
            }
            return record

        if (not sleep["timed_out"]
                and self._now_ms() - sleep["issued_at_ms"]
                >= SLEEP_OBSERVATION_WINDOW_MS):
            # Past the window with no trusted evidence that the native action did
            # nothing: uncertainty, not failure. It stays non-terminal and keeps
            # the anti-duplicate block.
            record["execution_status"] = "uncertain"
            record["error"] = "sleep_unconfirmed"
            record["result_rev"] = self.rev
            sleep["timed_out"] = True
        return record

    # ------------------------------------------------------------- read ops

    def _read(self, op, body):
        if op == "get_new_game_options":
            payload = {"ok": True, "op": op}
            payload.update(self._menu_view())
            return payload
        if op == "get_pending_prompt":
            return self._get_pending_prompt(op)
        if op == "get_recording_save_status":
            return self._status_payload(op)
        if op == "list_goods":
            return self._list_goods(op, body)
        if op == "list_inventory":
            return self._list_inventory(op, body)
        return {"ok": False, "op": op, "error": "unknown_op"}

    def _slot_prediction(self):
        known = _is_int(self.next_free_slot)
        return {
            "next_free_slot": self.next_free_slot if known else None,
            "next_free_known": known,
            "configured_slot_occupied": bool(
                self.bound and self.recording_slot is not None),
        }

    def _status_payload(self, op):
        prediction = self._slot_prediction()
        if self.recording_mode == "off":
            payload = {
                "ok": True,
                "op": op,
                "recording_mode": "disabled",
                "configured_slot": None,
            }
            payload.update(prediction)
            payload.update({
                "bound": False,
                "slot_matches": False,
                "run_matches": False,
                "last_save_time": None,
                "error": "recording_unconfigured",
            })
            return payload
        if not self.bound:
            payload = {
                "ok": True,
                "op": op,
                "recording_mode": "armed",
                "configured_slot": self.recording_slot,
            }
            payload.update(prediction)
            payload.update({
                "bound": False,
                "slot_matches": False,
                "run_matches": False,
                "last_save_time": None,
                "error": "recording_not_bound",
            })
            return payload
        binding = dict(self.binding)
        payload = {
            "ok": True,
            "op": op,
            "recording_mode": "bound",
            "configured_slot": self.recording_slot,
        }
        payload.update(prediction)
        payload.update({
            "bound": True,
            "binding": binding,
            "slot_matches": binding.get("slot") == self.recording_slot,
            "run_matches": True,
            "last_save_time": self.last_save_time,
            "error": None,
        })
        return payload

    def _list_goods(self, op, body):
        scope = (body.get("scope") or "all").lower()
        rows = [{
            "id": offer["id"],
            "name": offer["name"],
            "identifier": offer["identifier"],
            "container": "back",
            "value": offer["buy"],
            "count": 1,
            "owned": False,
            "sellable": False,
        } for offer in self.offers]
        if scope in ("all", "sellable"):
            rows.extend({
                "id": item["id"],
                "name": item["name"],
                "identifier": item["identifier"],
                "container": "main",
                "value": item.get("sell", item.get("value", 0)),
                "count": item["count"],
                "owned": True,
                "sellable": True,
            } for item in self.inventory)
        if scope == "client":
            rows = [row for row in rows if not row["owned"]]
        client = None
        if self.dialogue and self.dialogue.get("available"):
            client = {"name": "Landlord", "intent": "RENT", "budget": None,
                      "bargain_available": False}
        return {"ok": True, "op": op, "scope": scope, "items": rows, "client": client}

    def _list_inventory(self, op, body):
        container = (body.get("container") or "main").lower()
        if container != "main":
            return {"ok": False, "op": op, "error": "unknown_container"}
        try:
            page = int(body.get("page") or 1)
            page_size = int(body.get("page_size") or 20)
        except (TypeError, ValueError):
            return {"ok": False, "op": op, "error": "bad_page"}
        rows = [{
            "id": item["id"],
            "name": item["name"],
            "identifier": item["identifier"],
            "value": item.get("sell", item.get("value", 0)),
            "count": item["count"],
            "owned": True,
            "sellable": True,
        } for item in self.inventory]
        return {
            "ok": True,
            "op": op,
            "container": container,
            "total": len(rows),
            "page": max(1, page),
            "page_size": max(1, page_size),
            "pages": 1 if rows else 0,
            "items": rows,
        }

    # ----------------------------------------------------------- state build

    def _timestamp(self):
        return (datetime(2026, 1, 1)
                + timedelta(minutes=self.rev)).strftime("%Y-%m-%dT%H:%M:%S")

    def _state_payload(self):
        state = self._build_state()
        state["rev"] = self.rev
        state["timestamp"] = self._timestamp()
        return state

    def _build_state(self):
        state = {"in_game": self.in_game,
                 "legal_actions": self._legal_actions(),
                 # Mirror the B-module split: the fake never claims a candidate
                 # is legal, it simply has no unconfirmed actions.
                 "candidate_actions": [],
                 "prompt": self._prompt_object() if self.modal else None}
        if not self.in_game:
            state.update({
                "client": None,
                "negotiated_item": None,
                "negotiation_open": False,
                "run_summary": None,
                "rent": None,
            })
            return state

        dialogue_active = bool(self.dialogue and self.dialogue.get("available"))
        if self.client is not None:
            client = self._client_payload()
            client_name = client["name"]
            client_intent = client["intent"]
        elif dialogue_active:
            client = {"name": "Landlord", "intent": "RENT",
                      "uses_budget": False, "impatience": 0,
                      "is_wanted": False, "report_reward": 0,
                      "reported": False, "wanted_poster_id": None,
                      "bargain_available": False, "faction": None,
                      "budget": 0}
            client_name = "Landlord"
            client_intent = "RENT"
        else:
            client = None
            client_name = None
            client_intent = None
        client_items = (self.client or {}).get("items", []) if self.client else []
        state.update({
            "day": self.day,
            "shop_state": self.shop_state,
            "outside": False,
            "location": None,
            "afterhour_count": 0,
            "cash": self.cash,
            "rent_days_left": self.rent_days_left,
            "rent_value": self.rent,
            "starting_day": START_DAY,
            "client_arrived": bool(self.client is not None or dialogue_active),
            "waiting_customers": 0,
            "back_counter_count": len(self.offers),
            "back_counter": [{
                "name": offer["name"],
                "identifier": offer["identifier"],
                "count": 1,
                "value": offer["buy"],
            } for offer in self.offers],
            "front_counter_count": 0,
            "front_counter": [],
            "client_item_count": len(client_items),
            "client_items": [self._describe_item(item) for item in client_items],
            "negotiation_open": self._negotiation_is_open(),
            "bargain": {"open": False},
            "inventory_count": len(self.inventory),
            "inventory": [{
                "name": item["name"],
                "identifier": item["identifier"],
                "count": item["count"],
                "value": item.get("sell", item.get("value", 0)),
            } for item in self.inventory],
            "can_call_next": bool(self.client is not None
                                  and self.client.get("business_complete")),
            "can_end_day": self.can_end_day,
            "dialogue": self._dialogue_payload(),
            "rent": self._rent_state(),
            "client": client,
            "negotiated_item": self._describe_item(self._staged_item()),
            "run_summary": {
                "day": self.day,
                "shop_state": self.shop_state,
                "cash": self.cash,
                "rent_days_left": self.rent_days_left,
                "inventory_count": len(self.inventory),
                "front_counter_count": 0,
                "back_counter_count": len(self.offers),
                "client_name": client_name,
                "client_intent": client_intent,
            },
        })
        return state

    def _legal_actions(self):
        actions = []
        if not self.in_game:
            return actions
        # Mirror the C# contract: legal rows carry availability="available";
        # candidate rows (never legal) would carry "unknown" + requires.
        def add(action, **args):
            actions.append({"action": action, "args": args,
                            "availability": "available"})

        if self.client is not None:
            # Negotiation in progress: staging and the accept click are the two
            # player actions. Store-shelf rows would be a different workflow.
            intent = self._intent()
            if self.staged_item_id:
                add("confirm_trade", item_id=self.staged_item_id,
                    side="sell" if intent == "BUY" else "buy")
            stageable = (self.inventory if intent == "BUY"
                         else self.client["items"])
            for item in stageable:
                add("stage_negotiation_item", item_id=item["id"])
        else:
            for offer in self.offers:
                add("confirm_trade", item_id=offer["id"], side="buy")
            for item in self.inventory:
                add("confirm_trade", item_id=item["id"], side="sell")
        if self.can_end_day:
            add("end_day")
        if self.dialogue and self.dialogue.get("available"):
            for choice in self.dialogue["choices"]:
                if self._choice_enabled(choice):
                    add("choose_dialogue", index=choice["index"])
        if self.bound:
            add("sleep")
        return actions

    def _rent_state(self):
        return {
            "readable": True,
            "error": None,
            "due": self.rent,
            "rent_value": self.rent,
            "temporary_rent": 0,
            "days_left": self.rent_days_left,
            "days_until_rent": self.rent_days_left,
            "can_pay": self.cash >= self.rent,
            "rent_paid_total": self.rent_paid_total,
            "rent_weeks_paid": self.rent_weeks_paid,
            "rent_weeks_missed": self.rent_weeks_missed,
            "starting_rent": self.rent,
            "cash": self.cash,
            "day": self.day,
            "last_payment": copy.deepcopy(self.last_payment),
            "receipt_error": None,
        }

    def _dialogue_payload(self):
        if not self.dialogue or not self.dialogue.get("available"):
            return {
                "readable": True,
                "error": None,
                "available": False,
                "choice": False,
                "ended": True,
                "typing": False,
                "speaker": None,
                "text": None,
                "choices": [],
            }
        choices = [{
            "index": choice["index"],
            "text": choice["text"],
            "enabled": self._choice_enabled(choice),
        } for choice in self.dialogue["choices"]]
        return {
            "readable": True,
            "error": None,
            "available": True,
            "choice": True,
            "ended": False,
            "typing": False,
            "speaker": self.dialogue["speaker"],
            "text": self.dialogue["text"],
            "choices": choices,
        }

    def _choice_enabled(self, choice):
        if choice.get("action") == "pay_rent":
            return self.cash >= self.rent
        return bool(choice.get("enabled", True))

    # ---------------------------------------------------------- menu helpers

    def _gate_menu_write(self):
        if self.recording_mode == "off":
            return "recording_unconfigured"
        if not self.bound:
            return None
        return "recording_already_bound"

    def _menu_write_failure(self, op, error, reason=None):
        payload = {
            "ok": False,
            "op": op,
            "error": error,
            "dispatched": False,
            "prompt_pending": False,
            "view": self._menu_view(),
        }
        if reason is not None:
            payload["reason"] = reason
        return payload

    def _candidates(self):
        if self.menu_phase == "specialization":
            return SCENARIOS
        if self.menu_phase == "perks":
            return PERKS
        if self.menu_phase == "difficulty":
            return DIFFICULTIES
        return ()

    def _page_count(self):
        candidates = self._candidates()
        if not candidates:
            return 0
        return (len(candidates) + MENU_PAGE_SIZE - 1) // MENU_PAGE_SIZE

    def _selected_id(self):
        if self.menu_phase == "specialization":
            return self.selected_scenario
        if self.menu_phase == "difficulty":
            return self.selected_difficulty
        return None

    def _menu_view(self):
        view = {
            "phase": self.menu_phase,
            "page": self.page,
            "page_count": 0,
            "has_previous_page": False,
            "has_next_page": False,
            "options": [],
            "selected_page": self.selected_page,
            "selected_page_count": 0,
            "selected_row_paged": False,
            "has_previous_selected_page": False,
            "has_next_selected_page": False,
            "selected_options": [],
            "has_selection": False,
            "toggles": [],
            "continue": {},
            "prompt_pending": False,
        }
        if self.menu_phase == "closed":
            view["page"] = 0
            view["selected_page"] = 0
            return view

        candidates = self._candidates()
        page_count = self._page_count()
        view["page_count"] = page_count
        view["has_previous_page"] = self.page > 0
        view["has_next_page"] = self.page + 1 < page_count
        start = self.page * MENU_PAGE_SIZE
        page_candidates = candidates[start:start + MENU_PAGE_SIZE]

        if self.menu_phase == "perks":
            view["options"] = [self._perk_option(perk) for perk in page_candidates]
            self._fill_perk_state(view)
        else:
            view["options"] = [self._set_option(candidate)
                               for candidate in page_candidates]
            selected_id = self._selected_id()
            view["has_selection"] = selected_id is not None
            if selected_id is not None:
                view["selected_id"] = selected_id
            has_selection = selected_id is not None
            label = "Start" if self.menu_phase == "difficulty" else "Continue"
            view["continue"] = {
                "label": label,
                "visible": True,
                "enabled": has_selection,
            }

        if self.hovered is not None:
            view["hovered_id"] = self.hovered["id"]
            view["hovered_in_selected_row"] = self.hovered["row"] == "selected"
        return view

    def _set_option(self, candidate):
        selected = self._selected_id() == candidate["id"]
        enabled = bool(candidate.get("enabled", True))
        lock_reason = candidate.get("lock_reason")
        if selected:
            enabled = False
            lock_reason = "already_selected"
        option = {
            "id": candidate["id"],
            "display_name": candidate["display_name"],
            "description": candidate["description"],
            "cost": 0,
            "max_slot": 0,
            "selected": selected,
            "enabled": enabled,
            "hovered": False,
        }
        if lock_reason is not None:
            option["lock_reason"] = lock_reason
        return option

    def _perk_option(self, perk):
        lock_reason = self._perk_lock_reason(perk)
        option = {
            "id": perk["id"],
            "display_name": perk["display_name"],
            "description": perk["description"],
            "cost": perk["cost"],
            "max_slot": 0,
            "selected": perk["id"] in self.selected_perks,
            "enabled": lock_reason is None,
            "hovered": bool(self.hovered
                            and self.hovered["id"] == perk["id"]
                            and self.hovered["row"] == "available"),
        }
        if lock_reason is not None:
            option["lock_reason"] = lock_reason
        return option

    def _perk_lock_reason(self, perk):
        if perk["id"] in self.selected_perks:
            return None
        if self._used_points() + perk["cost"] > MAX_PERK_POINTS:
            return "insufficient_points"
        if len(self.selected_perks) + 1 > self._max_perk_count():
            return "perk_limit_reached"
        return None

    def _used_points(self):
        costs = {perk["id"]: perk["cost"] for perk in PERKS}
        return sum(costs.get(perk_id, 0) for perk_id in self.selected_perks)

    def _max_perk_count(self):
        return BASE_PERK_COUNT

    def _can_start_perks(self):
        return (self._used_points() <= MAX_PERK_POINTS
                and len(self.selected_perks) <= self._max_perk_count())

    def _fill_perk_state(self, view):
        used = self._used_points()
        count = len(self.selected_perks)
        view["has_selection"] = count > 0
        view["continue"] = {
            "label": "Continue",
            "visible": True,
            "enabled": self._can_start_perks(),
        }
        view["perks"] = {
            "first_run": False,
            "max_points": MAX_PERK_POINTS,
            "base_count": BASE_PERK_COUNT,
            "used_points": used,
            "remaining_points": MAX_PERK_POINTS - used,
            "selected_count": count,
            "max_count": self._max_perk_count(),
            "points_text": "%d/%d" % (used, MAX_PERK_POINTS),
            "count_text": "%d/%d" % (count, self._max_perk_count()),
            "instruction": "Spend at most %d perk points." % MAX_PERK_POINTS,
        }
        selected = []
        for perk_id in self.selected_perks:
            perk = next((row for row in PERKS if row["id"] == perk_id), None)
            if perk is None:
                continue
            selected.append({
                "id": perk["id"],
                "display_name": perk["display_name"],
                "description": perk["description"],
                "cost": perk["cost"],
                "max_slot": 0,
                "selected": True,
                "enabled": True,
                "hovered": bool(self.hovered
                                and self.hovered["id"] == perk["id"]
                                and self.hovered["row"] == "selected"),
            })
        page_count = 0
        if selected:
            page_count = ((len(selected) + SELECTED_PERK_PAGE_SIZE - 1)
                          // SELECTED_PERK_PAGE_SIZE)
        paged = len(selected) > SELECTED_PERK_PAGE_SIZE
        view["selected_page"] = self.selected_page
        view["selected_page_count"] = page_count
        view["selected_row_paged"] = paged
        view["has_previous_selected_page"] = paged and self.selected_page > 0
        view["has_next_selected_page"] = paged and self.selected_page + 1 < page_count
        start = self.selected_page * SELECTED_PERK_PAGE_SIZE
        view["selected_options"] = selected[start:start + SELECTED_PERK_PAGE_SIZE]

    # ------------------------------------------------------------ write ops

    def _write(self, op, body):
        if op == "open_new_game_menu":
            return self._open_menu(op)
        if op == "select_new_game_option":
            return self._select_option(op, body)
        if op == "new_game_next_page":
            return self._next_page(op, body)
        if op == "focus_new_game_selected_perk":
            return self._focus_perk(op, body)
        if op == "blur_new_game_option":
            return self._blur_option(op, body)
        if op == "new_game_continue":
            return self._continue_menu(op)
        if op == "confirm_new_game":
            return self._confirm_new_game(op)
        if op == "save_recording_game":
            return self._save_recording(op)
        if op == "load_recording_game":
            return self._load_recording(op)
        if op == "confirm_trade":
            return self._confirm_trade(op, body)
        if op == "stage_negotiation_item":
            return self._stage_negotiation_item(op, body)
        if op == "answer_prompt":
            return self._answer_prompt(op, body)
        if op == "choose_dialogue":
            return self._choose_dialogue(op, body)
        if op == "end_day":
            return self._end_day(op)
        if op == "sleep":
            return self._sleep(op)
        return {"ok": False, "op": op, "error": "unknown_op"}

    def _open_menu(self, op):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)
        if self.menu_phase != "closed":
            return self._menu_write_failure(op, "menu_already_open")
        self.menu_phase = "specialization"
        self.page = 0
        self.selected_page = 0
        self.selected_scenario = None
        self.selected_perks = []
        self.selected_difficulty = None
        self.hovered = None
        self.rev += 1
        return {"ok": True, "op": op, "dispatched": True,
                "prompt_pending": False, "view": self._menu_view()}

    def _select_option(self, op, body):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)

        kind = body.get("kind")
        option_id = body.get("id")
        result = {
            "ok": False,
            "op": op,
            "dispatched": False,
            "kind": kind if isinstance(kind, str) else "none",
            "requested_id": option_id if isinstance(option_id, str) else "",
            "action": "none",
            "view": self._menu_view(),
        }
        if kind not in PHASE_FOR_KIND:
            result["error"] = "unknown_kind"
            return result
        if not isinstance(option_id, str) or not option_id.strip():
            result["error"] = "empty_id"
            return result
        if self.menu_phase == "closed":
            result["error"] = "menu_closed"
            return result
        if self.menu_phase != PHASE_FOR_KIND[kind]:
            result["error"] = "wrong_phase"
            result["reason"] = "phase=" + self.menu_phase
            return result
        if kind == "perk":
            return self._select_perk(result, option_id)
        return self._select_set(result, option_id)

    def _select_set(self, result, option_id):
        candidates = self._candidates()
        matches = [candidate for candidate in candidates
                   if candidate["id"] == option_id]
        hidden = {row["id"] for row in HIDDEN_SCENARIOS}
        if not matches:
            if self.menu_phase == "specialization" and option_id in hidden:
                result["error"] = "option_hidden"
            else:
                result["error"] = "unknown_option"
                result["reason"] = "id=" + option_id
            return result
        candidate = matches[0]
        index = candidates.index(candidate)
        if index // MENU_PAGE_SIZE != self.page:
            result["error"] = "option_not_on_page"
            return result
        if self._selected_id() == option_id:
            result["error"] = "already_selected"
            return result
        if not candidate.get("enabled", True):
            result["error"] = "option_locked"
            if candidate.get("lock_reason"):
                result["reason"] = candidate["lock_reason"]
            return result

        if self.menu_phase == "specialization":
            self.selected_scenario = option_id
        else:
            self.selected_difficulty = option_id
        self.hovered = None
        self.rev += 1
        result.update({"ok": True, "dispatched": True, "action": "set",
                       "view": self._menu_view()})
        return result

    def _select_perk(self, result, perk_id):
        matches = [perk for perk in PERKS if perk["id"] == perk_id]
        if not matches:
            result["error"] = "unknown_option"
            result["reason"] = "id=" + perk_id
            return result
        perk = matches[0]
        selected = perk_id in self.selected_perks
        hovered_here = bool(self.hovered and self.hovered["id"] == perk_id)

        if selected:
            if self.selected_perks.count(perk_id) > 1:
                result["error"] = "duplicate_selected"
                result["reason"] = "selected_copies=%d" % self.selected_perks.count(perk_id)
                return result
            if hovered_here and self.hovered["row"] == "selected":
                self.selected_perks.remove(perk_id)
                self.hovered = None
                self.rev += 1
                result.update({"ok": True, "dispatched": True,
                               "action": "remove", "view": self._menu_view()})
                return result
            # The available card is built first and swallows the same-id click.
            result["error"] = "ambiguous_target"
            result["reason"] = ("available_card_shadows_selected_row"
                                if not hovered_here or self.hovered["row"] == "available"
                                else "selected_row_not_hovered")
            return result

        index = PERKS.index(perk)
        if index // MENU_PAGE_SIZE != self.page:
            result["error"] = "option_not_on_page"
            return result
        if hovered_here and self.hovered["row"] == "selected":
            result["error"] = "ambiguous_target"
            result["reason"] = "selected_row_without_selected_state"
            return result
        lock_reason = self._perk_lock_reason(perk)
        if lock_reason is not None:
            result["error"] = ("insufficient_points"
                               if lock_reason == "insufficient_points"
                               else "perk_limit_reached")
            return result

        self.selected_perks.append(perk_id)
        self.hovered = {"id": perk_id, "row": "available"}
        self.rev += 1
        result.update({"ok": True, "dispatched": True, "action": "add",
                       "view": self._menu_view()})
        return result

    def _next_page(self, op, body):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)
        row = body.get("row")
        direction = body.get("direction")
        if isinstance(direction, bool) or direction not in (1, -1):
            return self._menu_write_failure(op, "bad_direction")
        if self.menu_phase == "closed":
            return self._menu_write_failure(op, "menu_closed")

        if row == "selected":
            if self.menu_phase != "perks":
                return self._menu_write_failure(op, "wrong_row")
            count = len(self.selected_perks)
            page_count = ((count + SELECTED_PERK_PAGE_SIZE - 1)
                          // SELECTED_PERK_PAGE_SIZE)
            target = self.selected_page + direction
            if page_count <= 0 or target < 0 or target >= page_count:
                return self._menu_write_failure(op, "page_out_of_range")
            self.selected_page = target
        elif row == "available":
            page_count = self._page_count()
            target = self.page + direction
            if page_count <= 0 or target < 0 or target >= page_count:
                return self._menu_write_failure(op, "page_out_of_range")
            self.page = target
        else:
            return self._menu_write_failure(op, "wrong_row")

        self.hovered = None
        self.rev += 1
        return {"ok": True, "op": op, "dispatched": True,
                "prompt_pending": False, "view": self._menu_view()}

    def _focus_perk(self, op, body):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)
        if self.menu_phase == "closed":
            return self._menu_write_failure(op, "menu_closed")
        if self.menu_phase != "perks":
            return self._menu_write_failure(op, "wrong_phase",
                                            "phase=" + self.menu_phase)
        perk_id = body.get("id")
        if not isinstance(perk_id, str) or not perk_id.strip():
            return self._menu_write_failure(op, "empty_id")
        copies = self.selected_perks.count(perk_id)
        if copies == 0:
            return self._menu_write_failure(op, "unknown_option",
                                            "selected_row_missing")
        if copies > 1:
            return self._menu_write_failure(op, "duplicate_selected",
                                            "selected_copies=%d" % copies)
        if self.hovered and self.hovered["id"] == perk_id:
            if self.hovered["row"] == "available":
                return self._menu_write_failure(op, "hover_conflict",
                                                "available_card_is_hovered")
            return {"ok": True, "op": op, "dispatched": False,
                    "reason": "already_focused", "prompt_pending": False,
                    "view": self._menu_view()}

        self.hovered = {"id": perk_id, "row": "selected"}
        self.rev += 1
        return {"ok": True, "op": op, "dispatched": True,
                "prompt_pending": False, "view": self._menu_view()}

    def _blur_option(self, op, body):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)
        if self.menu_phase == "closed":
            return self._menu_write_failure(op, "menu_closed")
        if self.menu_phase != "perks":
            return self._menu_write_failure(op, "wrong_phase",
                                            "phase=" + self.menu_phase)
        perk_id = body.get("id")
        if not isinstance(perk_id, str) or not perk_id.strip():
            return self._menu_write_failure(op, "empty_id")
        if not (self.hovered and self.hovered["id"] == perk_id):
            return self._menu_write_failure(op, "not_hovered")
        row = self.hovered["row"]
        self.hovered = None
        self.rev += 1
        return {"ok": True, "op": op, "dispatched": True,
                "blurred_row": row, "prompt_pending": False,
                "view": self._menu_view()}

    def _continue_menu(self, op):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)
        if self.menu_phase == "closed":
            return self._menu_write_failure(op, "menu_closed")
        if self.menu_phase == "difficulty":
            # On the last page the native Continue *is* Start: it writes the
            # initial save, so only confirm_new_game may trigger it.
            return self._menu_write_failure(op, "difficulty_start_forbidden")
        if self.menu_phase == "specialization":
            if not self.selected_scenario:
                return self._menu_write_failure(op, "continue_unavailable",
                                                "no_scenario_selected")
            candidate = next((row for row in SCENARIOS
                              if row["id"] == self.selected_scenario), None)
            if candidate is None or not candidate.get("enabled", True):
                return self._menu_write_failure(op, "continue_unavailable",
                                                "scenario_unavailable")
            self.menu_phase = "perks"
        else:
            if not self._can_start_perks():
                return self._menu_write_failure(op, "continue_unavailable",
                                                "perks_cannot_start")
            self.menu_phase = "difficulty"

        self.page = 0
        self.selected_page = 0
        self.hovered = None
        self.rev += 1
        return {"ok": True, "op": op, "dispatched": True,
                "prompt_pending": False, "view": self._menu_view()}

    def _confirm_new_game(self, op):
        if self.recording_mode == "off":
            return {"ok": False, "op": op, "error": "recording_unconfigured",
                    "dispatched": False}
        if self.bound:
            return {"ok": False, "op": op, "error": "recording_already_bound",
                    "dispatched": False}
        if self.menu_phase != "difficulty":
            # Mirrors RecordingSaveService: a readable non-difficulty menu is a
            # wrong-phase refusal, not "menu unavailable".
            return {"ok": False, "op": op, "error": "recording_wrong_menu_phase",
                    "dispatched": False}
        if not self.selected_difficulty:
            return {"ok": False, "op": op, "error": "recording_start_unavailable",
                    "dispatched": False}
        if self.next_free_slot != self.recording_slot:
            # Fail closed *before* the one native start: never write a save to a
            # slot the operator did not bind.
            return {"ok": False, "op": op, "error": "recording_slot_not_next_free",
                    "dispatched": False}

        self._run_seq += 1
        self.bound = True
        self.binding = {
            "slot": self.recording_slot,
            "run_id": "run-%04d" % self._run_seq,
            "run_number": self._run_seq,
            "start_type": self.selected_difficulty,
            "start_scenario_id": self.selected_scenario,
        }
        self._start_run()
        self.rev += 1
        self._write_save()
        status = self._status_payload(op)
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "start_requested": True,
            "state": status["recording_mode"],
            "bound": True,
            "start_pending": False,
            "binding": dict(self.binding),
            "pending_error": None,
        }

    def _start_run(self):
        self.in_game = True
        self.menu_phase = "closed"
        self.page = 0
        self.selected_page = 0
        self.hovered = None
        self.day = START_DAY
        self.cash = self.start_cash
        self.shop_state = "OPEN"
        self.can_end_day = True
        self.rent_days_left = RENT_PERIOD_DAYS
        self.rent_paid_total = 0
        self.rent_weeks_paid = 0
        self.rent_weeks_missed = 0
        self.last_payment = None
        self.dialogue = None
        self._clear_negotiation()
        self.modal = None
        self._pending_sleep = None
        self.inventory = []
        self.item_seq = 0
        self.offers = self._offers_for(self.day)

    def _offers_for(self, day):
        return [{
            "id": day * 100 + index,
            "identifier": template["identifier"],
            "name": template["name"],
            "buy": template["buy"],
            "sell": template["sell"],
        } for index, template in enumerate(OFFERS, start=1)]

    def _save_recording(self, op):
        if self.recording_mode == "off":
            return {"ok": False, "op": op, "error": "recording_unconfigured",
                    "dispatched": False}
        if not self.bound:
            return {"ok": False, "op": op, "error": "recording_not_bound",
                    "dispatched": False}
        if not self.in_game:
            return {"ok": False, "op": op, "error": "recording_not_in_game",
                    "dispatched": False}
        self._write_save()
        status = self._status_payload(op)
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "saved": True,
            "state": status["recording_mode"],
            "bound": True,
            "slot_matches": status["slot_matches"],
            "run_matches": status["run_matches"],
            "last_save_time": status["last_save_time"],
            "binding": dict(self.binding),
        }

    def _load_recording(self, op):
        if self.recording_mode == "off":
            return {"ok": False, "op": op, "error": "recording_unconfigured",
                    "dispatched": False}
        if not self.bound:
            return {"ok": False, "op": op, "error": "recording_not_bound",
                    "dispatched": False}
        if self.in_game:
            return {"ok": False, "op": op, "error": "recording_already_in_game",
                    "dispatched": False}
        if self.save is None:
            return {"ok": False, "op": op, "error": "recording_readback_unavailable",
                    "dispatched": False}

        save = self.save
        self.in_game = True
        self.day = save["day"]
        self.cash = save["cash"]
        self.rent_days_left = save["rent_days_left"]
        self.rent_paid_total = save["rent_paid_total"]
        self.rent_weeks_paid = save["rent_weeks_paid"]
        self.rent_weeks_missed = save["rent_weeks_missed"]
        self.shop_state = save["shop_state"]
        self.can_end_day = save["can_end_day"]
        self.inventory = copy.deepcopy(save["inventory"])
        self.offers = self._offers_for(self.day)
        self.dialogue = None
        self._clear_negotiation()
        self.modal = None
        self._pending_sleep = None
        self.menu_phase = "closed"
        # The C# bridge resets its receipt tracker after a controlled load.
        self.last_payment = None
        self.rev += 1
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "load_requested": True,
            "state": "bound",
            "bound": True,
            "load_pending": False,
            "pending_error": None,
        }

    def _write_save(self):
        self.save = {
            "slot": self.recording_slot,
            "day": self.day,
            "cash": self.cash,
            "rent_days_left": self.rent_days_left,
            "rent_value": self.rent,
            "rent_paid_total": self.rent_paid_total,
            "rent_weeks_paid": self.rent_weeks_paid,
            "rent_weeks_missed": self.rent_weeks_missed,
            "shop_state": self.shop_state,
            "can_end_day": self.can_end_day,
            "inventory": copy.deepcopy(self.inventory),
            "run_id": (self.binding or {}).get("run_id"),
            "run_number": (self.binding or {}).get("run_number"),
        }
        self.last_save_time = self._timestamp()

    # -------------------------------------------------------- first-week ops

    def _trade_failure(self, op, error):
        return {"ok": False, "op": op, "error": error, "dispatched": False}

    def _confirm_trade(self, op, body):
        if not self.in_game:
            return self._trade_failure(op, "not_in_game")
        item_id = body.get("item_id")
        side = body.get("side")
        if isinstance(item_id, bool) or not isinstance(item_id, int):
            return self._trade_failure(op, "bad_item_id")
        if self.client is not None:
            # R5/A3 + R5/B1: a customer is negotiating, so the trade mirrors
            # Bridge/TradeService (staged item, per-trade evidence, budget gate).
            return self._negotiation_confirm_trade(op, body, item_id, side)
        return self._legacy_confirm_trade(op, item_id, side)

    def _legacy_confirm_trade(self, op, item_id, side):
        """The S6B simplified store model (no customer at the counter)."""
        if side == "buy":
            offer = next((row for row in self.offers if row["id"] == item_id), None)
            if offer is None:
                owned = any(item["id"] == item_id for item in self.inventory)
                return self._trade_failure(op, "already_owned" if owned else "item_not_found")
            if self.cash < offer["buy"]:
                # Refuse before any mutation: cash and inventory stay untouched.
                return self._trade_failure(op, "cannot_afford")
            self.cash -= offer["buy"]
            self.offers.remove(offer)
            self.item_seq += 1
            self.inventory.append({
                "id": 5000 + self.item_seq,
                "identifier": offer["identifier"],
                "name": offer["name"],
                "count": 1,
                "sell": offer["sell"],
                "value": offer["sell"],
                "container": "main",
                "owned": True,
            })
            self.rev += 1
            return {"ok": True, "op": op, "dispatched": True,
                    "action": "buy", "item_id": offer["id"]}

        if side == "sell":
            item = next((row for row in self.inventory if row["id"] == item_id), None)
            if item is None:
                offered = any(offer["id"] == item_id for offer in self.offers)
                return self._trade_failure(op, "not_owned" if offered else "item_not_found")
            self.inventory.remove(item)
            self.cash += item["sell"]
            self.rev += 1
            return {"ok": True, "op": op, "dispatched": True,
                    "action": "sell", "item_id": item_id}

        return self._trade_failure(op, "bad_side")

    # ------------------------------------------- negotiation trade (R5/A3+B1)

    def _trade_refusal(self, op, error, item_id, side):
        """Mirror Bridge/TradeService.Refusal (a pre-dispatch rejection)."""
        return {
            "ok": False,
            "op": op,
            "error": error,
            "dispatched": False,
            "execution_status": "rejected",
            "item_id": item_id,
            "side": side,
        }

    @staticmethod
    def _normalized_side(side):
        return side.strip().upper() if isinstance(side, str) else ""

    def _trade_uncertain(self, op, item_id, side, intent, staged_id,
                         detail, evidence=None):
        """Mirror Bridge/TradeService.Uncertain: dispatched, no proof, never ok."""
        row = {
            "ok": False,
            "op": op,
            "error": "outcome_unverified",
            "detail": detail,
            "dispatched": True,
            "unknown": True,
            "execution_status": "uncertain",
            "item_id": item_id,
            "side": side,
            "client_intent": intent,
            "staged_item_id": staged_id,
            "evidence": copy.deepcopy(evidence) if evidence else {},
            "next": ("the native accept click returned without trusted completion"
                     " evidence: do not retry with a new action id. Read get_state"
                     " (business_complete / client_items) and decide."),
        }
        if evidence is not None:
            row["business_complete"] = evidence["business_complete"]
            row["visit_complete"] = evidence["business_complete"]
            row["client_unchanged"] = evidence["client_unchanged"]
            row["items_transferred"] = evidence["items_transferred"]
            row["cash_moved"] = evidence["cash_moved"]
        return row

    def _negotiation_confirm_trade(self, op, body, item_id, side):
        """Mirror Core/TradeGuardPolicy + Bridge/TradeService.Confirm."""
        client = self.client
        intent = self._intent() or "UNDEFINED"
        staged_id = self.staged_item_id or 0
        staged_item = self._staged_item()
        sold = self._normalized_side(side)

        def refuse(error):
            return self._trade_refusal(op, error, item_id, side)

        # Core/TradeGuardPolicy.Evaluate order, as shipped in R5.
        if not self._negotiation_is_open():
            return refuse("no_negotiation")
        if item_id <= 0:
            return refuse("item_not_found")
        if staged_id != item_id:
            return refuse("item_not_staged")
        selling = sold == "SELL"
        if not selling and sold != "BUY":
            return refuse("bad_side")
        if intent == "UNDEFINED":
            return refuse("intent_undefined")
        if client.get("business_complete"):
            return refuse("business_complete")

        owned = staged_item is not None and self._find_owned_item(item_id) is not None
        if selling and not owned:
            return refuse("not_owned")
        if not selling and owned:
            return refuse("already_owned")

        if intent == "BARTER":
            return refuse("barter_not_acceptable")
        if intent == "INFORMATION_DEALER":
            return refuse("not_introduced")
        if intent == "GUNSMITH":
            return refuse("gun_order_not_accepted")

        price = self._item_value(staged_item)
        # B16: the native buy/sell gate for the requested direction. The fake
        # cannot read the native predicate, so it models the two player-visible
        # facts: a staged item exists and the player has the cash to buy.
        if selling:
            if staged_item is None:
                return refuse("client_refuses_item")
        else:
            if staged_item is None or self.cash < price:
                return refuse("cannot_buy_from_table")

        # R5/A3: the native sell path silently ignores an unaffordable sale;
        # refuse before the click with player-visible values only.
        if (selling and client.get("uses_budget")
                and price > int(client.get("budget") or 0)):
            return refuse("client_budget_insufficient")

        fault = self._take_native_fault(op)
        if fault is not None:
            # The native accept call returned without doing anything (R5 bug
            # shape): dispatched but unproven => uncertain, never completed.
            return self._trade_uncertain(op, item_id, side, intent, staged_id,
                                         fault)

        self.native_clicks["confirm_trade"] += 1
        cash_before = self.cash
        cash_after = cash_before
        items_transferred = False
        if selling:
            self.inventory.remove(self._find_owned_item(item_id))
            self.cash += price
            cash_after = self.cash
            if client.get("uses_budget"):
                client["budget"] = max(0, int(client["budget"]) - price)
            # The sold item leaves the negotiation table.
            self.staged_item_id = 0
            items_transferred = True
        else:
            bought = self._find_client_item(item_id)
            client["items"].remove(bought)
            bought = dict(bought)
            bought["owned"] = True
            bought["container"] = "main"
            self.inventory.append(bought)
            self.cash -= price
            cash_after = self.cash
            if client.get("uses_budget"):
                client["budget"] = int(client["budget"]) + price
            # R5/A3: a purchase keeps `currentNegociatedItem` on the bought
            # item, so `items_transferred` is false and cash is the evidence.
            self.staged_item_id = item_id
            items_transferred = False

        client["purchases_left"] = int(client.get("purchases_left", 1)) - 1
        business_complete = client["purchases_left"] <= 0
        client["business_complete"] = business_complete
        cash_moved = cash_before != cash_after
        self.rev += 1

        evidence = {
            "called": True,
            "error": None,
            "business_complete": business_complete,
            "client_unchanged": True,
            "items_transferred": items_transferred,
            "cash_moved": cash_moved,
            "cash_before": cash_before,
            "cash_after": cash_after,
            "staged_item_id_after": self.staged_item_id or 0,
        }
        if not (items_transferred or cash_moved):
            # Whole-visit completion is attribution context, never per-trade
            # proof: report uncertain, never ok:true.
            return self._trade_uncertain(
                op, item_id, side, intent, staged_id,
                "no_trade_evidence" if business_complete
                else "business_not_complete",
                evidence)
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "execution_status": "completed",
            "item_id": item_id,
            "side": "sell" if selling else "buy",
            "client_intent": intent,
            "business_complete": business_complete,
            "visit_complete": business_complete,
            "items_transferred": items_transferred,
            "cash_moved": cash_moved,
            "client_unchanged": True,
            "staged_item_id": staged_id,
            "cash_before": cash_before,
            "cash_after": cash_after,
            "cash_changed": cash_moved,
            "evidence": evidence,
        }

    # ---------------------------------------------- negotiation staging (B1)

    def _stage_refusal(self, op, error, item_id):
        return {
            "ok": False,
            "op": op,
            "error": error,
            "detail": STAGE_REFUSAL_DETAILS.get(error, error),
            "dispatched": False,
            "execution_status": "rejected",
            "item_id": item_id,
            "staged_item_id_before": self.staged_item_id or 0,
            "staged_item_id_after": None,
            "negotiation_open": self._negotiation_is_open(),
            "client_identity_unchanged": None,
            "client_intent": self._intent() or None,
        }

    def _stage_negotiation_item(self, op, body):
        """Mirror Bridge/NegotiationStagingService.Stage (read -> guard -> act)."""
        item_id = body.get("item_id")
        if isinstance(item_id, bool) or not isinstance(item_id, int):
            return {"ok": False, "op": op, "error": "bad_item_id",
                    "dispatched": False}
        if not self.in_game:
            return self._stage_refusal(op, "not_in_game", item_id)
        if self.client is None:
            return self._stage_refusal(op, "no_client", item_id)
        intent = self._intent() or "UNDEFINED"
        if intent == "UNDEFINED":
            return self._stage_refusal(op, "intent_undefined", item_id)
        if not self._negotiation_is_open():
            return self._stage_refusal(op, "negotiation_unreadable", item_id)
        if self.modal is not None:
            return self._stage_refusal(op, "modal_open", item_id)

        owned_item = self._find_owned_item(item_id)
        client_item = self._find_client_item(item_id)
        item = owned_item or client_item
        if item is None:
            return self._stage_refusal(op, "item_not_found", item_id)
        container = (item.get("container") or "main").strip().lower()
        if container == "sold":
            return self._stage_refusal(op, "item_sold", item_id)
        if container not in PLAYER_REACHABLE_CONTAINERS:
            return self._stage_refusal(op, "item_unreachable", item_id)
        if intent == "BUY" and owned_item is None:
            return self._stage_refusal(op, "not_owned", item_id)
        if intent == "SELL" and owned_item is not None:
            return self._stage_refusal(op, "already_owned", item_id)

        staged_before = self.staged_item_id or 0
        on_counter = staged_before == item_id

        # Host-side fault injection: the native call returns silently. The bridge
        # can only report uncertain; nothing moves and no native click counts.
        fault = self._take_native_fault(op)
        if fault is not None:
            return {
                "ok": False,
                "op": op,
                "error": "outcome_unverified",
                "detail": fault,
                "dispatched": True,
                "uncertain": True,
                "execution_status": "uncertain",
                "item_id": item_id,
                "staged_item_id_before": staged_before,
                "staged_item_id_after": None,
                "negotiation_open": None,
                "client_identity_unchanged": None,
                "client_intent": intent,
                "evidence": {},
                "next": NEGOTIATION_UNCERTAIN_NEXT,
            }

        self.native_clicks["stage_negotiation_item"] += 1
        primitive = ("weighted_area_remove_add" if on_counter
                     else "weighted_area_add")
        self.staged_item_id = item_id
        self.negotiation_open = True
        self.rev += 1
        evidence = {
            "called": True,
            "error": None,
            "staged_item_id_before": staged_before,
            "staged_item_id_after_readable": True,
            "staged_item_id_after": item_id,
            "item_on_weighted_counter": True,
            "negotiation_readable_after": True,
            "negotiation_open_after": True,
            "client_identity_unchanged": True,
            "native_primitive": primitive,
        }
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "execution_status": "completed",
            "item_id": item_id,
            "staged_item_id_before": staged_before,
            "staged_item_id_after": item_id,
            "negotiation_open": True,
            "client_identity_unchanged": True,
            "item_on_weighted_counter": True,
            "client_intent": intent,
            "native_primitive": primitive,
            "evidence": evidence,
        }

    # ---------------------------------------------- typed modal (R5/B2)

    def _get_pending_prompt(self, op):
        """Mirror Bridge/PromptService.GetPending (read-only, no rev needed)."""
        return {"ok": True, "op": op, "prompt": self._prompt_object()}

    def _prompt_failure(self, op, error, dispatched=False):
        """Mirror Bridge/PromptService.FailureRow."""
        return {
            "ok": False,
            "op": op,
            "error": error,
            "dispatched": dispatched,
            "prompt": self._prompt_object(),
        }

    def _answer_prompt(self, op, body):
        """Mirror Bridge/PromptService.AnswerPrompt + Core/PromptPolicy."""
        if self.modal is None:
            return self._prompt_failure(op, "no_prompt")
        choice = body.get("choice")
        normalized = choice.strip() if isinstance(choice, str) else None
        if normalized not in ("confirm", "cancel"):
            return self._prompt_failure(op, "bad_choice")
        prompt = self._prompt_object()
        if normalized == "confirm" and not prompt["confirm_enabled"]:
            return self._prompt_failure(op, "choice_unavailable")
        if normalized == "cancel" and (prompt["single_button"]
                                       or not prompt["cancel_enabled"]):
            return self._prompt_failure(op, "choice_unavailable")

        self.native_clicks["answer_prompt"] += 1
        if normalized == "confirm":
            # The day-end confirm panel's native callback closes the shop.
            self.shop_state = "CLOSED"
            self.can_end_day = False
        self.modal = None
        self.rev += 1
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "choice": normalized,
            "prompt_closed": True,
            "prompt": None,
            "state": {
                "readable": True,
                "day": self.day,
                "cash": self.cash,
                "outside": False,
                "location": None,
                "shop_state": self.shop_state,
            },
        }

    def _choose_dialogue(self, op, body):
        if not self.in_game:
            return {"ok": False, "op": op, "error": "not_in_game", "dispatched": False}
        if not (self.dialogue and self.dialogue.get("available")):
            return {"ok": False, "op": op, "error": "no_dialogue", "dispatched": False}
        index = body.get("index")
        choices = self.dialogue["choices"]
        if (isinstance(index, bool) or not isinstance(index, int)
                or index < 0 or index >= len(choices)):
            return {"ok": False, "op": op, "error": "bad_index", "dispatched": False}
        choice = choices[index]
        if not self._choice_enabled(choice):
            # Never invoke the native choice when the button is disabled: no
            # cash change, no dialogue advance, no revision bump.
            return {"ok": False, "op": op, "error": "choice_disabled",
                    "dispatched": False}

        before_cash = self.cash
        before_days = self.rent_days_left
        before_day = self.day
        dispatch_rev = body.get("rev")
        receipt = None
        if choice.get("action") == "pay_rent":
            self.cash -= self.rent
            self.rent_paid_total += self.rent
            self.rent_weeks_paid += 1
            self.rent_days_left = RENT_PERIOD_DAYS
            # Mirror Core/RentReceiptTracker: the id is built from the two
            # counters and paid_at_rev is the revision the native call ran at.
            receipt = {
                "id": "rent_%d_%d" % (self.rent_weeks_paid,
                                      self.rent_paid_total),
                "amount": self.rent,
                "cash_before": before_cash,
                "cash_after": self.cash,
                "cash_delta": self.cash - before_cash,
                "rent_days_left_before": before_days,
                "rent_days_left_after": self.rent_days_left,
                "paid_at_rev": dispatch_rev,
                "source": "dialogue_choice",
                "day": self.day,
                "weeks_paid_delta": 1,
                "rent_weeks_paid": self.rent_weeks_paid,
                "rent_weeks_missed": self.rent_weeks_missed,
                "due_before": self.rent,
                "day_before": before_day,
                "detection_complete": True,
            }
            self.last_payment = receipt
        else:
            self.rent_weeks_missed += 1

        self.dialogue = None
        self.rev += 1
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "index": index,
            "choice_text": choice["text"],
            "dialogue_changed": True,
            "dialogue": self._dialogue_payload(),
            "last_rent_payment": copy.deepcopy(receipt),
            "receipt_error": None,
            "rent": self._rent_state(),
        }

    def _end_day(self, op):
        if not self.in_game:
            return {"ok": False, "op": op, "error": "not_in_game", "dispatched": False}
        if self.async_sleep:
            # The R5 sleep model mirrors Core/DayEndPolicy: the shutter only
            # works while the shop is OPEN and no client is still at the counter,
            # and after it closes the day is *ready* to end (CanEndDay true).
            if self.client is not None and not self.client.get("business_complete"):
                return {"ok": False, "op": op, "error": "client_still_present",
                        "dispatched": False}
            if self.shop_state != "OPEN":
                return {"ok": False, "op": op, "error": "shop_not_open",
                        "dispatched": False}
            before_day = self.day
            before_days = self.rent_days_left
            before_cash = self.cash
            self.shop_state = "CLOSED"
            self.can_end_day = True
            self.rev += 1
            return {
                "ok": True,
                "op": op,
                "dispatched": True,
                "readback": {
                    "readable": True,
                    "error": None,
                    "day_before": before_day,
                    "day_after": self.day,
                    "rent_days_left_before": before_days,
                    "rent_days_left_after": self.rent_days_left,
                    "cash_before": before_cash,
                    "cash_after": self.cash,
                    "day_advanced": False,
                    "changed": True,
                },
                "can_end_day_after": True,
            }
        if not self.can_end_day:
            return {"ok": False, "op": op, "error": "cannot_end_day",
                    "dispatched": False}
        before_day = self.day
        before_days = self.rent_days_left
        before_cash = self.cash
        self.shop_state = "CLOSED"
        self.can_end_day = False
        self.rev += 1
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "readback": {
                "readable": True,
                "error": None,
                "day_before": before_day,
                "day_after": self.day,
                "rent_days_left_before": before_days,
                "rent_days_left_after": self.rent_days_left,
                "cash_before": before_cash,
                "cash_after": self.cash,
                "day_advanced": False,
                "changed": True,
            },
            "can_end_day_after": False,
        }

    def _sleep(self, op):
        if self.recording_mode == "off":
            return {"ok": False, "op": op, "error": "recording_unconfigured",
                    "dispatched": False}
        if not self.bound:
            return {"ok": False, "op": op, "error": "recording_not_bound",
                    "dispatched": False}
        if not self.in_game:
            return {"ok": False, "op": op, "error": "recording_not_in_game",
                    "dispatched": False}

        if self.async_sleep:
            # Mirror RecordingSaveService.Sleep: only a CLOSED shop with no
            # client waiting may sleep; the native blackscreen then advances the
            # day asynchronously (finish_sleep), so the dispatch itself is only
            # running/pending and never an immediate success.
            blocked = None
            if not self.can_end_day:
                blocked = "cannot_end_day"
            elif self.shop_state != "CLOSED":
                blocked = "not_closed"
            if blocked is not None:
                return {"ok": False, "op": op, "error": blocked,
                        "dispatched": False}
            before_day = self.day
            before_days = self.rent_days_left
            before_cash = self.cash
            self._pending_sleep = {
                "day_before": before_day,
                "day_after": before_day,
                "issued_at": self._now_ms(),
            }
            self.rev += 1
            status = self._status_payload(op)
            return {
                "ok": False,
                "op": op,
                "dispatched": True,
                "execution_status": "running",
                "pending": True,
                "day_changed": False,
                "saved": False,
                "slept": True,
                "save_evidence": "save_unknown",
                "save_completed": False,
                "save_unknown": True,
                "next": SLEEP_PENDING_NEXT,
                "readback": {
                    "readable": True,
                    "error": None,
                    "day_before": before_day,
                    "day_after": self.day,
                    "rent_days_left_before": before_days,
                    "rent_days_left_after": self.rent_days_left,
                    "cash_before": before_cash,
                    "cash_after": self.cash,
                    "day_advanced": False,
                    "changed": False,
                },
                "report": {
                    "readable": False,
                    "reason": "report_unavailable",
                    "day": self.day,
                    "lines": [],
                },
                "state": status["recording_mode"],
                "bound": True,
                "slot_matches": status["slot_matches"],
                "run_matches": status["run_matches"],
                "last_save_time": status["last_save_time"],
                "binding": dict(self.binding),
            }

        before_day = self.day
        before_days = self.rent_days_left
        before_cash = self.cash
        self.day += 1
        self.rent_days_left = max(0, self.rent_days_left - 1)
        self.shop_state = "OPEN"
        self.can_end_day = True
        if self.rent_days_left == 0 and not (
                self.dialogue and self.dialogue.get("available")):
            self._landlord_arrives()
        self.offers = self._offers_for(self.day)
        self.rev += 1
        self._write_save()
        status = self._status_payload(op)
        return {
            "ok": True,
            "op": op,
            "dispatched": True,
            "saved": False,
            "slept": True,
            "readback": {
                "readable": True,
                "error": None,
                "day_before": before_day,
                "day_after": self.day,
                "rent_days_left_before": before_days,
                "rent_days_left_after": self.rent_days_left,
                "cash_before": before_cash,
                "cash_after": self.cash,
                "day_advanced": True,
                "changed": True,
            },
            "state": status["recording_mode"],
            "bound": True,
            "slot_matches": status["slot_matches"],
            "run_matches": status["run_matches"],
            "last_save_time": status["last_save_time"],
            "binding": dict(self.binding),
        }

    def _landlord_arrives(self):
        self.dialogue = {
            "speaker": "Landlord",
            "text": "Rent is due: %d credits. Pay up or pack up." % self.rent,
            "available": True,
            "choices": [
                {"index": 0,
                 "text": "Pay the rent (%d credits)" % self.rent,
                 "enabled": self.cash >= self.rent,
                 "action": "pay_rent"},
                {"index": 1, "text": "Not now", "enabled": True,
                 "action": "defer"},
            ],
        }


class _FakeGameHandler(BaseHTTPRequestHandler):
    server_version = "PawnMindFake/1.0"
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):  # keep the test output clean
        pass

    def do_GET(self):
        self.server.fake._handle_http(self, "GET")

    def do_POST(self):
        self.server.fake._handle_http(self, "POST")
