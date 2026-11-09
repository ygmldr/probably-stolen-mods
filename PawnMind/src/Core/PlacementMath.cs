using System;

namespace PawnMind.Core
{
    public static class PlacementMath
    {
        /// <summary>Bottom-right anchor in a top-left origin grid, accounting for rotation.</summary>
        public static void BottomRight(int gridWidth, int gridHeight, int itemWidth, int itemHeight,
            bool rotated, out int x, out int y)
        {
            BottomRight(gridWidth, gridHeight, itemWidth, itemHeight, rotated ? 1 : 0, out x, out y);
        }

        public static void BottomRight(int gridWidth, int gridHeight, int itemWidth, int itemHeight,
            int orientation, out int x, out int y)
        {
            int turns = ((orientation % 4) + 4) % 4;
            bool swap = (turns & 1) == 1;
            int width = swap ? itemHeight : itemWidth;
            int height = swap ? itemWidth : itemHeight;
            x = Math.Max(0, gridWidth - width);
            y = Math.Max(0, gridHeight - height);
        }
    }
}
