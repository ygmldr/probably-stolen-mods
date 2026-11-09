using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class RepackItem
    {
        public int Id;
        public int Width;
        public int Height;
        public int[] Cells; // row-major, non-zero = occupied
    }

    public sealed class RepackPlacement
    {
        public int Id;
        public int X;
        public int Y;
        public int Turns;
    }

    public sealed class RepackResult
    {
        public bool Success;
        public List<RepackPlacement> Placements = new List<RepackPlacement>();
    }

    /// <summary>
    /// Small deterministic first-fit packer with rotation and backtracking, used as
    /// the fallback when a container is fragmented. No dependency on other mods.
    /// </summary>
    public static class RepackSolver
    {
        public static RepackResult Solve(int width, int height, bool[] blocked, IList<RepackItem> items,
            int maxNodes = 200000)
        {
            var result = new RepackResult();
            if (width <= 0 || height <= 0 || items == null || items.Count == 0)
            {
                result.Success = items != null && items.Count == 0;
                return result;
            }

            var ordered = new List<RepackItem>(items);
            ordered.Sort(delegate(RepackItem a, RepackItem b)
            {
                int areaA = a == null ? 0 : a.Width * a.Height;
                int areaB = b == null ? 0 : b.Width * b.Height;
                return areaB.CompareTo(areaA);
            });

            var occupied = new bool[width * height];
            if (blocked != null)
            {
                for (int i = 0; i < occupied.Length && i < blocked.Length; i++) occupied[i] = blocked[i];
            }

            var placements = new RepackPlacement[ordered.Count];
            int nodes = 0;

            bool success = Place(0, width, height, ordered, occupied, placements, maxNodes, ref nodes);
            result.Success = success;
            if (success)
            {
                for (int i = 0; i < placements.Length; i++) result.Placements.Add(placements[i]);
            }

            return result;
        }

        private static bool Place(int index, int width, int height, List<RepackItem> items,
            bool[] occupied, RepackPlacement[] placements, int maxNodes, ref int nodes)
        {
            if (index >= items.Count) return true;
            if (nodes++ > maxNodes) return false;

            RepackItem item = items[index];
            if (item == null || item.Cells == null) return false;

            for (int turns = 0; turns < 4; turns++)
            {
                RepackItem rotated = Rotate(item, turns);
                if (rotated.Width > width || rotated.Height > height) continue;

                for (int y = 0; y <= height - rotated.Height; y++)
                {
                    for (int x = 0; x <= width - rotated.Width; x++)
                    {
                        if (!Fits(rotated, x, y, width, height, occupied)) continue;

                        Mark(rotated, x, y, width, occupied, true);
                        placements[index] = new RepackPlacement
                        {
                            Id = item.Id, X = x, Y = y, Turns = turns
                        };

                        if (Place(index + 1, width, height, items, occupied, placements, maxNodes, ref nodes))
                            return true;

                        Mark(rotated, x, y, width, occupied, false);
                        placements[index] = null;
                    }
                }
            }

            return false;
        }

        private static bool Fits(RepackItem item, int originX, int originY, int width, int height, bool[] occupied)
        {
            for (int y = 0; y < item.Height; y++)
            {
                for (int x = 0; x < item.Width; x++)
                {
                    if (item.Cells[y * item.Width + x] == 0) continue;

                    int gx = originX + x;
                    int gy = originY + y;
                    if (gx < 0 || gy < 0 || gx >= width || gy >= height) return false;
                    if (occupied[gy * width + gx]) return false;
                }
            }

            return true;
        }

        private static void Mark(RepackItem item, int originX, int originY, int width, bool[] occupied, bool value)
        {
            for (int y = 0; y < item.Height; y++)
            {
                for (int x = 0; x < item.Width; x++)
                {
                    if (item.Cells[y * item.Width + x] == 0) continue;
                    occupied[(originY + y) * width + originX + x] = value;
                }
            }
        }

        private static RepackItem Rotate(RepackItem item, int turns)
        {
            int width = item.Width;
            int height = item.Height;
            int[] cells = item.Cells;
            int normalized = ((turns % 4) + 4) % 4;

            for (int t = 0; t < normalized; t++)
            {
                int nextWidth = height;
                int nextHeight = width;
                var next = new int[nextWidth * nextHeight];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (cells[y * width + x] == 0) continue;
                        int nx = height - 1 - y;
                        int ny = x;
                        next[ny * nextWidth + nx] = cells[y * width + x];
                    }
                }

                cells = next;
                width = nextWidth;
                height = nextHeight;
            }

            return new RepackItem { Id = item.Id, Width = width, Height = height, Cells = cells };
        }
    }
}
