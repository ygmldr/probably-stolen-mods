using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Builders for the S5 pure-logic tests. They mirror only player-visible
    /// values; no hidden appraisal/identity field exists here on purpose.
    /// </summary>
    internal static class FirstWeekTestData
    {
        public static DialogueChoiceView Choice(int index, string text, bool enabled)
        {
            return new DialogueChoiceView
            {
                Index = index,
                Text = text,
                Enabled = enabled,
                HasItem = true,
                ItemId = 100 + index,
                ItemName = text
            };
        }

        public static DialogueView Dialogue(string identity, string text, params DialogueChoiceView[] choices)
        {
            var view = new DialogueView
            {
                Readable = true,
                Available = true,
                Identity = identity,
                Speaker = identity,
                Text = text,
                Typing = false,
                Ended = choices == null || choices.Length == 0,
                Choice = choices != null && choices.Length > 0
            };

            if (choices != null) view.Choices.AddRange(choices);
            return view;
        }

        public static DialogueView NoDialogue()
        {
            return new DialogueView
            {
                Readable = true,
                Available = false,
                Ended = true,
                Choice = false
            };
        }

        public static DialogueView UnreadableDialogue()
        {
            return new DialogueView
            {
                Readable = false,
                Error = DialogueChoiceErrors.DialogueUnavailable,
                Available = false
            };
        }

        public static RentSnapshot Rent(
            int paid,
            int weeksPaid,
            int cash,
            int daysUntilRent,
            int due = 200,
            int day = 1,
            int weeksMissed = 0,
            int rentValue = 200,
            int temporaryRent = 0,
            int daysLeft = 0,
            int startingRent = 200,
            bool canPay = true,
            string runId = null)
        {
            return new RentSnapshot
            {
                Readable = true,
                Due = due,
                RentValue = rentValue,
                TemporaryRent = temporaryRent,
                DaysLeft = daysLeft,
                DaysUntilRent = daysUntilRent,
                CanPay = canPay,
                RentPaidTotal = paid,
                RentWeeksPaid = weeksPaid,
                RentWeeksMissed = weeksMissed,
                StartingRent = startingRent,
                Cash = cash,
                Day = day,
                RunId = runId
            };
        }

        public static RentSnapshot UnreadableRent()
        {
            return new RentSnapshot
            {
                Readable = false,
                Error = RentErrors.RentUnavailable
            };
        }

        public static ShopDaySnapshot Shop(
            int day,
            int cash,
            int rentDaysLeft,
            int due = 200,
            bool canEndDay = true,
            bool inGame = true)
        {
            return new ShopDaySnapshot
            {
                Readable = true,
                InGame = inGame,
                CanEndDay = canEndDay,
                Day = day,
                Cash = cash,
                RentDaysLeft = rentDaysLeft,
                RentDue = due
            };
        }

        public static ShopDaySnapshot UnreadableShop()
        {
            return new ShopDaySnapshot
            {
                Readable = false,
                Error = "readback_unavailable"
            };
        }
    }

    internal static class DialogueChoicePolicyTests
    {
        [Test]
        public static void Validate_Rejects_Unreadable_Dialogue()
        {
            string error = DialogueChoicePolicy.Validate(FirstWeekTestData.UnreadableDialogue(), 0);
            Assert.Equal(DialogueChoiceErrors.DialogueUnavailable, error, "unreadable dialogue must fail closed");
        }

        [Test]
        public static void Validate_Rejects_No_Dialogue()
        {
            string error = DialogueChoicePolicy.Validate(FirstWeekTestData.NoDialogue(), 0);
            Assert.Equal(DialogueChoiceErrors.NoDialogue, error, "no dialogue");
        }

        [Test]
        public static void Validate_Rejects_Dialogue_Without_Choices()
        {
            DialogueView view = FirstWeekTestData.Dialogue("a", "line only");
            string error = DialogueChoicePolicy.Validate(view, 0);
            Assert.Equal(DialogueChoiceErrors.NoChoice, error, "dialogue without choices");
        }

        [Test]
        public static void Validate_Rejects_Negative_And_Out_Of_Range_Index()
        {
            DialogueView view = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "zero", true),
                FirstWeekTestData.Choice(1, "one", true));

            Assert.Equal(DialogueChoiceErrors.BadIndex, DialogueChoicePolicy.Validate(view, -1), "negative index");
            Assert.Equal(DialogueChoiceErrors.BadIndex, DialogueChoicePolicy.Validate(view, 2), "index past end");
        }

        [Test]
        public static void Validate_Rejects_Disabled_Choice()
        {
            DialogueView view = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "pay rent", false));

            Assert.Equal(DialogueChoiceErrors.ChoiceDisabled, DialogueChoicePolicy.Validate(view, 0),
                "native-disabled choice must be refused");
        }

        [Test]
        public static void Validate_Accepts_Enabled_Choice()
        {
            DialogueView view = FirstWeekTestData.Dialogue("a", "pick",
                FirstWeekTestData.Choice(0, "pay rent", true));

            Assert.Null(DialogueChoicePolicy.Validate(view, 0), "enabled choice");
        }

        [Test]
        public static void Changed_Is_False_For_Identical_Line()
        {
            DialogueView before = FirstWeekTestData.Dialogue("a", "pay?",
                FirstWeekTestData.Choice(0, "pay", true));
            DialogueView after = FirstWeekTestData.Dialogue("a", "pay?",
                FirstWeekTestData.Choice(0, "pay", true));

            Assert.False(DialogueChoicePolicy.Changed(before, after), "same dialogue must count as unchanged");
        }

        [Test]
        public static void Changed_Is_True_For_New_Identity()
        {
            DialogueView before = FirstWeekTestData.Dialogue("a", "pay?",
                FirstWeekTestData.Choice(0, "pay", true));
            DialogueView after = FirstWeekTestData.Dialogue("b", "paid");

            Assert.True(DialogueChoicePolicy.Changed(before, after), "new dialogue identity");
        }

        [Test]
        public static void Changed_Is_True_For_Different_Text()
        {
            DialogueView before = FirstWeekTestData.Dialogue("a", "pay?");
            DialogueView after = FirstWeekTestData.Dialogue("a", "paid");

            Assert.True(DialogueChoicePolicy.Changed(before, after), "text change");
        }

        [Test]
        public static void Changed_Is_True_When_Dialogue_Ends()
        {
            DialogueView before = FirstWeekTestData.Dialogue("a", "pay?");
            DialogueView after = FirstWeekTestData.NoDialogue();

            Assert.True(DialogueChoicePolicy.Changed(before, after), "conversation ended");
        }

        [Test]
        public static void Changed_Is_True_When_Choice_Set_Changes()
        {
            DialogueView before = FirstWeekTestData.Dialogue("a", "pay?",
                FirstWeekTestData.Choice(0, "pay", true));
            DialogueView after = FirstWeekTestData.Dialogue("a", "pay?",
                FirstWeekTestData.Choice(0, "pay", false));

            Assert.True(DialogueChoicePolicy.Changed(before, after), "enabled state changed");
        }
    }

    internal static class RentReceiptTrackerTests
    {
        [Test]
        public static void First_Read_Establishes_Baseline_Without_Receipt()
        {
            var tracker = new RentReceiptTracker();
            RentObservation observation = tracker.Observe(
                FirstWeekTestData.Rent(0, 0, 600, 0, day: 3), "state_read", 12);

            Assert.True(observation.BaselineEstablished, "baseline established");
            Assert.Null(observation.Receipt, "no receipt on first observation");
            Assert.Null(observation.LastReceipt, "no last receipt");
            Assert.Null(observation.Error, "no receipt error");
        }

        [Test]
        public static void Rent_Payment_Creates_One_Receipt_And_Never_Duplicates_It()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 600, 0, day: 7, due: 200), "state_read", 10);

            RentObservation paid = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7, day: 7, due: 200),
                "dialogue_choice",
                42);

            Assert.NotNull(paid.Receipt, "payment receipt");
            Assert.Null(paid.Error, "no receipt error");
            Assert.Equal(200, paid.Receipt.Amount, "amount from RunStats.rentPaid delta");
            Assert.Equal(600, paid.Receipt.CashBefore, "cash before");
            Assert.Equal(400, paid.Receipt.CashAfter, "cash after");
            Assert.Equal(-200, paid.Receipt.CashDelta, "cash delta");
            Assert.Equal(0, paid.Receipt.RentDaysLeftBefore, "rent days before");
            Assert.Equal(7, paid.Receipt.RentDaysLeftAfter, "rent days after");
            Assert.Equal(7, paid.Receipt.DayBefore, "day before");
            Assert.Equal(42L, paid.Receipt.PaidAtRev, "paid at rev");
            Assert.Equal("dialogue_choice", paid.Receipt.Source, "source");
            Assert.Equal(1, paid.Receipt.WeeksPaidDelta, "weeks paid delta");
            Assert.True(paid.Receipt.DetectionComplete, "single payment is complete");
            Assert.False(string.IsNullOrEmpty(paid.Receipt.Id), "receipt id");

            RentObservation again = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7, day: 7, due: 200),
                "state_read",
                43);

            Assert.Null(again.Receipt, "the same payment must not be counted again");
            Assert.NotNull(again.LastReceipt, "last receipt is still available");
            Assert.Equal(paid.Receipt.Id, again.LastReceipt.Id, "same receipt identity");
            Assert.Equal(42L, again.LastReceipt.PaidAtRev, "original paid_at_rev is preserved");
        }

        [Test]
        public static void Counter_Decrease_On_Load_Resets_Baseline_Without_Receipt()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 600, 0, day: 7), "state_read", 1);
            RentObservation paid = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7, day: 7), "dialogue_choice", 2);
            Assert.NotNull(paid.Receipt, "first payment");

            RentObservation loaded = tracker.Observe(
                FirstWeekTestData.Rent(0, 0, 700, 0, day: 1), "state_read", 3);

            Assert.Null(loaded.Receipt, "load is not a new payment");
            Assert.Null(loaded.LastReceipt, "stale receipt cleared after a counter decrease");
            Assert.True(loaded.BaselineEstablished, "baseline re-established");
        }

        [Test]
        public static void Run_Identity_Change_Resets_Without_Receipt()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 600, 0, runId: "run-a"), "state_read", 1);

            RentObservation loaded = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7, runId: "run-b"), "state_read", 2);

            Assert.Null(loaded.Receipt, "loading another run must not look like a payment");
            Assert.True(loaded.BaselineEstablished, "baseline re-established");
        }

        [Test]
        public static void Weeks_Paid_Without_Amount_Is_Receipt_Unavailable()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(200, 1, 400, 7), "state_read", 1);

            RentObservation observation = tracker.Observe(
                FirstWeekTestData.Rent(200, 2, 400, 7), "state_read", 2);

            Assert.Null(observation.Receipt, "cannot invent an amount");
            Assert.Equal(RentErrors.ReceiptUnavailable, observation.Error, "explicit unavailable error");
        }

        [Test]
        public static void Unreadable_Read_Keeps_Previous_Baseline()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 600, 0), "state_read", 1);

            RentObservation unreadable = tracker.Observe(FirstWeekTestData.UnreadableRent(), "state_read", 2);
            Assert.Null(unreadable.Receipt, "no receipt while unreadable");
            Assert.Equal(RentErrors.ReceiptUnavailable, unreadable.Error, "receipt unavailable");

            RentObservation paid = tracker.Observe(
                FirstWeekTestData.Rent(200, 1, 400, 7), "state_read", 3);
            Assert.NotNull(paid.Receipt, "receipt after the state becomes readable again");
            Assert.Equal(600, paid.Receipt.CashBefore, "cash before comes from the last readable state");
        }

        [Test]
        public static void Multiple_Weeks_In_One_Read_Are_Marked_Incomplete()
        {
            var tracker = new RentReceiptTracker();
            tracker.Observe(FirstWeekTestData.Rent(0, 0, 1000, 0), "state_read", 1);

            RentObservation observation = tracker.Observe(
                FirstWeekTestData.Rent(500, 2, 500, 7), "state_read", 2);

            Assert.NotNull(observation.Receipt, "aggregate receipt");
            Assert.Equal(500, observation.Receipt.Amount, "aggregate amount");
            Assert.Equal(2, observation.Receipt.WeeksPaidDelta, "aggregate weeks");
            Assert.False(observation.Receipt.DetectionComplete, "cannot split individual weeks reliably");
        }
    }

    internal static class DayCycleRulesTests
    {
        [Test]
        public static void Evaluate_Reads_Back_Unchanged_Day()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 4, 5, 600,
                true, 4, 5, 600);

            Assert.True(readback.Readable, "readable");
            Assert.Null(readback.Error, "no error");
            Assert.False(readback.DayAdvanced, "end_day does not advance the day");
            Assert.False(readback.Changed, "nothing changed");
            Assert.Equal(4, readback.DayBefore, "day before");
            Assert.Equal(4, readback.DayAfter, "day after");
        }

        [Test]
        public static void Evaluate_Detects_Day_Advance()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 4, 5, 600,
                true, 5, 4, 550);

            Assert.Null(readback.Error, "sleep progression");
            Assert.True(readback.DayAdvanced, "day advanced");
            Assert.True(readback.Changed, "state changed");
        }

        [Test]
        public static void Evaluate_Fails_When_Before_Is_Unreadable()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                false, 0, 0, 0,
                true, 5, 4, 550);

            Assert.False(readback.Readable, "not readable");
            Assert.Equal(DayCycleErrors.ReadbackUnavailable, readback.Error, "unavailable");
        }

        [Test]
        public static void Evaluate_Fails_When_After_Is_Unreadable()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 4, 5, 600,
                false, 0, 0, 0);

            Assert.False(readback.Readable, "not readable");
            Assert.Equal(DayCycleErrors.ReadbackUnavailable, readback.Error, "unavailable");
        }

        [Test]
        public static void Evaluate_Fails_When_Day_Regresses()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 5, 5, 600,
                true, 4, 6, 600);

            Assert.Equal(DayCycleErrors.ReadbackRegression, readback.Error, "day regression");
        }

        [Test]
        public static void Evaluate_Fails_When_Rent_Countdown_Is_Out_Of_Range()
        {
            Assert.Equal(DayCycleErrors.ReadbackRegression,
                DayCycleRules.Evaluate(true, 4, 5, 600, true, 4, -1, 600).Error,
                "negative rent days");
            Assert.Equal(DayCycleErrors.ReadbackRegression,
                DayCycleRules.Evaluate(true, 4, 5, 600, true, 4, 8, 600).Error,
                "rent days above one week");
        }

        [Test]
        public static void Evaluate_Fails_When_Cash_Is_Negative()
        {
            DayCycleReadback readback = DayCycleRules.Evaluate(
                true, 4, 5, 600,
                true, 5, 4, -1);

            Assert.Equal(DayCycleErrors.ReadbackRegression, readback.Error, "negative cash");
        }
    }
}
