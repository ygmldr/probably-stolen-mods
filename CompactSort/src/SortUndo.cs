using System;
using System.Collections.Generic;

namespace CompactSort
{
    internal sealed class SortUndoEntry
    {
        public GameItem Item;
        public GameInventory Parent;
        public GridShape Shape;
    }

    /// <summary>
    /// Mod-owned undo snapshots. Keeping these outside InventorySortHelper's private
    /// dictionary lets the mod support undo without private reflection, which the next
    /// game update retires.
    /// </summary>
    internal static class ModUndo
    {
        private static readonly Dictionary<GameGridInventory, List<SortUndoEntry>> Snapshots =
            new Dictionary<GameGridInventory, List<SortUndoEntry>>();

        public static void Store(GameGridInventory inventory, List<SortUndoEntry> entries)
        {
            if (inventory == null) return;
            if (entries == null || entries.Count == 0)
            {
                Snapshots.Remove(inventory);
                return;
            }
            var copy = new List<SortUndoEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || e.Item == null || e.Shape == null) continue;
                copy.Add(new SortUndoEntry
                {
                    Item = e.Item,
                    Parent = e.Parent,
                    Shape = e.Shape.Clone()
                });
            }
            if (copy.Count == 0) Snapshots.Remove(inventory);
            else Snapshots[inventory] = copy;
        }

        public static void Clear(GameGridInventory inventory)
        {
            if (inventory != null) Snapshots.Remove(inventory);
        }

        public static bool Has(GameGridInventory inventory)
        {
            return inventory != null && Snapshots.ContainsKey(inventory);
        }

        public static List<SortUndoEntry> Get(GameGridInventory inventory)
        {
            if (inventory == null) return null;
            return Snapshots.TryGetValue(inventory, out var list) ? list : null;
        }

        public static void Remove(GameGridInventory inventory)
        {
            if (inventory != null) Snapshots.Remove(inventory);
        }
    }
}
