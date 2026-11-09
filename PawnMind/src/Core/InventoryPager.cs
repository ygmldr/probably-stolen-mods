using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class ItemRecord
    {
        public int Id;
        public string Name = string.Empty;
        public string Identifier = string.Empty;
        public string Container = string.Empty;
        public long Value;
        public int Count;
        public bool Owned;
        public bool Locked;
        public bool Sellable;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public string[] Mask = new string[0];
        public List<ShapeCell> Cells = new List<ShapeCell>();
    }

    public sealed class InventoryQuery
    {
        public string Container = "main";
        public string Search = string.Empty;
        public int Page = 1;
        public int PageSize = InventoryPager.DefaultPageSize;
    }

    public sealed class PagedItems
    {
        public List<ItemRecord> Items = new List<ItemRecord>();
        public int Total;
        public int Page;
        public int PageSize;
        public int Pages;
    }

    public static class InventoryPager
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;

        public static PagedItems Page(IEnumerable<ItemRecord> items, InventoryQuery query)
        {
            if (query == null) query = new InventoryQuery();

            int pageSize = query.PageSize;
            if (pageSize <= 0) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;

            int page = query.Page < 1 ? 1 : query.Page;

            var filtered = new List<ItemRecord>();
            if (items != null)
            {
                string search = (query.Search ?? string.Empty).Trim();
                foreach (ItemRecord item in items)
                {
                    if (item == null) continue;
                    if (search.Length > 0 && !Matches(item, search)) continue;
                    filtered.Add(item);
                }
            }

            int pages = filtered.Count == 0 ? 0 : (filtered.Count + pageSize - 1) / pageSize;
            var result = new PagedItems
            {
                Total = filtered.Count,
                Page = page,
                PageSize = pageSize,
                Pages = pages
            };

            int start = (page - 1) * pageSize;
            if (start >= filtered.Count) return result;

            int end = Math.Min(start + pageSize, filtered.Count);
            for (int i = start; i < end; i++) result.Items.Add(filtered[i]);
            return result;
        }

        private static bool Matches(ItemRecord item, string search)
        {
            if (item.Name != null && item.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return item.Identifier != null
                && item.Identifier.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
