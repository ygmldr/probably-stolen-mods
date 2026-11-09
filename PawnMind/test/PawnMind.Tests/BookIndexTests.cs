using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class BookIndexTests
    {
        private static List<BookEntry> Books()
        {
            return new List<BookEntry>
            {
                new BookEntry { ItemId = "water_guide", BookId = "WATER_BOOK", Title = "Water Trading Guide", Kind = "vanilla" },
                new BookEntry { ItemId = "ingot_book", BookId = "ingot_book", Title = "Metallurgy", Kind = "paper" }
            };
        }

        [Test]
        public static void Find_By_Item_Id()
        {
            BookEntry entry = BookIndex.Find(Books(), "water_guide");
            Assert.NotNull(entry, "found");
            Assert.Equal("WATER_BOOK", entry.BookId, "book id");
        }

        [Test]
        public static void Find_By_Book_Id_Is_Case_Insensitive()
        {
            BookEntry entry = BookIndex.Find(Books(), "water_book");
            Assert.NotNull(entry, "found by book id");
            Assert.Equal("water_guide", entry.ItemId, "item id");
        }

        [Test]
        public static void Find_Unknown_Returns_Null()
        {
            Assert.Null(BookIndex.Find(Books(), "missing"), "missing");
        }
    }
}
