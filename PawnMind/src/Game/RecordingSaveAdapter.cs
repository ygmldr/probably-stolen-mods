using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using PawnMind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnMind.Game
{
    /// <summary>
    /// S4 game adapter. It exposes only read-only save identity/inventory to the
    /// policy and performs the single native callback requested by the bridge:
    /// SaveGame, StartNewGameClick, EndDay or ConfirmPanelUIManager.OnYesClick.
    ///
    /// The adapter never reads a save file or preview, never calls ES3 directly,
    /// never sets NewGameData.saveSlot for a new game (Rev13 proves that field is
    /// the load target; EmporiumEntry.InitialSave allocates the new slot), and
    /// never retries a possibly successful callback.
    /// </summary>
    internal sealed class RecordingSaveAdapter :
        IRecordingGameStatePort,
        IRecordingSlotInventoryPort,
        IRecordingSaveMetadataPort,
        IRecordingNativeSavePort
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static readonly RecordingSaveAdapter Instance = new RecordingSaveAdapter();

        // -----------------------------------------------------------------
        // Read-only state / inventory
        // -----------------------------------------------------------------

        public RecordingGameState Read()
        {
            try
            {
                RecordingGameState state = new RecordingGameState
                {
                    Readable = true,
                    InGame = PlayerStore.HasInstance
                };

                NewGameData newGame = NewGameData.Instance;
                if (newGame != null)
                {
                    state.NewGameStartType = newGame.startType.ToString();
                    state.NewGameStartScenarioId = newGame.startScenarioId ?? string.Empty;
                    state.NewGameDifficultyId = newGame.difficultyId ?? string.Empty;
                }

                if (state.InGame)
                {
                    PlayerStore store = PlayerStore.Instance;
                    state.SaveSlotId = store.saveSlotId;
                    state.RunId = store.runID ?? string.Empty;
                    state.RunNumber = store.runNumber;
                    state.StartType = store.startType.ToString();
                    state.StartScenarioId = store.startScenarioId ?? string.Empty;
                    state.LastSaveTime = FormatTime(store.lastSaveTime);

                    // S5 day-cycle readback. These are the same visible fields the
                    // end-of-day tablet and calendar show; if the station is not
                    // readable the flag stays false and sleep fails closed.
                    try
                    {
                        StoreStation station = StoreStation.Instance;
                        if (station == null) throw new InvalidOperationException("no_station");
                        state.Day = station.dayCounter;
                        state.RentDaysLeft = store.dayUntilRent;
                        state.Cash = store.playerCash;
                        state.DayCycleReadable = true;
                    }
                    catch (Exception)
                    {
                        state.DayCycleReadable = false;
                    }
                }

                return state;
            }
            catch (Exception)
            {
                return UnavailableState();
            }
        }

        /// <summary>
        /// Reads only the game's save index metadata: registered slot ids and the
        /// allocation generator. It does not open a slot file, read a preview or
        /// call NextFreeSlotId (which writes the generator). The predicted next
        /// slot mirrors the index; if an orphan file outside the index changes the
        /// native result, post-start readback locks instead of retrying.
        /// </summary>
        public RecordingSlotInventory Inspect(int slot)
        {
            try
            {
                List<int> active = SaveFiles.IndexLoad<List<int>>(
                    "activeSaveSlotId", new List<int>());
                if (active == null) active = new List<int>();

                int generator = SaveFiles.IndexLoad<int>("slotIdGenerator", 0);
                int next = generator;
                bool known = next >= 0;
                int guard = 0;
                while (known && active.Contains(next))
                {
                    next++;
                    guard++;
                    if (guard > 100000)
                    {
                        known = false;
                    }
                }

                return new RecordingSlotInventory
                {
                    Readable = true,
                    TargetOccupied = slot < 0 || active.Contains(slot),
                    NextFreeKnown = known,
                    NextFreeSlot = known ? next : -1
                };
            }
            catch (Exception)
            {
                return new RecordingSlotInventory
                {
                    Readable = false,
                    Error = RecordingSaveErrors.InventoryUnavailable
                };
            }
        }

        /// <summary>
        /// Reads only the index preview metadata of the already-bound dedicated
        /// slot to report its last save time. It never loads the slot file and
        /// the status JSON exposes only the timestamp, never the preview object.
        /// </summary>
        public string ReadLastSaveTime(int slot)
        {
            try
            {
                SavePreviewData preview = SaveFiles.IndexLoad<SavePreviewData>(
                    "preview_" + slot, null);
                if (preview == null) return null;
                return FormatTime(preview.lastSaveDateTime);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // -----------------------------------------------------------------
        // One native callback each
        // -----------------------------------------------------------------

        public RecordingNativeResult Save()
        {
            PlayerStore store;
            if (!TryStore(out store)) return RecordingNativeResult.NativeNotCalled("not_in_game");

            bool invoked = false;
            try
            {
                invoked = true;
                store.SaveGame();
                return RecordingNativeResult.NativeCalled();
            }
            catch (Exception)
            {
                return invoked
                    ? RecordingNativeResult.NativeCalledWithError("native_error")
                    : RecordingNativeResult.NativeNotCalled("native_error");
            }
        }

        public RecordingNativeResult Load(int slot)
        {
            if (PlayerStore.HasInstance)
            {
                return RecordingNativeResult.NativeNotCalled("already_in_game");
            }

            NewGameData data = NewGameData.Instance;
            if (data == null) return RecordingNativeResult.NativeNotCalled("no_new_game_data");
            MainMenuUIController menu = MainMenuUIController.Instance;
            if (menu == null) return RecordingNativeResult.NativeNotCalled("no_main_menu");

            bool invoked = false;
            try
            {
                data.saveSlot = slot;
                data.saveFile = null;
                data.isLoadFromMainMenu = true;
                invoked = true;
                menu.StartNewGameClick();
                return RecordingNativeResult.NativeCalled();
            }
            catch (Exception)
            {
                return invoked
                    ? RecordingNativeResult.NativeCalledWithError("native_error")
                    : RecordingNativeResult.NativeNotCalled("native_error");
            }
        }

        public RecordingNativeResult Sleep()
        {
            PlayerStore store;
            if (!TryStore(out store)) return RecordingNativeResult.NativeNotCalled("not_in_game");

            EndOfDayUIManager ui = EndOfDayUIManager.Instance;
            if (ui == null) return RecordingNativeResult.NativeNotCalled("no_end_of_day_ui");

            bool invoked = false;
            try
            {
                invoked = true;
                ui.OnEndDayButtonClick();
                return RecordingNativeResult.NativeCalled();
            }
            catch (Exception)
            {
                return invoked
                    ? RecordingNativeResult.NativeCalledWithError("native_error")
                    : RecordingNativeResult.NativeNotCalled("native_error");
            }
        }

        public RecordingNativeResult StartNewGame()
        {
            if (PlayerStore.HasInstance)
            {
                return RecordingNativeResult.NativeNotCalled("already_in_game");
            }

            NewGameData data = NewGameData.Instance;
            if (data == null) return RecordingNativeResult.NativeNotCalled("no_new_game_data");
            if (data.startType == NewGameData.StartType.NONE)
            {
                return RecordingNativeResult.NativeNotCalled("no_start_type");
            }
            if (!NewGameDifficultyUI.IsOpen)
            {
                return RecordingNativeResult.NativeNotCalled("wrong_menu_phase");
            }
            if (StaticField<bool>(typeof(NewGameDifficultyUI), "startingGame"))
            {
                return RecordingNativeResult.NativeNotCalled("menu_transitioning");
            }
            if (NewGameDifficultyUI.Selected == null)
            {
                return RecordingNativeResult.NativeNotCalled("no_difficulty");
            }

            NewGameDifficultyFrame frame =
                StaticField<NewGameDifficultyFrame>(typeof(NewGameDifficultyUI), "frame");
            if (frame == null || !ButtonUsable(frame.continueButton))
            {
                return RecordingNativeResult.NativeNotCalled("start_unavailable");
            }

            bool invoked = false;
            try
            {
                // The native difficulty Continue handler applies the selected
                // difficulty, closes the menu and calls StartNewGameClick, which
                // is exactly what a player's Start click does. Rev13 proves
                // NewGameData.saveSlot is the load target, not the allocation
                // target: clearing it to -1 selects EmporiumEntry's new-game
                // branch, where InitialSave calls NextFreeSlotId itself.
                data.saveSlot = -1;
                data.saveFile = null;
                data.isLoadFromMainMenu = false;
                invoked = true;
                InvokeStaticVoid(typeof(NewGameDifficultyUI), "OnStartClicked");
                return RecordingNativeResult.NativeCalled();
            }
            catch (Exception)
            {
                return invoked
                    ? RecordingNativeResult.NativeCalledWithError("native_error")
                    : RecordingNativeResult.NativeNotCalled("native_error");
            }
        }

        public RecordingNativeResult ConfirmFirstRunPrompt(string promptId)
        {
            MainMenuUIController menu = MainMenuUIController.Instance;
            if (menu == null) return RecordingNativeResult.NativeNotCalled("no_main_menu");

            ConfirmPanelUIManager confirm = menu.confirmPanelUIManager;
            if (confirm == null
                || confirm.panel == null
                || !confirm.panel.activeInHierarchy
                || confirm.bodyTMP == null)
            {
                return RecordingNativeResult.NativeNotCalled("no_prompt");
            }

            string text = confirm.bodyTMP.text;
            string visibleId = IdentifyFirstRunPrompt(text);
            if (string.IsNullOrEmpty(visibleId)
                || !string.Equals(visibleId, promptId, StringComparison.Ordinal))
            {
                return RecordingNativeResult.NativeNotCalled("prompt_mismatch");
            }

            bool invoked = false;
            try
            {
                invoked = true;
                confirm.OnYesClick();
                return RecordingNativeResult.NativeCalled();
            }
            catch (Exception)
            {
                return invoked
                    ? RecordingNativeResult.NativeCalledWithError("native_error")
                    : RecordingNativeResult.NativeNotCalled("native_error");
            }
        }

        // -----------------------------------------------------------------
        // Helpers (main thread only)
        // -----------------------------------------------------------------

        private static string IdentifyFirstRunPrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                if (string.Equals(text, LocHelper.GetLocalizedMainMenu("first_run_confirm"),
                        StringComparison.Ordinal))
                {
                    return "first_run_confirm";
                }
                if (string.Equals(text,
                        LocHelper.GetLocalizedMainMenu("skip_intro_first_run_confirm"),
                        StringComparison.Ordinal))
                {
                    return "skip_intro_first_run_confirm";
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        private static string FormatTime(DateTime value)
        {
            if (value.Year <= 1) return null;
            return value.ToString("o", CultureInfo.InvariantCulture);
        }

        private static RecordingGameState UnavailableState()
        {
            return new RecordingGameState
            {
                Readable = false,
                Error = RecordingSaveErrors.GameStateUnavailable
            };
        }

        private static bool TryStore(out PlayerStore store)
        {
            store = null;
            try
            {
                if (!PlayerStore.HasInstance) return false;
                store = PlayerStore.Instance;
                return store != null;
            }
            catch (Exception)
            {
                store = null;
                return false;
            }
        }

        private static bool ButtonUsable(Button button)
        {
            return button != null
                && button.gameObject != null
                && button.gameObject.activeInHierarchy
                && button.interactable;
        }

        private static T StaticField<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(name, StaticMembers);
            if (field == null) return default(T);
            object value = field.GetValue(null);
            return value is T typed ? typed : default(T);
        }

        private static void InvokeStaticVoid(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, StaticMembers, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(type.Name, name);
            method.Invoke(null, null);
        }
    }
}
