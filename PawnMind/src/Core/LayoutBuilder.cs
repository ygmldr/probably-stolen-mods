using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class LayoutShape
    {
        public int Id;
        public int MinX;
        public int MinY;
        public int Width;
        public int Height;
        public int[] Layers;

        public int LayerAt(int x, int y)
        {
            int localX = x - MinX;
            int localY = y - MinY;
            if (localX < 0 || localY < 0 || localX >= Width || localY >= Height) return 0;
            if (Layers == null) return 0;

            int index = localY * Width + localX;
            return index >= 0 && index < Layers.Length ? Layers[index] : 0;
        }
    }

    public sealed class LayoutCell
    {
        public int X;
        public int Y;
        public int Id;
        public int Layer;
    }

    public sealed class LayoutResult
    {
        public int Width;
        public int Height;
        public int[][] Rows;
        public List<LayoutCell> Cells = new List<LayoutCell>();
    }

    public static class LayoutBuilder
    {
        public static LayoutResult Build(int width, int height, IEnumerable<LayoutShape> shapes)
        {
            var result = new LayoutResult
            {
                Width = width,
                Height = height,
                Rows = new int[height < 0 ? 0 : height][]
            };

            for (int y = 0; y < result.Rows.Length; y++) result.Rows[y] = new int[width < 0 ? 0 : width];

            var topLayers = new int[result.Rows.Length][];
            for (int y = 0; y < topLayers.Length; y++) topLayers[y] = new int[width < 0 ? 0 : width];

            if (shapes == null) return result;

            foreach (LayoutShape shape in shapes)
            {
                if (shape == null) continue;

                for (int y = shape.MinY; y < shape.MinY + shape.Height; y++)
                {
                    if (y < 0 || y >= height) continue;
                    for (int x = shape.MinX; x < shape.MinX + shape.Width; x++)
                    {
                        if (x < 0 || x >= width) continue;

                        int layer = shape.LayerAt(x, y);
                        if (layer <= 0) continue;

                        result.Cells.Add(new LayoutCell { X = x, Y = y, Id = shape.Id, Layer = layer });
                        if (layer >= topLayers[y][x])
                        {
                            topLayers[y][x] = layer;
                            result.Rows[y][x] = shape.Id;
                        }
                    }
                }
            }

            return result;
        }
    }
}
