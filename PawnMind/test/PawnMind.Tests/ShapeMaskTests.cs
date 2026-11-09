using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ShapeMaskTests
    {
        private static int L(int x, int y)
        {
            // 3x2 bounding box, cells: (0,0), (1,0), (0,1)
            if (x == 0 && y == 0) return 1;
            if (x == 1 && y == 0) return 1;
            if (x == 0 && y == 1) return 1;
            return 0;
        }

        [Test]
        public static void Mask_Rows_Keep_Irregular_Holes()
        {
            string[] rows = ShapeMask.Rows(0, 0, 2, 1, L);
            Assert.Equal(2, rows.Length, "row count");
            Assert.Equal("11.", rows[0], "first row");
            Assert.Equal("1..", rows[1], "second row");
        }

        [Test]
        public static void Mask_Cells_Only_Include_Filled_Cells()
        {
            List<ShapeCell> cells = ShapeMask.Cells(0, 0, 2, 1, L);
            Assert.Equal(3, cells.Count, "three filled cells");
            Assert.NotNull(Find(cells, 1, 0), "cell 1,0");
            Assert.Null(Find(cells, 2, 0), "hole 2,0 absent");
        }

        [Test]
        public static void Mask_Preserves_Layer()
        {
            string[] rows = ShapeMask.Rows(0, 0, 0, 0, delegate(int x, int y) { return 2; });
            Assert.Equal("2", rows[0], "layer digit");
        }

        private static ShapeCell Find(List<ShapeCell> cells, int x, int y)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].X == x && cells[i].Y == y) return cells[i];
            }

            return null;
        }
    }
}
