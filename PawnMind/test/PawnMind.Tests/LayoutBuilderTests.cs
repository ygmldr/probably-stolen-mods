using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class LayoutBuilderTests
    {
        private static LayoutShape Shape(int id, int minX, int minY, int width, int height, params int[] layers)
        {
            return new LayoutShape
            {
                Id = id,
                MinX = minX,
                MinY = minY,
                Width = width,
                Height = height,
                Layers = layers
            };
        }

        [Test]
        public static void Layout_Fills_Rectangle()
        {
            LayoutResult result = LayoutBuilder.Build(4, 3, new List<LayoutShape>
            {
                Shape(7, 0, 0, 2, 2, 1, 1, 1, 1)
            });

            Assert.Equal(7, result.Rows[0][0], "cell 0,0");
            Assert.Equal(7, result.Rows[1][1], "cell 1,1");
            Assert.Equal(0, result.Rows[0][2], "empty cell");
            Assert.Equal(4, result.Cells.Count, "four occupied cells");
        }

        [Test]
        public static void Layout_Keeps_Holes()
        {
            LayoutResult result = LayoutBuilder.Build(3, 2, new List<LayoutShape>
            {
                Shape(7, 0, 0, 2, 2, 1, 1, 1, 0)
            });

            Assert.Equal(7, result.Rows[0][0], "filled");
            Assert.Equal(7, result.Rows[0][1], "filled");
            Assert.Equal(7, result.Rows[1][0], "filled");
            Assert.Equal(0, result.Rows[1][1], "hole");
        }

        [Test]
        public static void Layout_Higher_Layer_Wins_Top_But_Keeps_All_Cells()
        {
            LayoutResult result = LayoutBuilder.Build(2, 2, new List<LayoutShape>
            {
                Shape(1, 0, 0, 1, 1, 1),
                Shape(2, 0, 0, 1, 1, 2)
            });

            Assert.Equal(2, result.Rows[0][0], "top layer id");
            Assert.Equal(2, result.Cells.Count, "both layers kept");
        }

        [Test]
        public static void Layout_Clips_Out_Of_Bounds_Cells()
        {
            LayoutResult result = LayoutBuilder.Build(2, 2, new List<LayoutShape>
            {
                Shape(9, 1, 1, 2, 2, 1, 1, 1, 1)
            });

            Assert.Equal(9, result.Rows[1][1], "inside cell");
            Assert.Equal(1, result.Cells.Count, "only inside cell");
        }
    }
}
