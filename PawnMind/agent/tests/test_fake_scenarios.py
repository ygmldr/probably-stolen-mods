"""R5-C4: six deterministic **L1-fake** scenarios for the R5 contract.

Each scenario is a regression against ``agent/fake_game.py``, which speaks the
same HTTP shape as the in-game bridge (Bearer auth, ``rev`` discipline,
``action_id`` idempotency, operation envelope, ``/operation`` query). The fake is
a test double: it never loads Unity, the game, a save or the network beyond
127.0.0.1, and every number it moves is canned test data. The scenarios below
assert the *contract field names and fail-closed semantics* the committed C#
bridge implements:

1. normal multi-trade  - two customers, stage A then B on customer 1, two
   ``confirm_trade`` completed with per-trade evidence (items_transferred OR
   cash_moved, client_unchanged), then a third trade for customer 2.
2. cannot afford / silent refusal - the client budget is below the negotiation
   price, so the bridge refuses ``client_budget_insufficient`` *before* the
   native accept click (``dispatched: false``, no ok:true, no cash/item move).
3. different-item staging - ``stage_negotiation_item`` A -> B -> A with
   before/after pointers; an uncertain staging blocks a same-item retry with a
   new action id (``operation_pending``) but a different item stays allowed.
4. day-end modal - ``get_pending_prompt`` reports the typed
   ``negotiation_confirm`` / ``negotiation_tablet_confirm`` panel; answering
   confirm closes it (``prompt_closed`` + ``state.shop_state``); answering when
   closed is ``no_prompt`` with zero native clicks.
5. async sleep - ``sleep`` is running/pending, immediate ``get_operation`` reads
   are inconclusive (never ``failed``/``day_not_changed``), a later observed day
   advance promotes the operation to ``completed``; the observation-window path
   becomes ``sleep_unconfirmed`` (non-terminal) and blocks a new action id;
   ``get_operation("*")`` exposes its bounded-list count fields.
6. first-week rent - paying the landlord through the dialogue choice produces a
   receipt (id/amount/cash_delta/weeks_paid_delta/paid_at_rev), cash decreases
   exactly by the rent, and a later trade's cash change is not attributed to the
   rent receipt.

Everything is deterministic: host-side hooks inject the game world (customers,
stock, the native day-end modal, the async day transition) and a simulated
clock; no scenario waits on the wall clock.

Label: ``L1-fake``. Real-machine behaviour is L3 and NOT_RUN.
"""

import copy
import json
import os
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if AGENT_DIR not in sys.path:
    sys.path.insert(0, AGENT_DIR)

from fake_game import FakeGame  # noqa: E402
from pawnmind_client import BridgeClient  # noqa: E402


class _Driver:
    """Thin rev-aware wrapper over BridgeClient (one call = one action)."""

    def __init__(self, client):
        self.client = client
        self.rev = -1

    def state(self):
        state = self.client.get_state()
        self.rev = state["rev"]
        return state

    def write(self, op, action_id=None, **args):
        if action_id is not None:
            args["action_id"] = action_id
        payload = self.client.action(op, rev=self.rev, **args)
        if isinstance(payload.get("rev"), int):
            self.rev = payload["rev"]
        return payload


class ScenarioCase(unittest.TestCase):
    """Common L1-fake plumbing: fake bridge + client + menu-driven first day."""

    recording_mode = "armed"
    recording_slot = 17
    start_cash = 25
    async_sleep = False

    def setUp(self):
        options = {
            "recording_mode": self.recording_mode,
            "recording_slot": self.recording_slot,
            "next_free_slot": self.recording_slot,
            "start_cash": self.start_cash,
        }
        if self.async_sleep:
            # Opt-in: only scenario 5 needs the asynchronous sleep model; the
            # legacy tests keep the synchronous fake behaviour.
            options["async_sleep"] = True
        self.game = FakeGame(**options)
        self.game.start()
        self.addCleanup(self.game.stop)
        handle, self.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (self.game.port, self.game.token))
        self.addCleanup(lambda: os.path.exists(self.config_path)
                        and os.unlink(self.config_path))
        self.client = BridgeClient(self.config_path)
        self.driver = _Driver(self.client)

    # ---------------------------------------------------------------- helpers

    def state(self):
        """Read /state through the client and refresh the tracked revision."""
        return self.driver.state()

    def enter_game(self):
        """Drive the new-game menu into the first day (the shop opens)."""
        self.state()
        steps = (
            ("open_new_game_menu", {}),
            ("select_new_game_option", {"kind": "scenario", "id": "scavenger"}),
            ("new_game_continue", {}),
            ("select_new_game_option", {"kind": "perk", "id": "saving"}),
            ("new_game_continue", {}),
            ("select_new_game_option", {"kind": "difficulty",
                                        "id": "apprentice"}),
        )
        for index, (op, args) in enumerate(steps):
            payload = self.driver.write(op, action_id="enter-%02d" % index, **args)
            self.assertTrue(payload.get("ok"), payload)
        started = self.driver.write("confirm_new_game", action_id="enter-start")
        self.assertTrue(started.get("ok"), started)
        assert started["state"] == "bound"
        self.state()
        return started

    def stock(self, name, value, container="main"):
        """Host-only fixture step: the player already owns this shop item."""
        item = self.game.stock_item(name=name, value=value, container=container)
        self.state()
        return item

    def arrive(self, name, intent="BUY", budget=500, purchases=1, items=None):
        """Host-only fixture step: a customer walks up to the counter."""
        self.game.arrive_customer(name=name, intent=intent, budget=budget,
                                  purchases=purchases, items=items)
        self.state()

    def write(self, op, action_id=None, **args):
        return self.driver.write(op, action_id=action_id, **args)

    def call(self, op, **args):
        return self.client.action(op, **args)

    def snapshot(self):
        return self.game.snapshot()

    def native_clicks(self, op):
        return self.snapshot()["native_clicks"].get(op, 0)

    def assert_refusal(self, payload, error):
        self.assertFalse(payload.get("ok"), payload)
        self.assertEqual(error, payload.get("error"), payload)
        self.assertFalse(payload.get("dispatched"), payload)
        self.assertEqual("rejected", payload.get("execution_status"), payload)

    def assert_operation_envelope(self, payload, action_id):
        self.assertEqual(action_id, payload.get("operation_id"), payload)
        self.assertEqual("delivered", payload.get("delivery_status"), payload)
        self.assertIn(payload.get("dispatch_status"),
                      ("dispatched", "not_dispatched", "unknown"))

    def operation(self, operation_id):
        return self.client.get_operation(operation_id)


# ---------------------------------------------------------------------------
# Scenario 1: normal multi-trade
# ---------------------------------------------------------------------------

class NormalMultiTradeScenarioTests(ScenarioCase):
    """L1-fake #1: two customers, per-trade evidence, readable cash/inventory."""

    def test_two_trades_for_one_customer_and_one_for_the_next(self):
        self.enter_game()
        first, second, third = (
            self.stock("Scrap Core", 22),
            self.stock("Cargo Crate", 110),
            self.stock("Widget", 35),
        )
        self.arrive("Ada", intent="BUY", budget=300, purchases=2)

        state = self.state()
        self.assertEqual(25, state["cash"])
        self.assertEqual(3, state["inventory_count"])
        self.assertTrue(state["negotiation_open"])
        self.assertEqual("BUY", state["client"]["intent"])
        self.assertEqual(300, state["client"]["budget"])
        self.assertTrue(state["client"]["uses_budget"])

        # --- stage item A ---------------------------------------------------
        staged = self.write("stage_negotiation_item", "scn1-stage-a",
                            item_id=first["id"])
        self.assertTrue(staged["ok"], staged)
        self.assert_operation_envelope(staged, "scn1-stage-a")
        self.assertEqual("stage_negotiation_item", staged["op"])
        self.assertTrue(staged["dispatched"])
        self.assertEqual("completed", staged["execution_status"])
        self.assertEqual(first["id"], staged["item_id"])
        self.assertEqual(0, staged["staged_item_id_before"],
                         "no staged item reads as the native id 0")
        self.assertEqual(first["id"], staged["staged_item_id_after"])
        self.assertTrue(staged["negotiation_open"])
        self.assertTrue(staged["client_identity_unchanged"])
        self.assertTrue(staged["item_on_weighted_counter"])
        self.assertEqual("BUY", staged["client_intent"])
        self.assertEqual("weighted_area_add", staged["native_primitive"])
        evidence = staged["evidence"]
        self.assertTrue(evidence["called"])
        self.assertIsNone(evidence["error"])
        self.assertTrue(evidence["staged_item_id_after_readable"])
        self.assertEqual(first["id"], evidence["staged_item_id_after"])
        self.assertTrue(evidence["item_on_weighted_counter"])
        self.assertTrue(evidence["negotiation_readable_after"])
        self.assertTrue(evidence["negotiation_open_after"])
        self.assertTrue(evidence["client_identity_unchanged"])
        self.assertEqual("weighted_area_add", evidence["native_primitive"])

        state = self.state()
        self.assertEqual("Scrap Core", state["negotiated_item"]["name"])
        self.assertTrue(state["negotiation_open"])

        # --- trade 1: the client buys item A --------------------------------
        trade = self.write("confirm_trade", "scn1-trade-a",
                           item_id=first["id"], side="sell")
        self.assertTrue(trade["ok"], trade)
        self.assert_operation_envelope(trade, "scn1-trade-a")
        self.assertEqual("confirm_trade", trade["op"])
        self.assertTrue(trade["dispatched"])
        self.assertEqual("completed", trade["execution_status"])
        self.assertEqual(first["id"], trade["item_id"])
        self.assertEqual("sell", trade["side"])
        self.assertEqual("BUY", trade["client_intent"])
        self.assertFalse(trade["business_complete"],
                         "a multi-item visit stays open after the first sale")
        self.assertFalse(trade["visit_complete"])
        self.assertTrue(trade["client_unchanged"])
        self.assertTrue(trade["items_transferred"])
        self.assertTrue(trade["cash_moved"])
        self.assertTrue(trade["cash_changed"])
        self.assertEqual(25, trade["cash_before"])
        self.assertEqual(47, trade["cash_after"])
        self.assertEqual(first["id"], trade["staged_item_id"])
        trade_evidence = trade["evidence"]
        self.assertTrue(trade_evidence["called"])
        self.assertIsNone(trade_evidence["error"])
        self.assertFalse(trade_evidence["business_complete"])
        self.assertTrue(trade_evidence["client_unchanged"])
        self.assertTrue(trade_evidence["items_transferred"])
        self.assertTrue(trade_evidence["cash_moved"])
        self.assertEqual(25, trade_evidence["cash_before"])
        self.assertEqual(47, trade_evidence["cash_after"])
        self.assertEqual(0, trade_evidence["staged_item_id_after"],
                         "the sold item left the negotiation table")

        state = self.state()
        self.assertEqual(47, state["cash"])
        self.assertEqual(2, state["inventory_count"])
        self.assertIsNone(state["negotiated_item"])
        self.assertEqual(278, state["client"]["budget"])

        # --- stage item B and trade it too ----------------------------------
        staged_b = self.write("stage_negotiation_item", "scn1-stage-b",
                              item_id=second["id"])
        self.assertTrue(staged_b["ok"], staged_b)
        self.assertEqual(0, staged_b["staged_item_id_before"])
        self.assertEqual(second["id"], staged_b["staged_item_id_after"])

        trade_b = self.write("confirm_trade", "scn1-trade-b",
                             item_id=second["id"], side="sell")
        self.assertTrue(trade_b["ok"], trade_b)
        self.assertEqual(110, trade_b["cash_after"] - trade_b["cash_before"])
        self.assertTrue(trade_b["business_complete"],
                        "the second sale finishes this visit")
        self.assertTrue(trade_b["visit_complete"])
        self.assertTrue(trade_b["items_transferred"])
        self.assertTrue(trade_b["cash_moved"])

        state = self.state()
        self.assertEqual(157, state["cash"])
        self.assertEqual(1, state["inventory_count"])

        # --- second customer: one more completed trade ----------------------
        self.arrive("Bo", intent="BUY", budget=100, purchases=1)
        staged_c = self.write("stage_negotiation_item", "scn1-stage-c",
                              item_id=third["id"])
        self.assertTrue(staged_c["ok"], staged_c)
        trade_c = self.write("confirm_trade", "scn1-trade-c",
                             item_id=third["id"], side="sell")
        self.assertTrue(trade_c["ok"], trade_c)
        self.assertEqual(35, trade_c["cash_after"] - trade_c["cash_before"])
        self.assertTrue(trade_c["business_complete"])

        state = self.state()
        self.assertEqual(192, state["cash"])
        self.assertEqual(0, state["inventory_count"])
        self.assertEqual(0, self.call("list_inventory",
                                      container="main")["total"])
        self.assertIsNone(state["negotiated_item"])

        reported = self.operation("scn1-trade-a")
        self.assertTrue(reported["known"], reported)
        self.assertTrue(reported["ok"], reported)
        self.assertEqual("confirm_trade", reported["op"])
        self.assertEqual("completed", reported["execution_status"])
        self.assertEqual(3, self.native_clicks("confirm_trade"))


# ---------------------------------------------------------------------------
# Scenario 2: cannot afford / silent native refusal
# ---------------------------------------------------------------------------

class ClientCannotAffordScenarioTests(ScenarioCase):
    """L1-fake #2: budget below the price => pre-dispatch refusal, no movement."""

    def test_client_budget_insufficient_is_refused_before_the_native_click(self):
        self.enter_game()
        item = self.stock("Scrap Core", 22)
        self.arrive("Broke", intent="BUY", budget=10, purchases=1)

        staged = self.write("stage_negotiation_item", "scn2-stage",
                            item_id=item["id"])
        self.assertTrue(staged["ok"], staged)
        state = self.state()
        before = copy.deepcopy(state)
        clicks_before = self.native_clicks("confirm_trade")

        refused = self.write("confirm_trade", "scn2-trade",
                             item_id=item["id"], side="sell")
        self.assert_refusal(refused, "client_budget_insufficient")
        self.assertEqual(item["id"], refused["item_id"])
        self.assertEqual("sell", refused["side"])
        for absent in ("items_transferred", "cash_moved", "cash_before",
                       "cash_after", "business_complete", "evidence"):
            self.assertNotIn(absent, refused,
                             "a pre-dispatch refusal must not fabricate "
                             "completion evidence: %s" % absent)
        self.assertEqual(clicks_before, self.native_clicks("confirm_trade"),
                         "the native accept click was never dispatched")
        after = self.state()
        self.assertEqual(before["cash"], after["cash"])
        self.assertEqual(before["inventory_count"], after["inventory_count"])
        self.assertEqual(before["client"]["budget"], after["client"]["budget"])
        self.assertEqual(item["id"],
                         after["negotiated_item"] and item["id"],
                         "the staged item stays on the table after a refusal")
        self.assertEqual(item["id"], self.snapshot()["staged_item_id"])

        operation = self.operation("scn2-trade")
        self.assertTrue(operation["known"], operation)
        self.assertFalse(operation["ok"], operation)
        self.assertEqual("rejected", operation["execution_status"])
        self.assertEqual("client_budget_insufficient", operation["error"])

    def test_silent_native_refusal_is_uncertain_never_a_completed_trade(self):
        self.enter_game()
        item = self.stock("Scrap Core", 22)
        self.arrive("Ada", intent="BUY", budget=100, purchases=1)
        staged = self.write("stage_negotiation_item", "scn2-stage-silent",
                            item_id=item["id"])
        self.assertTrue(staged["ok"], staged)
        self.state()
        before = self.snapshot()

        # Host-side fault injection: the native accept click is silently ignored
        # (the R5 bug shape). The bridge must report uncertain, not completed.
        self.game.inject_native_fault("confirm_trade",
                                      detail="native_not_called")
        silent = self.write("confirm_trade", "scn2-trade-silent",
                            item_id=item["id"], side="sell")
        self.assertFalse(silent["ok"], silent)
        self.assertTrue(silent["unknown"])
        self.assertTrue(silent["dispatched"])
        self.assertEqual("uncertain", silent["execution_status"])
        self.assertEqual("outcome_unverified", silent["error"])
        self.assertEqual("native_not_called", silent["detail"])
        self.assertEqual("BUY", silent["client_intent"])
        for absent in ("items_transferred", "cash_moved", "cash_before",
                       "cash_after", "business_complete"):
            self.assertNotIn(absent, silent,
                             "no completion evidence may be invented: %s" % absent)

        after = self.snapshot()
        self.assertEqual(before["cash"], after["cash"])
        self.assertEqual(before["inventory_ids"], after["inventory_ids"])
        self.assertEqual(before["client"]["budget"], after["client"]["budget"])
        self.assertEqual(0, self.native_clicks("confirm_trade"),
                         "the injected silent native call did not click")
        self.assertEqual(item["id"], after["staged_item_id"],
                         "the staged item stays staged after an uncertain trade")

        operation = self.operation("scn2-trade-silent")
        self.assertTrue(operation["known"], operation)
        self.assertFalse(operation["ok"], operation)
        self.assertEqual("uncertain", operation["execution_status"])
        self.assertEqual("outcome_unverified", operation["error"])

        retry = self.write("confirm_trade", "scn2-trade-retry",
                           item_id=item["id"], side="sell")
        self.assertFalse(retry["ok"], retry)
        self.assertEqual("operation_pending", retry["error"])
        self.assertEqual("scn2-trade-silent", retry["blocking_operation_id"])
        self.assertEqual("uncertain", retry["execution_status"])


# ---------------------------------------------------------------------------
# Scenario 3: different-item staging
# ---------------------------------------------------------------------------

class DifferentItemStagingScenarioTests(ScenarioCase):
    """L1-fake #3: A -> B -> A pointers; uncertain retry scoping."""

    def test_staging_a_then_b_then_a_reports_the_pointer_transitions(self):
        self.enter_game()
        item_a = self.stock("Scrap Core", 22)
        item_b = self.stock("Cargo Crate", 110)
        self.arrive("Ada", intent="BUY", budget=500, purchases=3)

        first = self.write("stage_negotiation_item", "scn3-stage-a1",
                            item_id=item_a["id"])
        second = self.write("stage_negotiation_item", "scn3-stage-b",
                            item_id=item_b["id"])
        third = self.write("stage_negotiation_item", "scn3-stage-a2",
                           item_id=item_a["id"])
        for payload in (first, second, third):
            self.assertTrue(payload["ok"], payload)
            self.assertTrue(payload["dispatched"])
            self.assertEqual("completed", payload["execution_status"])
            self.assertTrue(payload["negotiation_open"])
            self.assertTrue(payload["client_identity_unchanged"])
            self.assertTrue(payload["item_on_weighted_counter"])
            self.assertEqual("BUY", payload["client_intent"])
            self.assertEqual("weighted_area_add", payload["native_primitive"])

        self.assertEqual((0, item_a["id"]),
                         (first["staged_item_id_before"],
                          first["staged_item_id_after"]))
        self.assertEqual((item_a["id"], item_b["id"]),
                         (second["staged_item_id_before"],
                          second["staged_item_id_after"]))
        self.assertEqual((item_b["id"], item_a["id"]),
                         (third["staged_item_id_before"],
                          third["staged_item_id_after"]))
        self.assertEqual(item_a["id"], self.snapshot()["staged_item_id"])

    def test_uncertain_staging_blocks_same_item_retry_but_not_a_different_item(self):
        self.enter_game()
        item_a = self.stock("Scrap Core", 22)
        item_b = self.stock("Cargo Crate", 110)
        self.arrive("Ada", intent="BUY", budget=500, purchases=3)

        staged = self.write("stage_negotiation_item", "scn3-stage-base",
                            item_id=item_a["id"])
        self.assertTrue(staged["ok"], staged)
        clicks_before = self.native_clicks("stage_negotiation_item")

        # Host-side fault injection: the next native staging primitive returns
        # without doing anything, so the bridge must report uncertain.
        self.game.inject_native_fault("stage_negotiation_item",
                                      detail="native_not_called")
        uncertain = self.write("stage_negotiation_item", "scn3-stage-uncertain",
                               item_id=item_a["id"])
        self.assertFalse(uncertain["ok"], uncertain)
        self.assertTrue(uncertain["uncertain"])
        self.assertTrue(uncertain["dispatched"])
        self.assertEqual("uncertain", uncertain["execution_status"])
        self.assertEqual("outcome_unverified", uncertain["error"])
        self.assertEqual("native_not_called", uncertain["detail"])
        self.assertEqual(item_a["id"], uncertain["item_id"])
        self.assertEqual(item_a["id"], uncertain["staged_item_id_before"])
        self.assertIsNone(uncertain["staged_item_id_after"],
                          "an unverified dispatch must not fabricate an after")
        self.assertIsNone(uncertain["negotiation_open"])
        self.assertIsNone(uncertain["client_identity_unchanged"])
        self.assertEqual("BUY", uncertain["client_intent"])
        self.assertIsInstance(uncertain["next"], str)
        self.assertIn("do not retry", uncertain["next"])
        self.assertEqual(clicks_before,
                         self.native_clicks("stage_negotiation_item"),
                         "the injected silent native call did not click")

        # Same item + same args + new action id while the first attempt is
        # still non-terminal => operation_pending (call-scoped fingerprint).
        retry = self.write("stage_negotiation_item", "scn3-stage-retry",
                           item_id=item_a["id"])
        self.assertFalse(retry["ok"], retry)
        self.assertEqual("operation_pending", retry["error"])
        self.assertEqual("scn3-stage-uncertain",
                         retry["blocking_operation_id"])
        self.assertTrue(retry["blocking_same_call"])
        self.assertEqual("uncertain", retry["execution_status"])
        self.assertTrue(retry["dispatched"])

        # A different item is a different native call: still allowed.
        different = self.write("stage_negotiation_item", "scn3-stage-different",
                               item_id=item_b["id"])
        self.assertTrue(different["ok"], different)
        self.assertEqual(item_a["id"], different["staged_item_id_before"])
        self.assertEqual(item_b["id"], different["staged_item_id_after"])
        self.assertEqual("completed", different["execution_status"])
        self.assertEqual(clicks_before + 1,
                         self.native_clicks("stage_negotiation_item"))

        operation = self.operation("scn3-stage-uncertain")
        self.assertTrue(operation["known"], operation)
        self.assertFalse(operation["ok"], operation)
        self.assertEqual("uncertain", operation["execution_status"])
        self.assertEqual("outcome_unverified", operation["error"])
        self.assertIn("do not retry", operation["next"])

    def test_staging_refuses_fail_closed_without_a_client_or_the_item(self):
        self.enter_game()
        item = self.stock("Scrap Core", 22)

        no_client = self.write("stage_negotiation_item", "scn3-stage-noclient",
                               item_id=item["id"])
        self.assert_refusal(no_client, "no_client")
        self.assertEqual("no_current_client", no_client["detail"])
        self.assertEqual(0, no_client["staged_item_id_before"])
        self.assertIsNone(no_client["staged_item_id_after"])
        self.assertIsNone(no_client["client_identity_unchanged"])
        snapshot = self.snapshot()
        self.assertEqual(0, snapshot["staged_item_id"])
        self.assertEqual(0, snapshot["native_clicks"]["stage_negotiation_item"])
        self.assertFalse(snapshot["negotiation_open"])

        self.arrive("Ada", intent="BUY", budget=500, purchases=1)
        unknown = self.write("stage_negotiation_item", "scn3-stage-unknown",
                             item_id=999999)
        self.assert_refusal(unknown, "item_not_found")
        self.assertEqual("item_not_found", unknown["detail"])
        self.assertEqual(0, self.snapshot()["native_clicks"]["stage_negotiation_item"])


# ---------------------------------------------------------------------------
# Scenario 4: day-end modal
# ---------------------------------------------------------------------------

class DayEndModalScenarioTests(ScenarioCase):
    """L1-fake #4: typed negotiation-tablet confirm panel."""

    def test_prompt_is_typed_and_answering_confirm_reads_back_the_shop_state(self):
        self.enter_game()
        closed = self.call("get_pending_prompt")
        self.assertTrue(closed["ok"], closed)
        self.assertIsNone(closed["prompt"])
        self.assertEqual("get_pending_prompt", closed["op"])

        refused_closed = self.write("answer_prompt", "scn4-answer-closed",
                                    choice="confirm")
        self.assert_refusal(refused_closed, "no_prompt")
        self.assertIsNone(refused_closed["prompt"])
        self.assertEqual(0, self.native_clicks("answer_prompt"),
                         "no prompt means no native button click")

        # Host-side native flow: the day-end shutter raised the tablet confirm.
        self.game.raise_negotiation_confirm(
            body="End the business day?", confirm_text="End Day",
            cancel_text="Cancel")
        self.state()

        pending = self.call("get_pending_prompt")
        self.assertTrue(pending["ok"], pending)
        prompt = pending["prompt"]
        self.assertIsNotNone(prompt)
        self.assertEqual("negotiation_confirm", prompt["kind"])
        self.assertEqual("negotiation_tablet_confirm", prompt["target"])
        self.assertEqual("End the business day?", prompt["body"])
        self.assertEqual("End Day", prompt["confirm_text"])
        self.assertEqual("Cancel", prompt["cancel_text"])
        self.assertFalse(prompt["single_button"])
        self.assertTrue(prompt["confirm_enabled"])
        self.assertTrue(prompt["cancel_enabled"])
        self.assertEqual(prompt, self.state()["prompt"],
                         "/state.prompt and get_pending_prompt share the object")

        before = self.state()
        self.assertEqual("OPEN", before["shop_state"])

        answered = self.write("answer_prompt", "scn4-answer", choice="confirm")
        self.assertTrue(answered["ok"], answered)
        self.assert_operation_envelope(answered, "scn4-answer")
        self.assertTrue(answered["dispatched"])
        self.assertEqual("confirm", answered["choice"])
        self.assertTrue(answered["prompt_closed"])
        self.assertIsNone(answered["prompt"])
        readback = answered["state"]
        self.assertTrue(readback["readable"])
        self.assertEqual("CLOSED", readback["shop_state"])
        self.assertEqual(before["day"], readback["day"])
        self.assertEqual(before["cash"], readback["cash"])
        self.assertEqual(1, self.native_clicks("answer_prompt"))

        self.assertIsNone(self.call("get_pending_prompt")["prompt"])
        again = self.write("answer_prompt", "scn4-answer-again",
                           choice="confirm")
        self.assert_refusal(again, "no_prompt")
        self.assertEqual(1, self.native_clicks("answer_prompt"),
                         "answering a closed modal must not click anything")
        self.assertEqual("CLOSED", self.state()["shop_state"])


# ---------------------------------------------------------------------------
# Scenario 5: asynchronous sleep
# ---------------------------------------------------------------------------

class AsyncSleepScenarioTests(ScenarioCase):
    """L1-fake #5: sleep resolves from a day readback, never from a count."""

    async_sleep = True

    def _sleep_to_pending(self, action_id="scn5-sleep"):
        self.state()
        ended = self.write("end_day", action_id + "-end")
        self.assertTrue(ended["ok"], ended)
        self.assertEqual("CLOSED", self.state()["shop_state"])

        slept = self.write("sleep", action_id)
        self.assertFalse(slept["ok"], "an async sleep is never an immediate ok")
        self.assertTrue(slept["pending"])
        self.assertTrue(slept["dispatched"])
        self.assertEqual("running", slept["execution_status"])
        self.assertFalse(slept["day_changed"])
        self.assertEqual("save_unknown", slept["save_evidence"])
        self.assertFalse(slept["save_completed"])
        self.assertTrue(slept["save_unknown"])
        self.assertEqual(1, slept["readback"]["day_before"])
        self.assertEqual(1, slept["readback"]["day_after"])
        self.assertFalse(slept["readback"]["day_advanced"])
        self.assertIn("do not re-dispatch", slept["next"])
        raw = json.dumps(slept)
        self.assertNotIn("day_not_changed", raw)
        self.assertNotIn("failed", raw)
        return slept

    def test_pending_sleep_completes_only_when_the_day_is_observed(self):
        self.enter_game()
        self._sleep_to_pending()

        first = self.operation("scn5-sleep")
        self.assertTrue(first["known"], first)
        self.assertFalse(first["ok"], first)
        self.assertEqual("sleep", first["op"])
        self.assertEqual("running", first["execution_status"])
        self.assertEqual("dispatched", first["dispatch_status"])
        self.assertTrue(first["dispatched"])
        self.assertIsNone(first["error"], first)
        self.assertNotIn("day_not_changed", json.dumps(first))
        self.assertNotIn("failed", first["execution_status"])
        listed = self.operation("*")
        self.assertEqual(1, listed["count"], listed)
        self.assertEqual(1, listed["total_non_terminal"])
        self.assertFalse(listed["truncated"])
        self.assertEqual("scn5-sleep", listed["operations"][0]["operation_id"])

        second = self.operation("scn5-sleep")
        self.assertEqual("running", second["execution_status"],
                         "another immediate read is still inconclusive")
        self.assertEqual(1, self.state()["day"])

        self.assertTrue(self.game.finish_sleep(),
                        "host step: the native blackscreen advanced the day")

        completed = self.operation("scn5-sleep")
        self.assertTrue(completed["known"], completed)
        self.assertTrue(completed["ok"], completed)
        self.assertEqual("completed", completed["execution_status"])
        self.assertIsNone(completed["error"])
        evidence = completed["completion_evidence"]
        self.assertEqual("day_readback", evidence["source"])
        self.assertEqual(completed["result_rev"], evidence["observed_rev"])

        state = self.state()
        self.assertEqual(2, state["day"])
        self.assertEqual(6, state["rent_days_left"])
        self.assertEqual("OPEN", state["shop_state"])
        self.assertIsNone(self.snapshot()["sleep_pending"])

        # A replay of the original action id is decorated with the promoted
        # facts and the dispatch-time body stays as initial_result; no second
        # native sleep is dispatched.
        replay = self.write("sleep", "scn5-sleep")
        self.assertTrue(replay["ok"], replay)
        self.assertEqual("completed", replay["execution_status"])
        self.assertTrue(replay["initial_result"]["pending"])
        self.assertFalse(replay["initial_result"]["ok"])
        self.assertEqual(2, self.state()["day"])

    def test_observation_window_uncertainty_keeps_the_operation_non_terminal(self):
        self.enter_game()
        self._sleep_to_pending("scn5-sleep-u")

        self.game.advance_clock(5000)
        early = self.operation("scn5-sleep-u")
        self.assertEqual("running", early["execution_status"],
                         "inside the observation window a missing day change "
                         "proves nothing")

        self.game.advance_clock(6000)  # 11s > the 10s observation window
        uncertain = self.operation("scn5-sleep-u")
        self.assertFalse(uncertain["ok"], uncertain)
        self.assertEqual("uncertain", uncertain["execution_status"])
        self.assertEqual("sleep_unconfirmed", uncertain["error"])
        self.assertTrue(uncertain["known"])
        self.assertIn("do not retry", uncertain["next"])
        self.assertEqual(1, self.state()["day"])

        blocked = self.write("sleep", "scn5-sleep-new")
        self.assertFalse(blocked["ok"], blocked)
        self.assertEqual("operation_pending", blocked["error"])
        self.assertEqual("scn5-sleep-u", blocked["blocking_operation_id"])
        self.assertEqual("uncertain", blocked["execution_status"])
        self.assertEqual(1, self.state()["day"],
                         "the refused duplicate sleep must not advance the day")

        # A later observed day advance is still trusted evidence: the uncertain
        # operation recovers to completed instead of staying stuck forever.
        self.assertTrue(self.game.finish_sleep())
        recovered = self.operation("scn5-sleep-u")
        self.assertTrue(recovered["ok"], recovered)
        self.assertEqual("completed", recovered["execution_status"])
        self.assertIsNone(recovered["error"])

    def test_operation_listing_reports_its_bounded_window(self):
        self.enter_game()
        items = [self.stock("Widget-%02d" % index, 10 + index)
                 for index in range(21)]
        self.arrive("Ada", intent="BUY", budget=10000, purchases=21)
        self.game.inject_native_fault("stage_negotiation_item",
                                      detail="native_not_called", count=21)
        for index, item in enumerate(items):
            payload = self.write("stage_negotiation_item",
                                 "scn5-trunc-%02d" % index,
                                 item_id=item["id"])
            self.assertFalse(payload["ok"], payload)
            self.assertEqual("uncertain", payload["execution_status"], payload)

        listing = self.operation("*")
        self.assertTrue(listing["ok"], listing)
        self.assertEqual(21, listing["total_non_terminal"])
        self.assertEqual(20, listing["count"])
        self.assertEqual(20, len(listing["operations"]))
        self.assertTrue(listing["truncated"])
        self.assertIn("only the newest 20", listing["truncation_note"])
        newest = listing["operations"][0]
        self.assertEqual("scn5-trunc-20", newest["operation_id"],
                         "the listing is newest-first")
        self.assertEqual("stage_negotiation_item", newest["op"])
        self.assertEqual("uncertain", newest["execution_status"])
        self.assertEqual("delivered", newest["delivery_status"])
        self.assertEqual("dispatched", newest["dispatch_status"])
        self.assertTrue(newest["dispatched"])
        self.assertEqual("outcome_unverified", newest["error"])
        self.assertIn("rev", newest)
        self.assertIn("result_rev", newest)


# ---------------------------------------------------------------------------
# Scenario 6: first-week rent
# ---------------------------------------------------------------------------

class FirstWeekRentScenarioTests(ScenarioCase):
    """L1-fake #6: rent receipt is exact and is not credited with other cash."""

    start_cash = 200

    def _advance_to_landlord(self):
        for index in range(7):
            ended = self.write("end_day", "scn6-end-%02d" % index)
            self.assertTrue(ended["ok"], ended)
            slept = self.write("sleep", "scn6-sleep-%02d" % index)
            self.assertTrue(slept["ok"], slept)
        return self.state()

    def test_rent_payment_receipt_is_exact_and_not_reused_for_other_cash(self):
        self.enter_game()
        state = self._advance_to_landlord()
        self.assertEqual(8, state["day"])
        self.assertEqual(0, state["rent_days_left"])
        self.assertEqual(200, state["cash"])
        dialogue = state["dialogue"]
        self.assertTrue(dialogue["available"])
        self.assertTrue(dialogue["choices"][0]["enabled"])
        self.assertIn("100", dialogue["choices"][0]["text"])

        rev_before = self.driver.rev
        paid = self.write("choose_dialogue", "scn6-rent", index=0)
        self.assertTrue(paid["ok"], paid)
        self.assertTrue(paid["dispatched"])
        self.assertEqual(0, paid["index"])
        self.assertTrue(paid["dialogue_changed"])
        self.assertIsNone(paid["receipt_error"])
        receipt = paid["last_rent_payment"]
        self.assertEqual("rent_1_100", receipt["id"])
        self.assertEqual(100, receipt["amount"])
        self.assertEqual(200, receipt["cash_before"])
        self.assertEqual(100, receipt["cash_after"])
        self.assertEqual(-100, receipt["cash_delta"])
        self.assertEqual(0, receipt["rent_days_left_before"])
        self.assertEqual(7, receipt["rent_days_left_after"])
        self.assertEqual(rev_before, receipt["paid_at_rev"],
                         "the receipt is stamped with the dispatch revision")
        self.assertEqual("dialogue_choice", receipt["source"])
        self.assertEqual(8, receipt["day"])
        self.assertEqual(1, receipt["weeks_paid_delta"])
        self.assertEqual(1, receipt["rent_weeks_paid"])
        self.assertEqual(0, receipt["rent_weeks_missed"])
        self.assertEqual(100, receipt["due_before"])
        self.assertEqual(8, receipt["day_before"])
        self.assertTrue(receipt["detection_complete"])

        state = self.state()
        self.assertEqual(100, state["cash"])
        self.assertEqual(100, state["rent"]["rent_paid_total"])
        self.assertEqual(1, state["rent"]["rent_weeks_paid"])
        self.assertEqual(7, state["rent"]["days_left"])
        self.assertEqual(receipt, state["rent"]["last_payment"])
        self.assertFalse(state["dialogue"]["available"])

        # A later trade changes cash for a different reason; the rent receipt
        # must not absorb or re-attribute that delta.
        item = self.stock("Scrap Core", 30)
        self.arrive("Payer", intent="BUY", budget=50, purchases=1)
        staged = self.write("stage_negotiation_item", "scn6-stage",
                            item_id=item["id"])
        self.assertTrue(staged["ok"], staged)
        trade = self.write("confirm_trade", "scn6-trade",
                           item_id=item["id"], side="sell")
        self.assertTrue(trade["ok"], trade)
        self.assertEqual(30, trade["cash_after"] - trade["cash_before"])

        state = self.state()
        self.assertEqual(130, state["cash"])
        self.assertEqual(200 - 100 + 30, state["cash"],
                         "cash accounting: opening - rent + trade")
        self.assertEqual(receipt, state["rent"]["last_payment"],
                         "the rent receipt is unchanged by an unrelated trade")
        self.assertEqual(-100, state["rent"]["last_payment"]["cash_delta"])
        self.assertEqual(100, state["rent"]["rent_paid_total"])
        self.assertEqual(1, state["rent"]["rent_weeks_paid"])


if __name__ == "__main__":
    unittest.main()
