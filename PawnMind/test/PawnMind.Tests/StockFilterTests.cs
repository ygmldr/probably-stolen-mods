using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class StockFilterTests
    {
        private static List<StockItem> Stock()
        {
            return new List<StockItem>
            {
                new StockItem { Identifier = "bandage", Price = 20, Stock = 3 },
                new StockItem { Identifier = "wire", Price = 10, Stock = 0 },
                new StockItem { Identifier = "water", Price = 15, Stock = 1 }
            };
        }

        [Test]
        public static void InStock_Excludes_Zero()
        {
            List<StockItem> result = StockFilter.InStock(Stock());
            Assert.Equal(2, result.Count, "two in stock");
            Assert.Equal("bandage", result[0].Identifier, "first");
            Assert.Equal("water", result[1].Identifier, "second");
        }
    }
}
