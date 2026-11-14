using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RecordingDispatchGuardTests
    {
        private static void AssertDenied(
            RecordingDispatchGuard guard,
            string op,
            JObject request,
            string error)
        {
            ActionResult result;
            Assert.True(guard.TryHandle(op, request, out result), "guard handles " + op);
            Assert.False(result.Ok, "guard denies " + op);
            Assert.Equal(error, result.Error, "stable error for " + op);
        }

        [Test]
        public static void Guard_Act_Cannot_Bypass_Save_Or_New_Game()
        {
            RecordingHarness h = RecordingHarness.Create(RecordingTestData.Off());
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "act", JObject.Parse("{\"action\":\"save_game\"}"),
                RecordingSaveErrors.ActWrapperForbidden);
            AssertDenied(guard, "act", JObject.Parse("{\"action\":\"new_game\"}"),
                RecordingSaveErrors.ActWrapperForbidden);
            Assert.Equal(0, h.NativePort.SaveCalls, "no native save");
            Assert.Equal(0, h.NativePort.StartCalls, "no native start");
        }

        [Test]
        public static void Guard_Act_Cannot_Bypass_Press_Or_Confirm()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "act", JObject.Parse("{\"action\":\"press\"}"),
                RecordingSaveErrors.ActWrapperForbidden);
            AssertDenied(guard, "act", JObject.Parse("{\"action\":\"confirm_new_game\",\"args\":{}}"),
                RecordingSaveErrors.ActWrapperForbidden);
            Assert.Equal(0, h.NativePort.PromptCalls, "press never clicks a prompt");
            Assert.Equal(0, h.NativePort.StartCalls, "wrong phase never starts");
        }

        [Test]
        public static void Guard_Leaves_Non_Recording_Actions_For_Normal_Dispatch()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            ActionResult result;
            Assert.False(guard.TryHandle("act", JObject.Parse("{\"action\":\"open_shop\"}"), out result),
                "ordinary act wrapper is not consumed by the recording guard");
            Assert.Null(result, "no fabricated result");
            Assert.False(guard.TryHandle("open_shop", new JObject(), out result),
                "ordinary direct action is not consumed");
        }

        [Test]
        public static void Guard_Missing_Act_Action_Is_A_Protocol_Failure()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "act", new JObject(), "missing_action");
            AssertDenied(guard, "act", JObject.Parse("{\"action\":\"act\"}"), "missing_action");
        }

        [Test]
        public static void Guard_Load_Other_Slot_Is_Denied_Before_Native()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.StatePort.State = RecordingTestData.MainMenu();
            h.NativePort.ResetCalls();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "load_game", JObject.Parse("{\"slot\":8}"),
                RecordingSaveErrors.SlotNotBound);
            Assert.Equal(0, h.NativePort.LoadCalls, "other slot never reaches native load");
        }

        [Test]
        public static void Guard_Load_Bound_Slot_Reaches_Native_Once()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.StatePort.State = RecordingTestData.MainMenu();
            h.NativePort.ResetCalls();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            ActionResult result;
            Assert.True(guard.TryHandle("load_game", JObject.Parse("{\"slot\":7}"), out result),
                "guard handles load_game");
            Assert.True(result.Ok, "bound slot load succeeds: " + result.Error);
            Assert.Equal(1, h.NativePort.LoadCalls, "one native load");
            Assert.Equal(7, h.NativePort.LastLoadSlot, "only the bound slot is used");
        }

        [Test]
        public static void Guard_Recording_Load_Ignores_Any_Caller_Supplied_Slot()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.StatePort.State = RecordingTestData.MainMenu();
            h.NativePort.ResetCalls();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            ActionResult result;
            Assert.True(guard.TryHandle(
                "load_recording_game",
                JObject.Parse("{\"slot\":8}"),
                out result), "guard handles load_recording_game");
            Assert.True(result.Ok, "bound recording load succeeds: " + result.Error);
            Assert.Equal(7, h.NativePort.LastLoadSlot,
                "the bound slot is used; the caller's slot is ignored");
            Assert.Equal(1, h.NativePort.LoadCalls, "one native load");
        }

        [Test]
        public static void Guard_Save_Requires_Bound_State()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "save_game", new JObject(), RecordingSaveErrors.NotBound);
            Assert.Equal(0, h.NativePort.SaveCalls, "no native save before bind");

            h.Bind();
            ActionResult result;
            Assert.True(guard.TryHandle("save_game", new JObject(), out result), "guard handles save");
            Assert.True(result.Ok, "bound save succeeds: " + result.Error);
            Assert.Equal(1, h.NativePort.SaveCalls, "one native save");
        }

        [Test]
        public static void Router_Version_Gate_Blocks_Recording_Start_Even_When_Config_Is_Valid()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);
            int dispatched = 0;

            ActionRouter router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    dispatched++;
                    ActionResult result;
                    if (guard.TryHandle(op, request, out result)) return result;
                    if (op == "get_recording_save_status")
                    {
                        return ActionResult.SuccessRaw(op, h.Service.StatusJson());
                    }
                    return ActionResult.Failure(op, "not_dispatched");
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { return WriteGateDecision.Locked(GameVersionGate.ReasonUnconfigured); });

            string write = router.Handle(
                "{\"op\":\"confirm_new_game\",\"rev\":5,\"action_id\":\"gate-rec\"}");
            Assert.Contains(write, "version_gate_unconfigured",
                "explicit recording config cannot bypass the version gate");
            Assert.Contains(write, "\"dispatched\":false", "start was not dispatched");
            Assert.Equal(0, dispatched, "version gate refusal happens before dispatch");
            Assert.Equal(0, h.NativePort.StartCalls, "no native start");

            string read = router.Handle("{\"op\":\"get_recording_save_status\"}");
            Assert.Contains(read, "\"ok\":true", "status read remains available");
            Assert.Contains(read, "\"recording_mode\":\"armed\"", "recording config is valid");
            Assert.Equal(1, dispatched, "only the read dispatch reached the handler");
        }

        [Test]
        public static void Guard_Direct_New_Game_Is_Never_The_Controlled_Start()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "new_game", new JObject(), RecordingSaveErrors.NewGameForbidden);
            Assert.Equal(0, h.NativePort.StartCalls, "legacy new_game never starts");
        }

        [Test]
        public static void Guard_Without_Service_Still_Fails_Closed()
        {
            RecordingDispatchGuard guard = new RecordingDispatchGuard(null);
            ActionResult result;
            Assert.True(guard.TryHandle("save_game", new JObject(), out result),
                "protected op is consumed");
            Assert.Equal("recording_service_unavailable", result.Error,
                "missing service is a stable failure");
        }

        [Test]
        public static void Guard_Direct_Press_Is_Denied_Even_When_Bound()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            RecordingDispatchGuard guard = new RecordingDispatchGuard(h.Service);

            AssertDenied(guard, "press", JObject.Parse("{\"control_id\":\"b123:0\"}"),
                RecordingSaveErrors.PressForbidden);
            Assert.Equal(0, h.NativePort.PromptCalls, "no native UI callback");
        }
    }
}
