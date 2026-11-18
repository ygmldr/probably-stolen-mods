using System;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class FakeRecordingStatePort : IRecordingGameStatePort
    {
        public RecordingGameState State = RecordingTestData.MainMenu();
        public bool Throw;
        public int Reads;

        public RecordingGameState Read()
        {
            Reads++;
            if (Throw) throw new InvalidOperationException("state read failed");
            return State;
        }
    }

    internal sealed class FakeRecordingSlotInventoryPort : IRecordingSlotInventoryPort
    {
        public RecordingSlotInventory Result = RecordingTestData.FreeSlot(7);
        public bool Throw;
        public int Calls;
        public int LastSlot = -1;

        public RecordingSlotInventory Inspect(int slot)
        {
            Calls++;
            LastSlot = slot;
            if (Throw) throw new InvalidOperationException("inventory read failed");
            return Result;
        }
    }

    internal sealed class FakeRecordingSaveMetadataPort : IRecordingSaveMetadataPort
    {
        public string LastSaveTime;
        public bool Throw;
        public int Calls;
        public int LastSlot = -1;

        public string ReadLastSaveTime(int slot)
        {
            Calls++;
            LastSlot = slot;
            if (Throw) throw new InvalidOperationException("metadata read failed");
            return LastSaveTime;
        }
    }

    internal sealed class FakeRecordingNativePort : IRecordingNativeSavePort
    {
        public bool NotCalled;
        public string Error;
        public int SaveCalls;
        public int LoadCalls;
        public int SleepCalls;
        public int StartCalls;
        public int PromptCalls;
        public int LastLoadSlot = -1;
        public string LastPromptId;

        public Action SaveEffect;
        public Action LoadEffect;
        public Action SleepEffect;
        public Action StartEffect;
        public Action PromptEffect;

        public RecordingNativeResult Save()
        {
            SaveCalls++;
            return Invoke(SaveEffect);
        }

        public RecordingNativeResult Load(int slot)
        {
            LoadCalls++;
            LastLoadSlot = slot;
            return Invoke(LoadEffect);
        }

        public RecordingNativeResult Sleep()
        {
            SleepCalls++;
            return Invoke(SleepEffect);
        }

        public RecordingNativeResult StartNewGame()
        {
            StartCalls++;
            return Invoke(StartEffect);
        }

        public RecordingNativeResult ConfirmFirstRunPrompt(string promptId)
        {
            PromptCalls++;
            LastPromptId = promptId;
            return Invoke(PromptEffect);
        }

        public void ResetCalls()
        {
            SaveCalls = 0;
            LoadCalls = 0;
            SleepCalls = 0;
            StartCalls = 0;
            PromptCalls = 0;
            LastLoadSlot = -1;
            LastPromptId = null;
        }

        private RecordingNativeResult Invoke(Action effect)
        {
            if (NotCalled) return RecordingNativeResult.NativeNotCalled("native_not_called");
            if (effect != null) effect();
            if (!string.IsNullOrEmpty(Error))
            {
                return RecordingNativeResult.NativeCalledWithError(Error);
            }
            return RecordingNativeResult.NativeCalled();
        }
    }

    internal static class RecordingTestData
    {
        public static RecordingSettings Valid(int slot = 7)
        {
            return RecordingSettings.Create("on", slot, true);
        }

        public static RecordingSettings DedicatedSlot(int slot = 7)
        {
            return RecordingSettings.Create("on", slot, false, true);
        }

        public static RecordingSettings BothAcks(int slot = 7)
        {
            return RecordingSettings.Create("on", slot, true, true);
        }

        public static RecordingSettings Off()
        {
            return RecordingSettings.Create("off", null, false);
        }

        public static RecordingSettings Unacknowledged()
        {
            return RecordingSettings.Create("on", 7, false);
        }

        public static RecordingGameState MainMenu()
        {
            return new RecordingGameState
            {
                Readable = true,
                InGame = false,
                NewGameStartType = "APPRENTICE",
                NewGameStartScenarioId = "apprentice",
                NewGameDifficultyId = "normal"
            };
        }

        public static RecordingGameState InGame(
            int slot = 7,
            string runId = "run-a",
            int runNumber = 3,
            string startType = "APPRENTICE",
            string startScenarioId = "apprentice",
            int day = 3,
            int rentDaysLeft = 4,
            int cash = 600)
        {
            return new RecordingGameState
            {
                Readable = true,
                InGame = true,
                SaveSlotId = slot,
                RunId = runId,
                RunNumber = runNumber,
                StartType = startType,
                StartScenarioId = startScenarioId,
                LastSaveTime = "2026-11-14T12:00:00.0000000",
                DayCycleReadable = true,
                Day = day,
                RentDaysLeft = rentDaysLeft,
                Cash = cash,
                // The shop is closed for the day and nothing blocks ending it:
                // sleep tests exercise the transition, not the day-end gate.
                ShopState = "CLOSED",
                CanEndDay = true,
                WaitingCustomers = 0,
                NewGameStartType = "APPRENTICE",
                NewGameStartScenarioId = "apprentice",
                NewGameDifficultyId = "normal"
            };
        }

        public static RecordingGameState Unavailable()
        {
            return new RecordingGameState
            {
                Readable = false,
                Error = "test_unavailable"
            };
        }

        public static RecordingSlotInventory FreeSlot(int slot = 7)
        {
            return new RecordingSlotInventory
            {
                Readable = true,
                TargetOccupied = false,
                NextFreeKnown = true,
                NextFreeSlot = slot
            };
        }

        public static NewGameMenuSnapshot DifficultyMenu()
        {
            NewGameMenuSnapshot snapshot = new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Difficulty,
                Page = 0,
                PageCount = 1,
                SelectedId = "normal",
                HasSelection = true,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Start",
                    Visible = true,
                    Enabled = true
                }
            };
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "normal",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Normal"
            });
            return snapshot;
        }

        public static NewGameMenuSnapshot PromptMenu()
        {
            return new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Closed,
                Prompt = new NewGameMenuPrompt
                {
                    Id = "first_run_confirm",
                    Text = "First run prompt",
                    ConfirmText = "Yes",
                    CancelText = "No",
                    FirstRun = true
                }
            };
        }

        public static NewGameMenuSnapshot PerksMenu()
        {
            return new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Perks,
                Page = 0,
                PageCount = 1,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Continue",
                    Visible = true,
                    Enabled = true
                }
            };
        }
    }

    internal sealed class RecordingHarness
    {
        public readonly FakeRecordingStatePort StatePort = new FakeRecordingStatePort();
        public readonly FakeRecordingSlotInventoryPort InventoryPort =
            new FakeRecordingSlotInventoryPort();
        public readonly FakeRecordingNativePort NativePort = new FakeRecordingNativePort();
        public readonly FakeRecordingSaveMetadataPort MetadataPort =
            new FakeRecordingSaveMetadataPort();
        public readonly FakeNewGameMenuPort MenuPort;
        public readonly FakeDayReportPort ReportPort = new FakeDayReportPort();
        public readonly RecordingSlotPolicy Policy;
        public readonly RecordingSaveService Service;

        /// <summary>R5/A2: fake monotonic clock read by the injected sleep verifier.</summary>
        public long ClockMs;

        public RecordingHarness(RecordingSettings settings, NewGameMenuSnapshot menu)
        {
            MenuPort = new FakeNewGameMenuPort(menu ?? RecordingTestData.DifficultyMenu());
            Policy = new RecordingSlotPolicy(settings);
            Service = new RecordingSaveService(
                Policy,
                StatePort,
                InventoryPort,
                NativePort,
                MenuPort,
                MetadataPort,
                new ReportService(ReportPort),
                delegate { return ClockMs; });
        }

        public static RecordingHarness Create(
            RecordingSettings settings = null,
            NewGameMenuSnapshot menu = null)
        {
            return new RecordingHarness(
                settings ?? RecordingTestData.Valid(),
                menu ?? RecordingTestData.DifficultyMenu());
        }

        public void Bind(int slot = 7, string runId = "run-a")
        {
            StatePort.State = RecordingTestData.MainMenu();
            InventoryPort.Result = RecordingTestData.FreeSlot(slot);
            NativePort.StartEffect = delegate
            {
                StatePort.State = RecordingTestData.InGame(slot, runId);
            };

            ActionResult result = Service.ConfirmNewGame();
            if (!result.Ok)
            {
                throw new AssertionException("bind failed: " + result.Error);
            }

            NativePort.StartEffect = null;
            NativePort.ResetCalls();
        }
    }
}
