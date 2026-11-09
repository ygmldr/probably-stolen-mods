using System;
using System.Collections.Generic;
using System.Text;
using CompactSort.Packing;

namespace CompactSort.Tests
{
    internal static class PackingTestSupport
    {
        public static PackPiece Rect(int id, string key, string category, int width, int height,
            bool allowRotation = true, byte layer = 1)
        {
            var piece = new PackPiece
            {
                Id = id,
                Key = key,
                CategoryKey = category,
                MaxDim = Math.Max(width, height),
                OriginalRotation = 0,
                Area = width * height
            };

            int rotations = allowRotation ? 4 : 1;
            for (int rot = 0; rot < rotations; rot++)
            {
                var orientation = RectangleOrientation(width, height, rot, layer);
                bool duplicate = false;
                for (int i = 0; i < piece.Orientations.Count; i++)
                {
                    if (piece.Orientations[i].SameShape(orientation))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) piece.Orientations.Add(orientation);
            }
            return piece;
        }

        public static PackOrientation RectangleOrientation(int width, int height, int rotation, byte layer)
        {
            rotation = ((rotation % 4) + 4) % 4;
            var xs = new List<int>(width * height);
            var ys = new List<int>(width * height);
            var layers = new List<byte>(width * height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int nx;
                    int ny;
                    Rotate(x, y, width, height, rotation, out nx, out ny);
                    xs.Add(nx);
                    ys.Add(ny);
                    layers.Add(layer);
                }
            }

            return new PackOrientation
            {
                Width = rotation % 2 == 0 ? width : height,
                Height = rotation % 2 == 0 ? height : width,
                Rotation = rotation,
                CellX = xs.ToArray(),
                CellY = ys.ToArray(),
                CellLayer = layers.ToArray()
            };
        }

        private static void Rotate(int x, int y, int width, int height, int rotation,
            out int nx, out int ny)
        {
            switch (rotation)
            {
                case 1:
                    nx = y;
                    ny = width - 1 - x;
                    break;
                case 2:
                    nx = width - 1 - x;
                    ny = height - 1 - y;
                    break;
                case 3:
                    nx = height - 1 - y;
                    ny = x;
                    break;
                default:
                    nx = x;
                    ny = y;
                    break;
            }
        }

        public static PackOptions Options(PackAlgorithm algorithm = PackAlgorithm.Greedy,
            int seed = 20261101)
        {
            return new PackOptions
            {
                Algorithm = algorithm,
                BigFirst = true,
                FromEnd = false,
                AllowRotation = true,
                TimeBudgetMs = 1000,
                MaxStrategies = 12,
                RandomSeed = seed,
                CategoryFirst = false,
                StrictOccupancy = true,
                EnableGroupingSwap = false,
                EnableLns = false,
                LnsBudgetMs = 150,
                BeamWidth = 12,
                BeamBranch = 5,
                AnnealingBudgetMs = 150,
                GeneticPopulation = 8,
                GeneticGenerations = 3
            };
        }

        public static PackGrid BuildFinal(PackGrid baseGrid, PackResult result)
        {
            var final = baseGrid.Clone();
            for (int i = 0; i < result.Placements.Count; i++)
            {
                var placement = result.Placements[i];
                final.Place(placement.Orientation, placement.X, placement.Y, placement.Piece.Id);
            }
            final.RecalculateExtents();
            return final;
        }

        public static void AssertValidLayout(PackGrid baseGrid, List<PackPiece> pieces,
            PackResult result, PackOptions options, string context)
        {
            Assert.True(result != null, context + ": result is null");
            Assert.True(result.Success, context + ": packing failed (pieces=" + Pieces(pieces) + ")");
            Assert.Equal(pieces.Count, result.Placements.Count,
                context + ": placement count must equal piece count");

            var piecesById = new Dictionary<int, PackPiece>();
            for (int i = 0; i < pieces.Count; i++)
            {
                Assert.False(piecesById.ContainsKey(pieces[i].Id),
                    context + ": duplicate piece id " + pieces[i].Id);
                piecesById.Add(pieces[i].Id, pieces[i]);
            }

            var seenPieces = new HashSet<int>();
            var occupied = new bool[baseGrid.Width * baseGrid.Height];

            for (int pi = 0; pi < result.Placements.Count; pi++)
            {
                var placement = result.Placements[pi];
                Assert.True(placement != null, context + ": placement " + pi + " is null");
                Assert.True(placement.Piece != null, context + ": placement " + pi + " has no piece");
                Assert.True(placement.Orientation != null,
                    context + ": piece " + placement.Piece.Id + " has no orientation");
                Assert.True(piecesById.ContainsKey(placement.Piece.Id),
                    context + ": unknown piece id " + placement.Piece.Id);
                Assert.True(seenPieces.Add(placement.Piece.Id),
                    context + ": piece " + placement.Piece.Id + " placed more than once");
                Assert.True(placement.Piece.Orientations.Contains(placement.Orientation),
                    context + ": orientation does not belong to piece " + placement.Piece.Id);

                var orientation = placement.Orientation;
                Assert.True(placement.X >= 0 && placement.Y >= 0,
                    context + ": piece " + placement.Piece.Id + " has negative origin");
                Assert.True(placement.X + orientation.Width <= baseGrid.Width,
                    context + ": piece " + placement.Piece.Id + " exceeds grid width");
                Assert.True(placement.Y + orientation.Height <= baseGrid.Height,
                    context + ": piece " + placement.Piece.Id + " exceeds grid height");

                for (int ci = 0; ci < orientation.CellX.Length; ci++)
                {
                    int x = placement.X + orientation.CellX[ci];
                    int y = placement.Y + orientation.CellY[ci];
                    Assert.True(baseGrid.InBounds(x, y),
                        context + ": piece " + placement.Piece.Id + " cell outside grid");
                    Assert.False(baseGrid.Wall[y * baseGrid.Width + x],
                        context + ": piece " + placement.Piece.Id + " placed on a wall at (" + x + "," + y + ")");
                    Assert.Equal(0, (int)baseGrid.Layers[y * baseGrid.Width + x],
                        context + ": piece " + placement.Piece.Id + " overlaps an anchored cell at (" + x + "," + y + ")");
                    Assert.False(occupied[y * baseGrid.Width + x],
                        context + ": piece " + placement.Piece.Id + " overlaps another piece at (" + x + "," + y + ")");
                    occupied[y * baseGrid.Width + x] = true;
                }
            }

            Assert.Equal(pieces.Count, seenPieces.Count,
                context + ": not every piece was placed exactly once");

            // The public metrics must describe the returned placements, not an intermediate layout.
            var final = BuildFinal(baseGrid, result);
            var rescored = GridPacker.ScoreLayout(final, options);
            Assert.Equal(result.Score, rescored.Score, context + ": score does not match final layout");
            Assert.Equal(result.IsolatedHoles, rescored.IsolatedHoles,
                context + ": isolated holes do not match final layout");
            Assert.Equal(result.EmptyComponents, rescored.EmptyComponents,
                context + ": empty components do not match final layout");
            Assert.Equal(result.BBoxGap, rescored.BBoxGap, context + ": bbox gap does not match final layout");
            Assert.Equal(result.MaxEmptyRect, rescored.MaxEmptyRect,
                context + ": max empty rect does not match final layout");
            Assert.Equal(result.ReadingOrderDisorder, rescored.ReadingOrderDisorder,
                context + ": reading order disorder does not match final layout");
            Assert.Equal(result.SameItemBBoxWaste, rescored.SameItemBBoxWaste,
                context + ": same-item bbox waste does not match final layout");
            Assert.Equal(result.CategoryContacts, rescored.CategoryContacts,
                context + ": category contacts do not match final layout");
            Assert.Equal(result.SameItemContacts, rescored.SameItemContacts,
                context + ": same-item contacts do not match final layout");
            Assert.Equal(result.ItemClusters, rescored.ItemClusters,
                context + ": item clusters do not match final layout");
        }

        public static void AssertAnchorsUnchanged(PackGrid before, PackGrid after, string context)
        {
            Assert.Equal(before.Width, after.Width, context + ": width changed");
            Assert.Equal(before.Height, after.Height, context + ": height changed");
            for (int i = 0; i < before.Layers.Length; i++)
            {
                Assert.Equal((int)before.Layers[i], (int)after.Layers[i],
                    context + ": layer at index " + i + " changed");
                Assert.Equal(before.Owner[i], after.Owner[i],
                    context + ": owner at index " + i + " changed");
            }
        }

        public static string Pieces(List<PackPiece> pieces)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pieces[i].Key).Append("#").Append(pieces[i].Id)
                  .Append("(").Append(pieces[i].Area).Append(")");
            }
            return sb.ToString();
        }
    }
}
