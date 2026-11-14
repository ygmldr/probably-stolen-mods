using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RecordingSavePolicyTests
    {
        private static readonly RecordingOperation[] AllWrites =
        {
            RecordingOperation.NewGame,
            RecordingOperation.LoadGame,
            RecordingOperation.SaveGame,
            RecordingOperation.Sleep,
            RecordingOperation.Press,
            RecordingOperation.MenuWrite,
            RecordingOperation.ConfirmNewGame,
            RecordingOperation.RecordingLoad
        };

        [Test]
        public static void Policy_Default_Disables_All_Recording_Writes()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Off());
            Assert.Equal(RecordingSlotState.Disabled, policy.State, "default state is disabled");

            for (int i = 0; i < AllWrites.Length; i++)
            {
                RecordingDecision decision = policy.Check(AllWrites[i]);
                Assert.False(decision.Allowed, "write must be denied: " + AllWrites[i]);
                Assert.Equal(RecordingSaveErrors.Unconfigured, decision.Error,
                    "stable unconfigured code: " + AllWrites[i]);
            }
        }

        [Test]
        public static void Policy_Valid_Config_But_No_Ack_Stays_Locked()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Unacknowledged());
            Assert.Equal(RecordingSlotState.Disabled, policy.State, "unacknowledged config is disabled");
            Assert.Equal(RecordingAcknowledgment.None, policy.Settings.Acknowledgment,
                "neither declaration counts as an acknowledgment");
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                policy.Check(RecordingOperation.ConfirmNewGame).Error, "stable ack code");
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                policy.Check(RecordingOperation.SaveGame).Error, "ack gates every write");
        }

        [Test]
        public static void Policy_Dedicated_Slot_Ack_Alone_Arms_Recording()
        {
            RecordingSettings settings = RecordingTestData.DedicatedSlot();
            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);

            Assert.Equal(RecordingSlotState.Armed, policy.State,
                "a dedicated-slot-only declaration arms recording");
            Assert.Equal(RecordingAcknowledgment.DedicatedSlot, policy.Settings.Acknowledgment,
                "the actual confirmation method is recorded");
            Assert.False(policy.Settings.IsolationAcknowledged,
                "the dedicated-slot path must not claim an isolated environment");
            Assert.True(policy.Check(RecordingOperation.MenuWrite).Allowed,
                "menu preparation is allowed");
            Assert.True(policy.Check(RecordingOperation.ConfirmNewGame).Allowed,
                "the controlled start is allowed");
            Assert.Equal(RecordingSaveErrors.NotBound,
                policy.Check(RecordingOperation.SaveGame).Error,
                "saving still requires a bound dedicated slot");
        }

        [Test]
        public static void Policy_Isolation_Ack_Alone_Still_Arms_Recording()
        {
            RecordingSettings settings = RecordingTestData.Valid();
            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);

            Assert.Equal(RecordingSlotState.Armed, policy.State,
                "isolation-only acknowledgement stays a valid regression path");
            Assert.Equal(RecordingAcknowledgment.Isolation, policy.Settings.Acknowledgment,
                "the isolation declaration is recorded");
            Assert.False(policy.Settings.DedicatedSlotAcknowledged,
                "the isolation path must not claim a dedicated-slot confirmation");
        }

        [Test]
        public static void Policy_Both_Acks_Arm_And_Report_Isolation_First()
        {
            RecordingSettings settings = RecordingTestData.BothAcks();
            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);

            Assert.Equal(RecordingSlotState.Armed, policy.State, "both declarations are accepted");
            Assert.True(policy.Settings.IsolationAcknowledged, "isolation flag preserved");
            Assert.True(policy.Settings.DedicatedSlotAcknowledged,
                "dedicated-slot flag preserved");
            Assert.Equal(RecordingAcknowledgment.Isolation, policy.Settings.Acknowledgment,
                "isolation wins when both declarations are present");
        }

        [Test]
        public static void Policy_Valid_Config_Without_Slot_Is_Rejected()
        {
            RecordingSettings settings = RecordingSettings.Create("on", null, true);
            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);
            Assert.Equal(RecordingSlotState.Disabled, policy.State, "missing slot is disabled");
            Assert.Equal(RecordingSaveErrors.SlotInvalid,
                policy.Check(RecordingOperation.MenuWrite).Error, "stable slot code");
        }

        [Test]
        public static void Policy_Armed_Allows_Menu_But_Not_Save_Load_Or_Sleep()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());

            Assert.Equal(RecordingSlotState.Armed, policy.State, "valid config is armed");
            Assert.True(policy.Check(RecordingOperation.MenuWrite).Allowed, "menu prep is allowed");
            Assert.True(policy.Check(RecordingOperation.ConfirmNewGame).Allowed, "controlled start is allowed");

            Assert.Equal(RecordingSaveErrors.NotBound,
                policy.Check(RecordingOperation.SaveGame).Error, "save before bind is denied");
            Assert.Equal(RecordingSaveErrors.NotBound,
                policy.Check(RecordingOperation.RecordingLoad).Error, "load before bind is denied");
            Assert.Equal(RecordingSaveErrors.NotBound,
                policy.Check(RecordingOperation.Sleep).Error, "sleep before bind is denied");
            Assert.Equal(RecordingSaveErrors.NewGameForbidden,
                policy.Check(RecordingOperation.NewGame).Error,
                "legacy new_game is never the controlled entry");
            Assert.Equal(RecordingSaveErrors.PressForbidden,
                policy.Check(RecordingOperation.Press).Error, "generic press is denied");
        }

        [Test]
        public static void Policy_Start_Checks_Inventory_And_Consumes_One_Permit()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            RecordingGameState game = RecordingTestData.MainMenu();
            RecordingSlotInventory inventory = RecordingTestData.FreeSlot(7);

            RecordingDecision allowed = policy.AuthorizeStart(game, inventory);
            Assert.True(allowed.Allowed, "free next slot allows one start: " + allowed.Error);

            policy.MarkStartIssued();
            Assert.Equal(RecordingSaveErrors.StartPending,
                policy.Check(RecordingOperation.ConfirmNewGame).Error,
                "a second start is refused while the first may have succeeded");
            Assert.Equal(RecordingSaveErrors.StartPending,
                policy.Check(RecordingOperation.SaveGame).Error,
                "no writes are allowed before start readback");
            Assert.Equal(RecordingSaveErrors.StartPending,
                policy.Check(RecordingOperation.MenuWrite).Error,
                "menu writes are also blocked while start is pending");
        }

        [Test]
        public static void Policy_Start_Refuses_Occupied_Target_Slot()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            RecordingSlotInventory inventory = RecordingTestData.FreeSlot(7);
            inventory.TargetOccupied = true;

            RecordingDecision decision = policy.AuthorizeStart(RecordingTestData.MainMenu(), inventory);
            Assert.False(decision.Allowed, "occupied slot must be refused");
            Assert.Equal(RecordingSaveErrors.SlotOccupied, decision.Error, "stable occupied code");
        }

        [Test]
        public static void Policy_Start_Refuses_When_Target_Is_Not_Next_Free()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            RecordingSlotInventory inventory = RecordingTestData.FreeSlot(8);

            RecordingDecision decision = policy.AuthorizeStart(RecordingTestData.MainMenu(), inventory);
            Assert.False(decision.Allowed, "unpredictable allocation must be refused");
            Assert.Equal(RecordingSaveErrors.NotNextFree, decision.Error,
                "stable not-next-free code");
        }

        [Test]
        public static void Policy_Start_Refuses_Unknown_Inventory()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());

            RecordingSlotInventory unknown = RecordingTestData.FreeSlot(7);
            unknown.NextFreeKnown = false;
            Assert.Equal(RecordingSaveErrors.AllocationUnknown,
                policy.AuthorizeStart(RecordingTestData.MainMenu(), unknown).Error,
                "unknown allocation is fail-closed");

            RecordingSlotInventory unreadable = RecordingTestData.FreeSlot(7);
            unreadable.Readable = false;
            Assert.Equal(RecordingSaveErrors.InventoryUnavailable,
                policy.AuthorizeStart(RecordingTestData.MainMenu(), unreadable).Error,
                "unreadable inventory is fail-closed");
        }

        [Test]
        public static void Policy_Start_Refuses_When_Already_In_Game()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            RecordingDecision decision = policy.AuthorizeStart(
                RecordingTestData.InGame(),
                RecordingTestData.FreeSlot(7));
            Assert.Equal(RecordingSaveErrors.AlreadyInGame, decision.Error,
                "starting from an existing game is refused");
        }

        [Test]
        public static void Policy_Start_Requires_Known_Start_Intent()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            RecordingGameState game = RecordingTestData.MainMenu();
            game.NewGameStartType = string.Empty;

            RecordingDecision decision = policy.AuthorizeStart(game, RecordingTestData.FreeSlot(7));
            Assert.Equal(RecordingSaveErrors.StartIntentUnavailable, decision.Error,
                "missing start type is fail-closed");
        }

        [Test]
        public static void Policy_Start_Readback_Mismatch_Locks_Without_Retry()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();

            policy.Observe(RecordingTestData.InGame(8));
            Assert.Equal(RecordingSlotState.Locked, policy.State, "wrong slot locks immediately");
            Assert.Equal(RecordingSaveErrors.SlotMismatch,
                policy.Snapshot().Error, "status keeps the specific mismatch cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.SaveGame).Error,
                "all operations use the stable locked code");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.ConfirmNewGame).Error,
                "locked state denies another start");

            policy.Observe(RecordingTestData.InGame(7));
            Assert.Equal(RecordingSlotState.Locked, policy.State,
                "a later matching readback cannot silently unlock");
        }

        [Test]
        public static void Policy_Binding_Records_Fingerprint_And_Enables_Bound_Writes()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();

            policy.Observe(RecordingTestData.InGame(7, "run-a", 3));
            Assert.Equal(RecordingSlotState.Bound, policy.State, "matching readback binds");
            Assert.NotNull(policy.Binding, "binding fingerprint exists");
            Assert.Equal(7, policy.Binding.Slot, "bound slot");
            Assert.Equal("run-a", policy.Binding.RunId, "bound run id");
            Assert.Equal(3, policy.Binding.RunNumber, "bound run number");
            Assert.Equal("APPRENTICE", policy.Binding.StartType, "bound start type");
            Assert.Equal("apprentice", policy.Binding.StartScenarioId, "bound scenario");

            Assert.True(policy.Check(RecordingOperation.SaveGame).Allowed, "bound save allowed");
            Assert.True(policy.Check(RecordingOperation.RecordingSave).Allowed, "recording save allowed");
            Assert.True(policy.Check(RecordingOperation.Sleep).Allowed, "bound sleep allowed");
            Assert.True(policy.CheckLoad(7).Allowed, "bound slot load allowed");
            Assert.Equal(RecordingSaveErrors.SlotNotBound,
                policy.CheckLoad(8).Error, "other slot load denied");
            Assert.Equal(RecordingSaveErrors.AlreadyBound,
                policy.Check(RecordingOperation.ConfirmNewGame).Error, "second start denied");
            Assert.Equal(RecordingSaveErrors.AlreadyBound,
                policy.Check(RecordingOperation.MenuWrite).Error, "menu writes denied after bind");
            Assert.Equal(RecordingSaveErrors.PressForbidden,
                policy.Check(RecordingOperation.Press).Error, "press stays denied after bind");
        }

        [Test]
        public static void Policy_Bound_Run_Mismatch_Locks()
        {
            RecordingSlotPolicy policy = BoundPolicy();
            policy.Observe(RecordingTestData.InGame(7, "run-b"));

            Assert.Equal(RecordingSlotState.Locked, policy.State, "different run id locks");
            Assert.Equal(RecordingSaveErrors.RunMismatch,
                policy.Snapshot().Error, "status keeps the specific run-mismatch cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.SaveGame).Error, "stable locked code");
        }

        [Test]
        public static void Policy_Bound_Slot_Mismatch_Locks()
        {
            RecordingSlotPolicy policy = BoundPolicy();
            policy.Observe(RecordingTestData.InGame(8, "run-a"));

            Assert.Equal(RecordingSlotState.Locked, policy.State, "different slot locks");
            Assert.Equal(RecordingSaveErrors.SlotMismatch,
                policy.Snapshot().Error, "status keeps the specific slot-mismatch cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.Sleep).Error, "stable locked code");
        }

        [Test]
        public static void Policy_Start_Identity_Mismatch_Locks()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();

            policy.Observe(RecordingTestData.InGame(7, "run-a", 3, "SCAV", "scav"));
            Assert.Equal(RecordingSlotState.Locked, policy.State, "wrong start type locks");
            Assert.Equal(RecordingSaveErrors.StartIdentityMismatch,
                policy.Snapshot().Error, "status keeps the specific identity cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.SaveGame).Error, "stable locked code");
        }

        [Test]
        public static void Policy_Start_Missing_Run_Identity_Locks()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();

            policy.Observe(RecordingTestData.InGame(7, string.Empty));
            Assert.Equal(RecordingSlotState.Locked, policy.State, "empty run id locks");
            Assert.Equal(RecordingSaveErrors.RunIdentityMissing,
                policy.Snapshot().Error, "status keeps the specific missing-run cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.SaveGame).Error, "stable locked code");
        }

        [Test]
        public static void Policy_Unreadable_Readback_After_Start_Locks()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();

            policy.Observe(RecordingTestData.Unavailable());
            Assert.Equal(RecordingSlotState.Locked, policy.State, "unreadable readback locks");
            Assert.Equal(RecordingSaveErrors.ReadbackUnavailable,
                policy.Snapshot().Error, "status keeps the specific readback cause");
            Assert.Equal(RecordingSaveErrors.Locked,
                policy.Check(RecordingOperation.SaveGame).Error, "stable locked code");
        }

        [Test]
        public static void Policy_Load_Pending_Blocks_Writes_Until_Run_Is_Verified()
        {
            RecordingSlotPolicy policy = BoundPolicy();
            policy.MarkLoadIssued();

            Assert.Equal(RecordingSaveErrors.LoadPending,
                policy.Check(RecordingOperation.SaveGame).Error, "pending load blocks save");

            policy.Observe(RecordingTestData.MainMenu());
            Assert.Equal(RecordingSlotState.Bound, policy.State, "main menu does not unbind");
            Assert.Equal(RecordingSaveErrors.LoadPending,
                policy.Check(RecordingOperation.Sleep).Error, "still pending at the menu");

            policy.Observe(RecordingTestData.InGame(7, "run-a", 3));
            Assert.Equal(RecordingSlotState.Bound, policy.State, "same run verifies load");
            Assert.True(policy.Check(RecordingOperation.SaveGame).Allowed, "save allowed after load proof");
        }

        [Test]
        public static void Policy_Restart_Starts_Armed_And_Requires_A_New_Bind()
        {
            RecordingSlotPolicy first = BoundPolicy();
            Assert.Equal(RecordingSlotState.Bound, first.State, "first instance bound");

            RecordingSlotPolicy restarted = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.Equal(RecordingSlotState.Armed, restarted.State, "new instance is armed only");
            Assert.Null(restarted.Binding, "old binding is not reused");
            Assert.Equal(RecordingSaveErrors.NotBound,
                restarted.Check(RecordingOperation.SaveGame).Error, "restart blocks writes");

            restarted.Observe(RecordingTestData.InGame(7, "run-a", 3));
            Assert.Equal(RecordingSlotState.Armed, restarted.State,
                "observing an existing run must not auto-bind");
            Assert.Equal(RecordingSaveErrors.NotBound,
                restarted.Check(RecordingOperation.SaveGame).Error,
                "existing run still requires the controlled start");
        }

        [Test]
        public static void Policy_Does_Not_Auto_Lock_Armed_Menu_When_State_Is_Unreadable()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            policy.Observe(RecordingTestData.Unavailable());

            Assert.Equal(RecordingSlotState.Armed, policy.State, "pre-start read failure is not a bind");
            Assert.True(policy.Check(RecordingOperation.MenuWrite).Allowed,
                "menu preparation can continue, but start itself is checked");
            Assert.Equal(RecordingSaveErrors.GameStateUnavailable,
                policy.AuthorizeStart(RecordingTestData.Unavailable(),
                    RecordingTestData.FreeSlot(7)).Error, "start refuses unknown game state");
        }

        private static RecordingSlotPolicy BoundPolicy()
        {
            RecordingSlotPolicy policy = new RecordingSlotPolicy(RecordingTestData.Valid());
            Assert.True(policy.AuthorizeStart(
                RecordingTestData.MainMenu(),
                RecordingTestData.FreeSlot(7)).Allowed, "start authorized");
            policy.MarkStartIssued();
            policy.Observe(RecordingTestData.InGame(7, "run-a", 3));
            Assert.Equal(RecordingSlotState.Bound, policy.State, "fixture bound");
            return policy;
        }
    }
}
