using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Stable error codes for the S4 recording-slot policy. Callers branch on
    /// these strings; changing one is a protocol change.
    /// </summary>
    public static class RecordingSaveErrors
    {
        public const string Unconfigured = "recording_unconfigured";
        public const string SlotInvalid = "recording_slot_invalid";
        public const string IsolationUnacknowledged = "recording_isolation_unacknowledged";
        public const string Locked = "recording_locked";
        public const string NotBound = "recording_not_bound";
        public const string AlreadyBound = "recording_already_bound";
        public const string SlotNotBound = "recording_slot_not_bound";
        public const string StartPending = "recording_start_pending";
        public const string LoadPending = "recording_load_pending";
        public const string NewGameForbidden = "recording_new_game_forbidden";
        public const string ActWrapperForbidden = "recording_act_wrapper_forbidden";
        public const string PressForbidden = "recording_press_forbidden";
        public const string DebugForbidden = "recording_debug_forbidden";
        public const string SlotMismatch = "recording_slot_mismatch";
        public const string RunMismatch = "recording_run_mismatch";
        public const string StartIdentityMismatch = "recording_start_identity_mismatch";
        public const string RunIdentityMissing = "recording_run_identity_missing";
        public const string SlotOccupied = "recording_slot_occupied";
        public const string InventoryUnavailable = "recording_slot_inventory_unavailable";
        public const string AllocationUnknown = "recording_slot_allocation_unknown";
        public const string NotNextFree = "recording_slot_not_next_free";
        public const string GameStateUnavailable = "recording_game_state_unavailable";
        public const string ReadbackUnavailable = "recording_readback_unavailable";
        public const string NotInGame = "recording_not_in_game";
        public const string AlreadyInGame = "recording_already_in_game";
        public const string MenuUnavailable = "recording_menu_unavailable";
        public const string WrongMenuPhase = "recording_wrong_menu_phase";
        public const string MenuTransitioning = "recording_menu_transitioning";
        public const string StartUnavailable = "recording_start_unavailable";
        public const string PromptNotAllowed = "recording_prompt_not_allowed";
        public const string PromptAlreadyConfirmed = "recording_prompt_already_confirmed";
        public const string PromptReadbackMismatch = "recording_prompt_readback_mismatch";
        public const string StartIntentUnavailable = "recording_start_intent_unavailable";
        public const string NativeNotCalled = "recording_native_not_called";
        public const string NativeError = "recording_native_error";
        public const string OutcomeUnknown = "recording_outcome_unknown";
        public const string ServiceUnavailable = "recording_service_unavailable";
    }

    public enum RecordingSlotState
    {
        Disabled,
        Armed,
        Bound,
        Locked
    }

    /// <summary>
    /// The user declaration that actually unlocked recording writes. Isolation
    /// is reported first when both declarations are present.
    /// </summary>
    public enum RecordingAcknowledgment
    {
        None,
        Isolation,
        DedicatedSlot
    }

    public enum RecordingOperation
    {
        NewGame,
        LoadGame,
        SaveGame,
        Sleep,
        Press,
        MenuWrite,
        ConfirmNewGame,
        RecordingSave,
        RecordingLoad
    }

    /// <summary>
    /// User-provided recording settings, resolved from bridge.conf only. There is
    /// intentionally no factory that copies the installed game identity or the
    /// currently active save slot into these fields.
    /// </summary>
    public sealed class RecordingSettings
    {
        public const string ModeRecording = "recording";
        public const string ModeOn = "on";

        public string Mode { get; private set; }
        public int? Slot { get; private set; }
        public bool IsolationAcknowledged { get; private set; }
        public bool DedicatedSlotAcknowledged { get; private set; }

        /// <summary>
        /// Confirmation method that unlocked writes. None when recording is
        /// disabled, the slot is invalid, or neither confirmation was given.
        /// </summary>
        public RecordingAcknowledgment Acknowledgment { get; private set; }
        public string Error { get; private set; }

        private RecordingSettings()
        {
        }

        public static RecordingSettings Create(
            string mode,
            int? slot,
            bool isolationAcknowledged)
        {
            return Create(mode, slot, isolationAcknowledged, false);
        }

        public static RecordingSettings Create(
            string mode,
            int? slot,
            bool isolationAcknowledged,
            bool dedicatedSlotAcknowledged)
        {
            string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized != ModeRecording && normalized != ModeOn)
            {
                return Invalid(normalized, slot, isolationAcknowledged,
                    dedicatedSlotAcknowledged, RecordingSaveErrors.Unconfigured);
            }

            if (!slot.HasValue || slot.Value < 0)
            {
                return Invalid(normalized, slot, isolationAcknowledged,
                    dedicatedSlotAcknowledged, RecordingSaveErrors.SlotInvalid);
            }

            if (!isolationAcknowledged && !dedicatedSlotAcknowledged)
            {
                return Invalid(normalized, slot, isolationAcknowledged,
                    dedicatedSlotAcknowledged, RecordingSaveErrors.IsolationUnacknowledged);
            }

            return new RecordingSettings
            {
                Mode = normalized,
                Slot = slot,
                IsolationAcknowledged = isolationAcknowledged,
                DedicatedSlotAcknowledged = dedicatedSlotAcknowledged,
                Acknowledgment = isolationAcknowledged
                    ? RecordingAcknowledgment.Isolation
                    : RecordingAcknowledgment.DedicatedSlot,
                Error = null
            };
        }

        public static RecordingSettings From(BridgeConfig config)
        {
            if (config == null)
            {
                return Invalid(string.Empty, null, false, false,
                    RecordingSaveErrors.Unconfigured);
            }

            string normalized = (config.RecordingMode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized != ModeRecording && normalized != ModeOn)
            {
                return Invalid(normalized, config.RecordingSlot,
                    config.RecordingIsolationAck, config.RecordingDedicatedSlotAck,
                    RecordingSaveErrors.Unconfigured);
            }

            if (config.RecordingSlotInvalid
                || !config.RecordingSlot.HasValue
                || config.RecordingSlot.Value < 0)
            {
                return Invalid(normalized, config.RecordingSlot,
                    config.RecordingIsolationAck, config.RecordingDedicatedSlotAck,
                    RecordingSaveErrors.SlotInvalid);
            }

            if (!config.RecordingIsolationAck && !config.RecordingDedicatedSlotAck)
            {
                return Invalid(normalized, config.RecordingSlot,
                    false, false, RecordingSaveErrors.IsolationUnacknowledged);
            }

            return new RecordingSettings
            {
                Mode = normalized,
                Slot = config.RecordingSlot,
                IsolationAcknowledged = config.RecordingIsolationAck,
                DedicatedSlotAcknowledged = config.RecordingDedicatedSlotAck,
                Acknowledgment = config.RecordingIsolationAck
                    ? RecordingAcknowledgment.Isolation
                    : RecordingAcknowledgment.DedicatedSlot,
                Error = null
            };
        }

        private static RecordingSettings Invalid(
            string mode,
            int? slot,
            bool isolationAcknowledged,
            bool dedicatedSlotAcknowledged,
            string error)
        {
            return new RecordingSettings
            {
                Mode = mode,
                Slot = slot,
                IsolationAcknowledged = isolationAcknowledged,
                DedicatedSlotAcknowledged = dedicatedSlotAcknowledged,
                Acknowledgment = RecordingAcknowledgment.None,
                Error = error
            };
        }
    }

    public sealed class RecordingBinding
    {
        public int Slot;
        public string RunId;
        public int RunNumber;
        public string StartType;
        public string StartScenarioId;
    }

    /// <summary>
    /// Read-only projection of the game's authoritative save identity. The game
    /// adapter never loads a save file to fill this object.
    /// </summary>
    public sealed class RecordingGameState
    {
        public bool Readable;
        public string Error;
        public bool InGame;
        public int SaveSlotId = -1;
        public string RunId = string.Empty;
        public int RunNumber;
        public string StartType = string.Empty;
        public string StartScenarioId = string.Empty;
        public string LastSaveTime;
        public bool DayCycleReadable;
        public int Day;

        /// <summary>Customers queued; sleeping with them is what skipped the landlord.</summary>
        public int WaitingCustomers;

        /// <summary>PlayerStore.storeState name; sleep requires CLOSED.</summary>
        public string ShopState;

        /// <summary>PlayerStore.CanEndDay(): false while a non-dismissable client waits.</summary>
        public bool CanEndDay;

        public int RentDaysLeft;
        public int Cash;
        public string NewGameStartType = string.Empty;
        public string NewGameStartScenarioId = string.Empty;
        public string NewGameDifficultyId = string.Empty;
    }

    /// <summary>
    /// Read-only slot inventory. TargetOccupied is true when the configured slot
    /// is in the save index or a slot file exists. NextFreeSlot mirrors the
    /// game's NextFreeSlotId starting point and existence check; it never writes
    /// the index or increments the generator.
    /// </summary>
    public sealed class RecordingSlotInventory
    {
        public bool Readable;
        public string Error;
        public bool TargetOccupied;
        public bool NextFreeKnown;
        public int NextFreeSlot = -1;
    }

    public sealed class RecordingNativeResult
    {
        public bool Called;
        public string Error;

        public static RecordingNativeResult NativeCalled()
        {
            return new RecordingNativeResult { Called = true };
        }

        public static RecordingNativeResult NativeCalledWithError(string error)
        {
            return new RecordingNativeResult { Called = true, Error = error };
        }

        public static RecordingNativeResult NativeNotCalled(string error)
        {
            return new RecordingNativeResult { Called = false, Error = error };
        }
    }

    public sealed class RecordingDecision
    {
        public bool Allowed;
        public string Error;

        public static RecordingDecision Allow()
        {
            return new RecordingDecision { Allowed = true };
        }

        public static RecordingDecision Deny(string error)
        {
            return new RecordingDecision
            {
                Allowed = false,
                Error = string.IsNullOrEmpty(error) ? RecordingSaveErrors.Locked : error
            };
        }
    }

    public sealed class RecordingStatus
    {
        public RecordingSlotState State;
        public string Error;
        public int? ConfiguredSlot;
        public RecordingAcknowledgment Acknowledgment;
        public bool Bound;
        public RecordingBinding Binding;
        public bool InGame;
        public bool SlotMatches;
        public bool RunMatches;
        public string LastSaveTime;
        public bool StartPending;
        public bool LoadPending;
    }

    public interface IRecordingGameStatePort
    {
        RecordingGameState Read();
    }

    public interface IRecordingSlotInventoryPort
    {
        RecordingSlotInventory Inspect(int slot);
    }

    /// <summary>
    /// Only the bound dedicated slot's index metadata may be read for the status
    /// "last save time". The service never calls this while unbound.
    /// </summary>
    public interface IRecordingSaveMetadataPort
    {
        string ReadLastSaveTime(int slot);
    }

    public interface IRecordingNativeSavePort
    {
        RecordingNativeResult Save();
        RecordingNativeResult Load(int slot);
        RecordingNativeResult Sleep();
        RecordingNativeResult StartNewGame();
        RecordingNativeResult ConfirmFirstRunPrompt(string promptId);
    }

    /// <summary>
    /// S4 pure state machine. Disabled -> Armed (explicit config) -> Bound (one
    /// controlled native start plus authoritative readback) or Locked (any
    /// mismatch). A new instance never reuses a previous binding and never
    /// observes its way into Bound: only a consumed ConfirmNewGame permit can
    /// bind the slot.
    /// </summary>
    public sealed class RecordingSlotPolicy
    {
        private readonly RecordingSettings settings;
        private readonly HashSet<string> confirmedPrompts =
            new HashSet<string>(StringComparer.Ordinal);
        private RecordingSlotState state;
        private string lockedReason;
        private RecordingBinding binding;
        private bool startIssued;
        private bool loadIssued;
        private string startIntentType = string.Empty;
        private string startIntentScenario = string.Empty;
        private RecordingGameState lastObserved;

        public RecordingSlotPolicy(RecordingSettings settings)
        {
            this.settings = settings
                ?? RecordingSettings.Create("off", null, false);
            state = this.settings.Error == null
                ? RecordingSlotState.Armed
                : RecordingSlotState.Disabled;
            lockedReason = this.settings.Error;
        }

        public RecordingSlotState State
        {
            get { return state; }
        }

        public RecordingSettings Settings
        {
            get { return settings; }
        }

        public RecordingBinding Binding
        {
            get { return binding; }
        }

        public bool StartPending
        {
            get { return state == RecordingSlotState.Armed && startIssued; }
        }

        public bool LoadPending
        {
            get { return state == RecordingSlotState.Bound && loadIssued; }
        }

        public RecordingDecision Check(RecordingOperation operation)
        {
            if (state == RecordingSlotState.Disabled)
            {
                return RecordingDecision.Deny(settings.Error ?? RecordingSaveErrors.Unconfigured);
            }
            if (state == RecordingSlotState.Locked)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.Locked);
            }
            if (startIssued)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.StartPending);
            }
            if (loadIssued)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.LoadPending);
            }

            if (state == RecordingSlotState.Armed)
            {
                switch (operation)
                {
                    case RecordingOperation.MenuWrite:
                    case RecordingOperation.ConfirmNewGame:
                        return RecordingDecision.Allow();
                    case RecordingOperation.NewGame:
                        return RecordingDecision.Deny(RecordingSaveErrors.NewGameForbidden);
                    case RecordingOperation.Press:
                        return RecordingDecision.Deny(RecordingSaveErrors.PressForbidden);
                    default:
                        return RecordingDecision.Deny(RecordingSaveErrors.NotBound);
                }
            }

            if (state == RecordingSlotState.Bound)
            {
                switch (operation)
                {
                    case RecordingOperation.SaveGame:
                    case RecordingOperation.RecordingSave:
                    case RecordingOperation.Sleep:
                    case RecordingOperation.LoadGame:
                    case RecordingOperation.RecordingLoad:
                        return RecordingDecision.Allow();
                    case RecordingOperation.NewGame:
                        return RecordingDecision.Deny(RecordingSaveErrors.AlreadyBound);
                    case RecordingOperation.ConfirmNewGame:
                    case RecordingOperation.MenuWrite:
                        return RecordingDecision.Deny(RecordingSaveErrors.AlreadyBound);
                    case RecordingOperation.Press:
                        return RecordingDecision.Deny(RecordingSaveErrors.PressForbidden);
                    default:
                        return RecordingDecision.Deny(RecordingSaveErrors.NotBound);
                }
            }

            return RecordingDecision.Deny(RecordingSaveErrors.Locked);
        }

        public RecordingDecision CheckLoad(int requestedSlot)
        {
            if (state == RecordingSlotState.Disabled)
            {
                return RecordingDecision.Deny(settings.Error ?? RecordingSaveErrors.Unconfigured);
            }
            if (state == RecordingSlotState.Locked)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.Locked);
            }
            if (state != RecordingSlotState.Bound || binding == null)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.NotBound);
            }
            if (requestedSlot != binding.Slot)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.SlotNotBound);
            }
            return Check(RecordingOperation.LoadGame);
        }

        public RecordingDecision AuthorizeStart(
            RecordingGameState game,
            RecordingSlotInventory inventory)
        {
            if (state == RecordingSlotState.Disabled)
            {
                return RecordingDecision.Deny(settings.Error ?? RecordingSaveErrors.Unconfigured);
            }
            if (state == RecordingSlotState.Locked)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.Locked);
            }
            if (startIssued)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.StartPending);
            }
            if (state == RecordingSlotState.Bound)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.AlreadyBound);
            }
            if (game == null || !game.Readable)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.GameStateUnavailable);
            }
            if (game.InGame)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.AlreadyInGame);
            }
            if (string.IsNullOrEmpty(game.NewGameStartType)
                || string.Equals(game.NewGameStartType, "NONE", StringComparison.Ordinal))
            {
                return RecordingDecision.Deny(RecordingSaveErrors.StartIntentUnavailable);
            }
            if (inventory == null || !inventory.Readable)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.InventoryUnavailable);
            }
            if (inventory.TargetOccupied)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.SlotOccupied);
            }
            if (!inventory.NextFreeKnown || inventory.NextFreeSlot < 0)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.AllocationUnknown);
            }
            if (!settings.Slot.HasValue || inventory.NextFreeSlot != settings.Slot.Value)
            {
                return RecordingDecision.Deny(RecordingSaveErrors.NotNextFree);
            }

            startIntentType = game.NewGameStartType;
            startIntentScenario = game.NewGameStartScenarioId ?? string.Empty;
            return RecordingDecision.Allow();
        }

        public void MarkStartIssued()
        {
            if (state == RecordingSlotState.Armed) startIssued = true;
        }

        public void MarkLoadIssued()
        {
            if (state == RecordingSlotState.Bound) loadIssued = true;
        }

        public void MarkPromptConfirmed(string promptId)
        {
            if (!string.IsNullOrEmpty(promptId)) confirmedPrompts.Add(promptId);
        }

        public bool IsPromptConfirmed(string promptId)
        {
            return !string.IsNullOrEmpty(promptId) && confirmedPrompts.Contains(promptId);
        }

        public void LockOutcomeUnknown()
        {
            Lock(RecordingSaveErrors.OutcomeUnknown);
        }

        /// <summary>
        /// Accepts a read-only game-state observation. A mismatch during binding
        /// or while bound is terminal for this process: it never rolls back and
        /// never retries the native write that may have succeeded.
        /// </summary>
        public void Observe(RecordingGameState game)
        {
            lastObserved = game;

            if (state == RecordingSlotState.Disabled
                || state == RecordingSlotState.Locked)
            {
                return;
            }

            if (game == null || !game.Readable)
            {
                if (state == RecordingSlotState.Bound || startIssued || loadIssued)
                {
                    Lock(RecordingSaveErrors.ReadbackUnavailable);
                }
                return;
            }

            if (state == RecordingSlotState.Armed)
            {
                if (!startIssued) return;

                if (!game.InGame)
                {
                    // Scene load / first frame has not materialized the store yet.
                    return;
                }

                if (!settings.Slot.HasValue || game.SaveSlotId != settings.Slot.Value)
                {
                    Lock(RecordingSaveErrors.SlotMismatch);
                    return;
                }
                if (string.IsNullOrEmpty(game.RunId) || game.RunNumber <= 0)
                {
                    Lock(RecordingSaveErrors.RunIdentityMissing);
                    return;
                }
                if (!string.IsNullOrEmpty(startIntentType)
                    && !string.Equals(game.StartType ?? string.Empty,
                        startIntentType, StringComparison.Ordinal))
                {
                    Lock(RecordingSaveErrors.StartIdentityMismatch);
                    return;
                }
                if (!string.Equals(game.StartScenarioId ?? string.Empty,
                    startIntentScenario ?? string.Empty, StringComparison.Ordinal))
                {
                    Lock(RecordingSaveErrors.StartIdentityMismatch);
                    return;
                }

                binding = new RecordingBinding
                {
                    Slot = game.SaveSlotId,
                    RunId = game.RunId,
                    RunNumber = game.RunNumber,
                    StartType = game.StartType ?? string.Empty,
                    StartScenarioId = game.StartScenarioId ?? string.Empty
                };
                state = RecordingSlotState.Bound;
                startIssued = false;
                lockedReason = null;
                return;
            }

            if (state == RecordingSlotState.Bound)
            {
                if (!game.InGame)
                {
                    // The main menu is the only state from which Load is allowed.
                    return;
                }
                if (binding == null || game.SaveSlotId != binding.Slot)
                {
                    Lock(RecordingSaveErrors.SlotMismatch);
                    return;
                }
                if (string.IsNullOrEmpty(game.RunId))
                {
                    Lock(RecordingSaveErrors.RunIdentityMissing);
                    return;
                }
                if (!string.Equals(game.RunId, binding.RunId, StringComparison.Ordinal)
                    || game.RunNumber != binding.RunNumber)
                {
                    Lock(RecordingSaveErrors.RunMismatch);
                    return;
                }
                if (!string.Equals(game.StartType ?? string.Empty,
                        binding.StartType ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(game.StartScenarioId ?? string.Empty,
                        binding.StartScenarioId ?? string.Empty, StringComparison.Ordinal))
                {
                    Lock(RecordingSaveErrors.StartIdentityMismatch);
                    return;
                }

                loadIssued = false;
            }
        }

        public RecordingStatus Snapshot()
        {
            RecordingStatus status = new RecordingStatus
            {
                State = state,
                Error = CurrentError(),
                ConfiguredSlot = settings.Slot,
                Acknowledgment = settings.Acknowledgment,
                Bound = state == RecordingSlotState.Bound,
                Binding = binding,
                StartPending = startIssued,
                LoadPending = loadIssued
            };

            if (lastObserved != null && lastObserved.Readable && lastObserved.InGame)
            {
                status.InGame = true;
                if (binding != null)
                {
                    status.SlotMatches = lastObserved.SaveSlotId == binding.Slot;
                    status.RunMatches = string.Equals(
                        lastObserved.RunId ?? string.Empty,
                        binding.RunId ?? string.Empty,
                        StringComparison.Ordinal);
                }
                else if (settings.Slot.HasValue)
                {
                    status.SlotMatches = lastObserved.SaveSlotId == settings.Slot.Value;
                }
                status.LastSaveTime = lastObserved.LastSaveTime;
            }

            return status;
        }

        private string CurrentError()
        {
            if (state == RecordingSlotState.Disabled)
            {
                return settings.Error ?? RecordingSaveErrors.Unconfigured;
            }
            if (state == RecordingSlotState.Locked)
            {
                return lockedReason ?? RecordingSaveErrors.Locked;
            }
            if (startIssued) return RecordingSaveErrors.StartPending;
            if (loadIssued) return RecordingSaveErrors.LoadPending;
            if (state == RecordingSlotState.Armed
                && (lastObserved == null || !lastObserved.Readable))
            {
                return RecordingSaveErrors.GameStateUnavailable;
            }
            return null;
        }

        private void Lock(string reason)
        {
            if (state == RecordingSlotState.Locked) return;
            state = RecordingSlotState.Locked;
            lockedReason = string.IsNullOrEmpty(reason)
                ? RecordingSaveErrors.Locked
                : reason;
            startIssued = false;
            loadIssued = false;
        }
    }
}
