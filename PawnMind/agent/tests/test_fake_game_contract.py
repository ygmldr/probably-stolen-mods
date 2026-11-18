"""S6B contract tests for the offline fake game bridge.

The fake game is a **test double**: it mirrors the subset of the C# bridge
semantics the S6B demo needs (Bearer auth, rev checks, action_id idempotency,
new-game menu phases, recording-slot states, first-week trades, rent receipt,
end_day/sleep readback). It never touches the real game, the real bridge, a
save file or the network beyond 127.0.0.1.

Everything here goes through the same stdlib HTTP client the MCP server uses,
so the fake is exercised exactly like the production transport.
"""

import copy
import os
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

from fake_game import FakeGame  # noqa: E402  (Red: the fake does not exist yet)
import fake_game as fake_game_module  # noqa: E402
from pawnmind_client import BridgeClient, BridgeError  # noqa: E402

TOKEN_KEYS = {"ok", "op", "recording_mode", "configured_slot", "bound", "binding",
              "slot_matches", "run_matches", "last_save_time", "error", "rev",
              "next_free_slot", "next_free_known", "configured_slot_occupied"}


class _Driver:
    """Thin rev-aware wrapper over BridgeClient (one call = one action)."""

    def __init__(self, client):
        self.client = client
        self.rev = -1

    def state(self):
        state = self.client.get_state()
        self.rev = state["rev"]
        return state

    def call(self, op, **args):
        payload = self.client.action(op, **args)
        if isinstance(payload.get("rev"), int):
            self.rev = payload["rev"]
        return payload

    def write(self, op, **args):
        return self.call(op, rev=self.rev, **args)


class FakeGameTestCase(unittest.TestCase):
    recording_mode = "armed"
    recording_slot = 17
    next_free_slot = None

    def setUp(self):
        self.game = FakeGame(
            recording_mode=self.recording_mode,
            recording_slot=self.recording_slot,
            next_free_slot=(self.recording_slot if self.next_free_slot is None
                            else self.next_free_slot),
        )
        self.game.start()
        self.addCleanup(self.game.stop)
        self.config_path = self._write_config(self.game.token)
        self.client = BridgeClient(self.config_path)
        self.driver = _Driver(self.client)

    def _write_config(self, token, port=None):
        port = self.game.port if port is None else port
        handle, path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (port, token))
        self.addCleanup(lambda: os.path.exists(path) and os.unlink(path))
        return path

    def driver_for(self, game):
        """Start a second fake game (other recording settings) plus its client."""
        game.start()
        self.addCleanup(game.stop)
        return _Driver(BridgeClient(self._write_config(game.token, game.port)))

    def raw(self, payload):
        return self.client.request("POST", "/action", payload)

    def state(self):
        """Authoritative fake state, read directly (no HTTP)."""
        return self.game.state()

    def enter_game(self, scenario="scavenger", perk="saving", difficulty="apprentice"):
        self.driver.state()
        self.assert_menu_ok(self.driver.write("open_new_game_menu"))
        self.assert_menu_ok(self.driver.write("select_new_game_option",
                                              kind="scenario", id=scenario))
        self.assert_menu_ok(self.driver.write("new_game_continue"))
        self.assert_menu_ok(self.driver.write("select_new_game_option",
                                              kind="perk", id=perk))
        self.assert_menu_ok(self.driver.write("new_game_continue"))
        self.assert_menu_ok(self.driver.write("select_new_game_option",
                                              kind="difficulty", id=difficulty))
        started = self.driver.write("confirm_new_game")
        self.assertTrue(started.get("ok"), started)
        return started

    def assert_menu_ok(self, payload):
        self.assertTrue(payload.get("ok"), payload)
        self.assertIn("view", payload)

    def sleep_once(self):
        ended = self.driver.write("end_day")
        self.assertTrue(ended.get("ok"), ended)
        slept = self.driver.write("sleep")
        self.assertTrue(slept.get("ok"), slept)
        return slept

    def advance_to_landlord(self, trade=True):
        """Seven days of sleep bring rent_days_left to 0 (landlord arrives)."""
        for _ in range(7):
            if trade:
                self.earn(rounds=1)
            self.sleep_once()
        state = self.driver.state()
        self.assertEqual(8, state["day"])
        self.assertEqual(0, state["rent_days_left"])
        return state

    def earn(self, rounds=1):
        """Buy the cheapest offer and sell the item at its visible value."""
        for _ in range(rounds):
            goods = self.driver.call("list_goods", scope="all")
            self.assertTrue(goods.get("ok"), goods)
            cheapest = min(goods["items"], key=lambda row: row["value"])
            bought = self.driver.write("confirm_trade", item_id=cheapest["id"], side="buy")
            self.assertTrue(bought.get("ok"), bought)
            inventory = self.driver.call("list_inventory", container="main")
            self.assertTrue(inventory.get("ok"), inventory)
            item = inventory["items"][0]
            sold = self.driver.write("confirm_trade", item_id=item["id"], side="sell")
            self.assertTrue(sold.get("ok"), sold)


class TransportContractTests(FakeGameTestCase):
    def test_fake_only_listens_on_loopback_and_health_is_public(self):
        self.assertEqual("127.0.0.1", self.game.host)
        self.assertTrue(self.game.url.startswith("http://127.0.0.1:"))
        health = self.client.health()
        self.assertTrue(health["ok"])
        self.assertTrue(health["fake"], "the fake must label itself as a test double")
        self.assertIn("test double", health["note"].lower())
        self.assertIn("not the real game", health["note"].lower())

    def test_state_and_actions_require_the_bearer_token(self):
        bad = BridgeClient(self._write_config("f" * 32))
        with self.assertRaises(BridgeError) as caught:
            bad.get_state()
        self.assertEqual(401, caught.exception.status)
        with self.assertRaises(BridgeError) as caught:
            bad.action("ping")
        self.assertEqual(401, caught.exception.status)
        self.assertFalse(self.state()["in_game"])
        self.assertEqual([], [row for row in self.game.requests if row["authorized"]])

    def test_writes_require_action_id_rev_and_a_current_revision(self):
        self.driver.state()
        missing_id = self.raw({"op": "open_new_game_menu", "rev": 0})
        self.assertEqual("missing_action_id", missing_id["error"])
        missing_rev = self.raw({"op": "open_new_game_menu", "action_id": "a-1"})
        self.assertEqual("missing_rev", missing_rev["error"])
        bad_rev = self.raw({"op": "open_new_game_menu", "action_id": "a-2", "rev": "0"})
        self.assertEqual("bad_rev", bad_rev["error"])
        negative = self.raw({"op": "open_new_game_menu", "action_id": "a-3", "rev": -1})
        self.assertEqual("bad_rev", negative["error"])
        stale = self.raw({"op": "open_new_game_menu", "action_id": "a-4",
                          "rev": self.driver.rev + 5})
        self.assertEqual("stale_rev", stale["error"])
        self.assertEqual(self.driver.rev, stale["rev"])
        self.assertEqual("closed", self.driver.call("get_new_game_options")["phase"])

    def test_action_id_is_idempotent_and_bound_to_its_arguments(self):
        self.driver.state()
        body = {"op": "open_new_game_menu", "action_id": "fixed-action-id",
                "rev": self.driver.rev}
        first = self.raw(body)
        self.assertTrue(first["ok"], first)
        kept_rev = first["rev"]

        replay = self.raw(body)
        self.assertEqual(first, replay, "a replay must return the recorded first result")
        self.assertEqual(kept_rev, replay["rev"], "the replay must not dispatch again")

        conflict = self.raw({"op": "new_game_next_page", "row": "available",
                             "direction": 1, "action_id": "fixed-action-id",
                             "rev": self.driver.rev})
        self.assertEqual("action_conflict", conflict["error"])

        # A fresh id proves the first call really ran once: the menu is already open.
        repeated = self.raw({"op": "open_new_game_menu", "action_id": "fresh-id",
                             "rev": kept_rev})
        self.assertEqual("menu_already_open", repeated["error"])

    def test_unknown_or_forbidden_ops_are_rejected_without_state_change(self):
        self.driver.state()
        rev = self.driver.rev
        for op, args in (
            ("press", {"control_id": "start"}),
            ("act", {"action": "open_shop", "args": {}}),
            ("new_game", {}),
            ("load_game", {"slot": 17}),
            ("save_game", {}),
        ):
            with self.subTest(op=op):
                payload = self.driver.write(op, **args)
                self.assertFalse(payload.get("ok"), payload)
                self.assertIn(payload["error"], ("unknown_op", "unsupported_op"))
                self.assertEqual(rev, payload["rev"])
        self.assertEqual(rev, self.driver.state()["rev"])


class RecordingStatusContractTests(unittest.TestCase):
    def _status(self, game):
        handle, path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (game.port, game.token))
        self.addCleanup(lambda: os.path.exists(path) and os.unlink(path))
        driver = _Driver(BridgeClient(path))
        driver.state()
        return driver.call("get_recording_save_status")

    def test_disabled_and_armed_states_are_distinguishable(self):
        off = FakeGame(recording_mode="off")
        off.start()
        self.addCleanup(off.stop)
        disabled = self._status(off)
        self.assertEqual("disabled", disabled["recording_mode"])
        self.assertIsNone(disabled["configured_slot"])
        self.assertFalse(disabled["bound"])
        self.assertEqual("recording_unconfigured", disabled["error"])

        armed = FakeGame(recording_mode="armed", recording_slot=17, next_free_slot=17)
        armed.start()
        self.addCleanup(armed.stop)
        unbound = self._status(armed)
        self.assertEqual("armed", unbound["recording_mode"])
        self.assertEqual(17, unbound["configured_slot"])
        self.assertFalse(unbound["bound"])
        self.assertEqual("recording_not_bound", unbound["error"])
        self.assertNotIn("binding", unbound)

    def test_status_never_exposes_other_slots_or_paths(self):
        status = self._status(_armed_game(self))
        self.assertLessEqual(set(status), TOKEN_KEYS,
                             "status keys: %r" % (sorted(status),))
        self.assertNotIn("binding", status, "an unbound slot has no binding yet")
        for forbidden in ("slot", "previews", "paths", "save_slot", "path", "other_slots"):
            self.assertNotIn(forbidden, status)


def _armed_game(testcase):
    game = FakeGame(recording_mode="armed", recording_slot=17, next_free_slot=17)
    game.start()
    testcase.addCleanup(game.stop)
    return game


class _BoundTestCase(FakeGameTestCase):
    def setUp(self):
        super().setUp()
        self.enter_game()
        self.driver.state()


class MenuContractTests(FakeGameTestCase):
    def test_closed_menu_reports_no_phase_and_refuses_continue(self):
        driver = self.driver
        driver.state()
        closed = driver.call("get_new_game_options")
        self.assertEqual("closed", closed["phase"])
        self.assertEqual([], closed["options"])
        self.assertEqual({}, closed["continue"])
        self.assertFalse(closed["has_selection"])
        refused = driver.write("new_game_continue")
        self.assertEqual("menu_closed", refused["error"])
        self.assertEqual("closed", driver.call("get_new_game_options")["phase"])

    def test_scenario_phase_paginates_and_rejects_hidden_locked_offpage_and_unknown(self):
        self.driver.state()
        opened = self.driver.write("open_new_game_menu")
        self.assert_menu_ok(opened)
        view = opened["view"]
        self.assertEqual("specialization", view["phase"])
        self.assertEqual(0, view["page"])
        self.assertEqual(2, view["page_count"])
        self.assertTrue(view["has_next_page"])
        self.assertFalse(view["has_previous_page"])
        self.assertEqual(4, len(view["options"]))
        for option in view["options"]:
            for key in ("id", "display_name", "enabled", "selected"):
                self.assertIn(key, option)
        self.assertEqual({"fence", "scavenger", "courier", "prospector"},
                         {option["id"] for option in view["options"]})
        self.assertNotIn("smuggler", {option["id"] for option in view["options"]},
                         "hidden candidates must never be listed")

        out_of_range = self.driver.write("new_game_next_page", row="available", direction=-1)
        self.assertEqual("page_out_of_range", out_of_range["error"])

        second = self.driver.write("new_game_next_page", row="available", direction=1)
        self.assertTrue(second["ok"], second)
        self.assertEqual(1, second["view"]["page"])
        self.assertEqual(2, len(second["view"]["options"]))
        self.assertFalse(second["view"]["has_next_page"])
        beyond = self.driver.write("new_game_next_page", row="available", direction=1)
        self.assertEqual("page_out_of_range", beyond["error"])
        back = self.driver.write("new_game_next_page", row="available", direction=-1)
        self.assertEqual(0, back["view"]["page"])

        for args, expected in (
            ({"kind": "scenario", "id": "smuggler"}, "option_hidden"),
            ({"kind": "scenario", "id": "prospector"}, "option_locked"),
            ({"kind": "scenario", "id": "medic"}, "option_not_on_page"),
            ({"kind": "scenario", "id": "does-not-exist"}, "unknown_option"),
            ({"kind": "perk", "id": "tough"}, "wrong_phase"),
            ({"kind": "difficulty", "id": "apprentice"}, "wrong_phase"),
        ):
            with self.subTest(args=args):
                payload = self.driver.write("select_new_game_option", **args)
                self.assertFalse(payload.get("ok"), payload)
                self.assertEqual(expected, payload["error"])

        locked = self.driver.write("select_new_game_option", kind="scenario", id="prospector")
        row = [option for option in locked["view"]["options"]
               if option["id"] == "prospector"][0]
        self.assertFalse(row["enabled"])
        self.assertTrue(row["lock_reason"])

        chosen = self.driver.write("select_new_game_option", kind="scenario", id="scavenger")
        self.assertTrue(chosen["ok"], chosen)
        self.assertEqual("set", chosen["action"])
        self.assertEqual("scavenger", chosen["view"]["selected_id"])
        self.assertTrue(chosen["view"]["has_selection"])
        self.assertTrue(chosen["view"]["continue"]["enabled"])
        selected_row = [option for option in chosen["view"]["options"]
                        if option["id"] == "scavenger"][0]
        self.assertTrue(selected_row["selected"])
        self.assertFalse(selected_row["enabled"])
        self.assertEqual("already_selected", selected_row["lock_reason"])

        again = self.driver.write("select_new_game_option", kind="scenario", id="scavenger")
        self.assertEqual("already_selected", again["error"])

    def test_perks_phase_pagination_points_and_the_three_step_undo(self):
        self.driver.state()
        self.driver.write("open_new_game_menu")
        self.driver.write("select_new_game_option", kind="scenario", id="scavenger")
        advanced = self.driver.write("new_game_continue")
        self.assertEqual("perks", advanced["view"]["phase"])
        perks = advanced["view"]["perks"]
        self.assertEqual(3, perks["max_points"])
        self.assertEqual(0, perks["used_points"])
        self.assertEqual(0, perks["selected_count"])
        self.assertEqual(4, len(advanced["view"]["options"]))

        added = self.driver.write("select_new_game_option", kind="perk", id="tough")
        self.assertTrue(added["ok"], added)
        self.assertEqual("add", added["action"])
        self.assertEqual(2, added["view"]["perks"]["used_points"])
        self.assertEqual(1, added["view"]["perks"]["selected_count"])
        self.assertEqual(["tough"], [row["id"] for row in added["view"]["selected_options"]])
        self.assertEqual("tough", added["view"]["hovered_id"])
        self.assertFalse(added["view"]["hovered_in_selected_row"])

        # The native click leaves the available card hovered, so a same-id click
        # cannot target the selected row yet (S3B documented hover conflict).
        shadowed = self.driver.write("select_new_game_option", kind="perk", id="tough")
        self.assertEqual("ambiguous_target", shadowed["error"])
        self.assertEqual(2, shadowed["view"]["perks"]["used_points"])

        focused_too_early = self.driver.write("focus_new_game_selected_perk", id="tough")
        self.assertEqual("hover_conflict", focused_too_early["error"])

        blurred = self.driver.write("blur_new_game_option", id="tough")
        self.assertTrue(blurred["ok"], blurred)
        self.assertIsNone(blurred["view"].get("hovered_id"))

        focused = self.driver.write("focus_new_game_selected_perk", id="tough")
        self.assertTrue(focused["ok"], focused)
        self.assertEqual("tough", focused["view"]["hovered_id"])
        self.assertTrue(focused["view"]["hovered_in_selected_row"])

        removed = self.driver.write("select_new_game_option", kind="perk", id="tough")
        self.assertTrue(removed["ok"], removed)
        self.assertEqual("remove", removed["action"])
        self.assertEqual(0, removed["view"]["perks"]["used_points"])
        self.assertEqual(0, removed["view"]["perks"]["selected_count"])
        self.assertEqual([], removed["view"]["selected_options"])

        self.driver.write("select_new_game_option", kind="perk", id="saving")
        spent = self.driver.write("select_new_game_option", kind="perk", id="lucky")
        self.assertEqual(2, spent["view"]["perks"]["used_points"])
        too_much = self.driver.write("select_new_game_option", kind="perk", id="charming")
        self.assertEqual("insufficient_points", too_much["error"])
        self.assertEqual(2, too_much["view"]["perks"]["used_points"])

        off_page = self.driver.write("select_new_game_option", kind="perk", id="handy")
        self.assertEqual("option_not_on_page", off_page["error"])
        turned = self.driver.write("new_game_next_page", row="available", direction=1)
        self.assertEqual(1, turned["view"]["page"])
        self.assertEqual({"nightowl", "handy"},
                         {option["id"] for option in turned["view"]["options"]})
        handy = self.driver.write("select_new_game_option", kind="perk", id="handy")
        self.assertTrue(handy["ok"], handy)
        self.assertEqual(3, handy["view"]["perks"]["used_points"])
        self.assertEqual(0, handy["view"]["perks"]["remaining_points"])
        self.assertEqual(3, handy["view"]["perks"]["selected_count"])
        full = self.driver.write("select_new_game_option", kind="perk", id="nightowl")
        self.assertEqual("insufficient_points", full["error"])

        unknown_focus = self.driver.write("focus_new_game_selected_perk", id="charming")
        self.assertEqual("unknown_option", unknown_focus["error"])

        difficulty = self.driver.write("new_game_continue")
        self.assertEqual("difficulty", difficulty["view"]["phase"])

    def test_selected_row_paging_is_rejected_when_the_row_is_not_paged(self):
        self.driver.state()
        self.driver.write("open_new_game_menu")
        row = self.driver.write("new_game_next_page", row="selected", direction=1)
        self.assertEqual("wrong_row", row["error"])
        self.driver.write("select_new_game_option", kind="scenario", id="scavenger")
        self.driver.write("new_game_continue")
        self.driver.write("select_new_game_option", kind="perk", id="saving")
        paged = self.driver.write("new_game_next_page", row="selected", direction=1)
        self.assertEqual("page_out_of_range", paged["error"])
        self.assertFalse(paged["view"]["selected_row_paged"])


class StartGameContractTests(FakeGameTestCase):
    def test_confirm_new_game_binds_the_slot_and_enters_the_first_day(self):
        self.driver.state()
        early = self.driver.write("confirm_new_game")
        self.assertEqual("recording_wrong_menu_phase", early["error"])

        self.driver.write("open_new_game_menu")
        self.driver.write("select_new_game_option", kind="scenario", id="scavenger")
        self.driver.write("new_game_continue")
        self.driver.write("select_new_game_option", kind="perk", id="saving")
        self.driver.write("new_game_continue")
        no_difficulty = self.driver.write("confirm_new_game")
        self.assertEqual("recording_start_unavailable", no_difficulty["error"])

        locked = self.driver.write("select_new_game_option", kind="difficulty", id="master")
        self.assertEqual("option_locked", locked["error"])
        chosen = self.driver.write("select_new_game_option", kind="difficulty", id="apprentice")
        self.assertTrue(chosen["ok"], chosen)
        forbidden = self.driver.write("new_game_continue")
        self.assertEqual("difficulty_start_forbidden", forbidden["error"])

        started = self.driver.write("confirm_new_game")
        self.assertTrue(started["ok"], started)
        self.assertTrue(started["start_requested"])
        self.assertTrue(started["bound"])
        self.assertEqual("bound", started["state"])
        self.assertEqual(17, started["binding"]["slot"])
        self.assertEqual("apprentice", started["binding"]["start_type"])
        self.assertEqual("scavenger", started["binding"]["start_scenario_id"])

        state = self.driver.state()
        self.assertTrue(state["in_game"])
        self.assertEqual(1, state["day"])
        self.assertEqual(25, state["cash"])
        self.assertEqual("OPEN", state["shop_state"])
        self.assertEqual(0, state["inventory_count"])
        rent = state["rent"]
        self.assertTrue(rent["readable"])
        self.assertEqual(100, rent["due"])
        self.assertEqual(7, rent["days_left"])
        self.assertFalse(rent["can_pay"])
        self.assertEqual(0, rent["rent_paid_total"])
        self.assertEqual(0, rent["rent_weeks_paid"])
        self.assertIsNone(rent["last_payment"])

        status = self.driver.call("get_recording_save_status")
        self.assertEqual("bound", status["recording_mode"])
        self.assertEqual(17, status["configured_slot"])
        self.assertTrue(status["bound"])
        self.assertTrue(status["slot_matches"])
        self.assertTrue(status["run_matches"])
        self.assertEqual(17, status["binding"]["slot"])

        twice = self.driver.write("confirm_new_game")
        self.assertEqual("recording_already_bound", twice["error"])
        self.assertTrue(self.game.snapshot()["in_game"])

    def test_slot_allocation_mismatch_fails_closed_without_a_save(self):
        game = FakeGame(recording_mode="armed", recording_slot=17, next_free_slot=18)
        driver = self.driver_for(game)
        driver.state()
        driver.write("open_new_game_menu")
        driver.write("select_new_game_option", kind="scenario", id="scavenger")
        driver.write("new_game_continue")
        driver.write("select_new_game_option", kind="perk", id="saving")
        driver.write("new_game_continue")
        driver.write("select_new_game_option", kind="difficulty", id="apprentice")
        refused = driver.write("confirm_new_game")
        self.assertEqual("recording_slot_not_next_free", refused["error"])
        snapshot = game.snapshot()
        self.assertFalse(snapshot["in_game"])
        self.assertIsNone(snapshot["save"])
        self.assertEqual("armed", driver.call("get_recording_save_status")["recording_mode"])


class TradeContractTests(_BoundTestCase):
    def test_trade_updates_cash_and_inventory_atomically(self):
        state = self.driver.state()
        self.assertEqual(25, state["cash"])
        goods = self.driver.call("list_goods", scope="all")
        self.assertTrue(goods["ok"])
        self.assertEqual(2, len(goods["items"]))
        cheap = min(goods["items"], key=lambda row: row["value"])
        pricey = max(goods["items"], key=lambda row: row["value"])
        self.assertEqual(12, cheap["value"])

        before = copy.deepcopy(self.state())
        refused = self.driver.write("confirm_trade", item_id=pricey["id"], side="buy")
        self.assertEqual("cannot_afford", refused["error"])
        after = self.state()
        for key in ("cash", "inventory_count", "rev", "day", "back_counter_count"):
            self.assertEqual(before[key], after[key], key + " changed on a refused buy")

        bought = self.driver.write("confirm_trade", item_id=cheap["id"], side="buy")
        self.assertTrue(bought["ok"], bought)
        state = self.driver.state()
        self.assertEqual(13, state["cash"])
        self.assertEqual(1, state["inventory_count"])
        self.assertEqual(1, state["back_counter_count"])
        item = state["inventory"][0]
        self.assertEqual(22, item["value"])
        self.assertEqual(cheap["identifier"], item["identifier"])

        listed = self.driver.call("list_inventory", container="main")
        self.assertEqual(1, len(listed["items"]))
        owned = listed["items"][0]
        self.assertTrue(owned["owned"])
        self.assertTrue(owned["sellable"])
        self.assertEqual(22, owned["value"])

        sold = self.driver.write("confirm_trade", item_id=owned["id"], side="sell")
        self.assertTrue(sold["ok"], sold)
        state = self.driver.state()
        self.assertEqual(35, state["cash"])
        self.assertEqual(0, state["inventory_count"])

        missing = self.driver.write("confirm_trade", item_id=999999, side="buy")
        self.assertEqual("item_not_found", missing["error"])
        not_owned = self.driver.write("confirm_trade", item_id=pricey["id"], side="sell")
        self.assertEqual("not_owned", not_owned["error"])
        bad_side = self.driver.write("confirm_trade", item_id=cheap["id"], side="swap")
        self.assertEqual("bad_side", bad_side["error"])
        self.assertEqual(35, self.driver.state()["cash"])

    def test_daily_offers_refresh_after_sleep(self):
        goods = self.driver.call("list_goods", scope="all")
        first_ids = {row["id"] for row in goods["items"]}
        self.sleep_once()
        goods = self.driver.call("list_goods", scope="all")
        second_ids = {row["id"] for row in goods["items"]}
        self.assertFalse(first_ids & second_ids, "a new day must bring new offers")


class FirstWeekContractTests(_BoundTestCase):
    def test_disabled_rent_choice_is_refused_and_does_not_advance(self):
        state = self.advance_to_landlord()
        rent = state["rent"]
        self.assertEqual(95, state["cash"])
        self.assertEqual(0, rent["days_left"])
        self.assertFalse(rent["can_pay"])
        dialogue = state["dialogue"]
        self.assertTrue(dialogue["available"])
        self.assertTrue(dialogue["choice"])
        self.assertFalse(dialogue["choices"][0]["enabled"])
        self.assertIn("100", dialogue["choices"][0]["text"])

        before = copy.deepcopy(state)
        refused = self.driver.write("choose_dialogue", index=0)
        self.assertEqual("choice_disabled", refused["error"])
        self.assertFalse(refused["ok"])
        after = self.driver.state()
        self.assertEqual(before["rev"], after["rev"], "a refused choice must not advance")
        self.assertEqual(before["cash"], after["cash"])
        self.assertEqual(before["dialogue"], after["dialogue"])
        self.assertEqual(before["rent"], after["rent"])

        bad_index = self.driver.write("choose_dialogue", index=9)
        self.assertEqual("bad_index", bad_index["error"])

    def test_rent_payment_records_one_receipt_and_never_charges_twice(self):
        self.advance_to_landlord()
        self.earn(rounds=1)
        state = self.driver.state()
        self.assertEqual(105, state["cash"])
        self.assertTrue(state["rent"]["can_pay"])
        self.assertTrue(state["dialogue"]["choices"][0]["enabled"])

        paid = self.driver.write("choose_dialogue", index=0)
        self.assertTrue(paid["ok"], paid)
        self.assertTrue(paid["dispatched"])
        self.assertTrue(paid["dialogue_changed"])
        self.assertEqual(0, paid["index"])
        self.assertIn("100", paid["choice_text"])
        receipt = paid["last_rent_payment"]
        self.assertIsNone(paid["receipt_error"])
        self.assertEqual(100, receipt["amount"])
        self.assertEqual(105, receipt["cash_before"])
        self.assertEqual(5, receipt["cash_after"])
        self.assertEqual(-100, receipt["cash_delta"])
        self.assertEqual(0, receipt["rent_days_left_before"])
        self.assertEqual(7, receipt["rent_days_left_after"])
        self.assertEqual("dialogue_choice", receipt["source"])
        self.assertEqual(1, receipt["weeks_paid_delta"])
        self.assertEqual(1, receipt["rent_weeks_paid"])
        self.assertEqual(0, receipt["rent_weeks_missed"])
        self.assertTrue(receipt["detection_complete"])

        state = self.driver.state()
        self.assertEqual(5, state["cash"])
        self.assertEqual(100, state["rent"]["rent_paid_total"])
        self.assertEqual(1, state["rent"]["rent_weeks_paid"])
        self.assertEqual(7, state["rent"]["days_left"])
        self.assertEqual(receipt, state["rent"]["last_payment"])
        self.assertFalse(state["dialogue"]["available"])
        self.assertTrue(state["dialogue"]["ended"])
        self.assertIsNone(state["client"])

        again = copy.deepcopy(self.driver.state())
        self.assertEqual(again["rent"], state["rent"], "the same payment must not be counted twice")
        self.assertEqual(again["cash"], state["cash"])

        no_dialogue = self.driver.write("choose_dialogue", index=0)
        self.assertEqual("no_dialogue", no_dialogue["error"])
        self.assertEqual(5, self.driver.state()["cash"])
        self.assertEqual(100, self.driver.state()["rent"]["rent_paid_total"])

    def test_end_day_and_sleep_readback_reports_day_and_rent_changes(self):
        self.driver.state()
        ended = self.driver.write("end_day")
        self.assertTrue(ended["ok"], ended)
        self.assertFalse(ended["readback"]["day_advanced"])
        self.assertFalse(ended["can_end_day_after"])
        self.assertEqual("CLOSED", self.driver.state()["shop_state"])
        twice = self.driver.write("end_day")
        self.assertEqual("cannot_end_day", twice["error"])

        slept = self.driver.write("sleep")
        self.assertTrue(slept["ok"], slept)
        self.assertFalse(slept["saved"])
        self.assertTrue(slept["slept"])
        readback = slept["readback"]
        self.assertTrue(readback["day_advanced"])
        self.assertTrue(readback["changed"])
        self.assertEqual(1, readback["day_before"])
        self.assertEqual(2, readback["day_after"])
        self.assertEqual(7, readback["rent_days_left_before"])
        self.assertEqual(6, readback["rent_days_left_after"])
        self.assertEqual(25, readback["cash_before"])
        self.assertEqual(25, readback["cash_after"])
        state = self.driver.state()
        self.assertEqual(2, state["day"])
        self.assertEqual(6, state["rent_days_left"])
        self.assertEqual("OPEN", state["shop_state"])
        self.assertIsNotNone(slept["last_save_time"])

    def test_sleep_before_binding_is_refused(self):
        game = FakeGame(recording_mode="armed", recording_slot=17, next_free_slot=17)
        driver = self.driver_for(game)
        driver.state()
        refused = driver.write("sleep")
        self.assertEqual("recording_not_bound", refused["error"])
        self.assertFalse(game.snapshot()["in_game"])


class SaveLoadContractTests(_BoundTestCase):
    def test_save_and_load_round_trip_keeps_the_bound_slot(self):
        self.sleep_once()
        state = self.driver.state()
        self.assertEqual(2, state["day"])

        saved = self.driver.write("save_recording_game")
        self.assertTrue(saved["ok"], saved)
        self.assertTrue(saved["saved"])
        self.assertEqual("bound", saved["state"])
        self.assertEqual(17, saved["binding"]["slot"])
        save_time = saved["last_save_time"]
        self.assertIsNotNone(save_time)

        in_game_load = self.driver.write("load_recording_game")
        self.assertEqual("recording_already_in_game", in_game_load["error"])

        status = self.driver.call("get_recording_save_status")
        binding = copy.deepcopy(status["binding"])
        self.assertEqual(save_time, status["last_save_time"])

        self.game.return_to_menu()
        state = self.driver.state()
        self.assertFalse(state["in_game"])
        self.assertIsNone(state["rent"])

        at_menu = self.driver.write("save_recording_game")
        self.assertEqual("recording_not_in_game", at_menu["error"])

        loaded = self.driver.write("load_recording_game")
        self.assertTrue(loaded["ok"], loaded)
        self.assertTrue(loaded["load_requested"])
        self.assertEqual("bound", loaded["state"])
        state = self.driver.state()
        self.assertTrue(state["in_game"])
        self.assertEqual(2, state["day"])
        self.assertEqual(25, state["cash"])
        self.assertEqual(6, state["rent"]["days_left"])
        self.assertIsNone(state["rent"]["last_payment"],
                          "the receipt tracker resets on a controlled load")

        after = self.driver.call("get_recording_save_status")
        self.assertEqual(binding, after["binding"])
        self.assertEqual(save_time, after["last_save_time"])

    def test_save_before_binding_is_refused(self):
        game = FakeGame(recording_mode="armed", recording_slot=17, next_free_slot=17)
        driver = self.driver_for(game)
        driver.state()
        refused = driver.write("save_recording_game")
        self.assertEqual("recording_not_bound", refused["error"])
        self.assertFalse(game.snapshot()["in_game"])


class R5SurfaceContractTests(_BoundTestCase):
    """R5-C4: the fake exposes the same tool surface the bridge does.

    The deterministic scenarios in ``test_fake_scenarios.py`` assert the field
    semantics; this class pins the fake's surface itself so a bridge contract
    change cannot silently disappear from the L1 double.
    """

    def test_r5_tools_are_on_the_fake_surface(self):
        self.assertIn("stage_negotiation_item", fake_game_module.WRITE_OPS)
        self.assertIn("answer_prompt", fake_game_module.WRITE_OPS)
        self.assertIn("get_pending_prompt", fake_game_module.READ_ONLY_OPS)
        self.assertIn("confirm_trade", fake_game_module.CALL_SCOPED_OPS)
        self.assertIn("stage_negotiation_item",
                      fake_game_module.CALL_SCOPED_OPS)
        self.assertIn("get_operation",
                      dir(self.client))
        # The R5 sleep model is opt-in; the default stays the S6B sync model.
        self.assertFalse(self.game.async_sleep)

    def test_write_responses_carry_the_two_axis_operation_envelope(self):
        ended = self.driver.write("end_day")
        self.assertTrue(ended["ok"], ended)
        self.assertEqual("completed", ended["execution_status"])
        self.assertEqual("delivered", ended["delivery_status"])
        self.assertEqual("dispatched", ended["dispatch_status"])
        self.assertTrue(ended["dispatched"])
        operation_id = ended["operation_id"]
        self.assertTrue(operation_id)

        found = self.client.get_operation(operation_id)
        self.assertTrue(found["known"], found)
        self.assertTrue(found["ok"], found)
        self.assertEqual("end_day", found["op"])
        self.assertEqual("completed", found["execution_status"])

        unknown = self.client.get_operation("no-such-operation")
        self.assertFalse(unknown["known"])
        self.assertEqual("unknown_operation", unknown["error"])

        listing = self.client.get_operation("*")
        self.assertTrue(listing["ok"], listing)
        for key in ("operations", "count", "total_non_terminal", "truncated"):
            self.assertIn(key, listing)
        self.assertEqual(0, listing["count"], listing)
        self.assertFalse(listing["truncated"])


if __name__ == "__main__":
    unittest.main()
