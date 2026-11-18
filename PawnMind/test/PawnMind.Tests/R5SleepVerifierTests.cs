using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5-A2: three rapid readbacks cannot prove a native sleep failed. A day that
    /// has not moved yet only means "not observed complete": the game uses an
    /// asynchronous blackscreen transition, so the verifier now needs a real
    /// observation window (injectable monotonic clock). Past the window without
    /// proof the operation becomes <c>uncertain</c> (never <c>failed</c>) and keeps
    /// the anti-duplicate dispatch block; a later day advance is still accepted as
    /// safe recovery evidence.
    ///
    /// Evidence level: L1-fake (no game process). The real async blackscreen stays
    /// L3 NOT_RUN.
    /// </summary>
    internal static class R5SleepVerifierTests
    {
        private const long Rev = 3;

        private static RecordingHarness Bound(int day = 3)
        {
            RecordingHarness harness = RecordingHarness.Create();
            harness.Bind();
            harness.StatePort.State = RecordingTestData.InGame(day: day);
            return harness;
        }

        private static string Write(string op, string actionId)
        {
            return "{\"op\":\"" + op + "\",\"action_id\":\"" + actionId
                + "\",\"rev\":" + Rev + "}";
        }

        /// <summary>
        /// R5-SLEEP-01: three zero-interval readbacks must stay inconclusive.
        /// </summary>
        [Test]
        public static void R5_SLEEP_01_Three_Immediate_Readbacks_Prove_Nothing()
        {
            RecordingHarness harness = Bound();
            harness.NativePort.SleepEffect = delegate { };
            harness.ClockMs = 0;

            Assert.False(harness.Service.Sleep().Ok, "a no-op sleep is still pending");

            string execution;
            string error;
            for (int i = 0; i < 3; i++)
            {
                Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                    "zero-interval readback " + i + " is inconclusive, not a failure");
                Assert.Null(execution, "no execution verdict is produced");
            }
        }

        /// <summary>
        /// R5-SLEEP-02: the day advancing after an async delay completes the sleep.
        /// </summary>
        [Test]
        public static void R5_SLEEP_02_Async_Day_Advance_Completes()
        {
            RecordingHarness harness = Bound();
            harness.NativePort.SleepEffect = delegate { };
            harness.ClockMs = 0;

            harness.Service.Sleep();

            string execution;
            string error;
            Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                "no verdict before the day moves");

            // The blackscreen is still running but inside the observation window.
            harness.ClockMs = 5000;
            Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                "half a window is still inconclusive");

            // The async transition lands.
            harness.StatePort.State = RecordingTestData.InGame(day: 4);
            harness.ClockMs = 6000;
            Assert.True(harness.Service.TryResolveSleep(out execution, out error),
                "a real day advance resolves the sleep");
            Assert.Equal(ExecutionStatus.Completed, execution, "day advanced -> completed");
            Assert.Null(error, "no error");
        }

        /// <summary>
        /// R5-SLEEP-03: past the window with no day change the outcome is
        /// uncertain, a new action id is refused, and a later day advance is still
        /// accepted as safe recovery evidence (never an automatic re-dispatch).
        /// </summary>
        [Test]
        public static void R5_SLEEP_03_Timeout_Is_Uncertain_And_Blocks_A_New_Action_Id()
        {
            RecordingHarness harness = Bound();
            harness.NativePort.SleepEffect = delegate { };
            harness.ClockMs = 0;

            ActionRouter router = Round4OperationTests.Router(
                Rev,
                delegate(string op, JObject request)
                {
                    return op == "sleep"
                        ? harness.Service.Sleep()
                        : ActionResult.Success(op);
                },
                delegate(OperationRecord record)
                {
                    if (record.Op != "sleep") return record;
                    string execution;
                    string error;
                    if (!harness.Service.TryResolveSleep(out execution, out error)) return record;
                    record.Execution = execution;
                    record.Error = error;
                    if (execution == ExecutionStatus.Completed)
                    {
                        // Mirrors PawnMindMod.VerifySleep: completion carries the
                        // observation that decided it.
                        record.CompletionEvidence =
                            "{\"source\":\"day_readback\"}";
                    }
                    return record;
                });

            router.Handle(Write("sleep", "sleep-a"));
            Assert.Equal(1, harness.NativePort.SleepCalls, "the native sleep ran once");

            harness.ClockMs = 60000;
            JObject timedOut = router.GetOperationJson("sleep-a");

            Assert.Equal(ExecutionStatus.Uncertain, (string)timedOut["execution_status"],
                "no proof of failure after the window -> uncertain");
            Assert.Equal("sleep_unconfirmed", (string)timedOut["error"], "stable code");
            Assert.Null(timedOut["completion_evidence"], "no invented evidence");

            string second = router.Handle(Write("sleep", "sleep-b"));
            Assert.Contains(second, OperationErrors.OperationPending,
                "an uncertain sleep refuses a new action id");
            Assert.Equal(1, harness.NativePort.SleepCalls, "no blind re-dispatch");

            // Safe recovery evidence: the day finally advances.
            harness.StatePort.State = RecordingTestData.InGame(day: 4);
            harness.ClockMs = 61000;
            JObject recovered = router.GetOperationJson("sleep-a");
            Assert.Equal(ExecutionStatus.Completed, (string)recovered["execution_status"],
                "an observed day advance still completes the sleep after the timeout");
            Assert.NotNull(recovered["completion_evidence"],
                "the recovery evidence is attached by the verifier");
        }

        /// <summary>
        /// R5-SLEEP-04: a native call that provably did not run is safe to retry.
        /// </summary>
        [Test]
        public static void R5_SLEEP_04_Not_Dispatched_Allows_A_Safe_Retry()
        {
            RecordingHarness harness = Bound();
            harness.NativePort.NotCalled = true;
            harness.ClockMs = 0;

            ActionRouter router = Round4OperationTests.Router(
                Rev,
                delegate(string op, JObject request)
                {
                    return op == "sleep"
                        ? harness.Service.Sleep()
                        : ActionResult.Success(op);
                });

            string first = router.Handle(Write("sleep", "sleep-a"));
            Assert.Contains(first, "native_not_called", "the bridge reports the native call never ran");
            Assert.Contains(first, "\"dispatched\":false", "nothing was dispatched");

            string retry = router.Handle(Write("sleep", "sleep-b"));
            Assert.False(retry.Contains(OperationErrors.OperationPending),
                "a provably not-dispatched sleep does not block a safe retry");
            Assert.Equal(2, harness.NativePort.SleepCalls, "the retry issued exactly one more native call");
        }

        /// <summary>
        /// R5-SLEEP-05: a stuck sleep stays discoverable after the bounded history
        /// is overflowed, and a truncated listing says so instead of silently
        /// hiding operations.
        /// </summary>
        [Test]
        public static void R5_SLEEP_05_Stuck_Sleep_Stays_Discoverable_And_Truncation_Is_Explicit()
        {
            ActionRouter router = Round4OperationTests.Router(
                Rev,
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":false,\"dispatched\":true,\"pending\":true}");
                });

            router.Handle(Write("sleep", "sleep-stuck"));
            for (int i = 0; i < 25; i++)
            {
                router.Handle(Write("busy_op_" + i, "busy-" + i));
            }

            JObject listed = router.GetOperationJson("*");
            Assert.True((bool)listed["ok"], "the listing is a read");
            Assert.Equal(20, (int)listed["count"], "the listing is bounded");
            Assert.Equal(26, (int)listed["total_non_terminal"],
                "the caller can see how many operations exist");
            Assert.True((bool)listed["truncated"],
                "a bounded listing must say it is truncated");

            JObject one = router.GetOperationJson("sleep-stuck");
            Assert.True((bool)one["known"],
                "the stuck sleep id stays resolvable and read-only");
        }

        /// <summary>
        /// R5-SLEEP-06: a run change (or a Mod restart) must never let an old
        /// sleep be completed by the new run's day number.
        /// </summary>
        [Test]
        public static void R5_SLEEP_06_Run_Change_Does_Not_Attribute_An_Old_Sleep()
        {
            RecordingHarness harness = Bound(day: 3);
            harness.NativePort.SleepEffect = delegate { };
            harness.ClockMs = 0;

            harness.Service.Sleep();

            // The run switched (or the process restarted): nothing may carry over.
            harness.Service.ResetSleepTracking();

            // The new run happens to be on a later day; that is not this sleep.
            harness.StatePort.State = RecordingTestData.InGame(day: 5);

            string execution;
            string error;
            Assert.False(harness.Service.TryResolveSleep(out execution, out error),
                "a reset sleep must never be attributed to the new run");
            Assert.Null(execution, "no fake completion");
        }
    }
}
