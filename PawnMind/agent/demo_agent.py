"""Scripted 'AI' policy for S6B — deterministic and **not** an LLM.

What this module is for
-----------------------
It is the reference recording path for the PawnMind bridge: a single,
reproducible decision script that drives the *real* MCP stdio server
(`mcp_server.py`) with nothing but MCP tools. It exists so the transport, tool
surface, rev/action_id discipline and first-week bookkeeping can be verified
end to end against the offline fake game (`fake_game.py`), without a model, a
subscription, a game process or a save file.

It is **not** evidence that an AI can play the game. A real LLM (the planned
OpenCode + DeepSeek V4.1 Flash host) still has to be wired and observed in S8;
this script only proves that the plumbing and the required action sequence work.

Ground rules enforced here
--------------------------
* Only MCP tools are called, only from :data:`ALLOWED_OPS`. HTTP is never
  spoken directly and no bridge internals are imported.
* Every write carries the ``rev`` of the last observed payload; a ``stale_rev``,
  ``outcome_unknown`` or any unexpected stable error aborts the run with that
  reason instead of retrying (no double charge, no invented success).
* The caller (this script) never invents ``action_id`` values: the MCP/client
  layer generates them, and the bridge treats them as idempotent.
* The only step outside the MCP tool surface is
  :meth:`HostActions.return_to_menu` — the S8 runbook's human step that sends
  the game back to the main menu before a controlled load.

Run against a bridge config (offline fake or, only when a human is present in
S8, the real bridge):

    python demo_agent.py --config "<...>/bridge.conf"
"""

import argparse
import asyncio
import json
import os
import sys

from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

HERE = os.path.dirname(os.path.abspath(__file__))
MCP_SERVER = os.path.join(HERE, "mcp_server.py")

READ_ONLY_OPS = frozenset({
    "ping",
    "get_state",
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
ALLOWED_OPS = READ_ONLY_OPS | WRITE_OPS

#: Tools the demo needs; used as a surface check by the e2e test and by hosts
#: that want a minimal allowlist instead of the full 100-tool MCP surface.
#: Must stay a subset of `mcp_server.RECORDING_TOOL_NAMES`; the S7A test
#: `test_mcp_tools_mode.py` pins that relation so the two cannot drift apart.
REQUIRED_TOOLS = (
    "ping",
    "get_state",
    "get_new_game_options",
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_next_page",
    "focus_new_game_selected_perk",
    "blur_new_game_option",
    "new_game_continue",
    "confirm_new_game",
    "get_recording_save_status",
    "save_recording_game",
    "load_recording_game",
    "list_goods",
    "list_inventory",
    "confirm_trade",
    "choose_dialogue",
    "end_day",
    "sleep",
)
RECORDING_TOOLS = ALLOWED_OPS

#: Errors that must stop the run instead of being retried or worked around.
STOP_ERRORS = frozenset({
    "stale_rev",
    "outcome_unknown",
    "recording_outcome_unknown",
    "recording_locked",
    "recording_slot_mismatch",
    "recording_run_mismatch",
    "version_locked",
    "unauthorized",
})

SCENARIO_PREFERENCE = ("scavenger", "fence", "courier")
PERK_DRILL_PREFERENCE = ("tough",)
PERK_PAGE1_PREFERENCE = ("saving", "lucky", "charming")
PERK_PAGE2_PREFERENCE = ("handy", "nightowl")
DIFFICULTY_PREFERENCE = ("apprentice", "journeyman")
FIRST_WEEK_DAYS = 7


class DemoAbort(RuntimeError):
    """The run stopped on purpose; nothing else was attempted afterwards."""

    def __init__(self, reason, detail=None):
        self.reason = reason
        self.detail = detail
        message = reason if detail is None else "%s: %s" % (reason, detail)
        super().__init__(message)


class HostActions:
    """The only non-MCP step: a human sends the game back to the main menu."""

    def return_to_menu(self):
        raise NotImplementedError(
            "the host must return the game to the main menu before the controlled load"
        )


class ConsoleHost(HostActions):
    """Interactive host for a manual (S8) run."""

    def __init__(self, prompt=None, reader=input):
        self.prompt = prompt or (
            "Return the game to the main menu (quit the run), then press Enter: "
        )
        self._reader = reader

    def return_to_menu(self):
        self._reader(self.prompt)


class ToolBox:
    """MCP-only tool caller with rev tracking and stop-on-error semantics."""

    def __init__(self, session):
        self.session = session
        self.rev = -1
        self.steps = []

    async def call(self, tool, expect_error=None, **args):
        if tool not in ALLOWED_OPS:
            raise DemoAbort("tool_not_allowlisted", tool)
        if tool in WRITE_OPS and not isinstance(args.get("rev"), int):
            raise DemoAbort("rev_missing", tool)

        try:
            result = await self.session.call_tool(tool, args)
        except DemoAbort:
            raise
        except Exception as error:  # transport/stdio failure
            raise DemoAbort("mcp_transport_error", "%s: %s" % (tool, error))

        if getattr(result, "is_error", False):
            text = result.content[0].text if result.content else ""
            raise DemoAbort("mcp_tool_error", "%s: %s" % (tool, text))

        text = result.content[0].text if result.content else "{}"
        try:
            payload = json.loads(text)
        except ValueError:
            raise DemoAbort("bad_tool_payload", tool)
        if not isinstance(payload, dict):
            raise DemoAbort("bad_tool_payload", tool)

        rev = payload.get("rev")
        if isinstance(rev, int) and not isinstance(rev, bool):
            self.rev = rev

        ok = bool(payload.get("ok", True))
        error = payload.get("error")
        step = {"tool": tool, "args": dict(args), "ok": ok, "rev": self.rev}
        if not ok and error:
            # A successful read may still carry a diagnostic `error` field (for
            # example get_recording_save_status while armed); only real
            # refusals belong in the trace's error column.
            step["error"] = error
        self.steps.append(step)

        if expect_error is not None:
            if ok or error != expect_error:
                raise DemoAbort(
                    "unexpected_result",
                    "%s expected %s, got %r" % (tool, expect_error, payload),
                )
            return payload
        if not ok:
            if error in STOP_ERRORS:
                raise DemoAbort(error, tool)
            raise DemoAbort("unexpected_error", error or tool)
        return payload

    async def write(self, tool, expect_error=None, **args):
        if self.rev < 0:
            raise DemoAbort("rev_unknown", tool)
        args["rev"] = self.rev
        return await self.call(tool, expect_error, **args)


class ScriptedShopkeeper:
    """Deterministic first-week path (menu -> 7 days -> rent -> save/load)."""

    def __init__(self, session, host=None):
        self.tools = ToolBox(session)
        self.host = host
        self.buy_total = 0
        self.sell_total = 0
        self.days_traded = 0
        self.start_cash = None
        self.rent_due = None
        self.receipt = None
        self.scenario = None
        self.slot = None
        self.binding = None
        self.binding_before_save = None
        self.binding_after_load = None
        self.last_save_time = None
        self.final_state = None
        self.loaded_state = None
        self._load_expected = None

    # ------------------------------------------------------------- public API

    async def run(self):
        await self._self_check()
        await self._choose_start()
        await self._first_week()
        await self._rent_and_final_day()
        await self._verify_save_load()
        return self._report()

    # --------------------------------------------------------------- helpers

    @staticmethod
    def _view(payload):
        view = payload.get("view")
        if not isinstance(view, dict):
            raise DemoAbort("missing_view", payload.get("op"))
        return view

    def _require(self, condition, message, detail=None):
        if condition:
            return
        raise DemoAbort("assertion_failed",
                        message if detail is None else "%s (%r)" % (message, detail))

    @staticmethod
    def _pick_enabled(view, preference):
        options = view.get("options") or []
        by_id = {option.get("id"): option for option in options}
        for wanted in preference:
            option = by_id.get(wanted)
            if option is not None and option.get("enabled"):
                return option
        for option in options:
            if option.get("enabled"):
                return option
        raise DemoAbort("no_viable_option", "phase=%s" % (view.get("phase"),))

    @staticmethod
    def _pay_choice_index(choices):
        for position, choice in enumerate(choices):
            if "rent" in (choice.get("text") or "").lower():
                return position
        return 0

    # ------------------------------------------------------------- self check

    async def _self_check(self):
        ping = await self.tools.call("ping")
        self._require(ping.get("pong") is True, "the bridge ping did not answer", ping)

        state = await self.tools.call("get_state")
        self._require(state.get("in_game") is False,
                      "the run must start at the main menu", state.get("in_game"))

        menu = await self.tools.call("get_new_game_options")
        self._require(menu.get("phase") == "closed",
                      "the new-game menu must start closed", menu.get("phase"))
        await self.tools.write("new_game_continue", expect_error="menu_closed")

    # --------------------------------------------------------------- new game

    async def _choose_start(self):
        opened = await self.tools.write("open_new_game_menu")
        view = self._view(opened)
        self._require(view.get("phase") == "specialization",
                      "the menu did not open on the scenario page", view.get("phase"))
        self._require(view.get("page_count", 0) >= 2,
                      "the scenario row should span more than one page",
                      view.get("page_count"))

        forward = await self.tools.write("new_game_next_page",
                                         row="available", direction=1)
        self._require(self._view(forward).get("page") == 1,
                      "the page turn did not move to page 1",
                      self._view(forward).get("page"))
        await self.tools.write("new_game_next_page", row="available", direction=-1)
        await self.tools.write("new_game_next_page", row="available", direction=-1,
                               expect_error="page_out_of_range")

        view = await self.tools.call("get_new_game_options")
        self._require(view.get("page") == 0, "the scenario row did not return to page 0")
        scenario = self._pick_enabled(view, SCENARIO_PREFERENCE)
        picked = await self.tools.write("select_new_game_option",
                                        kind="scenario", id=scenario["id"])
        self._require(picked.get("action") == "set",
                      "the scenario click was not a set", picked.get("action"))
        self._require(self._view(picked).get("selected_id") == scenario["id"],
                      "the scenario was not read back as selected",
                      self._view(picked).get("selected_id"))
        self.scenario = scenario

        perks = self._view(await self.tools.write("new_game_continue"))
        self._require(perks.get("phase") == "perks",
                      "continue did not reach the perks page", perks.get("phase"))
        budget = perks.get("perks") or {}
        self._require(budget.get("selected_count") == 0,
                      "perks must start empty", budget.get("selected_count"))
        self._require(budget.get("max_points", 0) > 0,
                      "the perks page exposes no point budget", budget)

        await self._drill_perk_undo(perks)

        view = await self.tools.call("get_new_game_options")
        first = self._pick_enabled(view, PERK_PAGE1_PREFERENCE)
        added = await self.tools.write("select_new_game_option",
                                       kind="perk", id=first["id"])
        self._require(added.get("action") == "add",
                      "the first perk was not added", added.get("action"))

        turned = await self.tools.write("new_game_next_page",
                                        row="available", direction=1)
        self._require(self._view(turned).get("page") == 1,
                      "the perks row did not turn to page 1",
                      self._view(turned).get("page"))
        view = await self.tools.call("get_new_game_options")
        second = self._pick_enabled(view, PERK_PAGE2_PREFERENCE)
        added = await self.tools.write("select_new_game_option",
                                       kind="perk", id=second["id"])
        view = self._view(added)
        self._require(view["perks"]["selected_count"] >= 2,
                      "the second perk was not added", view["perks"])
        self._require(view["perks"]["used_points"] <= view["perks"]["max_points"],
                      "the perk budget was overrun", view["perks"])

        difficulty = self._view(await self.tools.write("new_game_continue"))
        self._require(difficulty.get("phase") == "difficulty",
                      "continue did not reach the difficulty page",
                      difficulty.get("phase"))
        locked = [option for option in difficulty.get("options") or []
                  if not option.get("enabled")]
        if locked:
            await self.tools.write("select_new_game_option", kind="difficulty",
                                   id=locked[0]["id"], expect_error="option_locked")
            difficulty = await self.tools.call("get_new_game_options")
        level = self._pick_enabled(difficulty, DIFFICULTY_PREFERENCE)
        picked = await self.tools.write("select_new_game_option",
                                        kind="difficulty", id=level["id"])
        self._require(picked.get("action") == "set",
                      "the difficulty click was not a set", picked.get("action"))
        await self.tools.write("new_game_continue",
                               expect_error="difficulty_start_forbidden")

        status = await self.tools.call("get_recording_save_status")
        self._require(status.get("recording_mode") == "armed",
                      "recording must be armed before the new game",
                      status.get("recording_mode"))
        self.slot = status.get("configured_slot")
        self._require(isinstance(self.slot, int) and not isinstance(self.slot, bool),
                      "no configured recording slot", self.slot)

        started = await self.tools.write("confirm_new_game")
        self._require(started.get("start_requested") is True,
                      "confirm_new_game did not request a start", started)
        self._require(started.get("bound") is True,
                      "confirm_new_game did not bind the recording slot", started)
        binding = started.get("binding") or {}
        self._require(binding.get("slot") == self.slot,
                      "the bound slot differs from the configured slot", binding)
        self.binding = dict(binding)

        state = await self.tools.call("get_state")
        self._require(state.get("in_game") is True,
                      "the run did not start", state.get("in_game"))
        self._require(state.get("day") == 1,
                      "the run should start on day 1", state.get("day"))
        rent = state.get("rent") or {}
        self._require(rent.get("readable") is True,
                      "rent must be readable on day 1", rent)
        self._require(rent.get("rent_paid_total") == 0,
                      "no rent should be paid on day 1", rent.get("rent_paid_total"))
        self.start_cash = state.get("cash")
        self.rent_due = rent.get("due")

        status = await self.tools.call("get_recording_save_status")
        self._require(status.get("bound") is True
                      and status.get("slot_matches") is True
                      and status.get("run_matches") is True,
                      "the recording status is not bound after the start", status)

    async def _drill_perk_undo(self, perks_view):
        """Select a perk, then undo it with the documented hover dance."""
        drill = self._pick_enabled(perks_view, PERK_DRILL_PREFERENCE)
        added = await self.tools.write("select_new_game_option",
                                       kind="perk", id=drill["id"])
        self._require(added.get("action") == "add",
                      "the drill perk was not added", added.get("action"))
        used = self._view(added)["perks"]["used_points"]

        # Documented S3B behavior: the available card still shadows the click.
        shadowed = await self.tools.write("select_new_game_option",
                                          kind="perk", id=drill["id"],
                                          expect_error="ambiguous_target")
        self._require(self._view(shadowed)["perks"]["used_points"] == used,
                      "a shadowed click changed the spent points")

        blurred = await self.tools.write("blur_new_game_option", id=drill["id"])
        self._require(self._view(blurred).get("hovered_id") is None,
                      "blur did not clear the hover",
                      self._view(blurred).get("hovered_id"))

        focused = await self.tools.write("focus_new_game_selected_perk",
                                         id=drill["id"])
        self._require(self._view(focused).get("hovered_in_selected_row") is True,
                      "focus did not land on the selected row",
                      self._view(focused))

        removed = await self.tools.write("select_new_game_option",
                                         kind="perk", id=drill["id"])
        self._require(removed.get("action") == "remove",
                      "the drill perk was not removed", removed.get("action"))
        self._require(self._view(removed)["perks"]["used_points"] == 0,
                      "the undo left points spent",
                      self._view(removed)["perks"])

    # ------------------------------------------------------------- first week

    async def _trade_day(self, state, day_index):
        goods = await self.tools.call("list_goods", scope="all")
        offers = [row for row in goods.get("items") or [] if not row.get("owned")]
        self._require(len(offers) >= 2,
                      "expected at least two buyable offers on day %d" % day_index,
                      goods.get("items"))
        cheapest = min(offers, key=lambda row: row["value"])

        # One deliberate failure branch: something is on offer that the current
        # cash cannot cover. Only meaningful while such an offer exists.
        if day_index == 1:
            priced_out = [row for row in offers
                          if row["value"] > state.get("cash", 0)]
            if priced_out:
                pricey = max(priced_out, key=lambda row: row["value"])
                before = await self.tools.call("get_state")
                await self.tools.write("confirm_trade", item_id=pricey["id"],
                                       side="buy", expect_error="cannot_afford")
                after = await self.tools.call("get_state")
                self._require(after.get("cash") == before.get("cash"),
                              "a refused buy changed the cash",
                              {"before": before.get("cash"), "after": after.get("cash")})
                self._require(after.get("inventory_count") == before.get("inventory_count"),
                              "a refused buy changed the inventory")
                self._require(after.get("rev") == before.get("rev"),
                              "a refused buy advanced the revision")
                state = after

        inventory_before = state.get("inventory_count")
        cash_before = state.get("cash")
        await self.tools.write("confirm_trade", item_id=cheapest["id"], side="buy")
        state = await self.tools.call("get_state")
        self._require(state.get("cash") == cash_before - cheapest["value"],
                      "buy cash does not match the offer", {
                          "cash_before": cash_before,
                          "price": cheapest["value"],
                          "cash_after": state.get("cash"),
                      })
        self._require(state.get("inventory_count") == inventory_before + 1,
                      "the bought item is not in the inventory",
                      {"before": inventory_before,
                       "after": state.get("inventory_count")})
        self.buy_total += cheapest["value"]

        inventory = await self.tools.call("list_inventory", container="main")
        items = inventory.get("items") or []
        matches = [row for row in items
                   if row.get("identifier") == cheapest.get("identifier")]
        self._require(len(matches) >= 1,
                      "the bought item is not listed in the inventory", items)
        owned = matches[-1]

        cash_before = state.get("cash")
        await self.tools.write("confirm_trade", item_id=owned["id"], side="sell")
        state = await self.tools.call("get_state")
        self._require(state.get("cash") == cash_before + owned["value"],
                      "sell cash does not match the visible value", {
                          "cash_before": cash_before,
                          "value": owned["value"],
                          "cash_after": state.get("cash"),
                      })
        self._require(state.get("inventory_count") == inventory_before,
                      "the sold item is still in the inventory",
                      {"before": inventory_before,
                       "after": state.get("inventory_count")})
        self.sell_total += owned["value"]
        self.days_traded += 1
        return state

    async def _first_week(self):
        state = await self.tools.call("get_state")
        for day_index in range(1, FIRST_WEEK_DAYS + 1):
            state = await self._trade_day(state, day_index)

            ended = await self.tools.write("end_day")
            readback = ended.get("readback") or {}
            self._require(readback.get("day_advanced") is False,
                          "end_day must close the shop without advancing the day",
                          readback)

            slept = await self.tools.write("sleep")
            readback = slept.get("readback") or {}
            self._require(slept.get("slept") is True and slept.get("saved") is False,
                          "sleep reported the wrong flags", slept)
            self._require(readback.get("day_advanced") is True,
                          "sleep did not advance the day", readback)
            self._require(slept.get("last_save_time") is not None,
                          "sleep did not report the auto-save")

            state = await self.tools.call("get_state")
            self._require(state.get("day") == day_index + 1,
                          "unexpected day after sleep", state.get("day"))
            self._require(state.get("shop_state") == "OPEN",
                          "the shop did not reopen", state.get("shop_state"))

    # ------------------------------------------------------------------- rent

    async def _rent_and_final_day(self):
        state = await self.tools.call("get_state")
        rent = state.get("rent") or {}
        self._require(state.get("day") == FIRST_WEEK_DAYS + 1,
                      "the first week did not end", state.get("day"))
        self._require(rent.get("days_left") == 0,
                      "the rent countdown should be at 0", rent.get("days_left"))
        self._require(rent.get("can_pay") is False,
                      "the cash should still be short of the rent", rent.get("cash"))
        self._require(rent.get("rent_paid_total") == 0,
                      "no rent should be paid yet", rent.get("rent_paid_total"))

        dialogue = state.get("dialogue") or {}
        self._require(dialogue.get("available") is True and dialogue.get("choice") is True,
                      "the landlord dialogue is not active", dialogue)
        choices = dialogue.get("choices") or []
        index = self._pay_choice_index(choices)
        self._require(choices[index].get("enabled") is False,
                      "the rent choice must be disabled while the cash is short",
                      choices[index])

        before = state
        await self.tools.write("choose_dialogue", index=index,
                               expect_error="choice_disabled")
        after = await self.tools.call("get_state")
        self._require(after.get("cash") == before.get("cash"),
                      "a refused rent choice changed the cash",
                      {"before": before.get("cash"), "after": after.get("cash")})
        self._require(after.get("rent") == before.get("rent"),
                      "a refused rent choice changed the rent state")
        self._require((after.get("dialogue") or {}).get("text")
                      == (before.get("dialogue") or {}).get("text"),
                      "a refused rent choice advanced the dialogue")

        state = await self._trade_day(after, FIRST_WEEK_DAYS + 1)
        rent = state.get("rent") or {}
        self._require(rent.get("can_pay") is True,
                      "the rent should be payable after earning", rent)
        choices = (state.get("dialogue") or {}).get("choices") or []
        self._require(choices[index].get("enabled") is True,
                      "the rent choice did not become enabled", choices[index])

        due = rent.get("due")
        cash_before = state.get("cash")
        paid = await self.tools.write("choose_dialogue", index=index)
        receipt = paid.get("last_rent_payment")
        self._require(isinstance(receipt, dict),
                      "choose_dialogue returned no rent receipt", paid)
        self._require(receipt.get("amount") == due,
                      "the receipt amount differs from the due rent",
                      {"due": due, "receipt": receipt})
        self._require(receipt.get("cash_before") == cash_before,
                      "the receipt cash_before is wrong", receipt)
        self._require(receipt.get("cash_after") == cash_before - due,
                      "the receipt cash_after is wrong", receipt)
        self._require(receipt.get("rent_days_left_after") > 0,
                      "the receipt does not reset the rent countdown", receipt)
        self._require(paid.get("dialogue_changed") is True,
                      "the paid dialogue did not change", paid)
        self.receipt = receipt

        first = await self.tools.call("get_state")
        second = await self.tools.call("get_state")
        self._require(first.get("rent") == second.get("rent"),
                      "reading the state twice changed the rent receipt")
        self._require((first.get("rent") or {}).get("rent_paid_total") == due,
                      "the paid total is not the rent",
                      (first.get("rent") or {}).get("rent_paid_total"))
        await self.tools.write("choose_dialogue", index=index,
                               expect_error="no_dialogue")

        state = await self.tools.call("get_state")
        self._require(state.get("cash") == receipt.get("cash_after"),
                      "the cash after paying does not match the receipt",
                      {"cash": state.get("cash"), "receipt": receipt})

        expected = (self.start_cash - self.buy_total + self.sell_total
                    - (state.get("rent") or {}).get("rent_paid_total", 0))
        self._require(state.get("cash") == expected,
                      "cash does not reconcile with every trade and the rent",
                      {"cash": state.get("cash"), "expected": expected})

        await self.tools.write("end_day")
        slept = await self.tools.write("sleep")
        self._require((slept.get("readback") or {}).get("day_advanced") is True,
                      "the final sleep did not advance the day", slept)
        state = await self.tools.call("get_state")
        rent = state.get("rent") or {}
        self._require(state.get("day") == FIRST_WEEK_DAYS + 2,
                      "unexpected day after the final sleep", state.get("day"))
        self._require(rent.get("rent_paid_total") == due,
                      "the paid total was lost across the day",
                      rent.get("rent_paid_total"))
        self._require(rent.get("rent_weeks_paid") == 1,
                      "the paid week was not recorded", rent.get("rent_weeks_paid"))
        expected_days = receipt.get("rent_days_left_after", FIRST_WEEK_DAYS) - 1
        self._require(rent.get("days_left") == expected_days,
                      "the rent countdown did not restart after the payment",
                      {"days_left": rent.get("days_left"),
                       "expected": expected_days})
        self.final_state = state

    # ------------------------------------------------------------- save/load

    async def _verify_save_load(self):
        state = self.final_state
        status = await self.tools.call("get_recording_save_status")
        self._require(status.get("bound") is True,
                      "the slot is not bound before saving", status)
        self.binding_before_save = dict(status.get("binding") or {})
        self._require(self.binding_before_save.get("slot") == self.slot,
                      "the binding slot changed before saving",
                      self.binding_before_save)

        saved = await self.tools.write("save_recording_game")
        self._require(saved.get("saved") is True,
                      "save_recording_game did not report a save", saved)
        self._require((saved.get("binding") or {}).get("slot") == self.slot,
                      "the save reported another slot", saved.get("binding"))
        self.last_save_time = saved.get("last_save_time")
        self._require(self.last_save_time is not None,
                      "the save reported no save time", saved)
        self._load_expected = {
            "day": state.get("day"),
            "cash": state.get("cash"),
            "rent_paid_total": (state.get("rent") or {}).get("rent_paid_total"),
            "rent_days_left": (state.get("rent") or {}).get("days_left"),
        }

        if self.host is None:
            raise DemoAbort("host_step_unavailable", "return_to_menu")
        self.host.return_to_menu()

        menu_state = await self.tools.call("get_state")
        self._require(menu_state.get("in_game") is False,
                      "the host step did not reach the main menu",
                      menu_state.get("in_game"))

        loaded = await self.tools.write("load_recording_game")
        self._require(loaded.get("load_requested") is True,
                      "load_recording_game did not request a load", loaded)
        state = await self.tools.call("get_state")
        self._require(state.get("in_game") is True,
                      "the run was not loaded", state.get("in_game"))
        self._require(state.get("day") == self._load_expected["day"],
                      "the loaded day differs",
                      {"loaded": state.get("day"), "saved": self._load_expected})
        self._require(state.get("cash") == self._load_expected["cash"],
                      "the loaded cash differs",
                      {"loaded": state.get("cash"), "saved": self._load_expected})
        rent = state.get("rent") or {}
        self._require(rent.get("rent_paid_total") == self._load_expected["rent_paid_total"],
                      "the loaded paid total differs",
                      {"loaded": rent.get("rent_paid_total"),
                       "saved": self._load_expected})
        self._require(rent.get("days_left") == self._load_expected["rent_days_left"],
                      "the loaded rent countdown differs",
                      {"loaded": rent.get("days_left"), "saved": self._load_expected})
        self._require(rent.get("last_payment") is None,
                      "the live receipt tracker must reset on a controlled load",
                      rent.get("last_payment"))
        self.loaded_state = state

        status = await self.tools.call("get_recording_save_status")
        self.binding_after_load = dict(status.get("binding") or {})
        self._require(self.binding_after_load == self.binding_before_save,
                      "the bound slot identity changed across save/load",
                      {"before": self.binding_before_save,
                       "after": self.binding_after_load})
        self._require(status.get("last_save_time") == self.last_save_time,
                      "the save time changed across the load",
                      {"before": self.last_save_time,
                       "after": status.get("last_save_time")})

    # ----------------------------------------------------------------- report

    def _report(self):
        state = self.final_state or {}
        loaded = self.loaded_state or {}
        rent = state.get("rent") or {}
        loaded_rent = loaded.get("rent") or {}
        return {
            "policy": "scripted",
            "note": ("deterministic scripted policy for the offline fake game; "
                     "not an LLM and not evidence of real-game play"),
            "stop_reason": "completed",
            "steps": self.tools.steps,
            "summary": {
                "start_cash": self.start_cash,
                "cash": state.get("cash"),
                "day": state.get("day"),
                "rent_due": self.rent_due,
                "rent_days_left": rent.get("days_left"),
                "rent_paid_total": rent.get("rent_paid_total"),
                "rent_weeks_paid": rent.get("rent_weeks_paid"),
                "rent_receipt": self.receipt,
                "buy_total": self.buy_total,
                "sell_total": self.sell_total,
                "days_traded": self.days_traded,
                "bound_slot": self.slot,
                "start_scenario_id": (self.binding or {}).get("start_scenario_id"),
                "scenario_display_name": (self.scenario or {}).get("display_name"),
                "start_type": (self.binding or {}).get("start_type"),
                "binding_before_save": self.binding_before_save,
                "binding_after_load": self.binding_after_load,
                "last_save_time": self.last_save_time,
                "day_after_load": loaded.get("day"),
                "cash_after_load": loaded.get("cash"),
                "rent_paid_total_after_load": loaded_rent.get("rent_paid_total"),
                "rent_last_payment_after_load": loaded_rent.get("last_payment"),
            },
        }


def build_params(config_path):
    args = [MCP_SERVER]
    if config_path:
        args += ["--config", config_path]
    return StdioServerParameters(command=sys.executable, args=args)


async def run_cli(config_path=None, host=None):
    async with stdio_client(build_params(config_path)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            agent = ScriptedShopkeeper(session, host=host)
            return await agent.run()


def main():
    parser = argparse.ArgumentParser(
        description="Scripted PawnMind demo policy (not an LLM).")
    parser.add_argument("--config", default=None, help="path to bridge.conf")
    parser.add_argument("--json-out", default=None,
                        help="write the redacted trace/report to this file")
    args = parser.parse_args()

    try:
        report = asyncio.run(run_cli(args.config, host=ConsoleHost()))
    except DemoAbort as error:
        print("demo aborted: %s" % (error,), file=sys.stderr)
        return 2

    text = json.dumps(report, indent=2, ensure_ascii=False)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            handle.write(text)
        print("report written to " + args.json_out, file=sys.stderr)
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
