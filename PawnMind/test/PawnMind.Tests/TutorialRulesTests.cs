using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class TutorialRulesTests
    {
        private static TutorialSnapshot Snapshot(bool enabled, bool hidden, bool groupReadable = true)
        {
            return new TutorialSnapshot
            {
                Readable = true,
                Enabled = enabled,
                HintsHidden = hidden,
                GroupReadable = groupReadable
            };
        }

        [Test]
        public static void NeedsWrite_Is_False_Only_When_Everything_Is_Already_Off()
        {
            Assert.True(TutorialRules.NeedsWrite(Snapshot(true, false)), "enabled hints need a write");
            Assert.True(TutorialRules.NeedsWrite(Snapshot(false, false)),
                "a stale visible group still needs a write");
            Assert.False(TutorialRules.NeedsWrite(Snapshot(false, true)), "already hidden");
            Assert.False(TutorialRules.NeedsWrite(null), "unreadable never writes");
            Assert.False(
                TutorialRules.NeedsWrite(new TutorialSnapshot { Readable = false }),
                "unreadable never writes");
        }

        [Test]
        public static void Evaluate_No_Write_Is_A_Success()
        {
            TutorialOutcome outcome = TutorialRules.Evaluate(Snapshot(false, true), null, false);

            Assert.True(outcome.Ok, "already dismissed is ok");
            Assert.False(outcome.Changed, "nothing changed");
            Assert.False(outcome.Dispatched, "no native write");
            Assert.False(outcome.Enabled, "tutorial disabled");
            Assert.True(outcome.HintsHidden, "hints hidden");
        }

        [Test]
        public static void Evaluate_Success_Reports_The_Transition()
        {
            TutorialOutcome outcome = TutorialRules.Evaluate(
                Snapshot(true, false), Snapshot(false, true), true);

            Assert.True(outcome.Ok, "success");
            Assert.True(outcome.Changed, "the flag changed");
            Assert.True(outcome.Dispatched, "one native write");
            Assert.False(outcome.Enabled, "tutorial disabled");
            Assert.True(outcome.HintsHidden, "hints hidden");
        }

        [Test]
        public static void Evaluate_Stale_Group_Is_Still_A_Success_Without_Flag_Change()
        {
            TutorialOutcome outcome = TutorialRules.Evaluate(
                Snapshot(false, false), Snapshot(false, true), true);

            Assert.True(outcome.Ok, "hiding a stale group is ok");
            Assert.False(outcome.Changed, "the tutorial flag itself was already off");
            Assert.True(outcome.HintsHidden, "hints hidden");
        }

        [Test]
        public static void Evaluate_Readback_Mismatch_Fails_Closed()
        {
            TutorialOutcome stillEnabled = TutorialRules.Evaluate(
                Snapshot(true, false), Snapshot(true, false), true);
            Assert.False(stillEnabled.Ok, "enabled flag must be false after the write");
            Assert.Equal(TutorialRules.ReadbackMismatch, stillEnabled.Error, "stable error");

            TutorialOutcome stillVisible = TutorialRules.Evaluate(
                Snapshot(true, false), Snapshot(false, false), true);
            Assert.False(stillVisible.Ok, "the hint group must be hidden after the write");
            Assert.Equal(TutorialRules.ReadbackMismatch, stillVisible.Error, "stable error");

            TutorialOutcome unreadableGroup = TutorialRules.Evaluate(
                Snapshot(true, false), Snapshot(false, true, groupReadable: false), true);
            Assert.False(unreadableGroup.Ok, "an unreadable group cannot prove the hints are hidden");
            Assert.Equal(TutorialRules.ReadbackMismatch, unreadableGroup.Error, "stable error");

            TutorialOutcome unreadableAfter = TutorialRules.Evaluate(
                Snapshot(true, false), new TutorialSnapshot { Readable = false }, true);
            Assert.False(unreadableAfter.Ok, "an unreadable readback fails closed");
            Assert.Equal(TutorialRules.ReadbackMismatch, unreadableAfter.Error, "stable error");
        }

        [Test]
        public static void Evaluate_Unreadable_Before_Is_Unavailable()
        {
            TutorialOutcome outcome = TutorialRules.Evaluate(
                new TutorialSnapshot { Readable = false, Error = "not_in_game" }, null, false);

            Assert.False(outcome.Ok, "cannot dismiss without reading");
            Assert.Equal("not_in_game", outcome.Error, "adapter error preserved");
            Assert.False(outcome.Dispatched, "no write");
        }
    }
}
