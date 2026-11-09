using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CompactSort.Packing
{
    public static partial class GridPacker
    {
        /// <summary>
        /// Visual-first candidate: pack same-identifier groups as one row/block, prefer
        /// portrait orientations, and place from the top-left in reading order.
        /// This is intentionally separate from the gap-first Hybrid so it can be compared
        /// and selected without changing the existing layout search.
        /// </summary>
        private static PackResult PackVisualFirst(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, Stopwatch sw)
        {
            var result = new PackResult();
            var grid = baseGrid.Clone();
            grid.StrictOccupancy = true;
            grid.SetOwnerCategories(pieces);

            var groups = new Dictionary<string, List<PackPiece>>(StringComparer.Ordinal);
            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                string key = piece.Key ?? "";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<PackPiece>();
                    groups[key] = list;
                }
                list.Add(piece);
            }

            var ordered = new List<List<PackPiece>>(groups.Values);
            ordered.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a[0].CategoryKey ?? "", b[0].CategoryKey ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(a[0].Key ?? "", b[0].Key ?? "");
                if (c != 0) return c;
                c = Area(b).CompareTo(Area(a));
                if (c != 0) return c;
                return string.CompareOrdinal(a[0].Key ?? "", b[0].Key ?? "");
            });

            var placed = new List<PackPlacement>(pieces.Count);
            for (int gi = 0; gi < ordered.Count; gi++)
            {
                if (sw.ElapsedMilliseconds > options.TimeBudgetMs)
                    return new PackResult();

                var group = ordered[gi];
                if (TryPlaceGroupRow(grid, group, placed, options)) continue;

                for (int i = 0; i < group.Count; i++)
                {
                    if (sw.ElapsedMilliseconds > options.TimeBudgetMs)
                        return new PackResult();
                    if (!TryPlaceOne(grid, group[i], pieces, placed, options))
                        return new PackResult();
                }
            }

            result.Success = true;
            result.Placements = placed;
            return FinalizeResult(baseGrid, result, options);
        }

        private static int Area(List<PackPiece> items)
        {
            int area = 0;
            for (int i = 0; i < items.Count; i++) area += items[i].Area;
            return area;
        }

        private static bool TryPlaceGroupRow(PackGrid grid, List<PackPiece> group,
            List<PackPlacement> placed, PackOptions options)
        {
            if (group.Count == 0) return false;
            var orientations = new List<PackOrientation>(group.Count);
            int width = 0;
            int height = 0;
            for (int i = 0; i < group.Count; i++)
            {
                var orientation = PreferredOrientation(group[i]);
                if (orientation == null) return false;
                orientations.Add(orientation);
                width += orientation.Width;
                if (orientation.Height > height) height = orientation.Height;
            }
            if (width > grid.Width || height > grid.Height) return false;

            for (int y = 0; y <= grid.Height - height; y++)
            {
                for (int x = 0; x <= grid.Width - width; x++)
                {
                    int cx = x;
                    bool fits = true;
                    for (int i = 0; i < orientations.Count && fits; i++)
                    {
                        var orientation = orientations[i];
                        if (!Fits(grid, orientation, cx, y) ||
                            (options.Validator != null &&
                             !options.Validator(group[i], orientation, cx, y)))
                            fits = false;
                        cx += orientation.Width;
                    }
                    if (!fits) continue;

                    cx = x;
                    for (int i = 0; i < group.Count; i++)
                    {
                        var orientation = orientations[i];
                        grid.Place(orientation, cx, y, group[i].Id);
                        placed.Add(new PackPlacement
                        {
                            Piece = group[i],
                            Orientation = orientation,
                            X = cx,
                            Y = y
                        });
                        cx += orientation.Width;
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool TryPlaceOne(PackGrid grid, PackPiece piece,
            List<PackPiece> allPieces, List<PackPlacement> placed, PackOptions options)
        {
            var orientations = new List<PackOrientation>(piece.Orientations);
            orientations.Sort((a, b) =>
            {
                int ap = a.Height >= a.Width ? 1 : 0;
                int bp = b.Height >= b.Width ? 1 : 0;
                if (ap != bp) return bp.CompareTo(ap);
                int c = (b.Height - b.Width).CompareTo(a.Height - a.Width);
                return c != 0 ? c : a.Rotation.CompareTo(b.Rotation);
            });

            for (int oi = 0; oi < orientations.Count; oi++)
            {
                var orientation = orientations[oi];
                for (int y = 0; y <= grid.Height - orientation.Height; y++)
                {
                    for (int x = 0; x <= grid.Width - orientation.Width; x++)
                    {
                        if (!Fits(grid, orientation, x, y)) continue;
                        if (options.Validator != null &&
                            !options.Validator(piece, orientation, x, y)) continue;
                        grid.Place(orientation, x, y, piece.Id);
                        placed.Add(new PackPlacement
                        {
                            Piece = piece,
                            Orientation = orientation,
                            X = x,
                            Y = y
                        });
                        return true;
                    }
                }
            }
            var fallback = FindPlacement(grid, allPieces, piece, options);
            if (fallback != null)
            {
                grid.Place(fallback.Orientation, fallback.X, fallback.Y, piece.Id);
                placed.Add(fallback);
                return true;
            }
            return false;
        }

        private static PackOrientation PreferredOrientation(PackPiece piece)
        {
            PackOrientation best = null;
            for (int i = 0; i < piece.Orientations.Count; i++)
            {
                var o = piece.Orientations[i];
                if (best == null) { best = o; continue; }
                int bestPortrait = best.Height >= best.Width ? 1 : 0;
                int portrait = o.Height >= o.Width ? 1 : 0;
                if (portrait != bestPortrait)
                {
                    if (portrait > bestPortrait) best = o;
                    continue;
                }
                int bestDiff = best.Height - best.Width;
                int diff = o.Height - o.Width;
                if (diff > bestDiff || (diff == bestDiff && o.Rotation < best.Rotation))
                    best = o;
            }
            return best;
        }

        private static bool Fits(PackGrid grid, PackOrientation o, int x, int y)
        {
            if (x < 0 || y < 0 || x + o.Width > grid.Width || y + o.Height > grid.Height)
                return false;
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                if (grid.IsWall(gx, gy)) return false;
                int idx = gy * grid.Width + gx;
                if (grid.StrictOccupancy)
                {
                    if (grid.Layers[idx] != 0) return false;
                }
                else if ((grid.Layers[idx] & (1 << o.CellLayer[i])) != 0)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
