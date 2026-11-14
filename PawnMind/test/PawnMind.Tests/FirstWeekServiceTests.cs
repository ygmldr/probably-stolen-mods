using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class FakeFirstWeekDialogue : IDialoguePort
    {
        public DialogueView View = FirstWeekTestData.NoDialogue();
        public DialogueView After;
        public bool UnreadableAfterSelect;
        public int Reads;
        public int SelectCalls;
        public int LastIndex;
        public Action<int> OnSelect;
        public Exception ThrowOnSelect;

        public DialogueView ReadDialogue()
        {
            Reads++;
            if (UnreadableAfterSelect && SelectCalls > 0) return FirstWeekTestData.UnreadableDialogue();
            return View;
        }

        public void SelectChoice(int index)
        {
            SelectCalls++;
            LastIndex = index;
            if (ThrowOnSelect != null) throw ThrowOnSelect;
            if (OnSelect != null) OnSelect(index);
            if (After != null) View = After;
        }
    }

    internal sealed class FakeFirstWeekRent : IRentPort
    {
        public RentSnapshot Snapshot = FirstWeekTestData.Rent(0, 0, 600, 0);
        public int Reads;

        public RentSnapshot ReadRent()
        {
            Reads++;
            return Snapshot;
        }
    }

    internal sealed class FakeFirstWeekShop : IShopDayPort
    {
        public ShopDaySnapshot Snapshot = FirstWeekTestData.Shop(5, 600, 5);
        public ShopDaySnapshot After;
        public bool UnreadableAfterEnd;
        public int Reads;
        public int EndCalls;
        public Action OnEndDay;
        public Exception ThrowOnEndDay;

        public ShopDaySnapshot ReadShopDay()
        {
            Reads++;
            if (UnreadableAfterEnd && EndCalls > 0) return FirstWeekTestData.UnreadableShop();
            return Snapshot;
        }

        public void EndDay()
        {
            EndCalls++;
            if (ThrowOnEndDay != null) throw ThrowOnEndDay;
            if (OnEndDay != null) OnEndDay();
            if (After != null) Snapshot = After;
        }
    }

    internal sealed class FirstWeekHarness
    {
        public readonly FakeFirstWeekDialogue Dialogue = new FakeFirstWeekDialogue();
        public readonly FakeFirstWeekRent Rent = new FakeFirstWeekRent();
        public readonly FakeFirstWeekShop Shop = new FakeFirstWeekShop();
        public readonly FirstWeekService Service;

        public FirstWeekHarness()
        {
            Service = new FirstWeekService(Dialogue, Rent, Shop);
        }

        public JObject Json(ActionResult result)
        {
            return JObject.Parse(result.ToJson(99));
        }

        public ActionResult Choose(int index)
        {
            return Service.ChooseDialogue(index, 42);
        }

        public ActionResult EndDay()
        {
            return Service.EndDay(42);
        }
    }

    internal static class FirstWeekServiceDialogueTests
    {
        private static JObject Json(FirstWeekHarness harness, ActionResult result)
        {
            return harness.Json(result);
        }

        private static void AssertJsonNull(JToken token, string message)
        {
            Assert.True(token == null || token.Type == JTokenType.Null, message
                + " (was " + (token == null ? "missing" : token.Type.ToString()) + ")");
        }

        [Test]
        public static void Choose_No_Dialogue_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.NoDialogue();

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "no dialogue must not report success");
            Assert.Equal(DialogueChoiceErrors.NoDialogue, result.Error, "stable error");
            Assert.Equal(0, harness.Dialogue.SelectCalls, "SelectChoice must not be called");
            JObject json = Json(harness, result);
            Assert.False((bool)json["ok"], "json ok=false");
            Assert.False((bool)json["dispatched"], "nothing dispatched");
        }

        [Test]
        public static void Choose_No_Choice_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("a", "just a line");

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "no choice must not report success");
            Assert.Equal(DialogueChoiceErrors.NoChoice, result.Error, "stable error");
            Assert.Equal(0, harness.Dialogue.SelectCalls, "SelectChoice must not be called");
        }

        [Test]
        public static void Choose_Bad_Index_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "zero", true),
                FirstWeekTestData.Choice(1, "one", true));

            Assert.Equal(DialogueChoiceErrors.BadIndex, harness.Choose(-1).Error, "negative index");
            Assert.Equal(DialogueChoiceErrors.BadIndex, harness.Choose(2).Error, "index past end");
            Assert.Equal(0, harness.Dialogue.SelectCalls, "SelectChoice must not be called");
        }

        [Test]
        public static void Choose_Disabled_Choice_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("landlord", "rent?",
                FirstWeekTestData.Choice(0, "pay rent choice", false),
                FirstWeekTestData.Choice(1, "Don't pay the rent", true));
            harness.Rent.Snapshot = FirstWeekTestData.Rent(0, 0, 100, 0, due: 200, canPay: false);

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "native-disabled pay-rent choice must fail");
            Assert.Equal(DialogueChoiceErrors.ChoiceDisabled, result.Error, "choice_disabled");
            Assert.Equal(0, harness.Dialogue.SelectCalls, "SelectChoice must not be called");
            Assert.Equal(0, harness.Rent.Reads, "no rent read needed for a disabled choice");
        }

        [Test]
        public static void Choose_Unchanged_Dialogue_Is_Failure()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "pay", true));
            harness.Dialogue.OnSelect = delegate { /* native no-op: dialogue stays identical */ };

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "unchanged dialogue must not report success");
            Assert.Equal(DialogueChoiceErrors.NotApplied, result.Error, "choice_not_applied");
            Assert.Equal(1, harness.Dialogue.SelectCalls, "native was called once");
            JObject json = Json(harness, result);
            Assert.False((bool)json["ok"], "json not ok");
            Assert.True((bool)json["dispatched"], "select was dispatched");
            Assert.False((bool)json["dialogue_changed"], "readback says unchanged");
        }

        [Test]
        public static void Choose_Success_Reports_Readback()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("landlord", "rent?",
                FirstWeekTestData.Choice(0, "pay rent choice", true));
            harness.Dialogue.OnSelect = delegate
            {
                harness.Dialogue.After = FirstWeekTestData.Dialogue("paid", "rent paid");
            };

            ActionResult result = harness.Choose(0);

            Assert.True(result.Ok, "changed dialogue is success");
            JObject json = Json(harness, result);
            Assert.True((bool)json["ok"], "json ok");
            Assert.Equal(0, (int)json["index"], "index echoed");
            Assert.Equal("pay rent choice", (string)json["choice_text"], "choice text echoed");
            Assert.True((bool)json["dialogue_changed"], "readback changed");
            Assert.Equal("paid", (string)json["dialogue"]["speaker"], "after speaker");
            Assert.Equal("rent paid", (string)json["dialogue"]["text"], "after text");
            AssertJsonNull(json["last_rent_payment"], "no payment, no receipt");
        }

        [Test]
        public static void Choose_Select_Throws_Is_Outcome_Unknown()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "pay", true));
            harness.Dialogue.ThrowOnSelect = new InvalidOperationException("boom");

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "a thrown native call must not report success");
            Assert.Equal("outcome_unknown", result.Error, "unknown outcome");
            JObject json = Json(harness, result);
            Assert.True((bool)json["unknown"], "unknown flag");
            Assert.True((bool)json["dispatched"], "may have run");
        }

        [Test]
        public static void Choose_Unreadable_After_Select_Is_Outcome_Unknown()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "pay", true));
            harness.Dialogue.UnreadableAfterSelect = true;

            ActionResult result = harness.Choose(0);

            Assert.False(result.Ok, "unreadable readback must not report success");
            Assert.Equal("outcome_unknown", result.Error, "unknown outcome");
            Assert.Equal(1, harness.Dialogue.SelectCalls, "native called once");
        }
    }

    internal static class FirstWeekServiceRentTests
    {
        private static JObject Json(FirstWeekHarness harness, ActionResult result)
        {
            return harness.Json(result);
        }

        private static void AssertJsonNull(JToken token, string message)
        {
            Assert.True(token == null || token.Type == JTokenType.Null, message
                + " (was " + (token == null ? "missing" : token.Type.ToString()) + ")");
        }

        [Test]
        public static void Choose_Rent_Payment_Emits_Receipt_Once()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("landlord", "rent?",
                FirstWeekTestData.Choice(0, "pay rent choice", true),
                FirstWeekTestData.Choice(1, "Don't pay the rent", true));
            harness.Rent.Snapshot = FirstWeekTestData.Rent(
                0, 0, 600, 0, due: 200, day: 7, canPay: true);
            harness.Dialogue.OnSelect = delegate
            {
                harness.Dialogue.After = FirstWeekTestData.Dialogue("paid", "rent paid");
                harness.Rent.Snapshot = FirstWeekTestData.Rent(
                    200, 1, 400, 7, due: 200, day: 7, canPay: true);
            };

            ActionResult result = harness.Choose(0);

            Assert.True(result.Ok, "dialogue changed and rent was paid");
            JObject json = Json(harness, result);
            JObject receipt = (JObject)json["last_rent_payment"];
            Assert.NotNull(receipt, "receipt present");
            Assert.Equal(200L, (long)receipt["amount"], "amount");
            Assert.Equal(600L, (long)receipt["cash_before"], "cash before");
            Assert.Equal(400L, (long)receipt["cash_after"], "cash after");
            Assert.Equal(0L, (long)receipt["rent_days_left_before"], "rent days before");
            Assert.Equal(7L, (long)receipt["rent_days_left_after"], "rent days after");
            Assert.Equal(42L, (long)receipt["paid_at_rev"], "paid at rev");
            Assert.Equal("dialogue_choice", (string)receipt["source"], "source");

            // The next observations (state reads) must not count the payment again.
            Dictionary<string, object> state = harness.Service.RentState(43);
            Assert.Equal(1, (int)state["rent_weeks_paid"], "weeks paid");
            Assert.Equal(200, (int)state["rent_paid_total"], "paid total");
            var last = state["last_payment"] as Dictionary<string, object>;
            Assert.NotNull(last, "last payment remains visible");
            Assert.Equal((string)receipt["id"], (string)last["id"], "same receipt");
            Assert.Equal(42L, (long)last["paid_at_rev"], "original paid_at_rev");

            Dictionary<string, object> again = harness.Service.RentState(44);
            var lastAgain = again["last_payment"] as Dictionary<string, object>;
            Assert.NotNull(lastAgain, "still visible");
            Assert.Equal((string)receipt["id"], (string)lastAgain["id"], "no duplicate receipt");
            Assert.Equal(1, (int)again["rent_weeks_paid"], "still one week");
        }

        [Test]
        public static void Choose_Rent_Receipt_Unavailable_Before_Does_Not_Invent_Receipt()
        {
            var harness = new FirstWeekHarness();
            harness.Dialogue.View = FirstWeekTestData.Dialogue("landlord", "rent?",
                FirstWeekTestData.Choice(0, "pay rent choice", true));
            harness.Rent.Snapshot = FirstWeekTestData.UnreadableRent();
            harness.Dialogue.OnSelect = delegate
            {
                harness.Dialogue.After = FirstWeekTestData.Dialogue("paid", "rent paid");
                harness.Rent.Snapshot = FirstWeekTestData.Rent(200, 1, 400, 7, day: 7);
            };

            ActionResult result = harness.Choose(0);

            Assert.True(result.Ok, "dialogue readback succeeded");
            JObject json = Json(harness, result);
            AssertJsonNull(json["last_rent_payment"], "no fabricated receipt");
            Assert.Equal(RentErrors.ReceiptUnavailable, (string)json["receipt_error"],
                "explicit receipt unavailable");
        }

        [Test]
        public static void Rent_State_Reports_CanPay_False_Without_Hidden_Data()
        {
            var harness = new FirstWeekHarness();
            harness.Rent.Snapshot = FirstWeekTestData.Rent(
                0, 0, 100, 0, due: 350, day: 6, canPay: false,
                rentValue: 300, temporaryRent: 50, daysLeft: 1, startingRent: 200);

            Dictionary<string, object> rent = harness.Service.RentState(7);

            Assert.True((bool)rent["readable"], "readable");
            Assert.False((bool)rent["can_pay"], "can_pay false");
            Assert.Equal(350, (int)rent["due"], "due includes temporary rent");
            Assert.Equal(300, (int)rent["rent_value"], "base rent visible");
            Assert.Equal(50, (int)rent["temporary_rent"], "temporary rent visible");
            Assert.Equal(1, (int)rent["days_left"], "native day counter");
            Assert.Equal(0, (int)rent["days_until_rent"], "dayUntilRent");
            Assert.Equal(200, (int)rent["starting_rent"], "starting rent");
            Assert.Null(rent["last_payment"], "no payment yet");
            Assert.Null(rent["receipt_error"], "no receipt error");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(rent);
            Assert.False(json.Contains("isHiddenFunc"), "no hidden function leak");
            Assert.False(json.Contains("correct_value"), "no appraisal answer leak");
        }

        [Test]
        public static void Rent_State_Unreadable_Uses_Stable_Error_And_No_Receipt()
        {
            var harness = new FirstWeekHarness();
            harness.Rent.Snapshot = FirstWeekTestData.UnreadableRent();

            Dictionary<string, object> rent = harness.Service.RentState(8);

            Assert.False((bool)rent["readable"], "readable false");
            Assert.Equal(RentErrors.RentUnavailable, (string)rent["error"], "rent_unavailable");
            Assert.Equal(RentErrors.ReceiptUnavailable, (string)rent["receipt_error"],
                "receipt_unavailable");
            Assert.Null(rent["last_payment"], "no receipt");
        }
    }

    internal static class FirstWeekServiceDayTests
    {
        [Test]
        public static void EndDay_Cannot_End_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.Shop(5, 600, 5, canEndDay: false);

            ActionResult result = harness.EndDay();

            Assert.False(result.Ok, "cannot end day");
            Assert.Equal(DayCycleErrors.CannotEndDay, result.Error, "cannot_end_day");
            Assert.Equal(0, harness.Shop.EndCalls, "native end day not called");
        }

        [Test]
        public static void EndDay_Unreadable_Before_Does_Not_Call_Native()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.UnreadableShop();

            ActionResult result = harness.EndDay();

            Assert.False(result.Ok, "unreadable state must not report success");
            Assert.Equal("readback_unavailable", result.Error, "stable readback error");
            Assert.Equal(0, harness.Shop.EndCalls, "native end day not called");
        }

        [Test]
        public static void EndDay_Success_Reports_Readback()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.Shop(5, 600, 5);

            ActionResult result = harness.EndDay();

            Assert.True(result.Ok, "end day success");
            Assert.Equal(1, harness.Shop.EndCalls, "native called once");
            JObject json = harness.Json(result);
            Assert.True((bool)json["ok"], "json ok");
            JObject readback = (JObject)json["readback"];
            Assert.NotNull(readback, "readback present");
            Assert.Equal(5L, (long)readback["day_before"], "day before");
            Assert.Equal(5L, (long)readback["day_after"], "day after");
            Assert.Equal(5L, (long)readback["rent_days_left_before"], "rent days before");
            Assert.Equal(600L, (long)readback["cash_before"], "cash before");
            Assert.Equal(600L, (long)readback["cash_after"], "cash after");
            Assert.False((bool)readback["day_advanced"], "end_day does not advance day");
        }

        [Test]
        public static void EndDay_Day_Regression_Is_Outcome_Unknown()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.Shop(5, 600, 5);
            harness.Shop.OnEndDay = delegate
            {
                harness.Shop.After = FirstWeekTestData.Shop(4, 600, 6);
            };

            ActionResult result = harness.EndDay();

            Assert.False(result.Ok, "regressed day must not report success");
            Assert.Equal("outcome_unknown", result.Error, "outcome_unknown");
            JObject json = harness.Json(result);
            Assert.True((bool)json["unknown"], "unknown flag");
            Assert.True((bool)json["dispatched"], "native was dispatched once");
            Assert.Equal("readback_regression", (string)json["readback"]["error"], "regression reason");
        }

        [Test]
        public static void EndDay_Unreadable_After_Is_Outcome_Unknown()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.Shop(5, 600, 5);
            harness.Shop.UnreadableAfterEnd = true;

            ActionResult result = harness.EndDay();

            Assert.False(result.Ok, "unreadable after must not report success");
            Assert.Equal("outcome_unknown", result.Error, "outcome_unknown");
            JObject json = harness.Json(result);
            Assert.True((bool)json["unknown"], "unknown flag");
            Assert.Equal(1, harness.Shop.EndCalls, "native called once");
        }

        [Test]
        public static void EndDay_Throws_Is_Outcome_Unknown()
        {
            var harness = new FirstWeekHarness();
            harness.Shop.Snapshot = FirstWeekTestData.Shop(5, 600, 5);
            harness.Shop.ThrowOnEndDay = new InvalidOperationException("boom");

            ActionResult result = harness.EndDay();

            Assert.False(result.Ok, "a thrown native call must not report success");
            Assert.Equal("outcome_unknown", result.Error, "outcome_unknown");
            JObject json = harness.Json(result);
            Assert.True((bool)json["unknown"], "unknown flag");
        }
    }
}

namespace PawnMind.Tests
{
    /// <summary>
    /// The existing trade/bargain action paths must keep their stable error codes
    /// and must never be wrapped as ok:true by the router.
    /// </summary>
    internal static class TradeFailureRoutingTests
    {
        private static ActionRouter Router(Func<string, JObject, ActionResult> dispatch)
        {
            return new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                dispatch,
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { return WriteGateDecision.Allow("v", "m", "v", "m"); });
        }

        [Test]
        public static void Confirm_Trade_Failure_Response_Is_Not_Ok()
        {
            int calls = 0;
            ActionRouter router = Router(delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Failure(op, "cannot_afford");
            });

            string response = router.Handle(
                "{\"op\":\"confirm_trade\",\"rev\":5,\"action_id\":\"trade-fail-1\","
                + "\"item_id\":7,\"side\":\"buy\"}");

            JObject json = JObject.Parse(response);
            Assert.False((bool)json["ok"], "confirm_trade failure must not be ok");
            Assert.Equal("cannot_afford", (string)json["error"], "error code preserved");
            Assert.Equal(1, calls, "failure dispatched exactly once");
        }

        [Test]
        public static void Bargain_Failure_Response_Is_Not_Ok()
        {
            int calls = 0;
            ActionRouter router = Router(delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Failure(op, "bad_percent");
            });

            string response = router.Handle(
                "{\"op\":\"bargain\",\"rev\":5,\"action_id\":\"bargain-fail-1\","
                + "\"direction\":\"sell_discount\",\"percent\":3}");

            JObject json = JObject.Parse(response);
            Assert.False((bool)json["ok"], "bargain failure must not be ok");
            Assert.Equal("bad_percent", (string)json["error"], "error code preserved");
            Assert.Equal(1, calls, "failure dispatched exactly once");
        }
    }
}

namespace PawnMind.Tests
{
    internal static class FirstWeekStateReceiptTests
    {
        [Test]
        public static void Rent_State_Detects_Payment_Once_Without_Duplicates()
        {
            var harness = new FirstWeekHarness();
            harness.Rent.Snapshot = FirstWeekTestData.Rent(
                0, 0, 600, 0, due: 200, day: 7, canPay: true);

            Dictionary<string, object> baseline = harness.Service.RentState(1);
            Assert.Null(baseline["last_payment"], "no receipt before payment");

            harness.Rent.Snapshot = FirstWeekTestData.Rent(
                200, 1, 400, 7, due: 220, day: 7, canPay: true);

            Dictionary<string, object> paid = harness.Service.RentState(2);
            var receipt = paid["last_payment"] as Dictionary<string, object>;
            Assert.NotNull(receipt, "state read detects the payment");
            Assert.Equal(200, (int)receipt["amount"], "amount");
            Assert.Equal("state_read", (string)receipt["source"], "state read source");
            Assert.Equal(2L, (long)receipt["paid_at_rev"], "paid at rev");
            Assert.Equal(1, (int)paid["rent_weeks_paid"], "weeks paid");

            Dictionary<string, object> again = harness.Service.RentState(3);
            var repeat = again["last_payment"] as Dictionary<string, object>;
            Assert.NotNull(repeat, "receipt remains visible");
            Assert.Equal((string)receipt["id"], (string)repeat["id"], "same receipt, not a second one");
            Assert.Equal(1, (int)again["rent_weeks_paid"], "still one week");
        }
    }
}

namespace PawnMind.Tests
{
    internal static class FirstWeekReceiptResetTests
    {
        [Test]
        public static void Reset_Rent_Receipts_Forgets_The_Last_Receipt()
        {
            var harness = new FirstWeekHarness();
            harness.Rent.Snapshot = FirstWeekTestData.Rent(0, 0, 600, 0, day: 7, runId: "run-a");
            harness.Service.RentState(1);
            harness.Rent.Snapshot = FirstWeekTestData.Rent(200, 1, 400, 7, day: 7, runId: "run-a");

            Dictionary<string, object> paid = harness.Service.RentState(2);
            Assert.NotNull(paid["last_payment"], "payment receipt");

            harness.Service.ResetRentReceipts();
            harness.Rent.Snapshot = FirstWeekTestData.Rent(200, 1, 400, 7, day: 7, runId: "run-a");
            Dictionary<string, object> afterReset = harness.Service.RentState(3);

            Assert.Null(afterReset["last_payment"], "reset forgets the old receipt");
            Assert.Null(afterReset["receipt_error"], "no fake error");
        }
    }
}

namespace PawnMind.Tests
{
    internal static class FirstWeekReceiptAttributionTests
    {
        [Test]
        public static void Choose_Unreadable_Before_Leaves_Payment_For_Next_State_Read()
        {
            var harness = new FirstWeekHarness();
            harness.Rent.Snapshot = FirstWeekTestData.Rent(0, 0, 600, 0, day: 7, runId: "run-a");
            harness.Service.RentState(1); // baseline

            harness.Dialogue.View = FirstWeekTestData.Dialogue("landlord", "rent?",
                FirstWeekTestData.Choice(0, "pay rent choice", true));
            harness.Rent.Snapshot = FirstWeekTestData.UnreadableRent();
            harness.Dialogue.OnSelect = delegate
            {
                harness.Dialogue.After = FirstWeekTestData.Dialogue("paid", "rent paid");
                harness.Rent.Snapshot = FirstWeekTestData.Rent(
                    200, 1, 400, 7, day: 7, runId: "run-a");
            };

            ActionResult result = harness.Choose(0);
            Assert.True(result.Ok, "dialogue readback succeeded");
            JObject json = harness.Json(result);
            Assert.True(json["last_rent_payment"] == null
                || json["last_rent_payment"].Type == JTokenType.Null,
                "this choice cannot be credited without a pre-read");
            Assert.Equal("receipt_unavailable", (string)json["receipt_error"],
                "explicit unavailable");

            Dictionary<string, object> state = harness.Service.RentState(3);
            var receipt = state["last_payment"] as Dictionary<string, object>;
            Assert.NotNull(receipt, "the next state read detects the payment");
            Assert.Equal(200, (int)receipt["amount"], "amount");
            Assert.Equal("state_read", (string)receipt["source"], "state read source");
        }
    }
}
