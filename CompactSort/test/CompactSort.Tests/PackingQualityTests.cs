using System;
using System.Collections.Generic;
using CompactSort.Packing;

namespace CompactSort.Tests
{
    internal static class PackingQualityTests
    {
        [Test("Regression_BBoxGap_ExcludesWallsFromGap")]
        public static void BBoxGap_ExcludesWallsFromGap()
        {
            // A wall inside the occupied bounding box is not empty space. Counting it as
            // "gap" overstates fragmentation in non-rectangular containers.
            var grid = new PackGrid(3, 3);
            grid.Wall[1 * 3 + 1] = true;

            var piece = PackingTestSupport.Rect(0, "can", "Food", 1, 1);
            grid.Place(piece.Orientations[0], 0, 0, 0);
            grid.Place(piece.Orientations[0], 2, 2, 1);
            grid.RecalculateExtents();

            var score = GridPacker.ScoreLayout(grid, PackingTestSupport.Options());

            Assert.Equal(6, score.BBoxGap,
                "wall cell must not be counted as empty gap inside the bounding box");
            Assert.Equal(2, score.EmptyComponents,
                "the wall should split the usable empty area into two components");
            Assert.Equal(0, score.IsolatedHoles,
                "none of the empty cells is fully enclosed in this fixture");
            Assert.Equal(2, score.MaxEmptyRect,
                "the largest empty rectangle is a 1x2 strip");
        }

        [Test]
        public static void Score_ReadingOrder_And_SameItemBBoxWaste()
        {
            var staggered = new PackGrid(4, 4);
            Put(staggered, 0, 0, 0, "A", "A");
            Put(staggered, 2, 0, 1, "A", "A");
            Put(staggered, 0, 1, 2, "A", "A");
            Put(staggered, 2, 1, 3, "A", "A");
            Put(staggered, 3, 3, 4, "B", "B");
            FinishOwners(staggered, 5);
            var options = PackingTestSupport.Options();
            var score = GridPacker.ScoreLayout(staggered, options);

            Assert.Equal(2, score.SameItemBBoxWaste,
                "three-column/two-row staggered group should have 2 wasted bbox cells");
            Assert.Equal(0, score.ReadingOrderDisorder,
                "A is above/left of B, so the semantic reading order is already correct");

            var reversed = new PackGrid(4, 4);
            Put(reversed, 0, 0, 0, "B", "B");
            Put(reversed, 2, 2, 1, "A", "A");
            Put(reversed, 3, 2, 2, "A", "A");
            Put(reversed, 2, 3, 3, "A", "A");
            Put(reversed, 3, 3, 4, "A", "A");
            FinishOwners(reversed, 5);
            var reversedScore = GridPacker.ScoreLayout(reversed, options);

            Assert.True(reversedScore.ReadingOrderDisorder > score.ReadingOrderDisorder,
                "B at top-left while A is bottom-right must be a reading-order inversion");
            // Reading order is a late tiebreak after clusters/same-item contacts;
            // this fixture only asserts the metric itself.
        }

        [Test]
        public static void VisualComparator_AllowsSmallGapCostButNotLargeGap()
        {
            var hybrid = new PackResult
            {
                Success = true,
                IsolatedHoles = 0,
                EmptyComponents = 1,
                BBoxGap = 2,
                ReadingOrderDisorder = 50,
                SameItemBBoxWaste = 10,
                ItemClusters = 5
            };
            var visual = new PackResult
            {
                Success = true,
                IsolatedHoles = 0,
                EmptyComponents = 1,
                BBoxGap = 8,
                ReadingOrderDisorder = 5,
                SameItemBBoxWaste = 1,
                ItemClusters = 3
            };

            Assert.True(visual.IsVisuallyBetterThan(hybrid, 6),
                "visual layout should win within the allowed gap tolerance");
            Assert.False(visual.IsVisuallyBetterThan(hybrid, 2),
                "visual layout must not win when its gap cost exceeds tolerance");
        }

        [Test]
        public static void Score_IsolatedHole_IsDetected()
        {
            var grid = new PackGrid(3, 3);
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    if (x == 1 && y == 1) continue;
                    int idx = y * 3 + x;
                    grid.Layers[idx] = 1;
                    grid.Owner[idx] = y * 3 + x;
                }
            }
            grid.RecalculateExtents();

            var score = GridPacker.ScoreLayout(grid, PackingTestSupport.Options());

            Assert.Equal(1, score.IsolatedHoles, "center cell should be an isolated hole");
            Assert.Equal(1, score.EmptyComponents, "the hole is the only empty component");
            Assert.Equal(1, score.BBoxGap, "the hole is the only empty cell in the bbox");
            Assert.Equal(1, score.MaxEmptyRect, "the hole is a 1x1 empty rectangle");
        }

        [Test]
        public static void Score_CompactLayout_IsTighterThanFragmentedLayout()
        {
            var compact = new PackGrid(4, 4);
            PlaceUnit(compact, 0, 0, 0);
            PlaceUnit(compact, 1, 0, 1);
            PlaceUnit(compact, 0, 1, 2);
            PlaceUnit(compact, 1, 1, 3);
            compact.RecalculateExtents();

            var fragmented = new PackGrid(4, 4);
            PlaceUnit(fragmented, 0, 0, 0);
            PlaceUnit(fragmented, 3, 0, 1);
            PlaceUnit(fragmented, 0, 3, 2);
            PlaceUnit(fragmented, 3, 3, 3);
            fragmented.RecalculateExtents();

            var options = PackingTestSupport.Options();
            var compactScore = GridPacker.ScoreLayout(compact, options);
            var fragmentedScore = GridPacker.ScoreLayout(fragmented, options);

            Assert.True(compactScore.IsTighterThan(fragmentedScore),
                "compact 2x2 block must be tighter than four corner items");
            Assert.False(fragmentedScore.IsTighterThan(compactScore),
                "fragmented layout must not win the comparison");
            Assert.True(compactScore.Score < fragmentedScore.Score,
                "compact score should be lower, compact=" + compactScore.Score +
                " fragmented=" + fragmentedScore.Score);
        }

        [Test]
        public static void Pack_FragmentedStart_ImprovesGapMetrics()
        {
            var current = new PackGrid(4, 4);
            PlaceUnit(current, 0, 0, 0);
            PlaceUnit(current, 3, 0, 1);
            PlaceUnit(current, 0, 3, 2);
            PlaceUnit(current, 3, 3, 3);
            current.RecalculateExtents();

            var pieces = new List<PackPiece>();
            for (int i = 0; i < 4; i++)
                pieces.Add(PackingTestSupport.Rect(i, "can", "Food", 1, 1));

            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            var packGrid = new PackGrid(4, 4);
            var result = GridPacker.Pack(packGrid, pieces, options);
            PackingTestSupport.AssertValidLayout(packGrid, pieces, result, options,
                "four singles 4x4");

            var currentScore = GridPacker.ScoreLayout(current, options);
            Assert.True(result.IsTighterThan(currentScore),
                "packer did not improve a deliberately fragmented layout: new gap=" +
                result.BBoxGap + " old gap=" + currentScore.BBoxGap +
                ", new holes=" + result.IsolatedHoles + " old holes=" + currentScore.IsolatedHoles);
            Assert.Equal(0, result.BBoxGap, "four 1x1 items should pack into a 2x2 block with no gap");
            Assert.Equal(0, result.IsolatedHoles, "four 1x1 items should not create a hole");
        }

        [Test]
        public static void Pack_ExactTiling_HasNoGapAndGroupsCategories()
        {
            var grid = new PackGrid(4, 4);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "med_a", "Meds", 2, 2),
                PackingTestSupport.Rect(1, "med_b", "Meds", 2, 2),
                PackingTestSupport.Rect(2, "food_a", "Food", 2, 2),
                PackingTestSupport.Rect(3, "food_b", "Food", 2, 2)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            options.CategoryFirst = true;
            options.MaxStrategies = 1;

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "category 4x4");

            Assert.Equal(0, result.BBoxGap, "exact 4x4 tiling must have no bounding-box gap");
            Assert.Equal(0, result.IsolatedHoles, "exact 4x4 tiling must have no hole");
            Assert.Equal(0, result.EmptyComponents, "exact 4x4 tiling must have no empty component");
            Assert.GreaterOrEqual(result.CategoryContacts, 4,
                "same-category 2x2 blocks should share at least one full edge each");
        }

        [Test]
        public static void Pack_SameIdentifier_StaysInOneCluster()
        {
            var grid = new PackGrid(2, 4);
            var pieces = new List<PackPiece>
            {
                PackingTestSupport.Rect(0, "syringe", "Meds", 1, 2, false),
                PackingTestSupport.Rect(1, "syringe", "Meds", 1, 2, false),
                PackingTestSupport.Rect(2, "wine", "Food", 1, 2, false),
                PackingTestSupport.Rect(3, "wine", "Food", 1, 2, false)
            };
            var options = PackingTestSupport.Options(PackAlgorithm.Greedy);
            options.CategoryFirst = true;
            options.MaxStrategies = 1;

            var result = GridPacker.Pack(grid, pieces, options);
            PackingTestSupport.AssertValidLayout(grid, pieces, result, options, "same key 2x4");

            Assert.Equal(2, result.ItemClusters,
                "two identical items per key should form exactly two clusters");
            // Each 1x2 item contributes one internal vertical edge, and the two
            // columns of each key touch along two horizontal edges: 4 internal + 4 shared.
            Assert.Equal(8, result.SameItemContacts,
                "same-key contacts must include both internal and inter-item edges");
        }

        [Test]
        public static void Pack_MaxEmptyRect_MatchesKnownLayout()
        {
            var grid = new PackGrid(3, 3);
            PlaceUnit(grid, 0, 0, 0);
            grid.RecalculateExtents();

            var score = GridPacker.ScoreLayout(grid, PackingTestSupport.Options());
            Assert.Equal(6, score.MaxEmptyRect,
                "one corner cell leaves a 3x2 / 2x3 empty rectangle");
        }

        private static void Put(PackGrid grid, int x, int y, int owner, string key, string category)
        {
            int idx = y * grid.Width + x;
            grid.Layers[idx] = 1;
            grid.Owner[idx] = owner;
            grid.RecalculateExtents();
            if (grid.OwnerKeys == null || grid.OwnerKeys.Length <= owner)
            {
                var keys = new string[owner + 1];
                var cats = new string[owner + 1];
                if (grid.OwnerKeys != null) Array.Copy(grid.OwnerKeys, keys, grid.OwnerKeys.Length);
                if (grid.OwnerCategories != null)
                    Array.Copy(grid.OwnerCategories, cats, grid.OwnerCategories.Length);
                grid.OwnerKeys = keys;
                grid.OwnerCategories = cats;
            }
            grid.OwnerKeys[owner] = key;
            grid.OwnerCategories[owner] = category;
        }

        private static void FinishOwners(PackGrid grid, int count)
        {
            if (grid.OwnerKeys == null || grid.OwnerKeys.Length < count)
            {
                var keys = new string[count];
                var cats = new string[count];
                if (grid.OwnerKeys != null) Array.Copy(grid.OwnerKeys, keys, grid.OwnerKeys.Length);
                if (grid.OwnerCategories != null)
                    Array.Copy(grid.OwnerCategories, cats, grid.OwnerCategories.Length);
                grid.OwnerKeys = keys;
                grid.OwnerCategories = cats;
            }
            grid.RecalculateExtents();
        }

        private static void PlaceUnit(PackGrid grid, int x, int y, int owner)
        {
            var orientation = PackingTestSupport.RectangleOrientation(1, 1, 0, 1);
            grid.Place(orientation, x, y, owner);
        }
    }
}
