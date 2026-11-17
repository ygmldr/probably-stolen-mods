using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal sealed class MachineSlot
    {
        public int Index;
        public GameInventory Inventory;
        public string Type;
    }

    internal static class MachineService
    {
        public static string ListJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                List<GameItem> machines = EmporiumEntry.Instance.GetAllOwnedItemsByTag("STANDARD_MACHINE_TAG");
                for (int i = 0; i < machines.Count; i++)
                {
                    GameItem machine = machines[i];
                    if (machine == null) continue;

                    List<MachineSlot> slots = Slots(machine);
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", machine.GetUniqueID() },
                        { "name", machine.name },
                        { "identifier", machine.identifier },
                        { "state", SafeState(machine) },
                        { "progress", Progress(machine) },
                        { "slot_count", slots.Count },
                        { "slots", SlotSummary(slots) }
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"machine_list_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "machines", rows }
            });
        }

        public static string GrowStatusJson(int machineId)
        {
            GameItem machine = GameState.FindItemAnywhere(machineId);
            if (machine == null) return "{\"ok\":false,\"error\":\"machine_not_found\"}";

            string growStatus = "";
            string water = "";
            string seed = "";
            try { growStatus = MachineHydroponic.GetGrowStatus(machine); } catch { }
            try
            {
                GameItem waterItem = MachineHydroponic.GetWaterSlotItem(machine);
                if (waterItem != null) water = waterItem.name;
            }
            catch { }
            try
            {
                GameItem seedItem = MachineHydroponic.GetInputSlot(machine).childItem;
                if (seedItem != null) seed = seedItem.name;
            }
            catch { }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "id", machineId },
                { "state", SafeState(machine) },
                { "progress", Progress(machine) },
                { "grow_status", growStatus },
                { "water", water },
                { "seed", seed }
            });
        }

        public static string StatusJson(int machineId)
        {
            GameItem machine = GameState.FindItemAnywhere(machineId);
            if (machine == null) return "{\"ok\":false,\"error\":\"machine_not_found\"}";

            List<MachineSlot> slots = Slots(machine);
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < slots.Count; i++)
            {
                var items = new List<Dictionary<string, object>>();
                List<GameItem> children = slots[i].Inventory.childItems;
                for (int j = 0; j < children.Count; j++)
                {
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", children[j].GetUniqueID() },
                        { "name", children[j].name },
                        { "identifier", children[j].identifier },
                        { "count", children[j].unitCount }
                    });
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "index", slots[i].Index },
                    { "type", slots[i].Type },
                    { "count", children.Count },
                    { "items", items }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "id", machine.GetUniqueID() },
                { "name", machine.name },
                { "state", SafeState(machine) },
                { "progress", Progress(machine) },
                { "slots", rows }
            });
        }

        public static string InsertJson(int machineId, int slotIndex, int itemId)
        {
            GameItem machine = GameState.FindItemAnywhere(machineId);
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (machine == null)
                return "{\"ok\":false,\"error\":\"machine_not_found\",\"dispatched\":false}";
            if (item == null)
                return "{\"ok\":false,\"error\":\"item_not_found\",\"dispatched\":false}";

            MachineSlot slot = FindSlot(machine, slotIndex);
            if (slot == null)
                return "{\"ok\":false,\"error\":\"slot_not_found\",\"dispatched\":false}";

            // R4/F4b: the inventory transfer was attempted from here on.
            GameInventory moved = slot.Inventory.TryAcceptAll(item);
            if (moved == null || item.parentInventory != slot.Inventory)
                return "{\"ok\":false,\"error\":\"cannot_insert\",\"dispatched\":true}";

            return "{\"ok\":true,\"dispatched\":true,\"slot\":" + slotIndex + "}";
        }

        public static string CollectJson(int machineId, int slotIndex)
        {
            GameItem machine = GameState.FindItemAnywhere(machineId);
            if (machine == null)
                return "{\"ok\":false,\"error\":\"machine_not_found\",\"dispatched\":false}";

            MachineSlot slot = FindSlot(machine, slotIndex);
            if (slot == null)
                return "{\"ok\":false,\"error\":\"slot_not_found\",\"dispatched\":false}";

            GameInventory main = GameState.GetContainer("main");
            if (main == null)
                return "{\"ok\":false,\"error\":\"no_main_inventory\",\"dispatched\":false}";

            List<GameItem> items = new List<GameItem>(slot.Inventory.childItems);
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;

                GameInventory moved = main.TryAcceptAll(item);
                if (moved == null || item.parentInventory != main) continue;

                rows.Add(new Dictionary<string, object>
                {
                    { "id", item.GetUniqueID() },
                    { "name", item.name }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "dispatched", true },
                { "moved", rows }
            });
        }

        private static List<MachineSlot> Slots(GameItem machine)
        {
            var slots = new List<MachineSlot>();
            for (int i = 0; i < 24; i++)
            {
                GraphNodeStorage storage = null;
                try { storage = MachineHelper.GetBodyStorage(machine, i); }
                catch { }
                if (storage == null) continue;

                var inventory = storage as GameInventory;
                if (inventory == null) continue;

                slots.Add(new MachineSlot
                {
                    Index = i,
                    Inventory = inventory,
                    Type = inventory is GameSlotInventory ? "slot" : "grid"
                });
            }

            return slots;
        }

        private static MachineSlot FindSlot(GameItem machine, int index)
        {
            List<MachineSlot> slots = Slots(machine);
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Index == index) return slots[i];
            }

            return null;
        }

        private static List<Dictionary<string, object>> SlotSummary(List<MachineSlot> slots)
        {
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < slots.Count; i++)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "index", slots[i].Index },
                    { "type", slots[i].Type },
                    { "count", slots[i].Inventory.childItems.Count }
                });
            }

            return rows;
        }

        private static string SafeState(GameItem machine)
        {
            try { return MachineProgressHelper.GetMachineState(machine); }
            catch { return ""; }
        }

        private static int Progress(GameItem machine)
        {
            try
            {
                int current = machine.GetTagReadonly("MACHINE_PROGRESS_CURRENT_TAG").GetInt();
                int target = machine.GetTagReadonly("MACHINE_PROGRESS_TARGET_TAG").GetInt();
                return MachineProgress.Percent(current, target);
            }
            catch
            {
                return 0;
            }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
