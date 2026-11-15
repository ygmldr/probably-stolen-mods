using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S4 bridge orchestration for the dedicated recording save slot. It owns no
    /// Unity objects: every game access goes through an injected port so this
    /// class is unit tested with fake slots and fake native callbacks.
    ///
    /// The service never accepts a caller-supplied slot for the recording-only
    /// load/save operations and never reads a save file. All status output is an
    /// explicit whitelist; no save preview, path or other slot is serialized.
    /// </summary>
    public sealed class RecordingSaveService
    {
        private readonly RecordingSlotPolicy policy;
        private readonly IRecordingGameStatePort gamePort;
        private readonly IRecordingSlotInventoryPort inventoryPort;
        private readonly IRecordingNativeSavePort nativePort;
        private readonly INewGameMenuPort menuPort;
        private readonly IRecordingSaveMetadataPort metadataPort;
        // Optional read-only intel: a missing service never affects sleep.
        private readonly ReportService reportService;

        public RecordingSaveService(
            RecordingSlotPolicy policy,
            IRecordingGameStatePort gamePort,
            IRecordingSlotInventoryPort inventoryPort,
            IRecordingNativeSavePort nativePort,
            INewGameMenuPort menuPort,
            IRecordingSaveMetadataPort metadataPort = null,
            ReportService reportService = null)
        {
            this.policy = policy;
            this.gamePort = gamePort;
            this.inventoryPort = inventoryPort;
            this.nativePort = nativePort;
            this.menuPort = menuPort;
            this.metadataPort = metadataPort;
            this.reportService = reportService;
        }

        /// <summary>
        /// True when the session is configured for recording (armed, bound or
        /// locked). Native debug/cheat operations stay reachable only while the
        /// policy is disabled, so a recording run can never mutate money or items
        /// through the debug back door.
        /// </summary>
        public bool RecordingActive
        {
            get
            {
                return policy != null
                    && policy.State != RecordingSlotState.Disabled;
            }
        }

        public string StatusJson()
        {
            JObject row = new JObject();
            row["ok"] = true;

            if (policy == null)
            {
                row["recording_mode"] = "disabled";
                row["acknowledgment"] = JValue.CreateNull();
                row["configured_slot"] = null;
                WriteSlotPrediction(row, InspectForStatus(), null);
                row["bound"] = false;
                row["slot_matches"] = false;
                row["run_matches"] = false;
                row["last_save_time"] = null;
                row["error"] = RecordingSaveErrors.ServiceUnavailable;
                return row.ToString(Formatting.None);
            }

            Refresh();
            RecordingStatus status = policy.Snapshot();
            row["recording_mode"] = StateText(status.State);
            row["acknowledgment"] = status.Acknowledgment == RecordingAcknowledgment.None
                ? JValue.CreateNull()
                : new JValue(AcknowledgmentText(status.Acknowledgment));
            row["configured_slot"] = status.ConfiguredSlot.HasValue
                ? new JValue(status.ConfiguredSlot.Value)
                : JValue.CreateNull();
            WriteSlotPrediction(row, InspectForStatus(), status.ConfiguredSlot);
            row["bound"] = status.Bound;
            if (status.Binding != null)
            {
                row["binding"] = BindingJson(status.Binding);
            }
            row["slot_matches"] = status.SlotMatches;
            row["run_matches"] = status.RunMatches;
            string lastSaveTime = BoundSaveTime(status);
            row["last_save_time"] = lastSaveTime == null
                ? JValue.CreateNull()
                : new JValue(lastSaveTime);
            row["error"] = status.Error == null
                ? JValue.CreateNull()
                : new JValue(status.Error);
            return row.ToString(Formatting.None);
        }

        public string CheckMenuWrite()
        {
            if (policy == null) return RecordingSaveErrors.ServiceUnavailable;

            Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.MenuWrite);
            return decision.Allowed ? null : decision.Error;
        }

        public ActionResult ConfirmNewGame()
        {
            const string op = "confirm_new_game";
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);
            if (nativePort == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            RecordingGameState game = Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.ConfirmNewGame);
            if (!decision.Allowed) return Failure(op, decision.Error);

            NewGameMenuSnapshot menu = ReadMenu();
            if (menu == null) return Failure(op, RecordingSaveErrors.MenuUnavailable);

            if (menu.Prompt != null)
            {
                return ConfirmPrompt(op, menu.Prompt);
            }

            if (!string.IsNullOrEmpty(menu.PhaseError))
            {
                return Failure(op, RecordingSaveErrors.MenuUnavailable);
            }
            if (menu.Phase != NewGameMenuPhase.Difficulty)
            {
                return Failure(op, RecordingSaveErrors.WrongMenuPhase);
            }
            if (menu.StartingGame)
            {
                return Failure(op, RecordingSaveErrors.MenuTransitioning);
            }
            if (menu.Continue == null
                || !menu.Continue.Visible
                || !menu.Continue.Enabled
                || !menu.HasSelection
                || string.IsNullOrEmpty(menu.SelectedId))
            {
                return Failure(op, RecordingSaveErrors.StartUnavailable);
            }

            RecordingSlotInventory inventory = InspectInventory();
            decision = policy.AuthorizeStart(game, inventory);
            if (!decision.Allowed) return Failure(op, decision.Error);

            RecordingNativeResult call;
            try
            {
                call = nativePort == null ? null : nativePort.StartNewGame();
            }
            catch (Exception)
            {
                // The callback may have run before the exception surfaced. Never
                // retry a possible native new game; lock for manual verification.
                policy.MarkStartIssued();
                policy.LockOutcomeUnknown();
                Refresh();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            if (call == null || !call.Called)
            {
                return Failure(op, call == null || string.IsNullOrEmpty(call.Error)
                    ? RecordingSaveErrors.NativeNotCalled
                    : call.Error);
            }

            policy.MarkStartIssued();
            if (!string.IsNullOrEmpty(call.Error))
            {
                Refresh();
                if (policy.State == RecordingSlotState.Locked)
                {
                    return Failure(op, policy.Snapshot().Error, true, true);
                }
                policy.LockOutcomeUnknown();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            Refresh();
            RecordingStatus status = policy.Snapshot();
            if (status.State == RecordingSlotState.Locked)
            {
                return Failure(op, status.Error, true, true);
            }
            return StartSuccess(op, status);
        }

        public ActionResult SaveGame()
        {
            return Save("save_game");
        }

        public ActionResult SaveRecordingGame()
        {
            return Save("save_recording_game");
        }

        public ActionResult LoadGame(int slot)
        {
            return Load("load_game", slot);
        }

        /// <summary>Explicit alias for the legacy op; the caller still cannot choose a slot.</summary>
        public ActionResult LegacyLoadGame(int slot)
        {
            return LoadGame(slot);
        }

        public ActionResult LoadRecordingGame()
        {
            int slot = policy != null && policy.Binding != null
                ? policy.Binding.Slot
                : -1;
            return Load("load_recording_game", slot);
        }

        public ActionResult Sleep(bool dismissWaiting = false)
        {
            const string op = "sleep";
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);
            if (nativePort == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            RecordingGameState game = Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.Sleep);
            if (!decision.Allowed) return Failure(op, decision.Error);
            if (game == null || !game.InGame)
            {
                return Failure(op, RecordingSaveErrors.NotInGame);
            }
            if (!game.DayCycleReadable)
            {
                // A sleep whose day/cash/rent cannot be compared must not be
                // dispatched: afterwards success cannot be verified.
                return Failure(op, RecordingSaveErrors.ReadbackUnavailable);
            }

            // The game only advances the day from CLOSED, and only when no
            // non-dismissable client (the landlord) is queued. Checking here as
            // well is what stops a sleep from skipping the rent collection.
            string blocked = DayEndPolicy.EvaluateSleep(
                true, game.CanEndDay, game.WaitingCustomers, dismissWaiting, game.ShopState);
            if (blocked != null)
            {
                return Failure(op, blocked);
            }

            RecordingNativeResult call;
            try
            {
                call = nativePort.Sleep();
            }
            catch (Exception)
            {
                policy.LockOutcomeUnknown();
                Refresh();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            if (call == null || !call.Called)
            {
                return Failure(op, call == null || string.IsNullOrEmpty(call.Error)
                    ? RecordingSaveErrors.NativeNotCalled
                    : call.Error);
            }

            RecordingGameState afterSleep = Refresh();
            RecordingStatus status = policy.Snapshot();
            if (status.State == RecordingSlotState.Locked)
            {
                return Failure(op, status.Error, true, true);
            }
            if (!string.IsNullOrEmpty(call.Error))
            {
                policy.LockOutcomeUnknown();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            DayCycleReadback readback = DayCycleRules.Evaluate(
                game.DayCycleReadable,
                game.Day,
                game.RentDaysLeft,
                game.Cash,
                afterSleep != null && afterSleep.DayCycleReadable,
                afterSleep == null ? 0 : afterSleep.Day,
                afterSleep == null ? 0 : afterSleep.RentDaysLeft,
                afterSleep == null ? 0 : afterSleep.Cash);
            if (!readback.Readable || !string.IsNullOrEmpty(readback.Error))
            {
                // The native day transition may still be running (the game uses an
                // asynchronous blackscreen sequence). Never report success when
                // the readback is unavailable or a key field regressed.
                policy.LockOutcomeUnknown();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true, readback);
            }

            return SaveSuccess(op, status, false, readback,
                SleepReport(game.Day, afterSleep.Day));
        }

        public ActionResult NewGame()
        {
            const string op = "new_game";
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.NewGame);
            return Failure(op, decision.Error);
        }

        public ActionResult Press()
        {
            const string op = "press";
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.Press);
            return Failure(op, decision.Error);
        }

        private ActionResult ConfirmPrompt(string op, NewGameMenuPrompt prompt)
        {
            if (!prompt.FirstRun || string.IsNullOrEmpty(prompt.Id))
            {
                return Failure(op, RecordingSaveErrors.PromptNotAllowed);
            }
            if (policy.IsPromptConfirmed(prompt.Id))
            {
                return Failure(op, RecordingSaveErrors.PromptAlreadyConfirmed);
            }
            if (nativePort == null)
            {
                return Failure(op, RecordingSaveErrors.ServiceUnavailable);
            }

            RecordingNativeResult call;
            try
            {
                call = nativePort.ConfirmFirstRunPrompt(prompt.Id);
            }
            catch (Exception)
            {
                policy.MarkPromptConfirmed(prompt.Id);
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            if (call == null || !call.Called)
            {
                return Failure(op, call == null || string.IsNullOrEmpty(call.Error)
                    ? RecordingSaveErrors.NativeNotCalled
                    : call.Error);
            }

            policy.MarkPromptConfirmed(prompt.Id);
            if (!string.IsNullOrEmpty(call.Error))
            {
                return Failure(op, RecordingSaveErrors.NativeError, true, true);
            }

            NewGameMenuSnapshot after = ReadMenu();
            if (after == null)
            {
                return Failure(op, RecordingSaveErrors.PromptReadbackMismatch, true);
            }
            if (after.Prompt != null
                && string.Equals(after.Prompt.Id, prompt.Id, StringComparison.Ordinal))
            {
                return Failure(op, RecordingSaveErrors.PromptReadbackMismatch, true);
            }

            RecordingStatus status = policy.Snapshot();
            JObject row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["prompt_confirmed"] = true;
            row["prompt_id"] = prompt.Id;
            row["state"] = StateText(status.State);
            row["pending_error"] = status.Error == null
                ? JValue.CreateNull()
                : new JValue(status.Error);
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private ActionResult Save(string op)
        {
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);
            if (nativePort == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            RecordingGameState game = Refresh();
            RecordingDecision decision = policy.Check(RecordingOperation.SaveGame);
            if (!decision.Allowed) return Failure(op, decision.Error);
            if (game == null || !game.InGame)
            {
                return Failure(op, RecordingSaveErrors.NotInGame);
            }

            RecordingNativeResult call;
            try
            {
                call = nativePort.Save();
            }
            catch (Exception)
            {
                policy.LockOutcomeUnknown();
                Refresh();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            if (call == null || !call.Called)
            {
                return Failure(op, call == null || string.IsNullOrEmpty(call.Error)
                    ? RecordingSaveErrors.NativeNotCalled
                    : call.Error);
            }

            Refresh();
            RecordingStatus status = policy.Snapshot();
            if (status.State == RecordingSlotState.Locked)
            {
                return Failure(op, status.Error, true, true);
            }
            if (!string.IsNullOrEmpty(call.Error))
            {
                policy.LockOutcomeUnknown();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            decision = policy.Check(RecordingOperation.SaveGame);
            if (!decision.Allowed) return Failure(op, decision.Error, true);
            return SaveSuccess(op, status, true);
        }

        private ActionResult Load(string op, int slot)
        {
            if (policy == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);
            if (nativePort == null) return Failure(op, RecordingSaveErrors.ServiceUnavailable);

            RecordingGameState game = Refresh();
            RecordingDecision decision = policy.CheckLoad(slot);
            if (!decision.Allowed) return Failure(op, decision.Error);
            if (game != null && game.InGame)
            {
                return Failure(op, RecordingSaveErrors.AlreadyInGame);
            }

            RecordingNativeResult call;
            try
            {
                call = nativePort.Load(slot);
            }
            catch (Exception)
            {
                policy.MarkLoadIssued();
                policy.LockOutcomeUnknown();
                Refresh();
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            if (call == null || !call.Called)
            {
                return Failure(op, call == null || string.IsNullOrEmpty(call.Error)
                    ? RecordingSaveErrors.NativeNotCalled
                    : call.Error);
            }

            policy.MarkLoadIssued();
            Refresh();
            RecordingStatus status = policy.Snapshot();
            if (status.State == RecordingSlotState.Locked)
            {
                return Failure(op, status.Error, true, true);
            }
            if (!string.IsNullOrEmpty(call.Error))
            {
                return Failure(op, RecordingSaveErrors.OutcomeUnknown, true, true);
            }

            JObject row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["load_requested"] = true;
            row["state"] = StateText(status.State);
            row["bound"] = status.Bound;
            row["load_pending"] = status.LoadPending;
            row["pending_error"] = status.Error == null
                ? JValue.CreateNull()
                : new JValue(status.Error);
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private ActionResult StartSuccess(string op, RecordingStatus status)
        {
            JObject row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["start_requested"] = true;
            row["state"] = StateText(status.State);
            row["bound"] = status.Bound;
            row["start_pending"] = status.StartPending;
            if (status.Binding != null) row["binding"] = BindingJson(status.Binding);
            row["pending_error"] = status.Error == null
                ? JValue.CreateNull()
                : new JValue(status.Error);
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private ActionResult SaveSuccess(
            string op,
            RecordingStatus status,
            bool saved,
            DayCycleReadback readback = null,
            JObject report = null)
        {
            JObject row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["saved"] = saved;
            if (!saved) row["slept"] = true;
            if (readback != null) row["readback"] = JObject.FromObject(FirstWeekJson.Readback(readback));
            if (report != null) row["report"] = report;
            row["state"] = StateText(status.State);
            row["bound"] = status.Bound;
            row["slot_matches"] = status.SlotMatches;
            row["run_matches"] = status.RunMatches;
            string lastSaveTime = BoundSaveTime(status);
            row["last_save_time"] = lastSaveTime == null
                ? JValue.CreateNull()
                : new JValue(lastSaveTime);
            if (status.Binding != null) row["binding"] = BindingJson(status.Binding);
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        /// <summary>
        /// Sleep returns before the game's asynchronous black screen advances
        /// the day. The report therefore degrades to an explicit pending row
        /// instead of attaching yesterday's lines; a failed report read must
        /// never change the sleep result.
        /// </summary>
        private JObject SleepReport(int dayBefore, int dayAfter)
        {
            if (reportService == null)
            {
                return PendingReport(dayAfter <= dayBefore ? dayBefore : dayAfter);
            }

            try
            {
                return JObject.FromObject(reportService.SleepReport(dayBefore, dayAfter));
            }
            catch (Exception)
            {
                return PendingReport(dayAfter <= dayBefore ? dayBefore : dayAfter);
            }
        }

        private static JObject PendingReport(int day)
        {
            JObject row = new JObject();
            row["readable"] = false;
            row["reason"] = DayReportErrors.Unavailable;
            row["day"] = day;
            row["lines"] = new JArray();
            return row;
        }

        private string BoundSaveTime(RecordingStatus status)
        {
            if (status == null || status.Binding == null || metadataPort == null)
            {
                return status == null ? null : status.LastSaveTime;
            }

            try
            {
                string bound = metadataPort.ReadLastSaveTime(status.Binding.Slot);
                return string.IsNullOrEmpty(bound) ? status.LastSaveTime : bound;
            }
            catch (Exception)
            {
                return status.LastSaveTime;
            }
        }

        private static JObject BindingJson(RecordingBinding binding)
        {
            JObject row = new JObject();
            row["slot"] = binding.Slot;
            row["run_id"] = binding.RunId;
            row["run_number"] = binding.RunNumber;
            row["start_type"] = binding.StartType;
            row["start_scenario_id"] = binding.StartScenarioId;
            return row;
        }

        private RecordingGameState Refresh()
        {
            RecordingGameState state;
            if (gamePort == null)
            {
                state = UnavailableGameState();
            }
            else
            {
                try
                {
                    state = gamePort.Read() ?? UnavailableGameState();
                }
                catch (Exception)
                {
                    state = UnavailableGameState();
                }
            }

            policy.Observe(state);
            return state;
        }

        /// <summary>
        /// Read-only slot prediction for the status line. It is inspected on
        /// every call so a save created or deleted in-game is not masked by a
        /// cache, and an unreadable index degrades to null/false without
        /// touching the policy or the status error.
        /// </summary>
        private RecordingSlotInventory InspectForStatus()
        {
            // The port takes the target slot only for the occupancy flag; -1
            // means "no configured slot" while still predicting the next one.
            int targetSlot = policy != null
                && policy.Settings != null
                && policy.Settings.Slot.HasValue
                    ? policy.Settings.Slot.Value
                    : -1;
            return InspectSlot(targetSlot);
        }

        private RecordingSlotInventory InspectSlot(int targetSlot)
        {
            if (inventoryPort == null) return null;
            try
            {
                return inventoryPort.Inspect(targetSlot);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteSlotPrediction(
            JObject row,
            RecordingSlotInventory inventory,
            int? configuredSlot)
        {
            bool readable = inventory != null && inventory.Readable;
            bool known = readable && inventory.NextFreeKnown && inventory.NextFreeSlot >= 0;

            row["next_free_slot"] = known
                ? new JValue(inventory.NextFreeSlot)
                : JValue.CreateNull();
            row["next_free_known"] = known;
            row["configured_slot_occupied"] = readable
                && configuredSlot.HasValue
                && inventory.TargetOccupied;
        }

        private RecordingSlotInventory InspectInventory()
        {
            if (inventoryPort == null)
            {
                return new RecordingSlotInventory
                {
                    Readable = false,
                    Error = RecordingSaveErrors.InventoryUnavailable
                };
            }
            if (!policy.Settings.Slot.HasValue)
            {
                return new RecordingSlotInventory
                {
                    Readable = false,
                    Error = RecordingSaveErrors.SlotInvalid
                };
            }

            RecordingSlotInventory inventory = InspectSlot(policy.Settings.Slot.Value);
            if (inventory == null)
            {
                return new RecordingSlotInventory
                {
                    Readable = false,
                    Error = RecordingSaveErrors.InventoryUnavailable
                };
            }
            return inventory;
        }

        private NewGameMenuSnapshot ReadMenu()
        {
            if (menuPort == null) return null;
            try
            {
                return menuPort.Read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static RecordingGameState UnavailableGameState()
        {
            return new RecordingGameState
            {
                Readable = false,
                Error = RecordingSaveErrors.GameStateUnavailable
            };
        }

        private static ActionResult Failure(
            string op,
            string error,
            bool dispatched = false,
            bool unknown = false,
            DayCycleReadback readback = null)
        {
            JObject row = new JObject();
            row["ok"] = false;
            row["op"] = op;
            row["error"] = string.IsNullOrEmpty(error)
                ? RecordingSaveErrors.Locked
                : error;
            row["dispatched"] = dispatched;
            if (unknown) row["unknown"] = true;
            if (readback != null) row["readback"] = JObject.FromObject(FirstWeekJson.Readback(readback));
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private static string StateText(RecordingSlotState state)
        {
            switch (state)
            {
                case RecordingSlotState.Armed: return "armed";
                case RecordingSlotState.Bound: return "bound";
                case RecordingSlotState.Locked: return "locked";
                default: return "disabled";
            }
        }

        private static string AcknowledgmentText(RecordingAcknowledgment acknowledgment)
        {
            switch (acknowledgment)
            {
                case RecordingAcknowledgment.Isolation: return "isolation";
                case RecordingAcknowledgment.DedicatedSlot: return "dedicated_slot";
                default: return null;
            }
        }
    }
}
