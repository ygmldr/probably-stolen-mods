using System;
using System.Collections.Generic;
using CompactSort.Packing;

namespace CompactSort.Tests
{
    internal static class PackingInvariantTests
    {
        [Test]
        public static void Pack_MixedItems_PlacesEveryPieceExactlyOnce()
        {
            var grid = new PackGrid(6, 5);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "rifle", "Weapons", 3, 2),
                PackingTestSupport.Rect(1, "medkit", "Meds", 2, 2),
                PackingTestSupport.Rect(2, "syringe", "Meds", 1, 2),
                PackingTestSupport.Rect(3, "syringe", "Meds", 1, 2),
                PackingTestSupport.Rect(4, "can", "Food", 1, 1),
                PackingTestSupport.Rect(5, "can", "Food", 1, 1),
                PackingTestSupport.Rect(6, "cpu", "Parts", 1, 1)
            };

            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            var result = GridPacker.Pack(grid, pieces, options);

            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "mixed 6x5");
        }

        [Test]
        public static void Pack_StrictOccupancy_RejectsDifferentLayersOnSameCell()
        {
            // Regression for "bullets and nuclear waste in the same cell": the game's
            // layer byte must never be used to allow two items to share a physical cell.
            var grid = new PackGrid(1, 1);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "bullet", "Ammo", 1, 1, false, 0),
                PackingTestSupport.Rect(1, "nuclear_waste", "Junk", 1, 1, false, 1)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            options.StrictOccupancy = true;

            var strict = GridPacker.Pack(grid, pieces, options);

            Assert.False(strict.Success,
                "strict occupancy packed two different layers into the same 1x1 cell");

            options.StrictOccupancy = false;
            var layered = GridPacker.Pack(grid, pieces, options);
            Assert.True(layered.Success, "non-strict layered packing should still be possible for this test");
        }

        [Test]
        public static void Pack_StrictOccupancy_RejectsAnchoredCellOverlap()
        {
            var grid = new PackGrid(1, 1);
            grid.Layers[0] = 1;
            grid.Owner[0] = -2;
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "bullet", "Ammo", 1, 1, false, 1)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);

            var result = GridPacker.Pack(grid, pieces, options);

            Assert.False(result.Success, "moving item was allowed to occupy an anchored cell");
        }

        [Test]
        public static void Pack_AnchoredItems_KeepTheirCellsAndOwner()
        {
            var grid = new PackGrid(5, 5);
            var anchor = PackingTestSupport.Rect(99, "locked", "Locked", 2, 2);
            grid.Place(anchor.Orientations[0], 1, 1, -2);
            grid.RecalculateExtents();

            var before = grid.Clone();
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "can", "Food", 1, 1),
                PackingTestSupport.Rect(1, "can", "Food", 1, 1),
                PackingTestSupport.Rect(2, "bottle", "Food", 1, 2),
                PackingTestSupport.Rect(3, "cpu", "Parts", 2, 1)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "anchored 5x5");

            var final = PackingTestSupport.BuildFinal(grid, result);
            for (int y = 1; y <= 2; y++)
            {
                for (int x = 1; x <= 2; x++)
                {
                    int idx = y * grid.Width + x;
                    Assert.Equal((int)before.Layers[idx], (int)final.Layers[idx],
                        "anchor layer changed at (" + x + "," + y + ")");
                    Assert.Equal(-2, final.Owner[idx],
                        "anchor owner changed at (" + x + "," + y + ")");
                }
            }
        }

        [Test]
        public static void Pack_Walls_AreNeverUsed()
        {
            var grid = new PackGrid(6, 6);
            for (int y = 0; y < 6; y++) grid.Wall[y * 6 + 2] = true;

            var pieces = new List<PackPiece>();
            for (int i = 0; i < 4; i++)
                pieces.Add(PackingTestSupport.Rect(i, "can", "Food", 1, 1));
            pieces.Add(PackingTestSupport.Rect(4, "bottle", "Food", 1, 2));
            pieces.Add(PackingTestSupport.Rect(5, "cpu", "Parts", 2, 1));

            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            var result = GridPacker.Pack(grid, pieces, options);

            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "wall 6x6");
        }

        [Test]
        public static void Pack_Validator_IsRespected_AndCanRejectTheWholePacking()
        {
            var grid = new PackGrid(4, 4);
            var pieces = new List<PackPiece>();
            for (int i = 0; i < 4; i++)
                pieces.Add(PackingTestSupport.Rect(i, "can", "Food", 1, 1));

            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            options.MaxStrategies = 1;
            int calls = 0;
            options.Validator = (piece, orientation, x, y) =>
            {
                calls++;
                return !(piece.Id == 0 && x == 0 && y == 0);
            };

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "validator 4x4");
            Assert.GreaterOrEqual(calls, 1, "validator was never called");

            for (int i = 0; i < result.Placements.Count; i++)
            {
                var placement = result.Placements[i];
                if (placement.Piece.Id == 0)
                    Assert.False(placement.X == 0 && placement.Y == 0,
                        "piece 0 was placed at the validator-rejected cell");
            }

            var impossible = PackingTestSupport.Options(PackAlgorithm.Greedy);
            impossible.MaxStrategies = 1;
            impossible.Validator = (piece, orientation, x, y) => piece.Id != 0;
            var failed = GridPacker.Pack(grid, pieces, impossible);
            Assert.False(failed.Success,
                "packing succeeded even though one piece had no validator-approved placement");
        }

        [Test]
        public static void Pack_Validator_CanSelectAPlacementOutsideTheHeuristicTopCandidates()
        {
            // The game adapter filters candidates through CheckShapePlacement. If only an
            // unattractive corner is legal, the packer must still find it instead of
            // returning "no complete packing" because that corner missed the top-16 list.
            var grid = new PackGrid(5, 5);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "can", "Food", 1, 1)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            options.MaxStrategies = 1;
            options.Validator = (piece, orientation, x, y) => x == 4 && y == 4;

            var result = GridPacker.Pack(grid, pieces, options);

            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "validator far corner");
            Assert.Equal(4, result.Placements[0].X, "validator-approved x was not used");
            Assert.Equal(4, result.Placements[0].Y, "validator-approved y was not used");
        }

        [Test]
        public static void Pack_AllAlgorithms_RespectLayoutInvariants()
        {
            var grid = new PackGrid(3, 3);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "crate", "Crate", 2, 2),
                PackingTestSupport.Rect(1, "rod", "Parts", 1, 2),
                PackingTestSupport.Rect(2, "can", "Food", 1, 1),
                PackingTestSupport.Rect(3, "can", "Food", 1, 1),
                PackingTestSupport.Rect(4, "can", "Food", 1, 1)
            };

            var algorithms = new[]
            {
                PackAlgorithm.Greedy,
                PackAlgorithm.GreedyLns,
                PackAlgorithm.Beam,
                PackAlgorithm.BeamLns,
                PackAlgorithm.Hybrid,
                PackAlgorithm.HybridSa,
                PackAlgorithm.Annealing,
                PackAlgorithm.Genetic,
                PackAlgorithm.BranchAndBound,
                PackAlgorithm.VisualFirst
            };

            for (int i = 0; i < algorithms.Length; i++)
            {
                var options = PackingTestSupport.Options(algorithms[i], 991 + i);
                options.TimeBudgetMs = 1200;
                options.MaxStrategies = 8;
                options.BeamWidth = 12;
                options.BeamBranch = 5;
                options.AnnealingBudgetMs = 150;
                options.GeneticPopulation = 8;
                options.GeneticGenerations = 3;

                var result = GridPacker.Pack(grid, pieces, options);
                PackingTestSupport.AssertValidLayout(grid, pieces, result, options,
                    "algorithm=" + algorithms[i]);

                Assert.Equal(0, result.BBoxGap,
                    "algorithm=" + algorithms[i] + " left a gap in an exact 3x3 tiling");
                Assert.Equal(0, result.IsolatedHoles,
                    "algorithm=" + algorithms[i] + " left an isolated hole");
                Assert.Equal(0, result.EmptyComponents,
                    "algorithm=" + algorithms[i] + " left empty components in a full grid");
            }
        }

        [Test]
        public static void Regression_Lns_ReinsertsEveryTornOutPiece()
        {
            var grid = new PackGrid(5, 5);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "rifle", "Test", 2, 2, false),
                PackingTestSupport.Rect(1, "medkit", "Test", 2, 2, false),
                PackingTestSupport.Rect(2, "box", "Test", 2, 2, false),
                PackingTestSupport.Rect(3, "crate", "Test", 2, 2, false)
            };

            var options = PackingTestSupport.Options(PackAlgorithm.GreedyLns);
            options.AllowRotation = false;
            options.TimeBudgetMs = 250;
            options.MaxStrategies = 24;
            options.BeamWidth = 20;
            options.BeamBranch = 6;
            options.EnableGroupingSwap = true;
            options.EnableLns = true;
            options.LnsBudgetMs = 220;
            options.Validator = (piece, orientation, x, y) => true;

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "lns four squares");
        }

        [Test]
        public static void Pack_VisualFirst_KeepsSameIdentifierInOneRow()
        {
            var grid = new PackGrid(4, 4);
            var pieces = new List<PackPiece>();
            for (int i = 0; i < 4; i++)
                pieces.Add(PackingTestSupport.Rect(i, "cola", "Food", 1, 2, false));
            var options = PackingTestSupport.Options(PackAlgorithm.VisualFirst);
            options.TimeBudgetMs = 500;

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "visual cola");
            Assert.Equal(0, result.SameItemBBoxWaste,
                "four vertical cola cans should form a clean 4x2 block");
            Assert.Equal(1, result.ItemClusters,
                "all cola cans should remain one identifier cluster");
        }

        [Test]
        public static void Pack_RandomRectangles_ConservesItemsWithoutOverlap()
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                var rng = new Random(seed * 7919);
                var pieces = new List<PackPiece>();
                int id = 0;
                int area = 0;
                int target = 10 + rng.Next(0, 6); // 10..15 cells out of 36
                int guard = 0;
                while (area < target && guard++ < 100)
                {
                    int w = rng.Next(1, 4);
                    int h = rng.Next(1, 4);
                    int a = w * h;
                    if (area + a > target) continue;
                    pieces.Add(PackingTestSupport.Rect(id, "r" + (id % 4), "C" + (id % 2), w, h));
                    area += a;
                    id++;
                }

                var grid = new PackGrid(6, 6);
                var options = PackingTestSupport.Options(PackAlgorithm.Greedy, seed);
                options.MaxStrategies = 12;
                options.TimeBudgetMs = 1000;

                var result = GridPacker.Pack(grid, pieces, options);
                PackingTestSupport.AssertValidLayout(grid, pieces, result, options,
                    "random seed=" + seed + " area=" + area);
            }
        }
    }
}
