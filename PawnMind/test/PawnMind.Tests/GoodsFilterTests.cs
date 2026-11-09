using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class GoodsFilterTests
    {
        private static List<ItemRecord> Items()
        {
            return new List<ItemRecord>
            {
                new ItemRecord { Id = 1, Name = "顾客货", Owned = false },
                new ItemRecord { Id = 2, Name = "可卖货", Owned = true, Sellable = true },
                new ItemRecord { Id = 3, Name = "不可卖", Owned = true, Sellable = false }
            };
        }

        [Test]
        public static void Client_Scope_Returns_NonOwned()
        {
            List<ItemRecord> result = GoodsFilter.Filter(Items(), GoodsScope.Client);
            Assert.Equal(1, result.Count, "one client item");
            Assert.Equal(1, result[0].Id, "client good");
        }

        [Test]
        public static void Sellable_Scope_Returns_Owned_And_Sellable()
        {
            List<ItemRecord> result = GoodsFilter.Filter(Items(), GoodsScope.Sellable);
            Assert.Equal(1, result.Count, "one sellable item");
            Assert.Equal(2, result[0].Id, "sellable good");
        }

        [Test]
        public static void All_Scope_Returns_Everything()
        {
            List<ItemRecord> result = GoodsFilter.Filter(Items(), GoodsScope.All);
            Assert.Equal(3, result.Count, "all items");
        }
    }
}
