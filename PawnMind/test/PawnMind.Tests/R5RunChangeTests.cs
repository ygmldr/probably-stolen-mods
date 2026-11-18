using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5-A1: a first-run prompt confirmation is an independent UI operation and
    /// must never be registered as a "run-changing native start issued" request.
    /// Only an actual StartNewGame/load request (or a dispatch that may have sent
    /// one) keeps the run-change gate and the blind-retry protection.
    ///
    /// Before R5 the router marked every <c>confirm_new_game</c> as run-changing
    /// before dispatch, so confirming the first-run prompt left an unconfirmed
    /// request (NoRun -&gt; NoRun) that refused the next, real confirm_new_game.
    ///
    /// Evidence level: L1-fake (no game process). The real native prompt/start
    /// identities stay L3 NOT_RUN.
    /// </summary>
    internal static class R5RunChangeTests
    {
        private const long Rev = 3;

        private static ActionRouter Router(
            Func<string, JObject, ActionResult> dispatch,
            Func<string> readRunId,
            Action onConfirmed = null)
        {
            return Round4OperationTests.Router(Rev, dispatch, null, readRunId, onConfirmed);
        }

        private static string Write(string op, string actionId)
        {
            return "{\"op\":\"" + op + "\",\"action_id\":\"" + actionId
                + "\",\"rev\":" + Rev + "}";
        }

        private static JObject Json(ActionResult result)
        {
            return JObject.Parse(result.ToJson(1));
        }

        /// <summary>
        /// R5-RUN-01: prompt confirmation then the real new-game confirmation.
        /// The prompt is not a run change; the start must be dispatched and the
        /// NoRun -> run identity transition confirmed.
        /// </summary>
        [Test]
        public static void R5_RUN_01_First_Run_Prompt_Does_Not_Block_The_Real_Start()
        {
            string runId = RunChangePolicy.NoRun;
            int promptConfirms = 0;
            int starts = 0;

            ActionRouter router = Router(delegate(string op, JObject request)
            {
                if (op != "confirm_new_game") return ActionResult.Success(op);

                if (promptConfirms == 0)
                {
                    promptConfirms++;
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":true,\"dispatched\":true,\"prompt_confirmed\":true,"
                        + "\"run_change\":\"not_issued\"}");
                }

                starts++;
                // The native start now really creates the run.
                runId = "run-new";
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                    + "\"run_change\":\"issued\"}");
            },
            delegate { return runId; });

            JObject prompt = JObject.Parse(router.Handle(Write("confirm_new_game", "prompt-1")));

            Assert.True((bool)prompt["ok"], "the prompt confirmation succeeds");
            Assert.Null(prompt["run_change_status"],
                "a confirmed prompt is not a run-changing request");
            Assert.Null(prompt["run_change_confirmed"], "nothing to confirm");

            string startResponse = router.Handle(Write("confirm_new_game", "start-1"));

            Assert.False(startResponse.Contains(OperationErrors.RunChangePending),
                "the real start must not be refused by the earlier prompt confirmation");

            JObject start = JObject.Parse(startResponse);
            Assert.Equal(RunChangeStatus.Confirmed, (string)start["run_change_status"],
                "NoRun -> real run identity confirms the switch");
            Assert.Equal(1, starts, "the real start dispatched exactly once");

            Assert.False((bool)router.GetOperationJson("prompt-1")["known"],
                "the confirmed switch evicts the previous run's evidence, including"
                + " the prompt confirmation");
        }

        /// <summary>
        /// R5-RUN-02: the same first-run prompt is never confirmed twice, and a
        /// repeated attempt stays a non-run-changing result.
        /// </summary>
        [Test]
        public static void R5_RUN_02_The_Same_First_Run_Prompt_Is_Only_Confirmed_Once()
        {
            RecordingHarness harness = RecordingHarness.Create(
                RecordingTestData.Valid(), RecordingTestData.PromptMenu());
            harness.NativePort.PromptEffect = delegate
            {
                harness.MenuPort.Snapshot.Prompt = null;
                harness.MenuPort.Snapshot.Phase = NewGameMenuPhase.Difficulty;
            };

            ActionResult first = harness.Service.ConfirmNewGame();
            Assert.True(first.Ok, "first-run prompt confirmation is allowed: " + first.Error);
            JObject firstJson = Json(first);
            Assert.Equal(true, (bool)firstJson["prompt_confirmed"], "prompt confirmed flag");
            Assert.Equal("not_issued", (string)firstJson["run_change"],
                "a prompt confirmation never claims a run-changing start");
            Assert.Equal(0, harness.NativePort.StartCalls, "the prompt does not start a run");

            // The prompt is shown again, but it was already confirmed.
            harness.MenuPort.Snapshot.Prompt = RecordingTestData.PromptMenu().Prompt;
            ActionResult second = harness.Service.ConfirmNewGame();
            Assert.False(second.Ok, "a repeat prompt confirmation is refused");
            Assert.Equal(RecordingSaveErrors.PromptAlreadyConfirmed, second.Error, "stable code");
            Assert.Equal("not_issued", (string)Json(second)["run_change"],
                "the refusal is not a run change either");
            Assert.Equal(1, harness.NativePort.PromptCalls, "no repeated native OnYesClick");
        }

        /// <summary>
        /// R5-RUN-03: a real start whose run identity is not readable yet stays
        /// "requested", and a second start is refused (blind-retry protection).
        /// </summary>
        [Test]
        public static void R5_RUN_03_Dispatched_But_Unconfirmed_Start_Blocks_A_Second_Start()
        {
            string runId = RunChangePolicy.NoRun;
            int starts = 0;

            ActionRouter router = Router(delegate(string op, JObject request)
            {
                starts++;
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                    + "\"run_change\":\"issued\"}");
            },
            delegate { return runId; });

            JObject first = JObject.Parse(router.Handle(Write("confirm_new_game", "start-a")));
            Assert.Equal(RunChangeStatus.Requested, (string)first["run_change_status"],
                "a dispatched start that has not switched yet is only requested");
            Assert.False((bool)first["run_change_confirmed"], "not confirmed");

            string second = router.Handle(Write("confirm_new_game", "start-b"));
            Assert.Contains(second, OperationErrors.RunChangePending,
                "a second start must not be issued while the first may be in flight");
            Assert.Equal(1, starts, "the second start was not dispatched");
        }

        /// <summary>
        /// R5-RUN-04: an unreadable run identity is unknown, never a fake success.
        /// </summary>
        [Test]
        public static void R5_RUN_04_Unreadable_Run_Identity_Is_Not_A_Fake_Success()
        {
            int starts = 0;

            ActionRouter router = Router(delegate(string op, JObject request)
            {
                starts++;
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                    + "\"run_change\":\"issued\"}");
            },
            delegate { return null; });

            JObject first = JObject.Parse(router.Handle(Write("confirm_new_game", "start-x")));
            Assert.Equal(RunChangeStatus.Unknown, (string)first["run_change_status"],
                "an unreadable identity is unknown");
            Assert.False((bool)first["run_change_confirmed"], "never confirmed without evidence");

            string second = router.Handle(Write("confirm_new_game", "start-y"));
            Assert.Contains(second, OperationErrors.RunChangePending,
                "an unknown switch keeps the blind-retry protection");
            Assert.Equal(1, starts, "no second native start");
        }

        /// <summary>
        /// R5-RUN-06 (reviewer A finding): the claim must also be applied when the
        /// post-dispatch revision refresh fails. Otherwise a first-run prompt
        /// confirmation keeps an unconfirmed run-change request behind an
        /// uncertain operation and the next, real confirm_new_game is refused
        /// until the Mod restarts.
        /// </summary>
        [Test]
        public static void R5_RUN_06_Prompt_Confirmation_Does_Not_Block_After_A_Refresh_Failure()
        {
            string runId = RunChangePolicy.NoRun;
            int confirmCalls = 0;
            int starts = 0;
            int refreshes = 0;

            ActionRouter router = Round4OperationTests.Router(
                Rev,
                delegate(string op, JObject request)
                {
                    if (op != "confirm_new_game") return ActionResult.Success(op);
                    confirmCalls++;
                    if (confirmCalls == 1)
                    {
                        return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"prompt_confirmed\":true,"
                            + "\"run_change\":\"not_issued\"}");
                    }

                    starts++;
                    runId = "run-new";
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                        + "\"run_change\":\"issued\"}");
                },
                null,
                delegate { return runId; },
                null,
                delegate
                {
                    refreshes++;
                    // Pre-dispatch refresh of the first action succeeds; its
                    // post-dispatch refresh fails (state unavailable), which is
                    // the branch that used to keep the run-change request.
                    if (refreshes == 2) throw new InvalidOperationException("state unavailable");
                    return 3L;
                });

            JObject prompt = JObject.Parse(router.Handle(Write("confirm_new_game", "prompt-refresh-fail")));
            Assert.False((bool)prompt["ok"], "the refresh failure is not a success");
            Assert.Equal("uncertain", (string)prompt["execution_status"],
                "the unknown revision leaves the execution uncertain");
            Assert.Null(prompt["run_change_status"],
                "a prompt confirmation still never registers a run change");

            string startResponse = router.Handle(Write("confirm_new_game", "start-after-refresh-fail"));
            Assert.False(startResponse.Contains(OperationErrors.OperationPending),
                "the uncertain prompt op must not block the real start");
            Assert.False(startResponse.Contains(OperationErrors.RunChangePending),
                "the prompt op must not own an unconfirmed run-change request");

            JObject start = JObject.Parse(startResponse);
            Assert.Equal(RunChangeStatus.Confirmed, (string)start["run_change_status"],
                "the real start was dispatched and confirmed");
            Assert.Equal(1, starts, "exactly one real start");
        }

        /// <summary>
        /// R5-RUN-07 (reviewer A W-A4-1): the real load is a run-changing
        /// request, so its success response must carry the same explicit
        /// `run_change:"issued"` claim the Skill/benchmark documents promise.
        /// Without it the conservative op-name fallback applies, but the
        /// documented field is missing.
        /// </summary>
        [Test]
        public static void R5_RUN_07_A_Real_Load_Claims_Issued()
        {
            RecordingHarness harness = RecordingHarness.Create();
            harness.Bind();
            harness.StatePort.State = RecordingTestData.MainMenu();

            ActionResult result = harness.Service.LoadRecordingGame();

            Assert.True(result.Ok, "the load request is accepted: " + result.Error);
            Assert.Equal(ActionResult.ClaimIssued, (string)Json(result)["run_change"],
                "a real load requests a run-changing native action");
        }

        /// <summary>
        /// R5-RUN-05: the pre-existing real-load protection must not regress.
        /// </summary>
        [Test]
        public static void R5_RUN_05_Real_Load_Protection_Still_Holds()
        {
            string runId = "run-a";
            int loads = 0;

            ActionRouter router = Router(delegate(string op, JObject request)
            {
                if (op == "load_game") loads++;
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}");
            },
            delegate { return runId; });

            router.Handle(Write("open_shop", "old-evidence"));
            JObject load = JObject.Parse(router.Handle(Write("load_game", "load-a")));
            Assert.Equal(RunChangeStatus.Requested, (string)load["run_change_status"],
                "a load request without a run identity change is still pending");
            Assert.True((bool)router.GetOperationJson("old-evidence")["known"],
                "old evidence is not evicted before the switch is confirmed");

            string second = router.Handle(Write("load_game", "load-b"));
            Assert.Contains(second, OperationErrors.RunChangePending,
                "a pending load blocks the next one");
            Assert.Equal(1, loads, "the second load was not dispatched");
        }
    }
}
