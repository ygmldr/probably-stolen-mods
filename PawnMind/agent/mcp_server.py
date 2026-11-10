#!/usr/bin/env python3
"""PawnMind MCP server (stdio).

M1 scaffold: exposes the read-only state surface plus ping/wait_for_change.
Gameplay actions are added after the bridge action layer lands.

Install:
  python -m venv .venv
  .venv/Scripts/python.exe -m pip install mcp

Run:
  python mcp_server.py --config "<...>/ModData/pawnmind/bridge.conf"
  python mcp_server.py --config "..." --probe
"""

import argparse
import sys
import time

try:  # mcp 2.x renamed FastMCP to MCPServer
    from mcp.server.mcpserver import MCPServer
except ImportError:  # mcp 1.x
    from mcp.server.fastmcp import FastMCP as MCPServer

from pawnmind_client import BridgeClient, BridgeError


def build_server(client):
    server = MCPServer(
        "pawnmind",
        instructions=(
            "You are the shopkeeper of a deep-space pawnshop. "
            "Call get_state before every decision. Actions are single-step: "
            "do one thing, then read the state again to verify the outcome. "
            "Never invent item ids, prices or customer names."
        ),
    )

    @server.tool()
    def get_state() -> dict:
        """Return the current shop snapshot: cash, rent, customer and negotiated item."""
        return client.get_state()

    @server.tool()
    def get_legal_actions() -> dict:
        """Return the actions that are legal right now (state-derived, includes args)."""
        state = client.get_state()
        return {"rev": state.get("rev", -1), "legal_actions": state.get("legal_actions", [])}

    @server.tool()
    def get_run_summary() -> dict:
        """Compact run summary: day, shop state, cash, rent, inventory, current client."""
        state = client.get_state()
        return state.get("run_summary", {})

    @server.tool()
    def get_action_history(limit: int = 20) -> list:
        """Recent actions with their args, ok flag and revision."""
        return client.action("get_action_history", limit=limit)

    @server.tool()
    def act(action: str, args: dict, rev: int, action_id: str = "") -> dict:
        """Generic entry point: run one legal action by name with its args."""
        return client.action("act", action=action, args=args, rev=rev, action_id=action_id or None)

    @server.tool()
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

    @server.tool()
    def get_ui_snapshot(rev: int) -> dict:
        """List active UI controls (tablet buttons and plain buttons) with stable ids."""
        return client.action("get_ui_snapshot", rev=rev)

    @server.tool()
    def press(control_id: str, rev: int) -> dict:
        """Invoke a control from the last get_ui_snapshot by id (for windows without a typed adapter)."""
        return client.action("press", control_id=control_id, rev=rev)

    @server.tool()
    def list_goods(scope: str = "all") -> dict:
        """List goods on the front/back counters: scope=client (non-owned), sellable (owned+client wants), all."""
        return client.action("list_goods", scope=scope)

    @server.tool()
    def list_locations() -> dict:
        """List discovered night locations (dumping ground / commissary / Dr Jackson) and availability."""
        return client.action("list_locations")

    @server.tool()
    def go_out(location: str, rev: int) -> dict:
        """Leave the shop and travel to a night location."""
        return client.action("go_out", location=location, rev=rev)

    @server.tool()
    def scavenge(rev: int) -> dict:
        """Scavenge at the dumping ground; returns gained items and remaining attempts."""
        return client.action("scavenge", rev=rev)

    @server.tool()
    def return_home(rev: int) -> dict:
        """Return from a night location to the shop."""
        return client.action("return_home", rev=rev)

    @server.tool()
    def buy_wholesale(rev: int) -> dict:
        """Buy the current client's whole wholesale offer (requires isOfferWholesale)."""
        return client.action("buy_wholesale", rev=rev)

    @server.tool()
    def commissary_buy(identifier: str, rev: int) -> dict:
        """Buy one item from the supply station (commissary) by identifier."""
        return client.action("commissary_buy", identifier=identifier, rev=rev)

    @server.tool()
    def list_books() -> dict:
        """List owned/unlocked books (vanilla BOOK_TAG and paper GUIDE_PAPER_TAG)."""
        return client.action("list_books")

    @server.tool()
    def read_book(book_id: str, page: int = -1) -> dict:
        """Read an owned book by item id or book id; returns pages and optionally one page."""
        return client.action("read_book", book_id=book_id, page=page)

    @server.tool()
    def read_item(item_id: int) -> dict:
        """Read one item's visible details plus book/document flags."""
        return client.action("read_item", item_id=item_id)

    @server.tool()
    def list_interactions(item_id: int) -> dict:
        """List available interactions for one item: activate/toggle/target."""
        return client.action("list_interactions", item_id=item_id)

    @server.tool()
    def activate_item(item_id: int, rev: int) -> dict:
        """Activate an owned item (books, lottery tickets, scanners)."""
        return client.action("activate_item", item_id=item_id, rev=rev)

    @server.tool()
    def toggle_item(item_id: int, rev: int) -> dict:
        """Toggle an owned item (gene scanner, machine switches)."""
        return client.action("toggle_item", item_id=item_id, rev=rev)

    @server.tool()
    def use_tool(tool_id: int, target_id: int, rev: int) -> dict:
        """Use an owned tool on a target item (screwdriver, UV, scanner, marking pen)."""
        return client.action("use_tool", tool_id=tool_id, target_id=target_id, rev=rev)

    @server.tool()
    def security_status() -> dict:
        """Security evidence, crimes, case officer and current inspection item."""
        return client.action("security_status")

    @server.tool()
    def list_services() -> dict:
        """Nightly services (insurance, janitorial, power, informant) and toggles."""
        return client.action("list_services")

    @server.tool()
    def toggle_service(id: str, on: bool, rev: int) -> dict:
        """Toggle a nightly service on/off."""
        return client.action("toggle_service", id=id, on=on, rev=rev)

    @server.tool()
    def husbandry_status() -> dict:
        """Owned animals/fish with health/dead state."""
        return client.action("husbandry_status")

    @server.tool()
    def dispatch_expedition(hireling_id: str, location_id: str, rev: int) -> dict:
        """Send a hireling to an unlocked expedition location."""
        return client.action("dispatch_expedition", hireling_id=hireling_id, location_id=location_id, rev=rev)

    @server.tool()
    def advance_expedition(expedition_id: str, rev: int) -> dict:
        """Advance an ongoing expedition one step."""
        return client.action("advance_expedition", expedition_id=expedition_id, rev=rev)

    @server.tool()
    def expedition_status() -> dict:
        """Expedition unlock, locations, hirelings and ongoing runs."""
        return client.action("expedition_status")

    @server.tool()
    def events_status() -> dict:
        """Active and queued store events."""
        return client.action("events_status")

    @server.tool()
    def list_signs() -> dict:
        """Owned marketing signs."""
        return client.action("list_signs")

    @server.tool()
    def deliver_request(id: str, rev: int) -> dict:
        """Deliver a fillable StoreRequest and claim rewards."""
        return client.action("deliver_request", id=id, rev=rev)

    @server.tool()
    def barter_accept(rev: int) -> dict:
        """Accept the current client's barter offer when acceptable."""
        return client.action("barter_accept", rev=rev)

    @server.tool()
    def arrest_wanted(id: str, rev: int) -> dict:
        """Report/arrest a wanted person by id."""
        return client.action("arrest_wanted", id=id, rev=rev)

    @server.tool()
    def list_factions() -> dict:
        """Faction reputation amounts, perks and influence."""
        return client.action("list_factions")

    @server.tool()
    def health_status() -> dict:
        """Player wounds, luck, addiction timers."""
        return client.action("health_status")

    @server.tool()
    def list_requests() -> dict:
        """Active customer/faction requests and rewards."""
        return client.action("list_requests")

    @server.tool()
    def wanted_status() -> dict:
        """Wanted-person visit state and wanted list."""
        return client.action("wanted_status")

    @server.tool()
    def barter_status() -> dict:
        """Current client's barter offer and conditions."""
        return client.action("barter_status")

    @server.tool()
    def list_wine() -> dict:
        """List owned wine containers/bottles and finished state."""
        return client.action("list_wine")

    @server.tool()
    def wine_status(item_id: int) -> dict:
        """Wine bottle fermentation/quality details."""
        return client.action("wine_status", item_id=item_id)

    @server.tool()
    def wine_add_water(item_id: int, ml: int = 750, rev: int = None) -> dict:
        """Add water to a wine bottle (test/gameplay helper)."""
        return client.action("wine_add_water", item_id=item_id, ml=ml, rev=rev)

    @server.tool()
    def wine_add_ingredient(bottle_id: int, item_id: int, rev: int) -> dict:
        """Move a berry/yeast into a wine bottle's ingredient grid."""
        return client.action("wine_add_ingredient", bottle_id=bottle_id, item_id=item_id, rev=rev)

    @server.tool()
    def start_fermentation(item_id: int, rev: int) -> dict:
        """Try to start fermentation on a wine bottle."""
        return client.action("start_fermentation", item_id=item_id, rev=rev)

    @server.tool()
    def list_machines() -> dict:
        """List owned standard machines with state, progress and slots."""
        return client.action("list_machines")

    @server.tool()
    def machine_grow_status(machine_id: int) -> dict:
        """Hydroponic grow status: power/water/seed/progress."""
        return client.action("machine_grow_status", machine_id=machine_id)

    @server.tool()
    def machine_status(machine_id: int) -> dict:
        """Show one machine's slots and contents."""
        return client.action("machine_status", machine_id=machine_id)

    @server.tool()
    def machine_insert(machine_id: int, slot: int, item_id: int, rev: int) -> dict:
        """Insert an inventory item into a machine slot."""
        return client.action("machine_insert", machine_id=machine_id, slot=slot, item_id=item_id, rev=rev)

    @server.tool()
    def machine_collect(machine_id: int, slot: int, rev: int) -> dict:
        """Move items from a machine slot back to the backpack."""
        return client.action("machine_collect", machine_id=machine_id, slot=slot, rev=rev)

    @server.tool()
    def list_upgrades() -> dict:
        """List network/store upgrades with cost/unlock/prerequisite state."""
        return client.action("list_upgrades")

    @server.tool()
    def buy_upgrade(id: str, rev: int) -> dict:
        """Buy a network upgrade by id (also unlocks supplier phone contacts)."""
        return client.action("buy_upgrade", id=id, rev=rev)

    @server.tool()
    def lottery_status() -> dict:
        """Lottery unlock state, edition, day and prize table."""
        return client.action("lottery_status")

    @server.tool()
    def list_tickets() -> dict:
        """List owned lottery tickets with number/edition/matches/prize."""
        return client.action("list_tickets")

    @server.tool()
    def inspect_ticket(item_id: int) -> dict:
        """Inspect one lottery ticket."""
        return client.action("inspect_ticket", item_id=item_id)

    @server.tool()
    def redeem_ticket(item_id: int, rev: int) -> dict:
        """Redeem a winning lottery ticket."""
        return client.action("redeem_ticket", item_id=item_id, rev=rev)

    @server.tool()
    def list_contacts(include_hidden: bool = False) -> dict:
        """List phone contacts, state, cooldown and spend."""
        return client.action("list_contacts", include_hidden=include_hidden)

    @server.tool()
    def call(number: int, rev: int) -> dict:
        """Open the phone and dial a contact."""
        return client.action("call", number=number, rev=rev)

    @server.tool()
    def phone_state() -> dict:
        """Current phone call state and dialogue."""
        return client.action("phone_state")

    @server.tool()
    def phone_advance(rev: int) -> dict:
        """Advance the phone dialogue by one step."""
        return client.action("phone_advance", rev=rev)

    @server.tool()
    def phone_choose(index: int, rev: int) -> dict:
        """Choose a phone dialogue option by index."""
        return client.action("phone_choose", index=index, rev=rev)

    @server.tool()
    def end_call(rev: int) -> dict:
        """End the current phone call."""
        return client.action("end_call", rev=rev)

    @server.tool()
    def list_labels(item_id: int) -> dict:
        """List editable label categories and conditions for an item."""
        return client.action("list_labels", item_id=item_id)

    @server.tool()
    def set_label(item_id: int, category: str, condition_id: str, rev: int) -> dict:
        """Set one editable label condition on an item."""
        return client.action("set_label", item_id=item_id, category=category, condition_id=condition_id, rev=rev)

    @server.tool()
    def list_analyzers() -> dict:
        """List owned analyzers (water strip/aquascan, chem scanner, plant analyzer, gene scanner)."""
        return client.action("list_analyzers")

    @server.tool()
    def analyze_item(analyzer_id: int, target_id: int, rev: int) -> dict:
        """Run an analyzer on a target and return its visible result fields."""
        return client.action("analyze_item", analyzer_id=analyzer_id, target_id=target_id, rev=rev)

    @server.tool()
    def set_pen_mode(pen_id: int, mode: int, rev: int) -> dict:
        """Set marking pen mode: 0 red, 1 purple, 2 green, 3 blue, 4 orange, 5 eraser."""
        return client.action("set_pen_mode", pen_id=pen_id, mode=mode, rev=rev)

    @server.tool()
    def list_marks() -> dict:
        """List marked fish/animals with their color."""
        return client.action("list_marks")

    @server.tool()
    def list_containers() -> dict:
        """List player containers (main/front/back/showcase/hidden/afterhour/trash/sold) with counts."""
        return client.action("list_containers")

    @server.tool()
    def list_inventory(container: str = "main", search: str = "", page: int = 1, page_size: int = 20) -> dict:
        """Paginated inventory for one container; search matches item name or identifier."""
        return client.action("list_inventory", container=container, search=search, page=page, page_size=page_size)

    @server.tool()
    def move_item(item_id: int, target: str, rev: int, x: int = -1, y: int = -1,
                  rotate: bool = False, turns: int = 0) -> dict:
        """Move one item; omit x/y to auto-place. turns=0..3 rotates 0/90/180/270 degrees."""
        return client.action("move_item", item_id=item_id, target=target, x=x, y=y,
                             rotate=rotate, turns=turns, rev=rev)

    @server.tool()
    def get_layout(container: str = "main") -> dict:
        """Return the grid layout (width/height, rows of item ids) and item positions for a container."""
        return client.action("get_layout", container=container)

    @server.tool()
    def get_item(item_id: int) -> dict:
        """Full visible details for one item id: container, value, count, lock, size, descriptions."""
        return client.action("get_item", item_id=item_id)

    @server.tool()
    def ping() -> dict:
        """Round-trip check used to verify the in-game bridge is alive."""
        return client.action("ping")

    @server.tool()
    def open_shop(rev: int) -> dict:
        """Open the shop shutter. Re-read get_state afterwards to confirm."""
        return client.action("open_shop", rev=rev)

    @server.tool()
    def call_next_customer(rev: int) -> dict:
        """Call the next customer to the counter. Re-read get_state afterwards."""
        return client.action("call_next_customer", rev=rev)

    @server.tool()
    def advance_dialogue(rev: int) -> dict:
        """Advance the current non-choice dialogue line. Re-read get_state afterwards."""
        return client.action("advance_dialogue", rev=rev)

    @server.tool()
    def choose_dialogue(index: int, rev: int) -> dict:
        """Pick a dialogue choice by index (0-based). Re-read get_state afterwards."""
        return client.action("choose_dialogue", index=index, rev=rev)

    @server.tool()
    def end_day(rev: int) -> dict:
        """Close the day when the game allows it. Re-read get_state afterwards."""
        return client.action("end_day", rev=rev)

    @server.tool()
    def bargain_preview(direction: str, percent: int) -> dict:
        """Preview one bargain: success/leave/refuse chance, price before/after, reputation estimate."""
        return client.action("bargain_preview", direction=direction, percent=percent)

    @server.tool()
    def bargain(direction: str, percent: int, rev: int) -> dict:
        """One bargain offer: direction is sell_discount|sell_markup|buy_markup|buy_discount; percent is 10|25|50|75."""
        return client.action("bargain", direction=direction, percent=percent, rev=rev)

    @server.tool()
    def new_game(rev: int) -> dict:
        """Start a fresh apprentice run from the main menu. Never touches existing save slots."""
        return client.action("new_game", rev=rev)

    @server.tool()
    def load_game(slot: int, rev: int) -> dict:
        """Load a save slot from the main menu (slot ids are listed in get_state.save_slots)."""
        return client.action("load_game", slot=slot, rev=rev)

    @server.tool()
    def sleep(rev: int) -> dict:
        """End the day: close the shutter, settle night services, save, advance to next morning."""
        return client.action("sleep", rev=rev)

    @server.tool()
    def save_game(rev: int) -> dict:
        """Save the current run. Re-read get_state afterwards; saving is synchronous."""
        return client.action("save_game", rev=rev)

    @server.tool()
    def confirm_trade(item_id: int, side: str, rev: int) -> dict:
        """Buy (side=buy) or sell (side=sell) one item currently on the back counter."""
        return client.action("confirm_trade", item_id=item_id, side=side, rev=rev)

    @server.tool()
    def wait_for_events(since_rev: int = -1, timeout_ms: int = 25000) -> dict:
        """Block until the game revision changes; returns the fresh state (long-poll, no busy loop)."""
        return client.wait_events(since_rev, timeout_ms)

    @server.tool()
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

    build_server(client).run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
