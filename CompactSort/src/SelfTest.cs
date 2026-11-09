using System;
using System.Collections.Generic;

namespace CompactSort
{
    /// <summary>
    /// Read-only in-game diagnostics. It never moves items; it checks the real Unity scene
    /// bindings that fake-inventory adapter tests cannot cover, and emits machine-readable
    /// lines for test/ingame/check_selftest.py.
    /// </summary>
    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;

        public static void Run()
        {
            _pass = 0;
            _fail = 0;
            ModLog.I("[CompactSort][TEST] start version=" +
                     (CompactSortMod.Manifest == null ? "?" : CompactSortMod.Manifest.ModVersion));

            var entry = EmporiumEntry.Instance;
            if (entry == null)
            {
                Check("store_loaded", false, "EmporiumEntry.Instance is null; run this inside the store");
                Summary();
                return;
            }

            Check("global_pool_disabled", !SortFeatures.GlobalPoolEnabled,
                "global pool should be disabled in this build");
            Check("per_container_disabled", !SortFeatures.PerContainerSortEnabled,
                "shortcut-driven full sort should be disabled in this build");

            var blocked = new List<GameGridInventory>();
            AddBlocked(blocked, "showcase", entry.showcaseElement);
            AddBlocked(blocked, "trashcan", entry.trashcanInvElement);
            AddBlocked(blocked, "front_counter", entry.frontInvinvElement);
            AddBlocked(blocked, "back_counter", entry.backInvinvElement);
            AddBlocked(blocked, "internal_counter", entry.backInvinvElementCounter);

            for (int i = 0; i < blocked.Count; i++)
            {
                Check("blocked_" + NameOf(blocked[i]),
                    CompactSorter.IsAutoSortBlocked(blocked[i]),
                    Describe(blocked[i]));
            }

            if (entry.hiddenElement == null)
            {
                Check("hidden_present", false, "null");
            }
            else
            {
                Check("hidden_present", true, Describe(entry.hiddenElement));
                Check("hidden_allowed",
                    !CompactSorter.IsAutoSortBlocked(entry.hiddenElement),
                    Describe(entry.hiddenElement));
            }

            List<GameGridInventory> containers = null;
            try
            {
                containers = CompactSorter.CollectAutoContainers();
            }
            catch (Exception e)
            {
                Check("collect", false, e.GetType().Name + ": " + e.Message);
            }

            if (containers != null)
            {
                Check("containers_found", containers.Count > 0, "count=" + containers.Count);
                for (int b = 0; b < blocked.Count; b++)
                {
                    Check("not_collected_" + NameOf(blocked[b]),
                        !Contains(containers, blocked[b]),
                        Describe(blocked[b]));
                }

                // Hidden stash is allowed, but empty containers are filtered from the
                // collection, so only require it when it actually contains items.
                if (entry.hiddenElement != null && entry.hiddenElement.childItems.Count > 0)
                {
                    Check("hidden_collected", Contains(containers, entry.hiddenElement),
                        Describe(entry.hiddenElement));
                }

                int items = 0;
                for (int i = 0; i < containers.Count; i++)
                {
                    var inv = containers[i];
                    string name = "container_" + i;
                    Check(name + "_shape", inv.inventoryShape != null,
                        inv.inventoryShape == null ? "null" :
                        inv.inventoryShape.width + "x" + inv.inventoryShape.height);

                    if (inv.inventoryShape == null) continue;
                    items += inv.childItems.Count;
                    Check(name + "_items", inv.childItems.Count > 0, "count=" + inv.childItems.Count);
                    Check(name + "_no_overlap", !CompactSorter.HasOverlap(inv), Describe(inv));
                    Check(name + "_inside", AllItemsInside(inv), Describe(inv));
                }
                ModLog.I("[CompactSort][TEST] audited containers=" + containers.Count +
                         " items=" + items);
            }

            Summary();
        }

        private static void AddBlocked(List<GameGridInventory> list, string name,
            GameGridInventory inventory)
        {
            if (inventory == null)
            {
                Check("field_" + name, false, "null");
                return;
            }
            list.Add(inventory);
            Check("field_" + name, true, Describe(inventory));
        }

        private static bool AllItemsInside(GameGridInventory inventory)
        {
            int w = inventory.inventoryShape.width;
            int h = inventory.inventoryShape.height;
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var item = inventory.childItems[i];
                if (item == null) continue;
                var shape = item.modifiedShape;
                if (shape == null) return false;
                if (shape.minX < 0 || shape.minY < 0 || shape.maxX >= w || shape.maxY >= h)
                    return false;
            }
            return true;
        }

        private static bool Contains(List<GameGridInventory> list, GameGridInventory inventory)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], inventory)) return true;
            return false;
        }

        private static string NameOf(GameGridInventory inventory)
        {
            if (inventory == null) return "null";
            string id = inventory.identifier;
            if (!string.IsNullOrEmpty(id)) return id;
            return "inv_" + inventory.GetHashCode();
        }

        private static string Describe(GameGridInventory inventory)
        {
            if (inventory == null) return "null";
            int w = inventory.inventoryShape == null ? -1 : inventory.inventoryShape.width;
            int h = inventory.inventoryShape == null ? -1 : inventory.inventoryShape.height;
            return "id=" + NameOf(inventory) + " size=" + w + "x" + h +
                   " items=" + (inventory.childItems == null ? -1 : inventory.childItems.Count);
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++;
            else _fail++;
            ModLog.I("[CompactSort][TEST] " + (ok ? "PASS" : "FAIL") +
                     " case=" + name + " detail=" + (detail ?? ""));
        }

        private static void Summary()
        {
            ModLog.I("[CompactSort][TEST] SUMMARY pass=" + _pass + " fail=" + _fail);
            try
            {
                if (StoreUIManager.Instance != null)
                {
                    StoreUIManager.Instance.Notify(
                        "紧凑整理自检 " + (_fail == 0 ? "PASS" : "FAIL") +
                        " " + _pass + "/" + (_pass + _fail));
                }
            }
            catch (Exception e)
            {
                ModLog.W("[CompactSort] self-test notify failed: " + e.Message);
            }
        }
    }
}
