using System;

namespace PawnMind.Core
{
    public static class PlacementMath
    {
        /// <summary>Bottom-right anchor in a top-left origin grid, accounting for rotation.</summary>
        public static void BottomRight(int gridWidth, int gridHeight, int itemWidth, int itemHeight,
            bool rotated, out int x, out int y)
        {
            int width = rotated ? itemHeight : itemWidth;
            int height = rotated ? itemWidth : itemHeight;
            x = Math.Max(0, gridWidth - width);
            y = Math.Max(0, gridHeight - height);
        }
    }
}
