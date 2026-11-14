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
import os
import sys
import time
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
TOOL_SET_CHOICES = (TOOL_SET_FULL, TOOL_SET_RECORDING)

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
    "activate_item", "advance_dialogue", "advance_expedition", "analyze_item",
    "arrest_wanted", "bargain", "bargain_preview", "barter_accept",
    "barter_status", "blur_new_game_option", "buy_upgrade", "buy_wholesale",
    "call", "call_next_customer", "choose_dialogue", "commissary_buy",
    "confirm_new_game", "confirm_trade", "contraband_status", "deliver_request",
    "dispatch_expedition", "end_call", "end_day", "events_status",
    "expedition_status", "focus_new_game_selected_perk", "get_action_history", "get_item",
    "get_layout", "get_legal_actions", "get_new_game_options", "get_recording_save_status",
    "get_run_summary", "get_state", "go_out", "health_status",
    "husbandry_status", "inspect_ticket", "list_analyzers", "list_books",
    "list_contacts", "list_containers", "list_factions", "list_goods",
    "list_interactions", "list_inventory", "list_labels", "list_locations",
    "list_machines", "list_marks", "list_requests", "list_services",
    "list_signs", "list_tickets", "list_upgrades", "list_wine",
    "load_game", "load_recording_game", "lottery_status", "machine_collect",
    "machine_grow_status", "machine_insert", "machine_status", "move_item",
    "new_game_continue", "new_game_next_page", "open_new_game_menu", "open_shop",
    "phone_advance", "phone_choose", "phone_state", "ping",
    "read_book", "read_item", "redeem_ticket", "report_client",
    "return_home", "save_game", "save_recording_game", "scavenge",
    "security_status", "select_new_game_option", "set_label", "set_pen_mode",
    "sleep", "start_fermentation", "toggle_item", "toggle_service",
    "use_tool", "wait_for_change", "wait_for_events", "wanted_status",
    "wine_add_ingredient", "wine_status",
    "capture_screenshot",
    "list_screenshots",
})

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


def _check_id(identifier):
    if type(identifier) is not str or not identifier.strip():
        return _invalid_argument("id")
    return None


def _first_error(*errors):
    for error in errors:
        if error is not None:
            return error
    return None


def build_server(client, tool_set=TOOL_SET_FULL):
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
            if tool_set == TOOL_SET_RECORDING and function.__name__ not in RECORDING_TOOL_NAMES:
                return function
            return server.tool(*args, **kwargs)(function)

        return decorator

    @tool()
    def get_state() -> dict:
        """Return the current shop snapshot: cash, rent, customer and negotiated item."""
        return client.get_state()

    @tool()
    def get_legal_actions() -> dict:
        """Return the actions that are legal right now (state-derived, includes args)."""
        state = client.get_state()
        return {"rev": state.get("rev", -1), "legal_actions": state.get("legal_actions", [])}

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
        """Faction reputation amounts, perks and influence."""
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
    def wine_add_water(item_id: int, ml: int = 750, rev: int = None) -> dict:
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
    def end_day(rev: int) -> dict:
        """Close the day when the game allows it. Re-read get_state afterwards."""
        return client.action("end_day", rev=rev)

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
    def sleep(rev: int) -> dict:
        """End the day: close the shutter, settle night services, save, advance to next morning."""
        return client.action("sleep", rev=rev)

    @tool()
    def save_game(rev: int) -> dict:
        """Save the current run. Re-read get_state afterwards; saving is synchronous."""
        return client.action("save_game", rev=rev)

    @tool()
    def confirm_trade(item_id: int, side: str, rev: int) -> dict:
        """Buy (side=buy) or sell (side=sell) one item currently on the back counter."""
        return client.action("confirm_trade", item_id=item_id, side=side, rev=rev)

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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=None, help="path to bridge.conf")
    parser.add_argument(
        "--tools",
        choices=TOOL_SET_CHOICES,
        default=TOOL_SET_FULL,
        help=(
            "tool surface to register: full (all 100 tools, default) or "
            "recording (94 player-equivalent tools; act/press/debug shortcuts "
            "and the hardcoded new_game are left out)"
        ),
    )
    parser.add_argument("--probe", action="store_true", help="check the bridge and exit")
    args = parser.parse_args()

    try:
        client = BridgeClient(args.config)
    except (BridgeError, OSError) as error:
        print("pawnmind: " + str(error), file=sys.stderr)
        return 2

    if args.probe:
        probe(client)
        return 0

    build_server(client, args.tools).run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
