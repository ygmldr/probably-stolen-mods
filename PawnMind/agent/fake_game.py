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
    "end_day",
    "sleep",
})

PHASE_FOR_KIND = {
    "scenario": "specialization",
    "perk": "perks",
    "difficulty": "difficulty",
}


class FakeGame:
    """A loopback HTTP stand-in for the in-game bridge (tests/demos only)."""

    def __init__(self, recording_mode="off", recording_slot=None,
                 next_free_slot=None, token=None, start_cash=START_CASH,
                 rent=RENT_DUE):
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
        self.requests = []
        self.health_payload = {
            "ok": True,
            "name": "pawnmind",
            "fake": True,
            "mode": "fake_game",
            "note": "offline test double; not the real game",
            "game_version": FAKE_GAME_VERSION,
            "game_assembly_mvid": FAKE_ASSEMBLY_MVID,
            "expected_game_version": FAKE_GAME_VERSION,
            "expected_assembly_mvid": FAKE_ASSEMBLY_MVID,
            "write_gate": "open",
            "write_gate_reason": None,
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
            self.menu_phase = "closed"
            self.rev += 1

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

        if path not in ("/state", "/action"):
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
            return copy.deepcopy(cached[1])

        if rev != self.rev:
            return {"ok": False, "error": "stale_rev", "rev": self.rev}
        if op not in WRITE_OPS:
            return {"ok": False, "op": op, "error": "unknown_op", "rev": self.rev}

        payload = self._write(op, body)
        payload["rev"] = self.rev
        self._cache[action_id] = (fingerprint, copy.deepcopy(payload))
        return payload

    @staticmethod
    def _fingerprint(op, body):
        args = {key: value for key, value in body.items()
                if key not in ("action_id", "rev")}
        return (op, json.dumps(args, sort_keys=True, default=str))

    # ------------------------------------------------------------- read ops

    def _read(self, op, body):
        if op == "get_new_game_options":
            payload = {"ok": True, "op": op}
            payload.update(self._menu_view())
            return payload
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
                "value": item["sell"],
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
            "value": item["sell"],
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
                 "candidate_actions": []}
        if not self.in_game:
            state.update({
                "client": None,
                "negotiated_item": None,
                "run_summary": None,
                "rent": None,
            })
            return state

        dialogue_active = bool(self.dialogue and self.dialogue.get("available"))
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
            "client_arrived": dialogue_active,
            "back_counter_count": len(self.offers),
            "back_counter": [{
                "name": offer["name"],
                "identifier": offer["identifier"],
                "count": 1,
                "value": offer["buy"],
            } for offer in self.offers],
            "front_counter_count": 0,
            "front_counter": [],
            "client_item_count": 0,
            "client_items": [],
            "negotiation_open": False,
            "bargain": {"open": False},
            "inventory_count": len(self.inventory),
            "inventory": [{
                "name": item["name"],
                "identifier": item["identifier"],
                "count": item["count"],
                "value": item["sell"],
            } for item in self.inventory],
            "can_call_next": False,
            "can_end_day": self.can_end_day,
            "dialogue": self._dialogue_payload(),
            "rent": self._rent_state(),
            "client": ({"name": "Landlord", "intent": "RENT"}
                       if dialogue_active else None),
            "negotiated_item": None,
            "run_summary": {
                "day": self.day,
                "shop_state": self.shop_state,
                "cash": self.cash,
                "rent_days_left": self.rent_days_left,
                "inventory_count": len(self.inventory),
                "front_counter_count": 0,
                "back_counter_count": len(self.offers),
                "client_name": "Landlord" if dialogue_active else None,
                "client_intent": "RENT" if dialogue_active else None,
            },
        })
        return state

    def _legal_actions(self):
        actions = []
        if not self.in_game:
            return actions
        for offer in self.offers:
            actions.append({"action": "confirm_trade",
                            "args": {"item_id": offer["id"], "side": "buy"}})
        for item in self.inventory:
            actions.append({"action": "confirm_trade",
                            "args": {"item_id": item["id"], "side": "sell"}})
        if self.can_end_day:
            actions.append({"action": "end_day"})
        if self.dialogue and self.dialogue.get("available"):
            for choice in self.dialogue["choices"]:
                if self._choice_enabled(choice):
                    actions.append({"action": "choose_dialogue",
                                    "args": {"index": choice["index"]}})
        if self.bound:
            actions.append({"action": "sleep"})
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
        return {"ok": True, "op": op, "prompt_pending": False,
                "view": self._menu_view()}

    def _select_option(self, op, body):
        denied = self._gate_menu_write()
        if denied is not None:
            return self._menu_write_failure(op, denied)

        kind = body.get("kind")
        option_id = body.get("id")
        result = {
            "ok": False,
            "op": op,
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
        result.update({"ok": True, "action": "set", "view": self._menu_view()})
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
                result.update({"ok": True, "action": "remove",
                               "view": self._menu_view()})
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
        result.update({"ok": True, "action": "add", "view": self._menu_view()})
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
        return {"ok": True, "op": op, "prompt_pending": False,
                "view": self._menu_view()}

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
            return {"ok": True, "op": op, "reason": "already_focused",
                    "prompt_pending": False, "view": self._menu_view()}

        self.hovered = {"id": perk_id, "row": "selected"}
        self.rev += 1
        return {"ok": True, "op": op, "prompt_pending": False,
                "view": self._menu_view()}

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
        return {"ok": True, "op": op, "blurred_row": row,
                "prompt_pending": False, "view": self._menu_view()}

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
        return {"ok": True, "op": op, "prompt_pending": False,
                "view": self._menu_view()}

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
        return {"ok": False, "op": op, "error": error}

    def _confirm_trade(self, op, body):
        if not self.in_game:
            return self._trade_failure(op, "not_in_game")
        item_id = body.get("item_id")
        side = body.get("side")
        if isinstance(item_id, bool) or not isinstance(item_id, int):
            return self._trade_failure(op, "bad_item_id")

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
            })
            self.rev += 1
            return {"ok": True, "op": op, "action": "buy", "item_id": offer["id"]}

        if side == "sell":
            item = next((row for row in self.inventory if row["id"] == item_id), None)
            if item is None:
                offered = any(offer["id"] == item_id for offer in self.offers)
                return self._trade_failure(op, "not_owned" if offered else "item_not_found")
            self.inventory.remove(item)
            self.cash += item["sell"]
            self.rev += 1
            return {"ok": True, "op": op, "action": "sell", "item_id": item_id}

        return self._trade_failure(op, "bad_side")

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
        receipt = None
        if choice.get("action") == "pay_rent":
            self.cash -= self.rent
            self.rent_paid_total += self.rent
            self.rent_weeks_paid += 1
            self.rent_days_left = RENT_PERIOD_DAYS
            receipt = {
                "id": "rent-%04d" % self.rent_weeks_paid,
                "amount": self.rent,
                "cash_before": before_cash,
                "cash_after": self.cash,
                "cash_delta": self.cash - before_cash,
                "rent_days_left_before": before_days,
                "rent_days_left_after": self.rent_days_left,
                "paid_at_rev": self.rev + 1,
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
