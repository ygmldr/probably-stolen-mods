using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class LayoutDisplayTests
    {
        [Test]
        public static void Symbols_Are_Stable_And_Distinct()
        {
            Dictionary<int, char> symbols = LayoutDisplay.Symbols(new List<int> { 5, 7, 5 });
            Assert.Equal('A', symbols[5], "first id");
            Assert.Equal('B', symbols[7], "second id");
            Assert.Equal(2, symbols.Count, "deduplicated");
        }

        [Test]
        public static void Rows_Use_Symbols_And_Dots_For_Empty()
        {
            var symbols = new Dictionary<int, char> { { 5, 'A' }, { 7, 'B' } };
            string[] rows = LayoutDisplay.Rows(new[]
            {
                new[] { 5, 0, 7 }
            }, symbols);

            Assert.Equal("A.B", rows[0], "display row");
        }
    }
}
