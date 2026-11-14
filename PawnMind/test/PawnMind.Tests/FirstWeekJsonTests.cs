using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class FirstWeekJsonTests
    {
        private static void AssertJsonNull(JToken token, string message)
        {
            Assert.True(token == null || token.Type == JTokenType.Null, message
                + " (was " + (token == null ? "missing" : token.Type.ToString()) + ")");
        }

        private static void AssertAbsent(string json, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                Assert.False(json.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0,
                    "serialized state must not leak " + needles[i]);
            }
        }

        [Test]
        public static void Dialogue_Json_Exposes_Enabled_And_Visible_Text_Only()
        {
            DialogueView view = FirstWeekTestData.Dialogue("landlord", "Pay the rent?", 
                FirstWeekTestData.Choice(0, "pay rent choice", false),
                FirstWeekTestData.Choice(1, "Don't pay the rent", true));

            Dictionary<string, object> dialogue = FirstWeekJson.Dialogue(view);
            string json = JsonConvert.SerializeObject(dialogue);
            JObject parsed = JObject.Parse(json);

            Assert.True((bool)parsed["readable"], "readable");
            Assert.True((bool)parsed["available"], "available");
            Assert.True((bool)parsed["choice"], "choice");
            Assert.False((bool)parsed["ended"], "not ended");
            Assert.Equal("landlord", (string)parsed["speaker"], "speaker");
            Assert.Equal("Pay the rent?", (string)parsed["text"], "text");

            JArray choices = (JArray)parsed["choices"];
            Assert.Equal(2, choices.Count, "two choices");
            Assert.False((bool)choices[0]["enabled"], "disabled choice reflected");
            Assert.True((bool)choices[1]["enabled"], "enabled choice reflected");
            Assert.Equal(0, (int)choices[0]["index"], "index");
            Assert.Equal("pay rent choice", (string)choices[0]["text"], "choice text");

            AssertAbsent(json,
                "isDisabledFunc", "isHiddenFunc", "forceDisabled", "forceHidden",
                "seenTimes", "nextDialogue", "startAction", "endAction", "isMainDialog",
                "correct_value", "appraisal", "hidden_identity", "stealth_wanted",
                "\"identity\"", "\"run_id\"");
        }

        [Test]
        public static void Dialogue_Json_No_Dialogue_Is_Empty_And_Ended()
        {
            Dictionary<string, object> dialogue = FirstWeekJson.Dialogue(FirstWeekTestData.NoDialogue());
            JObject parsed = JObject.Parse(JsonConvert.SerializeObject(dialogue));

            Assert.True((bool)parsed["readable"], "readable");
            Assert.False((bool)parsed["available"], "not available");
            Assert.True((bool)parsed["ended"], "ended");
            Assert.Equal(0, ((JArray)parsed["choices"]).Count, "no choices");
        }

        [Test]
        public static void Dialogue_Json_Unreadable_Is_Explicit()
        {
            JObject parsed = JObject.Parse(JsonConvert.SerializeObject(
                FirstWeekJson.Dialogue(FirstWeekTestData.UnreadableDialogue())));

            Assert.False((bool)parsed["readable"], "not readable");
            Assert.Equal("dialogue_unavailable", (string)parsed["error"], "stable error");
            Assert.False((bool)parsed["available"], "not available");
        }

        [Test]
        public static void Rent_Json_Exposes_Receipt_Fields_And_No_Hidden_Data()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 600, 0, day: 7, due: 200), "state_read", 5);
            RentObservation observation = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7, day: 7, due: 220),
                "dialogue_choice",
                42);

            Dictionary<string, object> rent = FirstWeekJson.Rent(observation);
            string json = JsonConvert.SerializeObject(rent);
            JObject parsed = JObject.Parse(json);

            Assert.True((bool)parsed["readable"], "readable");
            Assert.True((bool)parsed["can_pay"], "can_pay");
            Assert.Equal(220L, (long)parsed["due"], "due");
            Assert.Equal(1L, (long)parsed["rent_weeks_paid"], "weeks paid");
            Assert.Equal(200L, (long)parsed["rent_paid_total"], "paid total");
            Assert.Equal(0L, (long)parsed["rent_weeks_missed"], "weeks missed");
            Assert.Equal(200L, (long)parsed["starting_rent"], "starting rent");

            JObject receipt = (JObject)parsed["last_payment"];
            Assert.NotNull(receipt, "receipt object");
            Assert.Equal(200L, (long)receipt["amount"], "amount");
            Assert.Equal(600L, (long)receipt["cash_before"], "cash before");
            Assert.Equal(400L, (long)receipt["cash_after"], "cash after");
            Assert.Equal(-200L, (long)receipt["cash_delta"], "cash delta");
            Assert.Equal(0L, (long)receipt["rent_days_left_before"], "days before");
            Assert.Equal(7L, (long)receipt["rent_days_left_after"], "days after");
            Assert.Equal(42L, (long)receipt["paid_at_rev"], "paid at rev");
            Assert.Equal("dialogue_choice", (string)receipt["source"], "source");
            Assert.Equal(7L, (long)receipt["day_before"], "day before");

            AssertAbsent(json,
                "isDisabledFunc", "isHiddenFunc", "forceDisabled", "forceHidden",
                "seenTimes", "nextDialogue", "correct_value", "appraisal",
                "hidden_identity", "stealth_wanted", "runID", "run_id",
                "saveSlot", "saveSlotId");
        }

        [Test]
        public static void Rent_Json_Unreadable_Never_Fabricates_A_Receipt()
        {
            RentObservation observation = new RentReceiptTracker().Observe(
                FirstWeekTestData.UnreadableRent(), "state_read", 3);

            Dictionary<string, object> rent = FirstWeekJson.Rent(observation);
            JObject parsed = JObject.Parse(JsonConvert.SerializeObject(rent));

            Assert.False((bool)parsed["readable"], "not readable");
            Assert.Equal(RentErrors.RentUnavailable, (string)parsed["error"], "rent_unavailable");
            Assert.Equal(RentErrors.ReceiptUnavailable, (string)parsed["receipt_error"],
                "receipt_unavailable");
            AssertJsonNull(parsed["last_payment"], "no receipt");
            AssertJsonNull(parsed["due"], "no invented due");
        }

        [Test]
        public static void Readback_Json_Includes_All_Compared_Fields()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 4, 5, 600,
                true, 5, 4, 550);

            JObject parsed = JObject.Parse(
                JsonConvert.SerializeObject(FirstWeekJson.Readback(readback)));

            Assert.Equal(4L, (long)parsed["day_before"], "day before");
            Assert.Equal(5L, (long)parsed["day_after"], "day after");
            Assert.Equal(5L, (long)parsed["rent_days_left_before"], "rent before");
            Assert.Equal(4L, (long)parsed["rent_days_left_after"], "rent after");
            Assert.Equal(600L, (long)parsed["cash_before"], "cash before");
            Assert.Equal(550L, (long)parsed["cash_after"], "cash after");
            Assert.True((bool)parsed["day_advanced"], "day advanced");
            Assert.True((bool)parsed["readable"], "readable");
        }
    }

    internal static class ActionResultFailureTests
    {
        [Test]
        public static void Trade_And_Bargain_Failures_Serialize_As_Not_Ok()
        {
            var cases = new[]
            {
                new[] { "confirm_trade", "cannot_afford" },
                new[] { "confirm_trade", "item_not_found" },
                new[] { "confirm_trade", "already_owned" },
                new[] { "confirm_trade", "not_owned" },
                new[] { "confirm_trade", "bad_side" },
                new[] { "bargain", "bad_percent" },
                new[] { "bargain", "bad_direction" },
                new[] { "bargain", "no_bargain_ui" },
                new[] { "bargain", "unavailable" }
            };

            for (int i = 0; i < cases.Length; i++)
            {
                ActionResult result = ActionResult.Failure(cases[i][0], cases[i][1]);
                JObject json = JObject.Parse(result.ToJson(77));

                Assert.False((bool)json["ok"], cases[i][0] + " failure must not be ok");
                Assert.Equal(cases[i][1], (string)json["error"], "error code preserved");
                Assert.Equal(77L, (long)json["rev"], "rev echoed");
            }
        }

        [Test]
        public static void Raw_False_Response_Is_Not_Ok()
        {
            ActionResult result = ActionResult.SuccessRaw(
                "confirm_trade", "{\"ok\":false,\"error\":\"cannot_afford\"}");

            Assert.False(result.Ok, "raw ok=false is a failure");
            Assert.Equal("cannot_afford", result.Error, "raw error preserved");
            JObject json = JObject.Parse(result.ToJson(5));
            Assert.False((bool)json["ok"], "serialized ok=false");
        }
    }
}
