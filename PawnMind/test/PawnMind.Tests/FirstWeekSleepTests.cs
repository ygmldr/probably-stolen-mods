using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// S5 cross-day readback: sleep is the only real day advance (end_day just
    /// closes the shop). These tests use the S4 recording harness so the native
    /// sleep callback is a fake, never the game.
    /// </summary>
    internal static class FirstWeekSleepTests
    {
        private static RecordingHarness Bound(int day, int rentDaysLeft, int cash)
        {
            RecordingHarness harness = RecordingHarness.Create();
            harness.Bind();
            harness.StatePort.State = RecordingTestData.InGame(
                day: day, rentDaysLeft: rentDaysLeft, cash: cash);
            return harness;
        }

        [Test]
        public static void Sleep_Readback_Reports_Day_Cash_And_Rent()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.NativePort.SleepEffect = delegate
            {
                harness.StatePort.State = RecordingTestData.InGame(
                    day: 4, rentDaysLeft: 3, cash: 550);
            };

            ActionResult result = harness.Service.Sleep();

            Assert.True(result.Ok, "sleep accepted");
            Assert.Equal(1, harness.NativePort.SleepCalls, "native sleep called once");
            JObject json = JObject.Parse(result.ToJson(1));
            JObject readback = (JObject)json["readback"];
            Assert.NotNull(readback, "readback present");
            Assert.Equal(3L, (long)readback["day_before"], "day before");
            Assert.Equal(4L, (long)readback["day_after"], "day after");
            Assert.Equal(4L, (long)readback["rent_days_left_before"], "rent days before");
            Assert.Equal(3L, (long)readback["rent_days_left_after"], "rent days after");
            Assert.Equal(600L, (long)readback["cash_before"], "cash before");
            Assert.Equal(550L, (long)readback["cash_after"], "cash after");
            Assert.True((bool)readback["day_advanced"], "day advanced");
            Assert.True((bool)readback["readable"], "readable");
        }

        /// <summary>
        /// R4-SLEEP-01: a sleep that advanced the day resolves as completed on the
        /// next readback. This is what keeps a pending sleep from blocking the
        /// operation class forever.
        /// </summary>
        [Test]
        public static void Sleep_Verifier_Sees_The_Day_Advance()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.NativePort.SleepEffect = delegate { };   // nothing changes yet

            ActionResult result = harness.Service.Sleep();
            Assert.False(result.Ok, "a no-op sleep is still pending");

            harness.StatePort.State = RecordingTestData.InGame(
                day: 4, rentDaysLeft: 3, cash: 590);

            string execution;
            string error;
            Assert.True(harness.Service.TryResolveSleep(out execution, out error),
                "the verifier resolves once the day moved");
            Assert.Equal("completed", execution, "day advanced -> completed");
            Assert.Null(error, "no error");
        }

        /// <summary>
        /// R4-SLEEP-02: a sleep whose click did nothing must become terminal, not stay
        /// pending forever: one stuck sleep used to block every later sleep until the
        /// Mod restarted (live session, 2026-11-19).
        /// </summary>
        [Test]
        public static void Sleep_Verifier_Gives_Up_After_Repeated_No_Op_Readbacks()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.NativePort.SleepEffect = delegate { };

            harness.Service.Sleep();

            string execution;
            string error;
            Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                "first readback is inconclusive");
            Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                "second readback is inconclusive");
            Assert.True(harness.Service.TryResolveSleep(out execution, out error),
                "the third readback stops pretending it is still running");
            Assert.Equal("failed", execution, "day did not move -> failed");
            Assert.Equal("day_not_changed", error, "stable code");
        }

        /// <summary>
        /// R4-C: without the day-end tablet the native click is a silent no-op, so the
        /// bridge must refuse before dispatching. A no-op sleep leaves a running
        /// operation behind that blocks every later sleep (live session, 2026-11-19).
        /// </summary>
        [Test]
        public static void Sleep_Without_The_Day_End_Panel_Is_Not_Dispatched()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.StatePort.State.EndOfDayReady = false;

            ActionResult result = harness.Service.Sleep();

            Assert.False(result.Ok, "no panel, no sleep");
            Assert.Equal(RecordingSaveErrors.EndOfDayPanelClosed, result.Error, "stable code");
            Assert.Equal(0, harness.NativePort.SleepCalls, "the native click is never issued");
        }

        [Test]
        public static void Sleep_Unreadable_Before_Does_Not_Call_Native()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.StatePort.State = RecordingTestData.InGame(day: 3, rentDaysLeft: 4, cash: 600);
            harness.StatePort.State.DayCycleReadable = false;

            ActionResult result = harness.Service.Sleep();

            Assert.False(result.Ok, "unverifiable sleep must not report success");
            Assert.Equal(RecordingSaveErrors.ReadbackUnavailable, result.Error, "stable readback error");
            Assert.Equal(0, harness.NativePort.SleepCalls, "native sleep must not run");
        }

        [Test]
        public static void Sleep_Unreadable_After_Is_Outcome_Unknown()
        {
            RecordingHarness harness = Bound(3, 4, 600);
            harness.NativePort.SleepEffect = delegate
            {
                harness.StatePort.State = RecordingTestData.Unavailable();
            };

            ActionResult result = harness.Service.Sleep();

            Assert.False(result.Ok, "unreadable after must not report success");
            Assert.True(
                result.Error == RecordingSaveErrors.OutcomeUnknown
                || result.Error == RecordingSaveErrors.ReadbackUnavailable,
                "stable failure code, got: " + result.Error);
            Assert.Equal(1, harness.NativePort.SleepCalls, "native called once");
            JObject json = JObject.Parse(result.ToJson(1));
            Assert.True((bool)json["unknown"], "unknown flag");
            Assert.True((bool)json["dispatched"], "dispatched");
        }

        [Test]
        public static void Sleep_Day_Regression_Is_Outcome_Unknown()
        {
            RecordingHarness harness = Bound(5, 5, 600);
            harness.NativePort.SleepEffect = delegate
            {
                harness.StatePort.State = RecordingTestData.InGame(
                    day: 4, rentDaysLeft: 6, cash: 600);
            };

            ActionResult result = harness.Service.Sleep();

            Assert.False(result.Ok, "regressed day must not report success");
            Assert.Equal(RecordingSaveErrors.OutcomeUnknown, result.Error, "outcome_unknown");
            JObject json = JObject.Parse(result.ToJson(1));
            Assert.True((bool)json["unknown"], "unknown flag");
            Assert.Equal("readback_regression", (string)json["readback"]["error"], "regression reason");
        }
    }
}
