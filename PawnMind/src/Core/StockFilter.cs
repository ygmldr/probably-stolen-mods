using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class StockItem
    {
        public string Identifier;
        public int Price;
        public int Stock;
    }

    public static class StockFilter
    {
        public static List<StockItem> InStock(IEnumerable<StockItem> items)
        {
            var result = new List<StockItem>();
            if (items == null) return result;

            foreach (StockItem item in items)
            {
                if (item == null || item.Stock <= 0) continue;
                result.Add(item);
            }

            return result;
        }
    }
}
