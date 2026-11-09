using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum GoodsScope
    {
        All,
        Client,
        Sellable
    }

    public static class GoodsFilter
    {
        public static List<ItemRecord> Filter(IEnumerable<ItemRecord> items, GoodsScope scope)
        {
            var result = new List<ItemRecord>();
            if (items == null) return result;

            foreach (ItemRecord item in items)
            {
                if (item == null) continue;
                if (scope == GoodsScope.Client && !item.Owned) result.Add(item);
                else if (scope == GoodsScope.Sellable && item.Owned && item.Sellable) result.Add(item);
                else if (scope == GoodsScope.All) result.Add(item);
            }

            return result;
        }
    }
}
