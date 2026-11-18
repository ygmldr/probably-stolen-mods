using System;
using System.Collections.Generic;
using PawnMind.Core;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PawnMind.Game
{
    /// <summary>
    /// Strongly typed adapter over the game's runtime state. All access happens on
    /// the Unity main thread. Only the private StoreClientInstance.storeClient field
    /// needs reflection; reflecting on a game type is free for the capability scanner.
    /// </summary>
    internal static class GameState
    {
        private static readonly FieldInfo StoreClientField =
            typeof(StoreClientInstance).GetField("storeClient", BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool IsInGame
        {
            get
            {
                try
                {
                    // Instance creates an empty store on first access; HasInstance
                    // is the only safe main-menu/game-state probe.
                    return PlayerStore.HasInstance;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static long GetCash()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? 0 : store.GetCash();
            }
            catch
            {
                return 0;
            }
        }

        public static int GetRentDaysLeft()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? 0 : store.dayUntilRent;
            }
            catch
            {
                return 0;
            }
        }

        public static int GetRentValue()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? 0 : store.rentValue;
            }
            catch
            {
                return 0;
            }
        }

        public static int GetStartingDay()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? 0 : store.startingDay;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsClientArrived()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.isClientArrived;
            }
            catch
            {
                return false;
            }
        }

        public static StoreClient GetCurrentClient()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null) return null;

                StoreClientInstance instance = store.currentClientInstance;
                if (instance == null || StoreClientField == null) return null;

                return StoreClientField.GetValue(instance) as StoreClient;
            }
            catch
            {
                return null;
            }
        }

        private static readonly FieldInfo ClientGridField =
            typeof(StoreClientInstance).GetField("gridInv", BindingFlags.Instance | BindingFlags.NonPublic);

        public static List<GameItem> GetFrontCounterItems()
        {
            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null || entry.frontInvinvElement == null) return new List<GameItem>();
                return entry.frontInvinvElement.childItems;
            }
            catch
            {
                return new List<GameItem>();
            }
        }

        public static bool IsOutside
        {
            get
            {
                try
                {
                    return MapUIManager.Instance != null && MapUIManager.Instance.IsOutside;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string CurrentLocationId
        {
            get
            {
                try
                {
                    AfterhourLocation current = MapUIManager.Instance == null ? null : MapUIManager.Instance.Current;
                    return current == null ? null : current.Id;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static int GetAfterhourItemCount()
        {
            try
            {
                return EmporiumEntry.Instance.afterhourInventory.childItems.Count;
            }
            catch
            {
                return 0;
            }
        }

        public static List<StockItem> GetCommissaryStock()
        {
            var rows = new List<StockItem>();
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.commissaryData == null) return rows;

                List<CommissaryItemData> items = store.commissaryData.commissaryItemDatas;
                for (int i = 0; i < items.Count; i++)
                {
                    CommissaryItemData item = items[i];
                    if (item == null) continue;
                    rows.Add(new StockItem
                    {
                        Identifier = item.identifier,
                        Price = item.price,
                        Stock = item.StockCount
                    });
                }
            }
            catch
            {
            }

            return rows;
        }

        public static GameItem GetFirstClientItem()
        {
            List<GameItem> items = GetFrontCounterItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && !IsOwned(items[i])) return items[i];
            }

            return null;
        }

        public static List<GameItem> GetClientItems()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.currentClientInstance == null || ClientGridField == null)
                    return new List<GameItem>();

                GameGridInventory grid = ClientGridField.GetValue(store.currentClientInstance) as GameGridInventory;
                return grid == null ? new List<GameItem>() : grid.childItems;
            }
            catch
            {
                return new List<GameItem>();
            }
        }

        /// <summary>
        /// Makes the current client leave through the game's own dismissal path.
        /// This is the escape hatch for a client the bridge cannot serve (for
        /// example a seller who arrived with nothing on the table): without it the
        /// shop is hard-stuck, because both call_next_customer and end_day require
        /// an empty client slot.
        /// </summary>
        public static void DismissCurrentClient()
        {
            PlayerStore store = PlayerStore.Instance;
            if (store == null || store.currentClientInstance == null)
            {
                throw new InvalidOperationException("no_client");
            }

            store.DismissCurrentClient(callNextWhenPossible: false);
        }

        public static bool IsNegotiationOpen()
        {
            try
            {
                return NegociationUIManager.Instance != null && NegociationUIManager.Instance.IsOpen;
            }
            catch
            {
                return false;
            }
        }

        public static List<GameItem> GetBackCounterItems()
        {
            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null || entry.backInvinvElement == null) return new List<GameItem>();
                return entry.backInvinvElement.childItems;
            }
            catch
            {
                return new List<GameItem>();
            }
        }

        public static GameItem FindItem(int uniqueId)
        {
            GameItem item = FindIn(GetBackCounterItems(), uniqueId);
            if (item != null) return item;
            item = FindIn(GetFrontCounterItems(), uniqueId);
            if (item != null) return item;
            return FindIn(GetClientItems(), uniqueId);
        }

        private static GameItem FindIn(List<GameItem> items, int uniqueId)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].GetUniqueID() == uniqueId) return items[i];
            }

            return null;
        }

        public static GameItem FindBackCounterItem(int uniqueId)
        {
            List<GameItem> items = GetBackCounterItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].GetUniqueID() == uniqueId) return items[i];
            }

            return null;
        }

        public static bool IsOwned(GameItem item)
        {
            try
            {
                return GeneralHelper.IsItemOwned(item);
            }
            catch
            {
                return false;
            }
        }

        public static int GetDay()
        {
            try
            {
                return StoreStation.Instance == null ? 0 : StoreStation.Instance.dayCounter;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Customers already queued and still waiting to be served. The daily
        /// quota is deliberately not computed here: GetNormalCustomerCount rolls
        /// the RNG, so it is only read by the cached get_clientele op.
        /// </summary>
        public static int GetWaitingCustomerCount()
        {
            try
            {
                PlayerStore store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                StoreClientManager manager = store == null ? null : store.storeClientManager;
                if (manager == null || manager.clientStack == null) return 0;
                return manager.clientStack.Count;
            }
            catch
            {
                return 0;
            }
        }

        public static string GetShopState()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? "UNKNOWN" : store.storeState.ToString();
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        public static bool GetCanNextClient()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.CanNextClient();
            }
            catch
            {
                return false;
            }
        }

        public static bool GetCanEndDay()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.CanEndDay();
            }
            catch
            {
                return false;
            }
        }

        public static GameInventory GetContainer(string containerId)
        {
            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null) return null;

                switch ((containerId ?? "main").ToLowerInvariant())
                {
                    case "main": return entry.invElement;
                    case "front": return entry.frontInvinvElement;
                    case "back": return entry.backInvinvElement;
                    case "showcase": return entry.showcaseElement;
                    case "hidden": return entry.hiddenElement;
                    case "afterhour": return entry.afterhourInventory;
                    case "trash": return entry.trashcanInvElement;
                    case "sold": return entry.soldElement;
                    case "vending": return entry.vendingMachineElement;
                    case "fountain": return entry.vendingFountainElement;
                    case "hireling": return entry.hirelingInv;
                    default: return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static GameItem FindItemAnywhere(int uniqueId)
        {
            List<string> ids = GetContainerIds();
            for (int i = 0; i < ids.Count; i++)
            {
                GameInventory inventory = GetContainer(ids[i]);
                if (inventory == null) continue;

                List<GameItem> items = inventory.childItems;
                for (int j = 0; j < items.Count; j++)
                {
                    if (items[j] != null && items[j].GetUniqueID() == uniqueId) return items[j];
                }
            }

            GameItem machineItem = FindInMachines(uniqueId);
            if (machineItem != null) return machineItem;

            return null;
        }

        private static GameItem FindInMachines(int uniqueId)
        {
            try
            {
                List<GameItem> machines = EmporiumEntry.Instance.GetAllOwnedItemsByTag("STANDARD_MACHINE_TAG");
                for (int i = 0; i < machines.Count; i++)
                {
                    for (int slot = 0; slot < 24; slot++)
                    {
                        GraphNodeStorage storage = null;
                        try { storage = MachineHelper.GetBodyStorage(machines[i], slot); } catch { }
                        var inventory = storage as GameInventory;
                        if (inventory == null) continue;

                        List<GameItem> items = inventory.childItems;
                        for (int j = 0; j < items.Count; j++)
                        {
                            if (items[j] != null && items[j].GetUniqueID() == uniqueId) return items[j];
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public static List<string> GetContainerIds()
        {
            return new List<string>
            {
                "main", "front", "back", "showcase", "hidden", "afterhour", "trash", "sold",
                "vending", "fountain", "hireling"
            };
        }

        public static List<GameItem> GetInventoryItems()
        {
            try
            {
                EmporiumEntry entry = EmporiumEntry.Instance;
                if (entry == null || entry.invElement == null) return new List<GameItem>();
                return entry.invElement.childItems;
            }
            catch
            {
                return new List<GameItem>();
            }
        }

        /// <summary>
        /// S5 read-only dialogue projection. Enabled mirrors the native button
        /// interactable state (DialogUIManager.DisplayChoice uses the same
        /// Dialogue.IsChoiceEnabledAt call). The identity is an opaque handle
        /// used only for post-select readback and is never serialized.
        /// </summary>
        public static DialogueView ReadDialogueView()
        {
            var view = new DialogueView
            {
                Readable = true,
                Available = false,
                Ended = true
            };

            try
            {
                DialogUIManager ui = DialogUIManager.Instance;
                if (ui == null) return view;

                Dialogue current = ui.GetCurrentDialog();
                if (current == null) return view;

                view.Available = true;
                view.Identity = "dialogue_" + RuntimeHelpers.GetHashCode(current);
                view.Speaker = current.title;
                view.Text = current.plainText;
                view.Ended = ui.IsCurrentDialogEnd();
                view.Choice = ui.IsChoice();
                view.Typing = ui.IsLineStillDisplaying(current);

                if (current.choices != null)
                {
                    for (int i = 0; i < current.choices.Count; i++)
                    {
                        var choice = current.choices[i];
                        Dialogue line = choice.Item2;
                        var cell = new DialogueChoiceView
                        {
                            Index = i,
                            Text = line == null ? string.Empty : (line.plainText ?? string.Empty),
                            Enabled = SafeChoiceEnabled(current, i),
                            HasItem = choice.Item1 != null
                        };
                        if (cell.HasItem)
                        {
                            cell.ItemId = choice.Item1.GetUniqueID();
                            cell.ItemName = choice.Item1.name;
                        }

                        view.Choices.Add(cell);
                    }
                }

                return view;
            }
            catch
            {
                return new DialogueView
                {
                    Readable = false,
                    Error = DialogueChoiceErrors.DialogueUnavailable,
                    Available = false,
                    Ended = true
                };
            }
        }

        private static bool SafeChoiceEnabled(Dialogue current, int index)
        {
            try
            {
                return current.IsChoiceEnabledAt(index);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Single guarded native call; the service owns the pre-check.</summary>
        public static void SelectDialogueChoice(int index)
        {
            DialogUIManager ui = DialogUIManager.Instance;
            if (ui == null) throw new InvalidOperationException("no_dialogue");
            ui.SelectChoice(index);
        }

        /// <summary>
        /// R4/F1b: the opaque native run identity, or null when it is not readable
        /// (main menu, loading screen, no PlayerStore). The bridge only compares it
        /// to prove that a load/new game really switched the run; it is never
        /// serialized to a client and never substituted by rev/day/scene.
        /// </summary>
        public static string ReadRunId()
        {
            try
            {
                // "No run" and "cannot tell" are different facts (R4/F1b): the main
                // menu is the first, a loading screen or a broken store the second.
                if (!PlayerStore.HasInstance) return RunChangePolicy.NoRun;
                PlayerStore store = PlayerStore.Instance;
                if (store == null) return null;
                string runId = store.runID;
                return string.IsNullOrEmpty(runId) ? null : runId;
            }
            catch
            {
                return null;
            }
        }

        public static RentSnapshot ReadRentSnapshot()
        {
            try
            {
                if (!PlayerStore.HasInstance) return UnreadableRent();
                PlayerStore store = PlayerStore.Instance;
                StoreStation station = StoreStation.Instance;
                if (store == null || store.runStats == null || station == null) return UnreadableRent();

                return new RentSnapshot
                {
                    Readable = true,
                    Due = store.GetRentValue(),
                    RentValue = store.rentValue,
                    TemporaryRent = store.temporaryRent,
                    DaysLeft = store.GetRentDayCounter(),
                    DaysUntilRent = store.dayUntilRent,
                    CanPay = store.CanPayRent(),
                    RentPaidTotal = store.runStats.rentPaid,
                    RentWeeksPaid = store.runStats.rentWeeksPaid,
                    RentWeeksMissed = store.runStats.rentWeeksMissed,
                    StartingRent = store.startingRent,
                    Cash = store.playerCash,
                    Day = station.dayCounter,
                    RunId = store.runID ?? string.Empty
                };
            }
            catch
            {
                return UnreadableRent();
            }
        }

        private static RentSnapshot UnreadableRent()
        {
            return new RentSnapshot
            {
                Readable = false,
                Error = RentErrors.RentUnavailable
            };
        }

        public static ShopDaySnapshot ReadShopDaySnapshot()
        {
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    return new ShopDaySnapshot
                    {
                        Readable = true,
                        InGame = false
                    };
                }

                PlayerStore store = PlayerStore.Instance;
                StoreStation station = StoreStation.Instance;
                if (store == null || station == null)
                {
                    return new ShopDaySnapshot
                    {
                        Readable = false,
                        Error = DayCycleErrors.ReadbackUnavailable
                    };
                }

                return new ShopDaySnapshot
                {
                    Readable = true,
                    InGame = true,
                    CanEndDay = store.CanEndDay(),
                    Day = station.dayCounter,
                    Cash = store.playerCash,
                    RentDaysLeft = store.dayUntilRent,
                    RentDue = store.GetRentValue(),
                    WaitingCustomers = GetWaitingCustomerCount(),
                    ShopState = store.storeState.ToString(),
                    ShutterBusy = store.lockShutterButtonFlag > 0,
                    ClientPresent = store.currentClientInstance != null
                };
            }
            catch
            {
                return new ShopDaySnapshot
                {
                    Readable = false,
                    Error = DayCycleErrors.ReadbackUnavailable
                };
            }
        }

        /// <summary>
        /// Closes the shop through the shutter button: PlayerStore.EndDay() is the
        /// inner half and skips the teardown the player path performs (clearing the
        /// table, closing dialogue, the shutter animation, the delayed dismissal).
        /// </summary>
        public static void EndDay(bool dismissWaiting)
        {
            if (!PlayerStore.HasInstance) throw new InvalidOperationException("not_in_game");
            StoreShutterButton button = StoreShutterButton.Instance;
            if (button == null) throw new InvalidOperationException("no_shutter_button");

            if (dismissWaiting)
            {
                // The player's "yes" on the confirmation dialog.
                button.EndAndClose();
                return;
            }

            button.CloseShutter();
        }

        /// <summary>
        /// Pure snapshot of the bargain panel. Do not expose the offered/used
        /// flags here: every one of those getters calls BargainUIManager.Refresh(),
        /// which resets the per-client offer state when the tracked client changes.
        /// The flags remain available through the bargain action's own result.
        /// </summary>
        public static Dictionary<string, object> GetBargainSnapshot()
        {
            try
            {
                BargainUIManager ui = BargainUIManager.Instance;
                if (ui == null) return null;

                return Core.SnapshotRules.Bargain(ui.IsOpen);
            }
            catch
            {
                return null;
            }
        }

        public static int GetClientBudget(StoreClient client)
        {
            try
            {
                return client == null ? 0 : client.GetBudget();
            }
            catch
            {
                return 0;
            }
        }

        public static bool CanSellItem(GameItem item)
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.CanSellThisItem(item, true);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the player's own "accept offer" button. Going through the native
        /// click (instead of moving money/items by hand) is what completes the
        /// client's business and starts the closing dialogue, so the customer
        /// can leave and the queue keeps moving.
        /// </summary>
        public static void AcceptNegotiationOffer()
        {
            NegociationUIManager ui = NegociationUIManager.Instance;
            if (ui == null || !ui.IsOpen)
            {
                throw new InvalidOperationException("no_negotiation");
            }

            ui.OnOfferAcceptClick();
        }

        /// <summary>
        /// In-process identity of the current client (never serialized). Used by the
        /// trade evidence layer so another client's state change cannot back this
        /// trade (PP-TRADE-08).
        /// </summary>
        public static int GetCurrentClientHandle()
        {
            try
            {
                StoreClient client = GetCurrentClient();
                return client == null ? 0 : RuntimeHelpers.GetHashCode(client);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>True once the current client's deal is recorded as done.</summary>
        public static bool IsCurrentClientBusinessComplete()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                if (store == null || store.currentClientInstance == null) return false;
                StoreClient blueprint = store.currentClientInstance.GetClientBlueprint();
                return blueprint != null && blueprint.isBusinessComplete;
            }
            catch
            {
                return false;
            }
        }

        public static GameItem GetNegotiatedItem()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store == null ? null : store.GetCurrentlyNegociatedItem();
            }
            catch
            {
                return null;
            }
        }
    }
}
