using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class ShapeCell
    {
        public int X;
        public int Y;
        public int Layer;
    }

    /// <summary>
    /// Converts a real grid shape (including holes and layers) into a compact mask
    /// so callers never have to reason about irregular items as rectangles.
    /// </summary>
    public static class ShapeMask
    {
        public static string[] Rows(int minX, int minY, int maxX, int maxY, Func<int, int, int> layerAt)
        {
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            if (width <= 0 || height <= 0) return new string[0];

            var rows = new string[height];
            for (int y = minY; y <= maxY; y++)
            {
                var chars = new char[width];
                for (int x = minX; x <= maxX; x++)
                {
                    int layer = layerAt == null ? 0 : layerAt(x, y);
                    chars[x - minX] = layer <= 0 ? '.' : (layer <= 9 ? (char)('0' + layer) : '?');
                }

                rows[y - minY] = new string(chars);
            }

            return rows;
        }

        public static List<ShapeCell> Cells(int minX, int minY, int maxX, int maxY, Func<int, int, int> layerAt)
        {
            var cells = new List<ShapeCell>();
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int layer = layerAt == null ? 0 : layerAt(x, y);
                    if (layer <= 0) continue;
                    cells.Add(new ShapeCell { X = x, Y = y, Layer = layer });
                }
            }

            return cells;
        }
    }
}
