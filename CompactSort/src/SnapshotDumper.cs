using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CompactSort
{
    /// <summary>
    /// Read-only dump of the player's grid containers. One file is written after each
    /// in-game sleep / new day so real layouts can be accumulated for the benchmark.
    /// </summary>
    internal static class SnapshotDumper
    {
        private sealed class Entry
        {
            public string Kind;
            public GameGridInventory Inventory;
        }

        public static void Capture(string source)
        {
            try
            {
                if (EmporiumEntry.Instance == null || PlayerStore.Instance == null) return;

                var entries = new List<Entry>();
                var seen = new HashSet<GameGridInventory>();
                Add(entries, seen, "warehouse", PlayerStore.Instance.GetStoreInventory());
                var entry = EmporiumEntry.Instance;
                Add(entries, seen, "main_inv", entry.invElement);
                Add(entries, seen, "front_counter", entry.frontInvinvElement);
                Add(entries, seen, "back_counter", entry.backInvinvElement);
                Add(entries, seen, "back_counter_internal", entry.backInvinvElementCounter);
                Add(entries, seen, "bazar_left", entry.bazarLeftinvElement);
                Add(entries, seen, "showcase", entry.showcaseElement);
                Add(entries, seen, "trashcan", entry.trashcanInvElement);
                Add(entries, seen, "hidden", entry.hiddenElement);
                Add(entries, seen, "hireling", entry.hirelingInv);
                Add(entries, seen, "afterhour", entry.afterhourInventory);

                var sb = new StringBuilder(64 * 1024);
                sb.Append("{\"version\":1,\"source\":\"");
                Json(sb, source);
                sb.Append("\",\"capturedAt\":\"");
                Json(sb, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                sb.Append("\",\"day\":");
                sb.Append(StoreStation.Instance == null
                    ? "0"
                    : StoreStation.Instance.dayCounter.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"containers\":[");

                int itemCount = 0;
                for (int ci = 0; ci < entries.Count; ci++)
                {
                    if (ci > 0) sb.Append(',');
                    var e = entries[ci];
                    var inv = e.Inventory;
                    var shape = inv.inventoryShape;
                    sb.Append("{\"kind\":\"");
                    Json(sb, e.Kind);
                    sb.Append("\",\"width\":");
                    sb.Append(shape.width.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"height\":");
                    sb.Append(shape.height.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"wall\":\"");
                    for (int y = 0; y < shape.height; y++)
                        for (int x = 0; x < shape.width; x++)
                            sb.Append(shape.Get(x, y) == 0 ? '0' : '1');
                    sb.Append("\",\"items\":[");
                    bool firstItem = true;

                    var items = inv.childItems;
                    for (int ii = 0; ii < items.Count; ii++)
                    {
                        var item = items[ii];
                        if (item == null) continue;
                        var itemShape = item.modifiedShape;
                        if (itemShape == null) continue;
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        itemCount++;
                        sb.Append("{\"identifier\":\"");
                        Json(sb, item.identifier ?? "");
                        sb.Append("\",\"key\":\"");
                        Json(sb, item.identifier ?? "");
                        sb.Append("\",\"category\":\"");
                        Json(sb, CompactSorter.CategoryOf(item) ?? "");
                        sb.Append("\",\"x\":");
                        sb.Append(itemShape.minX.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"y\":");
                        sb.Append(itemShape.minY.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"width\":");
                        sb.Append(itemShape.width.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"height\":");
                        sb.Append(itemShape.height.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"orientation\":");
                        sb.Append(itemShape.orientation.ToString(CultureInfo.InvariantCulture));
                        sb.Append(",\"mask\":\"");
                        AppendShapeMask(sb, itemShape, false);
                        sb.Append("\",\"layers\":\"");
                        AppendShapeMask(sb, itemShape, true);
                        sb.Append("\",\"anchored\":");
                        sb.Append(IsAnchored(item) ? "true" : "false");
                        sb.Append(",\"unitCount\":");
                        sb.Append(item.unitCount.ToString(CultureInfo.InvariantCulture));
                        sb.Append('}');
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");

                string dir = Path.Combine(Application.persistentDataPath, "CompactSortSnapshots");
                System.IO.Directory.CreateDirectory(dir);
                string day = StoreStation.Instance == null
                    ? "000"
                    : StoreStation.Instance.dayCounter.ToString("D3", CultureInfo.InvariantCulture);
                string file = Path.Combine(dir,
                    "snapshot-day-" + day + "-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                System.IO.File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                ModLog.I("[CompactSort] snapshot: " + file +
                         " containers=" + entries.Count + " items=" + itemCount);
            }
            catch (Exception e)
            {
                ModLog.W("[CompactSort] snapshot failed: " + e.Message);
            }
        }

        private static void Add(List<Entry> entries, HashSet<GameGridInventory> seen,
            string kind, GameGridInventory inventory)
        {
            if (inventory == null || inventory.inventoryShape == null) return;
            if (!seen.Add(inventory)) return;
            entries.Add(new Entry { Kind = kind, Inventory = inventory });
        }

        private static bool IsAnchored(GameItem item)
        {
            return ItemPinHelper.IsPinned(item) || DebrisHelper.IsDebris(item);
        }

        private static void AppendShapeMask(StringBuilder sb, GridShape shape, bool layers)
        {
            for (int y = 0; y < shape.height; y++)
            {
                for (int x = 0; x < shape.width; x++)
                {
                    byte value = shape.GetLocal(x, y);
                    if (layers)
                        sb.Append(value.ToString("X", CultureInfo.InvariantCulture));
                    else
                        sb.Append(value == 0 ? '0' : '1');
                }
            }
        }

        private static void Json(StringBuilder sb, string value)
        {
            if (value == null) return;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
        }
    }
}
