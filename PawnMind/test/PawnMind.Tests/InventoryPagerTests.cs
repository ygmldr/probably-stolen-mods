using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class InventoryPagerTests
    {
        private static List<ItemRecord> Items()
        {
            return new List<ItemRecord>
            {
                new ItemRecord { Id = 1, Name = "绷带", Identifier = "bandage", Value = 16 },
                new ItemRecord { Id = 2, Name = "线缆", Identifier = "wire", Value = 5 },
                new ItemRecord { Id = 3, Name = "电子元件", Identifier = "electronic", Value = 33 },
                new ItemRecord { Id = 4, Name = "绷带", Identifier = "bandage", Value = 16 },
                new ItemRecord { Id = 5, Name = "撬棍", Identifier = "crowbar", Value = 75 }
            };
        }

        [Test]
        public static void Pager_Returns_First_Page()
        {
            PagedItems page = InventoryPager.Page(Items(), new InventoryQuery { Page = 1, PageSize = 2 });
            Assert.Equal(5, page.Total, "total items");
            Assert.Equal(3, page.Pages, "page count");
            Assert.Equal(2, page.Items.Count, "first page size");
            Assert.Equal(1, page.Items[0].Id, "first item");
        }

        [Test]
        public static void Pager_Returns_Last_Partial_Page()
        {
            PagedItems page = InventoryPager.Page(Items(), new InventoryQuery { Page = 3, PageSize = 2 });
            Assert.Equal(1, page.Items.Count, "last page size");
            Assert.Equal(5, page.Items[0].Id, "last item");
        }

        [Test]
        public static void Pager_Beyond_End_Is_Empty()
        {
            PagedItems page = InventoryPager.Page(Items(), new InventoryQuery { Page = 99, PageSize = 2 });
            Assert.Equal(0, page.Items.Count, "empty page");
            Assert.Equal(5, page.Total, "total still reported");
        }

        [Test]
        public static void Pager_Search_Matches_Name_And_Identifier()
        {
            PagedItems byName = InventoryPager.Page(Items(), new InventoryQuery { Search = "绷带" });
            Assert.Equal(2, byName.Total, "two bandages");

            PagedItems byId = InventoryPager.Page(Items(), new InventoryQuery { Search = "WIRE" });
            Assert.Equal(1, byId.Total, "identifier search is case-insensitive");
            Assert.Equal(2, byId.Items[0].Id, "wire id");
        }

        [Test]
        public static void Pager_Clamps_Page_Size()
        {
            PagedItems zero = InventoryPager.Page(Items(), new InventoryQuery { PageSize = 0 });
            Assert.Equal(20, zero.PageSize, "zero clamps to default");

            PagedItems huge = InventoryPager.Page(Items(), new InventoryQuery { PageSize = 1000 });
            Assert.Equal(100, huge.PageSize, "huge clamps to 100");
        }

        [Test]
        public static void Pager_Search_And_Paging_Compose()
        {
            PagedItems page = InventoryPager.Page(Items(), new InventoryQuery { Search = "绷带", Page = 2, PageSize = 1 });
            Assert.Equal(2, page.Total, "filtered total");
            Assert.Equal(1, page.Items.Count, "second filtered page");
            Assert.Equal(4, page.Items[0].Id, "second bandage");
        }
    }
}
