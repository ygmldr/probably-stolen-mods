using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class InventoryService
    {
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "main", "背包" },
            { "front", "前台" },
            { "back", "后台" },
            { "showcase", "展示柜" },
            { "hidden", "暗格" },
            { "afterhour", "夜间背包" },
            { "trash", "垃圾桶" },
            { "sold", "已售" }
        };

        public static string ListContainersJson()
        {
            var containers = new List<Dictionary<string, object>>();
            List<string> ids = GameState.GetContainerIds();
            for (int i = 0; i < ids.Count; i++)
            {
                GameInventory inventory = GameState.GetContainer(ids[i]);
                var row = new Dictionary<string, object>();
                row["id"] = ids[i];
                row["label"] = Labels.ContainsKey(ids[i]) ? Labels[ids[i]] : ids[i];
                row["count"] = inventory == null ? 0 : inventory.childItems.Count;
                containers.Add(row);
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "containers", containers }
            });
        }

        public static string ListJson(string container, string search, int page, int pageSize)
        {
            string id = string.IsNullOrEmpty(container) ? "main" : container.ToLowerInvariant();
            GameInventory inventory = GameState.GetContainer(id);
            if (inventory == null)
            {
                return "{\"ok\":false,\"error\":\"unknown_container\"}";
            }

            var records = new List<ItemRecord>();
            List<GameItem> items = inventory.childItems;
            for (int i = 0; i < items.Count; i++)
            {
                ItemRecord record = Describe(id, items[i]);
                if (record != null) records.Add(record);
            }

            PagedItems paged = InventoryPager.Page(records, new InventoryQuery
            {
                Container = id,
                Search = search ?? string.Empty,
                Page = page,
                PageSize = pageSize
            });

            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < paged.Items.Count; i++) rows.Add(Row(paged.Items[i]));

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "container", id },
                { "total", paged.Total },
                { "page", paged.Page },
                { "page_size", paged.PageSize },
                { "pages", paged.Pages },
                { "items", rows }
            });
        }

        public static string GetItemJson(int itemId)
        {
            List<string> ids = GameState.GetContainerIds();
            for (int i = 0; i < ids.Count; i++)
            {
                GameInventory inventory = GameState.GetContainer(ids[i]);
                if (inventory == null) continue;

                List<GameItem> items = inventory.childItems;
                for (int j = 0; j < items.Count; j++)
                {
                    GameItem item = items[j];
                    if (item == null || item.GetUniqueID() != itemId) continue;

                    var row = new Dictionary<string, object>();
                    row["ok"] = true;
                    row["container"] = ids[i];
                    row["id"] = itemId;
                    row["name"] = item.name;
                    row["identifier"] = item.identifier;
                    row["value"] = item.GetNegociatedValue();
                    row["count"] = item.unitCount;
                    row["owned"] = GameState.IsOwned(item);
                    row["locked"] = ItemPinHelper.IsPinned(item);
                    row["sellable"] = GameState.CanSellItem(item);
                    row["short_description"] = item.shortDescription;
                    row["long_description"] = item.longDescription;
                    row["flavor_text"] = item.flavorText;
                    if (item.modifiedShape != null && item.modifiedShape != null)
                    {
                        row["width"] = item.modifiedShape.globalWidth;
                        row["height"] = item.modifiedShape.globalHeight;
                    }

                    row["x"] = item.modifiedShape != null ? item.modifiedShape.minX : item.modifiedXOrigin;
                    row["y"] = item.modifiedShape != null ? item.modifiedShape.minY : item.modifiedYOrigin;
                    if (item.modifiedShape != null)
                    {
                        row["mask"] = ShapeMask.Rows(item.modifiedShape.minX, item.modifiedShape.minY,
                            item.modifiedShape.maxX, item.modifiedShape.maxY,
                            delegate(int x, int y) { return item.modifiedShape.Get(x, y); });
                        var cells = new List<Dictionary<string, object>>();
                        List<ShapeCell> shapeCells = ShapeMask.Cells(item.modifiedShape.minX, item.modifiedShape.minY,
                            item.modifiedShape.maxX, item.modifiedShape.maxY,
                            delegate(int x, int y) { return item.modifiedShape.Get(x, y); });
                        for (int k = 0; k < shapeCells.Count; k++)
                        {
                            cells.Add(new Dictionary<string, object>
                            {
                                { "x", shapeCells[k].X },
                                { "y", shapeCells[k].Y },
                                { "layer", shapeCells[k].Layer }
                            });
                        }

                        row["cells"] = cells;
                    }
                    return JsonConvert.SerializeObject(row);
                }
            }

            return "{\"ok\":false,\"error\":\"item_not_found\"}";
        }

        public static string LayoutJson(string container)
        {
            string id = string.IsNullOrEmpty(container) ? "main" : container.ToLowerInvariant();
            GameInventory inventory = GameState.GetContainer(id);
            var grid = inventory as GameGridInventory;
            if (grid == null) return "{\"ok\":false,\"error\":\"not_a_grid_container\"}";

            int width = grid.inventoryShape.width;
            int height = grid.inventoryShape.height;
            var cells = new int[height][];
            for (int y = 0; y < height; y++)
            {
                cells[y] = new int[width];
            }

            List<GameItem> items = grid.childItems;
            for (int i = 0; i < items.Count; i++)
            {
                GameItem item = items[i];
                if (item == null || item.modifiedShape == null) continue;

                GridShape shape = item.modifiedShape;
                int uniqueId = item.GetUniqueID();
                for (int y = shape.minY; y <= shape.maxY; y++)
                {
                    if (y < 0 || y >= height) continue;
                    for (int x = shape.minX; x <= shape.maxX; x++)
                    {
                        if (x < 0 || x >= width) continue;
                        if (shape.Get(x, y) != 0) cells[y][x] = uniqueId;
                    }
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "container", id },
                { "width", width },
                { "height", height },
                { "rows", cells },
                { "items", DescribeAll(id, items) }
            });
        }

        private static List<Dictionary<string, object>> DescribeAll(string container, List<GameItem> items)
        {
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < items.Count; i++)
            {
                ItemRecord record = Describe(container, items[i]);
                if (record != null) rows.Add(Row(record));
            }

            return rows;
        }

        private static Dictionary<string, object> Row(ItemRecord record)
        {
            var row = new Dictionary<string, object>();
            row["id"] = record.Id;
            row["name"] = record.Name;
            row["identifier"] = record.Identifier;
            row["container"] = record.Container;
            row["value"] = record.Value;
            row["count"] = record.Count;
            row["owned"] = record.Owned;
            row["locked"] = record.Locked;
            row["x"] = record.X;
            row["y"] = record.Y;
            row["width"] = record.Width;
            row["height"] = record.Height;
            row["mask"] = record.Mask ?? new string[0];

            var cells = new List<Dictionary<string, object>>();
            for (int i = 0; i < record.Cells.Count; i++)
            {
                cells.Add(new Dictionary<string, object>
                {
                    { "x", record.Cells[i].X },
                    { "y", record.Cells[i].Y },
                    { "layer", record.Cells[i].Layer }
                });
            }

            row["cells"] = cells;
            return row;
        }

        private static ItemRecord Describe(string container, GameItem item)
        {
            if (item == null) return null;

            return new ItemRecord
            {
                Id = item.GetUniqueID(),
                Name = item.name ?? string.Empty,
                Identifier = item.identifier ?? string.Empty,
                Container = container,
                Value = item.GetNegociatedValue(),
                Count = item.unitCount,
                Owned = GameState.IsOwned(item),
                Locked = ItemPinHelper.IsPinned(item),
                X = item.modifiedShape != null ? item.modifiedShape.minX : item.modifiedXOrigin,
                Y = item.modifiedShape != null ? item.modifiedShape.minY : item.modifiedYOrigin,
                Width = item.modifiedShape != null ? item.modifiedShape.globalWidth : 0,
                Height = item.modifiedShape != null ? item.modifiedShape.globalHeight : 0,
                Mask = item.modifiedShape == null ? new string[0] : ShapeMask.Rows(
                    item.modifiedShape.minX, item.modifiedShape.minY,
                    item.modifiedShape.maxX, item.modifiedShape.maxY,
                    delegate(int x, int y) { return item.modifiedShape.Get(x, y); }),
                Cells = item.modifiedShape == null ? new List<ShapeCell>() : ShapeMask.Cells(
                    item.modifiedShape.minX, item.modifiedShape.minY,
                    item.modifiedShape.maxX, item.modifiedShape.maxY,
                    delegate(int x, int y) { return item.modifiedShape.Get(x, y); })
            };
        }
    }
}
