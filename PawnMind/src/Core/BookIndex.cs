using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class BookEntry
    {
        public string ItemId = string.Empty;
        public string BookId = string.Empty;
        public string Title = string.Empty;
        public string Kind = string.Empty;
        public int PageCount;
    }

    public static class BookIndex
    {
        public static BookEntry Find(IEnumerable<BookEntry> books, string key)
        {
            if (books == null || string.IsNullOrEmpty(key)) return null;

            foreach (BookEntry book in books)
            {
                if (book == null) continue;
                if (string.Equals(book.ItemId, key, StringComparison.OrdinalIgnoreCase)) return book;
                if (string.Equals(book.BookId, key, StringComparison.OrdinalIgnoreCase)) return book;
            }

            return null;
        }
    }
}
