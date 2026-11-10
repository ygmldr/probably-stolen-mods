using System;
using System.Collections.Generic;
using PawnMind.Core;
using System.Reflection;

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
                    return PlayerStore.Instance != null;
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

        public static List<int> GetSaveSlots()
        {
            try
            {
                return SaveFiles.IndexLoad<List<int>>("activeSaveSlotId", new List<int>());
            }
            catch
            {
                return new List<int>();
            }
        }

        public static List<Dictionary<string, object>> GetSavePreviews()
        {
            var previews = new List<Dictionary<string, object>>();
            try
            {
                List<int> slots = GetSaveSlots();
                for (int i = 0; i < slots.Count; i++)
                {
                    SavePreviewData preview = SaveUIManager.GetPreview(slots[i]);
                    if (preview == null) continue;

                    var row = new Dictionary<string, object>();
                    row["slot"] = slots[i];
                    row["day"] = preview.dayCount;
                    row["credit"] = preview.credit;
                    row["name"] = preview.GetDisplayName();
                    row["saved_at"] = preview.lastSaveDateTime.ToString("o");
                    previews.Add(row);
                }
            }
            catch
            {
            }

            return previews;
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
            return new List<string> { "main", "front", "back", "showcase", "hidden", "afterhour", "trash", "sold" };
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

        public static List<Dictionary<string, object>> GetDialogueChoices()
        {
            var described = new List<Dictionary<string, object>>();
            try
            {
                DialogUIManager ui = DialogUIManager.Instance;
                if (ui == null) return described;

                Dialogue current = ui.GetCurrentDialog();
                if (current == null || current.choices == null) return described;

                for (int i = 0; i < current.choices.Count; i++)
                {
                    var choice = current.choices[i];
                    Dialogue line = choice.Item2;
                    if (line == null) continue;

                    var row = new Dictionary<string, object>();
                    row["index"] = i;
                    row["text"] = line.plainText ?? string.Empty;
                    if (choice.Item1 != null)
                    {
                        row["item_id"] = choice.Item1.GetUniqueID();
                        row["item_name"] = choice.Item1.name;
                    }

                    described.Add(row);
                }
            }
            catch
            {
            }

            return described;
        }

        public static Dictionary<string, object> GetBargainSnapshot()
        {
            try
            {
                BargainUIManager ui = BargainUIManager.Instance;
                if (ui == null) return null;

                var snapshot = new Dictionary<string, object>();
                snapshot["open"] = ui.IsOpen;
                snapshot["can_sell_discount"] = ui.CanOfferDiscount();
                snapshot["can_sell_markup"] = ui.CanOfferMarkup();
                snapshot["can_buy_markup"] = ui.CanOfferBuyingMarkup();
                snapshot["can_buy_discount"] = ui.CanDemandBuyingDiscount();
                snapshot["sell_discount_used"] = ui.DiscountOffered;
                snapshot["sell_markup_used"] = ui.MarkupOffered;
                snapshot["buy_markup_used"] = ui.BuyingMarkupOffered;
                snapshot["buy_discount_used"] = ui.BuyingDiscountOffered;
                return snapshot;
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

        public static Dictionary<string, object> GetDialogueSnapshot()
        {
            try
            {
                DialogUIManager ui = DialogUIManager.Instance;
                if (ui == null) return null;

                var snapshot = new Dictionary<string, object>();
                snapshot["choice"] = ui.IsChoice();
                snapshot["ended"] = ui.IsCurrentDialogEnd();
                Dialogue current = ui.GetCurrentDialog();
                snapshot["typing"] = current != null && ui.IsLineStillDisplaying(current);
                return snapshot;
            }
            catch
            {
                return null;
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
