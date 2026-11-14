using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RecordingSaveServiceTests
    {
        private static void AssertDenied(ActionResult result, string error)
        {
            Assert.False(result.Ok, "operation must be denied: " + error);
            Assert.Equal(error, result.Error, "stable error code");
        }

        private static JObject Json(ActionResult result)
        {
            Assert.NotNull(result.RawJson, "raw json exists");
            return JObject.Parse(result.RawJson);
        }

        [Test]
        public static void Service_Default_Config_Denies_All_Recording_Operations()
        {
            RecordingHarness h = RecordingHarness.Create(RecordingTestData.Off());

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.SaveGame(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.SaveRecordingGame(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.LoadRecordingGame(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.LegacyLoadGame(7), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.Sleep(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.NewGame(), RecordingSaveErrors.Unconfigured);
            AssertDenied(h.Service.Press(), RecordingSaveErrors.Unconfigured);

            Assert.Equal(RecordingSaveErrors.Unconfigured, h.Service.CheckMenuWrite(),
                "S3B menu writes use the same policy");
            Assert.Equal(0, h.NativePort.StartCalls, "no native start");
            Assert.Equal(0, h.NativePort.SaveCalls, "no native save");
            Assert.Equal(0, h.NativePort.LoadCalls, "no native load");
            Assert.Equal(0, h.NativePort.SleepCalls, "no native sleep");
            Assert.Equal(0, h.NativePort.PromptCalls, "no native prompt click");
            Assert.Equal(0, h.InventoryPort.Calls, "no inventory read is needed when locked");
        }

        [Test]
        public static void Service_Status_Default_And_Ack_Missing_Are_Diagnostic()
        {
            RecordingHarness off = RecordingHarness.Create(RecordingTestData.Off());
            string offJson = off.Service.StatusJson();
            Assert.Contains(offJson, "\"ok\":true", "status is a read");
            Assert.Contains(offJson, "\"recording_mode\":\"disabled\"", "default mode");
            Assert.Contains(offJson, "\"configured_slot\":null", "no slot configured");
            Assert.Contains(offJson, "\"bound\":false", "not bound");
            Assert.Contains(offJson, "\"error\":\"recording_unconfigured\"",
                "default error code");
            Assert.Equal(0, off.MetadataPort.Calls, "no bound slot metadata");

            RecordingHarness ack = RecordingHarness.Create(RecordingTestData.Unacknowledged());
            string ackJson = ack.Service.StatusJson();
            Assert.Contains(ackJson, "\"configured_slot\":7", "configured slot is diagnostic");
            Assert.Contains(ackJson, "\"error\":\"recording_isolation_unacknowledged\"",
                "ack error code");
        }

        [Test]
        public static void Service_Valid_Config_Without_Ack_Denies_Everything()
        {
            RecordingHarness h = RecordingHarness.Create(RecordingTestData.Unacknowledged());

            AssertDenied(h.Service.ConfirmNewGame(),
                RecordingSaveErrors.IsolationUnacknowledged);
            AssertDenied(h.Service.SaveRecordingGame(),
                RecordingSaveErrors.IsolationUnacknowledged);
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged, h.Service.CheckMenuWrite(),
                "menu writes use the stable ack code");
            Assert.Equal(0, h.NativePort.StartCalls, "no native start");
            Assert.Equal(0, h.InventoryPort.Calls, "no inventory read");
        }

        [Test]
        public static void Service_Start_Refuses_Occupied_Slot_Without_Native_Call()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Result = RecordingTestData.FreeSlot(7);
            h.InventoryPort.Result.TargetOccupied = true;

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.SlotOccupied);
            Assert.Equal(0, h.NativePort.StartCalls, "occupied slot must not start a game");
            Assert.Equal(7, h.InventoryPort.LastSlot, "the configured slot was inspected");
        }

        [Test]
        public static void Service_Start_Refuses_When_Allocation_Is_Not_Predictable()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Result = RecordingTestData.FreeSlot(8);

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.NotNextFree);
            Assert.Equal(0, h.NativePort.StartCalls, "unknown allocation must not start a game");
        }

        [Test]
        public static void Service_Start_Refuses_Unreadable_Inventory()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Result = RecordingTestData.FreeSlot(7);
            h.InventoryPort.Result.Readable = false;

            AssertDenied(h.Service.ConfirmNewGame(),
                RecordingSaveErrors.InventoryUnavailable);
            Assert.Equal(0, h.NativePort.StartCalls, "unreadable inventory must not start a game");
        }

        [Test]
        public static void Service_Start_Requires_The_Difficulty_Page()
        {
            RecordingHarness h = RecordingHarness.Create(
                RecordingTestData.Valid(),
                RecordingTestData.PerksMenu());

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.WrongMenuPhase);
            Assert.Equal(0, h.NativePort.StartCalls, "only the difficulty Start page can start");
        }

        [Test]
        public static void Service_Start_Requires_An_Enabled_Start_Button()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.MenuPort.Snapshot.Continue.Enabled = false;

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.StartUnavailable);
            Assert.Equal(0, h.NativePort.StartCalls, "a disabled native Start must not be invoked");
        }

        [Test]
        public static void Service_First_Run_Prompt_Uses_Native_OnYesClick_Only_Once()
        {
            RecordingHarness h = RecordingHarness.Create(
                RecordingTestData.Valid(),
                RecordingTestData.PromptMenu());
            h.NativePort.PromptEffect = delegate
            {
                h.MenuPort.Snapshot.Prompt = null;
                h.MenuPort.Snapshot.Phase = NewGameMenuPhase.Difficulty;
            };

            ActionResult first = h.Service.ConfirmNewGame();
            Assert.True(first.Ok, "first-run prompt confirmation is allowed while armed: " + first.Error);
            Assert.Equal(true, (bool)Json(first)["prompt_confirmed"], "prompt confirmed flag");
            Assert.Equal("first_run_confirm", h.NativePort.LastPromptId, "exact native prompt identity");
            Assert.Equal(1, h.NativePort.PromptCalls, "exactly one native OnYesClick");

            h.MenuPort.Snapshot.Prompt = RecordingTestData.PromptMenu().Prompt;
            AssertDenied(h.Service.ConfirmNewGame(),
                RecordingSaveErrors.PromptAlreadyConfirmed);
            Assert.Equal(1, h.NativePort.PromptCalls, "no repeated native OnYesClick");
        }

        [Test]
        public static void Service_Non_First_Run_Prompt_Is_Not_Confirmed()
        {
            RecordingHarness h = RecordingHarness.Create(
                RecordingTestData.Valid(),
                RecordingTestData.PromptMenu());
            h.MenuPort.Snapshot.Prompt.FirstRun = false;

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.PromptNotAllowed);
            Assert.Equal(0, h.NativePort.PromptCalls, "unknown prompt must not be clicked");
        }

        [Test]
        public static void Service_Prompt_Persisting_After_Yes_Is_A_Readback_Mismatch()
        {
            RecordingHarness h = RecordingHarness.Create(
                RecordingTestData.Valid(),
                RecordingTestData.PromptMenu());

            AssertDenied(h.Service.ConfirmNewGame(),
                RecordingSaveErrors.PromptReadbackMismatch);
            Assert.Equal(1, h.NativePort.PromptCalls, "native was called once and is not retried");
        }

        [Test]
        public static void Service_Start_Readback_Mismatch_Locks_And_Does_Not_Retry()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.NativePort.StartEffect = delegate
            {
                h.StatePort.State = RecordingTestData.InGame(8);
            };

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.SlotMismatch);
            Assert.Equal(RecordingSlotState.Locked, h.Policy.State, "wrong readback locks immediately");
            Assert.Equal(1, h.NativePort.StartCalls, "start is dispatched once");

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.Locked);
            Assert.Equal(1, h.NativePort.StartCalls, "locked state must not retry the start");
            Assert.Contains(h.Service.StatusJson(), "\"recording_mode\":\"locked\"",
                "status reports the lock");
        }

        [Test]
        public static void Service_Start_Pending_Then_Binds_And_Allows_Save()
        {
            RecordingHarness h = RecordingHarness.Create();

            ActionResult started = h.Service.ConfirmNewGame();
            Assert.True(started.Ok, "start request is accepted while the scene loads: " + started.Error);
            JObject startedJson = Json(started);
            Assert.Equal(true, (bool)startedJson["start_requested"], "explicit start_requested");
            Assert.Equal("armed", (string)startedJson["state"], "still pending before readback");

            AssertDenied(h.Service.SaveRecordingGame(), RecordingSaveErrors.StartPending);
            Assert.Equal(0, h.NativePort.SaveCalls, "no save before binding");

            h.StatePort.State = RecordingTestData.InGame(7, "run-a", 3);
            string status = h.Service.StatusJson();
            Assert.Contains(status, "\"recording_mode\":\"bound\"", "readback binds the slot");
            Assert.Contains(status, "\"bound\":true", "status reports bound");
            Assert.Contains(status, "\"run_id\":\"run-a\"", "binding fingerprint is exposed");

            Assert.True(h.Service.SaveRecordingGame().Ok, "bound save is allowed");
            Assert.Equal(1, h.NativePort.SaveCalls, "native save called once");

            AssertDenied(h.Service.ConfirmNewGame(), RecordingSaveErrors.AlreadyBound);
            Assert.Equal(1, h.NativePort.StartCalls, "no second native start");
        }

        [Test]
        public static void Service_Only_The_Bound_Slot_May_Be_Loaded()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.StatePort.State = RecordingTestData.MainMenu();

            Assert.True(h.Service.LoadRecordingGame().Ok, "recording load uses the bound slot");
            Assert.Equal(7, h.NativePort.LastLoadSlot, "native load received the bound slot");
            Assert.Equal(1, h.NativePort.LoadCalls, "one native load");
            Assert.Equal("run-a", h.Policy.Binding.RunId, "binding fingerprint stays");

            h.StatePort.State = RecordingTestData.InGame(7, "run-a", 3);
            h.Service.StatusJson();
            h.StatePort.State = RecordingTestData.MainMenu();
            AssertDenied(h.Service.LegacyLoadGame(8), RecordingSaveErrors.SlotNotBound);
            Assert.Equal(1, h.NativePort.LoadCalls, "other slot must not reach native load");

            h.StatePort.State = RecordingTestData.MainMenu();
            Assert.True(h.Service.LegacyLoadGame(7).Ok, "legacy load of the bound slot is allowed");
            Assert.Equal(7, h.NativePort.LastLoadSlot, "only the bound slot is passed through");
            Assert.Equal(2, h.NativePort.LoadCalls, "one native load per accepted request");
        }

        [Test]
        public static void Service_Load_Requires_Main_Menu_And_Not_Another_Run()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();

            AssertDenied(h.Service.LoadRecordingGame(), RecordingSaveErrors.AlreadyInGame);
            Assert.Equal(0, h.NativePort.LoadCalls, "in-game load is refused before native");

            h.StatePort.State = RecordingTestData.MainMenu();
            h.NativePort.LoadEffect = delegate
            {
                h.StatePort.State = RecordingTestData.InGame(7, "run-b");
            };
            AssertDenied(h.Service.LoadRecordingGame(), RecordingSaveErrors.RunMismatch);
            Assert.Equal(RecordingSlotState.Locked, h.Policy.State, "a different run after load locks");
            Assert.Equal(1, h.NativePort.LoadCalls, "load is not retried");
        }

        [Test]
        public static void Service_Save_Readback_Mismatch_Locks_Without_Retry()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.NativePort.SaveEffect = delegate
            {
                h.StatePort.State = RecordingTestData.InGame(8);
            };

            AssertDenied(h.Service.SaveRecordingGame(), RecordingSaveErrors.SlotMismatch);
            Assert.Equal(RecordingSlotState.Locked, h.Policy.State, "wrong save slot locks");
            Assert.Equal(1, h.NativePort.SaveCalls, "save dispatched once");

            AssertDenied(h.Service.SaveGame(), RecordingSaveErrors.Locked);
            Assert.Equal(1, h.NativePort.SaveCalls, "locked save is not retried");
        }

        [Test]
        public static void Service_Bound_Save_And_Sleep_Are_Denied_At_Main_Menu()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.StatePort.State = RecordingTestData.MainMenu();

            AssertDenied(h.Service.SaveRecordingGame(), RecordingSaveErrors.NotInGame);
            AssertDenied(h.Service.Sleep(), RecordingSaveErrors.NotInGame);
            Assert.Equal(0, h.NativePort.SaveCalls, "no native save outside the game");
            Assert.Equal(0, h.NativePort.SleepCalls, "no native sleep outside the game");
        }

        [Test]
        public static void Service_Sleep_Is_Allowed_Only_When_Bound()
        {
            RecordingHarness h = RecordingHarness.Create();
            AssertDenied(h.Service.Sleep(), RecordingSaveErrors.NotBound);
            Assert.Equal(0, h.NativePort.SleepCalls, "no native sleep before bind");

            h.Bind();
            Assert.True(h.Service.Sleep().Ok, "bound sleep is allowed");
            Assert.Equal(1, h.NativePort.SleepCalls, "native sleep called once");
        }

        [Test]
        public static void Service_Native_Error_After_Save_Is_Treated_As_Unknown()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            h.NativePort.Error = "native_error";

            AssertDenied(h.Service.SaveRecordingGame(), RecordingSaveErrors.OutcomeUnknown);
            Assert.Equal(RecordingSlotState.Locked, h.Policy.State,
                "a possibly-successful save must not be retried");
            Assert.Equal(1, h.NativePort.SaveCalls, "save dispatched once");
        }

        [Test]
        public static void Service_Legacy_NewGame_And_Press_Never_Reach_Native()
        {
            RecordingHarness h = RecordingHarness.Create();
            AssertDenied(h.Service.NewGame(), RecordingSaveErrors.NewGameForbidden);
            AssertDenied(h.Service.Press(), RecordingSaveErrors.PressForbidden);
            Assert.Equal(0, h.NativePort.StartCalls, "legacy new_game never starts");
            Assert.Equal(0, h.NativePort.PromptCalls, "press never clicks a prompt");

            h.Bind();
            AssertDenied(h.Service.NewGame(), RecordingSaveErrors.AlreadyBound);
            AssertDenied(h.Service.Press(), RecordingSaveErrors.PressForbidden);
            Assert.Equal(0, h.NativePort.StartCalls, "legacy new_game and press never start");
        }

        [Test]
        public static void Service_Status_Is_Sanitized_And_Has_No_Other_Slots()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            string status = h.Service.StatusJson();

            Assert.Contains(status, "\"ok\":true", "status is a successful read");
            Assert.Contains(status, "\"recording_mode\":\"bound\"", "state");
            Assert.Contains(status, "\"configured_slot\":7", "configured slot");
            Assert.Contains(status, "\"bound\":true", "bound flag");
            Assert.Contains(status, "\"slot_matches\":true", "current slot match");
            Assert.Contains(status, "\"run_matches\":true", "current run match");
            Assert.Contains(status, "\"binding\"", "binding fingerprint");
            Assert.Contains(status, "\"run_id\":\"run-a\"", "bound run id");
            Assert.Contains(status, "\"last_save_time\":\"2026-11-14T12:00:00.0000000\"",
                "last save time is reported");
            Assert.Contains(status, "\"next_free_slot\":7", "predicted next free slot");
            Assert.Contains(status, "\"next_free_known\":true", "prediction flag");
            Assert.Contains(status, "\"configured_slot_occupied\":false",
                "configured slot is free");

            Assert.False(status.Contains("preview"), "no save preview");
            Assert.False(status.Contains("path"), "no save path");
            Assert.False(status.Contains("save_0"), "no other slot names");
            Assert.False(status.Contains("slot_arg"), "no native slot arguments");
            Assert.False(status.Contains("current_slot"), "no unbound current slot value");
            Assert.False(status.Contains("current_run_id"), "no unbound current run value");

            JObject parsed = JObject.Parse(status);
            string[] allowed =
            {
                "ok", "recording_mode", "configured_slot", "next_free_slot",
                "next_free_known", "configured_slot_occupied", "bound", "binding",
                "slot_matches", "run_matches", "last_save_time", "error"
            };
            foreach (JProperty property in parsed.Properties())
            {
                Assert.True(System.Array.IndexOf(allowed, property.Name) >= 0,
                    "status must not expose an unlisted field: " + property.Name);
            }
        }

        [Test]
        public static void Service_Status_Reports_Next_Free_Slot_And_Configured_Occupancy()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Result = RecordingTestData.FreeSlot(18);
            h.InventoryPort.Result.TargetOccupied = true;

            JObject status = JObject.Parse(h.Service.StatusJson());
            Assert.NotNull(status["next_free_slot"], "next_free_slot is always present");
            Assert.Equal(18, (int)status["next_free_slot"],
                "predicted next automatic allocation slot");
            Assert.Equal(true, (bool)status["next_free_known"], "prediction is known");
            Assert.Equal(true, (bool)status["configured_slot_occupied"],
                "configured slot occupancy is diagnostic for recording_slot_occupied");
            Assert.Equal(1, h.InventoryPort.Calls, "status inspects the index once");
            Assert.Equal(7, h.InventoryPort.LastSlot, "inspection targets the configured slot");
        }

        [Test]
        public static void Service_Status_Predicts_Next_Free_Without_A_Configured_Slot()
        {
            RecordingHarness h = RecordingHarness.Create(RecordingTestData.Off());
            h.InventoryPort.Result = RecordingTestData.FreeSlot(18);

            JObject status = JObject.Parse(h.Service.StatusJson());
            Assert.Equal("disabled", (string)status["recording_mode"],
                "recording stays locked by default");
            Assert.Equal(JTokenType.Null, status["configured_slot"].Type,
                "no configured slot");
            Assert.NotNull(status["next_free_slot"],
                "next_free_slot is present when unconfigured");
            Assert.Equal(18, (int)status["next_free_slot"],
                "the predicted slot number is readable without a config");
            Assert.Equal(true, (bool)status["next_free_known"], "prediction is known");
            Assert.Equal(false, (bool)status["configured_slot_occupied"],
                "no configured slot can be occupied");
            Assert.Equal(1, h.InventoryPort.Calls,
                "unconfigured status still inspects the read-only index");
        }

        [Test]
        public static void Service_Status_Unreadable_Inventory_Is_Unknown_Without_Error()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Throw = true;

            JObject status = JObject.Parse(h.Service.StatusJson());
            Assert.Equal(true, (bool)status["ok"], "status remains a successful read");
            Assert.Equal("armed", (string)status["recording_mode"],
                "inventory failure must not lock the recording slot");
            Assert.Equal(RecordingSlotState.Armed, h.Policy.State,
                "the policy state is unaffected");
            Assert.NotNull(status["next_free_known"], "next_free_known is always present");
            Assert.Equal(false, (bool)status["next_free_known"], "prediction failed");
            Assert.NotNull(status["next_free_slot"], "next_free_slot is always present");
            Assert.Equal(JTokenType.Null, status["next_free_slot"].Type,
                "no slot value when the prediction is unknown");
            Assert.Equal(false, (bool)status["configured_slot_occupied"],
                "occupancy is not asserted when the index is unreadable");
            Assert.Equal(JTokenType.Null, status["error"].Type,
                "an inventory read failure does not become a status error");

            h.InventoryPort.Throw = false;
            h.InventoryPort.Result = RecordingTestData.FreeSlot(7);
            h.InventoryPort.Result.Readable = false;
            status = JObject.Parse(h.Service.StatusJson());
            Assert.Equal(false, (bool)status["next_free_known"],
                "an unreadable inventory result is unknown");
            Assert.Equal(JTokenType.Null, status["next_free_slot"].Type,
                "no slot value for an unreadable result");

            h.InventoryPort.Result = null;
            status = JObject.Parse(h.Service.StatusJson());
            Assert.Equal(false, (bool)status["next_free_known"],
                "a null inventory result is unknown");
            Assert.Equal(JTokenType.Null, status["next_free_slot"].Type,
                "no slot value for a null result");
        }

        [Test]
        public static void Service_Status_Reports_Occupied_Slot_Even_When_Prediction_Is_Unknown()
        {
            RecordingHarness h = RecordingHarness.Create();
            RecordingSlotInventory inventory = RecordingTestData.FreeSlot(7);
            inventory.NextFreeKnown = false;
            inventory.NextFreeSlot = -1;
            inventory.TargetOccupied = true;
            h.InventoryPort.Result = inventory;

            JObject status = JObject.Parse(h.Service.StatusJson());
            Assert.NotNull(status["next_free_known"], "next_free_known is present");
            Assert.Equal(false, (bool)status["next_free_known"], "prediction is unknown");
            Assert.Equal(JTokenType.Null, status["next_free_slot"].Type,
                "no stale slot value is reported");
            Assert.Equal(true, (bool)status["configured_slot_occupied"],
                "indexed occupancy is still diagnosable");
        }

        [Test]
        public static void Service_Status_Reinspects_Inventory_On_Every_Call()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.InventoryPort.Result = RecordingTestData.FreeSlot(7);

            JObject first = JObject.Parse(h.Service.StatusJson());
            Assert.NotNull(first["next_free_slot"], "next_free_slot is present");
            Assert.Equal(7, (int)first["next_free_slot"], "first prediction");

            h.InventoryPort.Result = RecordingTestData.FreeSlot(18);
            JObject second = JObject.Parse(h.Service.StatusJson());
            Assert.Equal(18, (int)second["next_free_slot"],
                "a save created or deleted in-game is seen on the next status read");
            Assert.Equal(2, h.InventoryPort.Calls, "status never caches the inventory");

            h.InventoryPort.Throw = true;
            JObject third = JObject.Parse(h.Service.StatusJson());
            Assert.NotNull(third["next_free_known"], "next_free_known is present");
            Assert.Equal(false, (bool)third["next_free_known"],
                "a later read failure is not masked by a cached value");
            Assert.Equal(3, h.InventoryPort.Calls, "every status call re-inspects");
        }

        [Test]
        public static void Service_Status_Without_Policy_Still_Reports_The_Slot_Prediction()
        {
            FakeRecordingSlotInventoryPort inventory = new FakeRecordingSlotInventoryPort();
            inventory.Result = RecordingTestData.FreeSlot(18);
            RecordingSaveService service = new RecordingSaveService(
                null, null, inventory, null, null);

            JObject status = JObject.Parse(service.StatusJson());
            Assert.Equal("disabled", (string)status["recording_mode"],
                "an unavailable service stays disabled");
            Assert.Equal(JTokenType.Null, status["configured_slot"].Type,
                "no configured slot");
            Assert.NotNull(status["next_free_slot"], "next_free_slot is still present");
            Assert.Equal(18, (int)status["next_free_slot"],
                "the prediction does not require a policy");
            Assert.Equal(true, (bool)status["next_free_known"], "prediction is known");
            Assert.Equal(false, (bool)status["configured_slot_occupied"],
                "no configured slot can be occupied");
            Assert.Equal(1, inventory.Calls, "status reads the index");
        }

        [Test]
        public static void Service_Status_Reads_Last_Save_Time_Only_For_The_Bound_Slot()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.MetadataPort.LastSaveTime = "2026-11-14T13:30:00.0000000";

            string armed = h.Service.StatusJson();
            Assert.Contains(armed, "\"last_save_time\":null",
                "unbound status does not read any slot preview");
            Assert.Equal(0, h.MetadataPort.Calls, "metadata read waits for a bound slot");

            h.Bind();
            string bound = h.Service.StatusJson();
            Assert.Contains(bound, "\"last_save_time\":\"2026-11-14T13:30:00.0000000\"",
                "bound status reports the dedicated slot's last save time");
            Assert.Equal(1, h.MetadataPort.Calls, "one metadata read");
            Assert.Equal(7, h.MetadataPort.LastSlot, "only the bound slot is read");
        }

        [Test]
        public static void Service_Status_Unreadable_After_Start_Locks()
        {
            RecordingHarness h = RecordingHarness.Create();
            Assert.True(h.Service.ConfirmNewGame().Ok, "start requested");
            h.StatePort.Throw = true;

            string status = h.Service.StatusJson();
            Assert.Contains(status, "\"recording_mode\":\"locked\"", "read failure locks");
            Assert.Contains(status, "\"error\":\"recording_readback_unavailable\"",
                "stable readback error");

            h.StatePort.Throw = false;
            h.StatePort.State = RecordingTestData.InGame(7, "run-a", 3);
            Assert.Contains(h.Service.StatusJson(), "\"recording_mode\":\"locked\"",
                "later state cannot silently unlock");
        }

        [Test]
        public static void Service_Restart_Requires_A_New_Controlled_Bind()
        {
            RecordingHarness first = RecordingHarness.Create();
            first.Bind();

            RecordingHarness restarted = RecordingHarness.Create();
            restarted.StatePort.State = RecordingTestData.InGame(7, "run-a", 3);

            AssertDenied(restarted.Service.SaveRecordingGame(), RecordingSaveErrors.NotBound);
            Assert.Equal(0, restarted.NativePort.SaveCalls, "restart does not reuse the old binding");
            Assert.Contains(restarted.Service.StatusJson(), "\"bound\":false",
                "status starts unbound after restart");
        }

        [Test]
        public static void Service_Menu_Writes_Share_The_Bound_And_Pending_States()
        {
            RecordingHarness h = RecordingHarness.Create();
            Assert.Null(h.Service.CheckMenuWrite(), "armed menu writes are allowed");

            Assert.True(h.Service.ConfirmNewGame().Ok, "start requested");
            Assert.Equal(RecordingSaveErrors.StartPending, h.Service.CheckMenuWrite(),
                "menu writes blocked while start is pending");

            h.StatePort.State = RecordingTestData.InGame(7, "run-a", 3);
            Assert.Equal(RecordingSaveErrors.AlreadyBound, h.Service.CheckMenuWrite(),
                "menu writes blocked after binding");
        }
    }
}
