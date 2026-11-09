using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CompactSort.Packing
{
    /// <summary>
    /// Gap-first grid packer. The objective is not "sort by category" but "leave as few
    /// fragmented holes as possible": pack tightly against one corner, prefer high contact,
    /// then run a gravity pass that pulls every piece towards that corner.
    /// </summary>
    public static partial class GridPacker
    {
        private const int TopCandidates = 16;

        private sealed class Worker
        {
            public PackGrid Grid;
            public List<PackPiece> Pieces;
            public PackOptions Options;
            public int[] PieceStamp;
            public int[] HoleStamp;
            public int Stamp;

            public Worker(PackGrid grid, List<PackPiece> pieces, PackOptions options)
            {
                Grid = grid;
                Pieces = pieces;
                Options = options;
                PieceStamp = new int[grid.Width * grid.Height];
                HoleStamp = new int[grid.Width * grid.Height];
            }

            public int NextStamp()
            {
                Stamp++;
                if (Stamp == int.MaxValue)
                {
                    Array.Clear(PieceStamp, 0, PieceStamp.Length);
                    Array.Clear(HoleStamp, 0, HoleStamp.Length);
                    Stamp = 1;
                }
                return Stamp;
            }

            public bool IsOccupiedAfter(int x, int y, int stamp)
            {
                if (Grid.IsWall(x, y)) return true;
                int idx = y * Grid.Width + x;
                return Grid.Layers[idx] != 0 || PieceStamp[idx] == stamp;
            }
        }

        private struct Candidate
        {
            public PackOrientation Orientation;
            public int X;
            public int Y;
            public long Score;
        }

        public static PackResult Pack(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options)
        {
            if (baseGrid == null || pieces == null || pieces.Count == 0 || options == null)
                return new PackResult();

            baseGrid.SetOwnerCategories(pieces);
            baseGrid.StrictOccupancy = options.StrictOccupancy;
            var sw = Stopwatch.StartNew();
            PackResult result;
            switch (options.Algorithm)
            {
                case PackAlgorithm.Beam:
                    result = PackBeam(baseGrid, pieces, options, sw, runLns: false);
                    break;
                case PackAlgorithm.BeamLns:
                    result = PackBeam(baseGrid, pieces, options, sw, runLns: true);
                    break;
                case PackAlgorithm.Hybrid:
                    result = PackHybrid(baseGrid, pieces, options, sw);
                    break;
                case PackAlgorithm.HybridSa:
                    {
                        var hybrid = PackHybrid(baseGrid, pieces, options, sw);
                        result = AnnealFromResult(baseGrid, pieces, options, hybrid, sw);
                        break;
                    }
                case PackAlgorithm.Annealing:
                    result = PackAnnealing(baseGrid, pieces, options, sw);
                    break;
                case PackAlgorithm.Genetic:
                    result = PackGenetic(baseGrid, pieces, options, sw);
                    break;
                case PackAlgorithm.BranchAndBound:
                    result = PackBranchAndBound(baseGrid, pieces, options, sw);
                    break;
                case PackAlgorithm.VisualFirst:
                    result = PackVisualFirst(baseGrid, pieces, options, sw);
                    break;
                case PackAlgorithm.Greedy:
                    result = PackGreedy(baseGrid, pieces, options, sw, runLns: false);
                    break;
                default:
                    result = PackGreedy(baseGrid, pieces, options, sw, runLns: true);
                    break;
            }
            if (result.Success && options.EnableGroupingSwap &&
                options.Algorithm != PackAlgorithm.VisualFirst)
                result = GroupingSwapImprove(baseGrid, pieces, options, result, sw);
            if (result.Success && options.EnableNeaten &&
                options.Algorithm != PackAlgorithm.VisualFirst)
                result = NeatenSameItemGroups(baseGrid, pieces, options, result, sw);

            // Never report success with a partial or duplicated placement set. The adapter
            // commits exactly the returned placements, so a silently shortened list would
            // become item loss in the game. Fail closed instead.
            if (result.Success)
            {
                var seen = new HashSet<int>();
                bool complete = result.Placements != null && result.Placements.Count == pieces.Count;
                if (complete)
                {
                    for (int i = 0; i < result.Placements.Count; i++)
                    {
                        if (!seen.Add(result.Placements[i].Piece.Id))
                        {
                            complete = false;
                            break;
                        }
                    }
                }
                if (!complete || seen.Count != pieces.Count)
                {
                    result.Success = false;
                    if (result.Placements != null) result.Placements.Clear();
                }
            }
            return FinalizeResult(baseGrid, result, options);
        }

        /// <summary>Recompute the metrics from the final placements so every algorithm reports the same numbers.</summary>
        private static PackResult FinalizeResult(PackGrid baseGrid, PackResult result, PackOptions options)
        {
            if (result == null || !result.Success || result.Placements == null || result.Placements.Count == 0)
                return result;
            var grid = baseGrid.Clone();
            for (int i = 0; i < result.Placements.Count; i++)
            {
                var p = result.Placements[i];
                grid.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
            }
            grid.RecalculateExtents();
            int comps, holes, gap, contact;
            result.Score = Score(grid, options, out comps, out holes, out gap, out contact);
            result.EmptyComponents = comps;
            result.IsolatedHoles = holes;
            result.BBoxGap = gap;
            result.Contact = contact;
            result.MaxEmptyRect = LargestEmptyRectangle(grid);
            result.ReadingOrderDisorder = CountReadingOrderDisorder(grid);
            result.SameItemBBoxWaste = CountSameItemBBoxWaste(grid);
            result.CategoryContacts = CountCategoryContacts(grid);
            result.SameItemContacts = CountSameItemContacts(grid);
            result.ItemClusters = CountItemClusters(grid);
            return result;
        }

        /// <summary>
        /// Try to repack one identifier group at a time inside the free space left by all
        /// other items. This is what turns four same-key cans from a staggered shape into a
        /// tight 2x2/row block without sacrificing the global gap metrics.
        /// </summary>
        private static PackResult NeatenSameItemGroups(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, PackResult incumbent, Stopwatch sw)
        {
            if (incumbent == null || !incumbent.Success ||
                incumbent.Placements.Count != pieces.Count)
                return incumbent;

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(incumbent.Placements.Count);
            for (int i = 0; i < incumbent.Placements.Count; i++)
            {
                var p = incumbent.Placements[i];
                work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                placed.Add(p);
            }
            work.RecalculateExtents();
            work.SetOwnerCategories(pieces);
            var current = ScoreLayout(work, options);

            var keys = new List<string>();
            var groupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < pieces.Count; i++)
            {
                string key = pieces[i].Key ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                if (!groupCounts.TryGetValue(key, out int count))
                {
                    groupCounts[key] = 1;
                    keys.Add(key);
                }
                else groupCounts[key] = count + 1;
            }

            long end = Math.Min(options.TimeBudgetMs, sw.ElapsedMilliseconds + 180);
            for (int ki = 0; ki < keys.Count; ki++)
            {
                if (sw.ElapsedMilliseconds >= end) break;
                string key = keys[ki];
                if (groupCounts[key] < 2) continue;

                var group = new List<PackPlacement>();
                var groupPieces = new List<PackPiece>();
                for (int i = 0; i < placed.Count; i++)
                    if (placed[i].Piece.Key == key) group.Add(placed[i]);
                if (group.Count < 2) continue;

                // Skip groups that already occupy a perfect rectangle.
                if (CountGroupWaste(work, key) == 0) continue;

                for (int i = 0; i < group.Count; i++)
                {
                    var original = group[i].Piece;
                    var clone = new PackPiece
                    {
                        Id = i,
                        Key = original.Key,
                        CategoryKey = original.CategoryKey,
                        Area = original.Area,
                        MaxDim = original.MaxDim,
                        OriginalRotation = original.OriginalRotation,
                        Tag = original
                    };
                    clone.Orientations.AddRange(original.Orientations);
                    groupPieces.Add(clone);
                }

                var baseOnly = new PackGrid(work.Width, work.Height);
                for (int i = 0; i < work.Wall.Length; i++) baseOnly.Wall[i] = work.Wall[i];
                for (int i = 0; i < work.Layers.Length; i++)
                {
                    if (work.Owner[i] != -2 || work.Layers[i] == 0) continue;
                    baseOnly.Layers[i] = work.Layers[i];
                    baseOnly.Owner[i] = -2;
                }
                for (int i = 0; i < placed.Count; i++)
                {
                    if (placed[i].Piece.Key == key) continue;
                    var p = placed[i];
                    baseOnly.Place(p.Orientation, p.X, p.Y, -2);
                }
                baseOnly.RecalculateExtents();

                var groupOptions = new PackOptions
                {
                    Algorithm = PackAlgorithm.Greedy,
                    BigFirst = true,
                    FromEnd = options.FromEnd,
                    AllowRotation = options.AllowRotation,
                    TimeBudgetMs = 80,
                    MaxStrategies = 8,
                    RandomSeed = options.RandomSeed,
                    StrictOccupancy = true,
                    EnableGroupingSwap = false,
                    EnableLns = false,
                    EnableNeaten = false,
                    Validator = options.Validator
                };

                var packed = Pack(baseOnly, groupPieces, groupOptions);
                if (!packed.Success || packed.Placements.Count != groupPieces.Count) continue;

                var full = work.Clone();
                for (int i = 0; i < group.Count; i++)
                {
                    var p = group[i];
                    full.Remove(p.Orientation, p.X, p.Y);
                }
                for (int i = 0; i < packed.Placements.Count; i++)
                {
                    var p = packed.Placements[i];
                    var original = (PackPiece)p.Piece.Tag;
                    full.Place(p.Orientation, p.X, p.Y, original.Id);
                }
                full.RecalculateExtents();
                full.SetOwnerCategories(pieces);
                var candidate = ScoreLayout(full, options);
                bool better = candidate.IsTighterThan(current);
                if (!better)
                {
                    // Neatening may spend at most one bbox-gap cell, as long as it
                    // does not create holes/components and improves reading order or
                    // same-item rectangularity.
                    bool smallCost = candidate.IsolatedHoles <= current.IsolatedHoles &&
                                     candidate.EmptyComponents <= current.EmptyComponents &&
                                     candidate.BBoxGap <= current.BBoxGap + 1 &&
                                     (candidate.SameItemBBoxWaste < current.SameItemBBoxWaste ||
                                      (candidate.SameItemBBoxWaste == current.SameItemBBoxWaste &&
                                       candidate.ReadingOrderDisorder < current.ReadingOrderDisorder));
                    better = smallCost;
                }
                if (!better) continue;

                // Adopt the candidate and rebuild the placement list.
                work = full;
                current = candidate;
                // Rebuild placements from the packed group plus the preserved
                // non-group placements.
                var next = new List<PackPlacement>();
                for (int i = 0; i < group.Count; i++)
                {
                    var original = group[i].Piece;
                    for (int j = 0; j < packed.Placements.Count; j++)
                    {
                        if (!ReferenceEquals(packed.Placements[j].Piece.Tag, original)) continue;
                        next.Add(new PackPlacement
                        {
                            Piece = original,
                            Orientation = packed.Placements[j].Orientation,
                            X = packed.Placements[j].X,
                            Y = packed.Placements[j].Y
                        });
                    }
                }
                for (int i = 0; i < placed.Count; i++)
                    if (placed[i].Piece.Key != key) next.Add(placed[i]);
                placed = next;
                incumbent = new PackResult
                {
                    Success = true,
                    Placements = ClonePlacements(placed),
                    Score = current.Score,
                    EmptyComponents = current.EmptyComponents,
                    IsolatedHoles = current.IsolatedHoles,
                    BBoxGap = current.BBoxGap,
                    Contact = current.Contact,
                    MaxEmptyRect = current.MaxEmptyRect,
                    ReadingOrderDisorder = current.ReadingOrderDisorder,
                    SameItemBBoxWaste = current.SameItemBBoxWaste,
                    CategoryContacts = current.CategoryContacts,
                    SameItemContacts = current.SameItemContacts,
                    ItemClusters = current.ItemClusters,
                    LocalMoves = incumbent.LocalMoves,
                    LocalImprovements = incumbent.LocalImprovements,
                    LnsIterations = incumbent.LnsIterations,
                    LnsImprovements = incumbent.LnsImprovements
                };
            }

            return incumbent;
        }

        private static int CountGroupWaste(PackGrid grid, string key)
        {
            if (grid.OwnerKeys == null) return 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            int cells = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (owner < 0 || owner >= grid.OwnerKeys.Length) continue;
                    if (grid.OwnerKeys[owner] != key) continue;
                    cells++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (cells == 0) return 0;
            return Math.Max(0, (maxX - minX + 1) * (maxY - minY + 1) - cells);
        }

        /// <summary>
        /// Try direct two-item swaps. This is what fixes "two pink syringes in the middle,
        /// wine bottles on the right": if swapping increases same-item adjacency without
        /// increasing holes/components and with at most +1 bbox gap, take it.
        /// </summary>
        private static PackResult GroupingSwapImprove(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, PackResult incumbent, Stopwatch sw)
        {
            if (incumbent == null || !incumbent.Success || incumbent.Placements.Count != pieces.Count)
                return incumbent;

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(incumbent.Placements.Count);
            for (int i = 0; i < incumbent.Placements.Count; i++)
            {
                var p = incumbent.Placements[i];
                work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                placed.Add(p);
            }
            work.RecalculateExtents();
            var worker = new Worker(work, pieces, options);

            int curSame = CountSameItemContacts(work);
            int curClusters = CountItemClusters(work);
            int curComps = incumbent.EmptyComponents;
            int curHoles = incumbent.IsolatedHoles;
            int curGap = incumbent.BBoxGap;
            long curScore = incumbent.Score;
            int curCategory = incumbent.CategoryContacts;
            int curReading = CountReadingOrderDisorder(work);
            int curWaste = CountSameItemBBoxWaste(work);

            long end = Math.Min(options.TimeBudgetMs, sw.ElapsedMilliseconds + 180);
            int guard = 0;
            bool improved = true;

            while (improved && sw.ElapsedMilliseconds < end && guard++ < 30)
            {
                improved = false;
                for (int i = 0; i < placed.Count && !improved; i++)
                {
                    if (sw.ElapsedMilliseconds >= end) break;
                    var a = placed[i];
                    for (int j = i + 1; j < placed.Count; j++)
                    {
                        if (sw.ElapsedMilliseconds >= end) break;
                        var b = placed[j];
                        if (a.Piece.Key == b.Piece.Key) continue;

                        work.Remove(a.Orientation, a.X, a.Y);
                        work.Remove(b.Orientation, b.X, b.Y);

                        var a2 = new PackPlacement
                        {
                            Piece = a.Piece,
                            Orientation = a.Orientation,
                            X = b.X,
                            Y = b.Y
                        };
                        var b2 = new PackPlacement
                        {
                            Piece = b.Piece,
                            Orientation = b.Orientation,
                            X = a.X,
                            Y = a.Y
                        };

                        bool ok = CanPlace(work, a2.Orientation, a2.X, a2.Y);
                        if (ok)
                        {
                            work.Place(a2.Orientation, a2.X, a2.Y, a2.Piece.Id);
                            ok = CanPlace(work, b2.Orientation, b2.X, b2.Y);
                            work.Remove(a2.Orientation, a2.X, a2.Y);
                        }
                        if (ok && options.Validator != null)
                        {
                            ok = options.Validator(a2.Piece, a2.Orientation, a2.X, a2.Y)
                                 && options.Validator(b2.Piece, b2.Orientation, b2.X, b2.Y);
                        }

                        if (ok)
                        {
                            work.Place(a2.Orientation, a2.X, a2.Y, a2.Piece.Id);
                            work.Place(b2.Orientation, b2.X, b2.Y, b2.Piece.Id);
                            work.RecalculateExtents();
                            int comps, holes, gap, contact;
                            long score = Score(work, options, out comps, out holes, out gap, out contact);
                            int same = CountSameItemContacts(work);
                            int category = CountCategoryContacts(work);
                            int clusters = CountItemClusters(work);
                            int reading = CountReadingOrderDisorder(work);
                            int waste = CountSameItemBBoxWaste(work);
                            bool preferredImproves = clusters < curClusters ||
                                (clusters == curClusters && same > curSame) ||
                                (clusters == curClusters && same == curSame &&
                                 (waste < curWaste ||
                                  (waste == curWaste && reading < curReading)));
                            if (preferredImproves &&
                                holes <= curHoles && comps <= curComps && gap <= curGap + 1)
                            {
                                placed[i] = a2;
                                placed[j] = b2;
                                curSame = same;
                                curClusters = clusters;
                                curComps = comps;
                                curHoles = holes;
                                curGap = gap;
                                curScore = score;
                                curCategory = category;
                                curReading = reading;
                                curWaste = waste;
                                improved = true;
                                break;
                            }
                            work.Remove(a2.Orientation, a2.X, a2.Y);
                            work.Remove(b2.Orientation, b2.X, b2.Y);
                        }

                        work.Place(a.Orientation, a.X, a.Y, a.Piece.Id);
                        work.Place(b.Orientation, b.X, b.Y, b.Piece.Id);
                    }
                }
            }

            if (guard <= 1 && curSame == incumbent.SameItemContacts &&
                curReading == incumbent.ReadingOrderDisorder &&
                curWaste == incumbent.SameItemBBoxWaste)
                return incumbent;

            return new PackResult
            {
                Success = true,
                Score = curScore,
                Placements = ClonePlacements(placed),
                EmptyComponents = curComps,
                IsolatedHoles = curHoles,
                BBoxGap = curGap,
                Contact = incumbent.Contact,
                CategoryContacts = curCategory,
                SameItemContacts = curSame,
                ItemClusters = curClusters,
                MaxEmptyRect = incumbent.MaxEmptyRect,
                ReadingOrderDisorder = curReading,
                SameItemBBoxWaste = curWaste,
                LocalMoves = incumbent.LocalMoves,
                LocalImprovements = incumbent.LocalImprovements,
                LnsIterations = incumbent.LnsIterations,
                LnsImprovements = incumbent.LnsImprovements
            };
        }

        private static PackResult PackHybrid(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options,
            Stopwatch sw)
        {
            var greedy = PackGreedy(baseGrid, pieces, options, sw, runLns: true);
            greedy = FinalizeResult(baseGrid, greedy, options);
            if (greedy.Success && greedy.BBoxGap == 0 && greedy.IsolatedHoles == 0 &&
                greedy.EmptyComponents <= 1)
                return greedy;

            var beam = PackBeam(baseGrid, pieces, options, sw, runLns: true);
            beam = FinalizeResult(baseGrid, beam, options);
            if (beam.Success && (!greedy.Success || beam.IsTighterThan(greedy))) return beam;
            return greedy;
        }

        private static PackResult PackGreedy(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options,
            Stopwatch sw, bool runLns)
        {
            var result = new PackResult();
            var orders = BuildOrders(pieces, options);
            long bestScore = long.MaxValue;

            for (int oi = 0; oi < orders.Count && oi < options.MaxStrategies; oi++)
            {
                if (sw.ElapsedMilliseconds > options.TimeBudgetMs && bestScore != long.MaxValue)
                {
                    result.TimedOut = true;
                    break;
                }

                var attempt = TryOrder(baseGrid, pieces, options, orders[oi], sw);
                if (attempt == null) continue;
                if (bestScore == long.MaxValue || attempt.IsTighterThan(result))
                {
                    bestScore = attempt.Score;
                    result = attempt;
                }
                if (result.BBoxGap == 0 && result.IsolatedHoles == 0 && result.EmptyComponents <= 1)
                    break;
            }

            result.Success = bestScore != long.MaxValue;
            if (result.Success && runLns && options.EnableLns)
            {
                result = LocalMoveImprove(baseGrid, pieces, options, result, sw);
                result = LnsImprove(baseGrid, pieces, options, result, sw);
            }
            return result;
        }

        /// <summary>Scores an existing layout (used to avoid "sorting" an already compact container).</summary>
        public static PackResult ScoreLayout(PackGrid grid, PackOptions options)
        {
            var result = new PackResult();
            result.Score = Score(grid, options, out result.EmptyComponents, out result.IsolatedHoles,
                out result.BBoxGap, out result.Contact);
            result.MaxEmptyRect = LargestEmptyRectangle(grid);
            result.ReadingOrderDisorder = CountReadingOrderDisorder(grid);
            result.SameItemBBoxWaste = CountSameItemBBoxWaste(grid);
            result.CategoryContacts = CountCategoryContacts(grid);
            result.SameItemContacts = CountSameItemContacts(grid);
            result.ItemClusters = CountItemClusters(grid);
            result.Success = true;
            return result;
        }


        /// <summary>
        /// Gap-targeted hill climbing: pick an empty cell that looks like a hole/notch, then try to
        /// move one nearby item so that it covers that cell. Single-item moves preserve the rest of
        /// the layout, so they can improve a greedy result without the disruption of a full rebuild.
        /// </summary>
        private static PackResult LocalMoveImprove(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, PackResult incumbent, Stopwatch sw)
        {
            if (incumbent == null || !incumbent.Success || incumbent.Placements.Count != pieces.Count)
                return incumbent;

            long bestScore = incumbent.Score;
            var bestPlacements = ClonePlacements(incumbent.Placements);
            int bestComponents = incumbent.EmptyComponents;
            int bestHoles = incumbent.IsolatedHoles;
            int bestGap = incumbent.BBoxGap;
            int bestContact = incumbent.Contact;

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(incumbent.Placements.Count);
            for (int i = 0; i < incumbent.Placements.Count; i++)
            {
                var p = incumbent.Placements[i];
                work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                placed.Add(p);
            }
            work.RecalculateExtents();
            var worker = new Worker(work, pieces, options);

            long start = sw.ElapsedMilliseconds;
            long end = Math.Min(options.TimeBudgetMs, start + options.LnsBudgetMs);
            int rounds = 0;
            int improvements = 0;

            while (sw.ElapsedMilliseconds < end && rounds < 250)
            {
                work.RecalculateExtents();
                var targets = CollectGapCells(work);
                if (targets.Count == 0) break;

                bool moved = false;
                for (int ti = 0; ti < targets.Count && !moved; ti++)
                {
                    if (sw.ElapsedMilliseconds >= end) break;
                    int target = targets[ti];
                    var nearby = NearbyPieces(work, placed, target, 4, 16);
                    for (int ni = 0; ni < nearby.Count; ni++)
                    {
                        if (TryRelocatePiece(work, placed, worker, nearby[ni], target, options,
                                ref bestScore, ref bestPlacements,
                                ref bestComponents, ref bestHoles, ref bestGap, ref bestContact))
                        {
                            moved = true;
                            improvements++;
                            break;
                        }
                    }
                }

                rounds++;
                if (!moved) break;
            }

            return new PackResult
            {
                Success = true,
                TimedOut = incumbent.TimedOut,
                Score = bestScore,
                Placements = bestPlacements,
                EmptyComponents = bestComponents,
                IsolatedHoles = bestHoles,
                BBoxGap = bestGap,
                Contact = bestContact,
                LocalMoves = rounds,
                LocalImprovements = improvements,
                LnsIterations = incumbent.LnsIterations,
                LnsImprovements = incumbent.LnsImprovements
            };
        }

        private static List<int> CollectGapCells(PackGrid grid)
        {
            var holes = new List<int>();
            var notches = new List<int>();
            var inner = new List<int>();
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    if (grid.Wall[idx] || grid.Layers[idx] != 0) continue;

                    int occupied = 0;
                    if (IsSolid(grid, x - 1, y)) occupied++;
                    if (IsSolid(grid, x + 1, y)) occupied++;
                    if (IsSolid(grid, x, y - 1)) occupied++;
                    if (IsSolid(grid, x, y + 1)) occupied++;

                    if (occupied == 4) holes.Add(idx);
                    else if (occupied == 3) notches.Add(idx);
                    else if (InsideBBox(grid, x, y)) inner.Add(idx);
                }
            }

            var all = new List<int>(holes.Count + notches.Count + inner.Count);
            all.AddRange(holes);
            all.AddRange(notches);
            all.AddRange(inner);
            if (all.Count > 80) all.RemoveRange(80, all.Count - 80);
            return all;
        }

        private static List<int> NearbyPieces(PackGrid grid, List<PackPlacement> placed,
            int target, int radius, int max)
        {
            int tx = target % grid.Width;
            int ty = target / grid.Width;
            var distances = new int[placed.Count];
            var order = new List<int>(placed.Count);
            for (int i = 0; i < placed.Count; i++)
            {
                int d = MinDistanceToCell(placed[i], tx, ty);
                distances[i] = d;
                if (d <= radius) order.Add(i);
            }

            if (order.Count == 0)
            {
                for (int i = 0; i < placed.Count; i++) order.Add(i);
            }

            order.Sort((a, b) => distances[a].CompareTo(distances[b]));
            if (order.Count > max) order.RemoveRange(max, order.Count - max);
            return order;
        }

        private static bool TryRelocatePiece(PackGrid work, List<PackPlacement> placed, Worker worker,
            int pieceIndex, int target, PackOptions options, ref long bestScore,
            ref List<PackPlacement> bestPlacements, ref int bestComponents, ref int bestHoles,
            ref int bestGap, ref int bestContact)
        {
            if (pieceIndex < 0 || pieceIndex >= placed.Count) return false;

            var original = placed[pieceIndex];
            var piece = original.Piece;
            int tx = target % work.Width;
            int ty = target / work.Width;

            work.Remove(original.Orientation, original.X, original.Y);
            placed.RemoveAt(pieceIndex);

            PackPlacement bestCandidate = null;
            long bestCandidateScore = long.MaxValue;
            int candComponents = 0, candHoles = 0, candGap = 0, candContact = 0;
            int bestCandComponents = int.MaxValue, bestCandHoles = int.MaxValue, bestCandGap = int.MaxValue;
            var seen = new HashSet<int>();

            for (int oi = 0; oi < piece.Orientations.Count; oi++)
            {
                var ori = piece.Orientations[oi];
                for (int i = 0; i < ori.CellX.Length; i++)
                {
                    int x = tx - ori.CellX[i];
                    int y = ty - ori.CellY[i];
                    if (x < 0 || y < 0 || x + ori.Width > work.Width || y + ori.Height > work.Height)
                        continue;

                    int key = y * work.Width + x;
                    if (!seen.Add(key)) continue;
                    if (!CanPlace(work, ori, x, y)) continue;
                    if (options.Validator != null && !options.Validator(piece, ori, x, y)) continue;

                    work.Place(ori, x, y, piece.Id);
                    work.RecalculateExtents();
                    int comps, holes, gap, contact;
                    long score = Score(work, options, out comps, out holes, out gap, out contact);
                    work.Remove(ori, x, y);

                    if (bestCandidate == null ||
                        PackResult.IsTighter(holes, comps, gap, score,
                            bestCandHoles, bestCandComponents, bestCandGap, bestCandidateScore))
                    {
                        bestCandidateScore = score;
                        bestCandidate = new PackPlacement
                        {
                            Piece = piece,
                            Orientation = ori,
                            X = x,
                            Y = y
                        };
                        candComponents = comps;
                        candHoles = holes;
                        candGap = gap;
                        candContact = contact;
                        bestCandComponents = comps;
                        bestCandHoles = holes;
                        bestCandGap = gap;
                    }
                }
            }

            if (bestCandidate != null &&
                PackResult.IsTighter(candHoles, candComponents, candGap, bestCandidateScore,
                    bestHoles, bestComponents, bestGap, bestScore))
            {
                work.Place(bestCandidate.Orientation, bestCandidate.X, bestCandidate.Y, piece.Id);
                placed.Insert(pieceIndex, bestCandidate);
                work.RecalculateExtents();
                bestScore = bestCandidateScore;
                bestPlacements = ClonePlacements(placed);
                bestComponents = candComponents;
                bestHoles = candHoles;
                bestGap = candGap;
                bestContact = candContact;
                return true;
            }

            work.Place(original.Orientation, original.X, original.Y, piece.Id);
            placed.Insert(pieceIndex, original);
            work.RecalculateExtents();
            return false;
        }

        /// <summary>Best placement that physically covers the target empty cell.</summary>
        private static PackPlacement FindCovering(Worker w, PackPiece piece, int target, PackOptions options)
        {
            var top = new List<Candidate>(TopCandidates);
            int tx = target % w.Grid.Width;
            int ty = target / w.Grid.Width;
            var seen = new HashSet<int>();

            for (int oi = 0; oi < piece.Orientations.Count; oi++)
            {
                var ori = piece.Orientations[oi];
                for (int i = 0; i < ori.CellX.Length; i++)
                {
                    int x = tx - ori.CellX[i];
                    int y = ty - ori.CellY[i];
                    if (x < 0 || y < 0 || x + ori.Width > w.Grid.Width || y + ori.Height > w.Grid.Height)
                        continue;

                    int key = (y * w.Grid.Width + x) * 4 + oi;
                    if (!seen.Add(key)) continue;
                    if (!CanPlace(w.Grid, ori, x, y)) continue;

                    int stamp = w.NextStamp();
                    long score = Evaluate(w, piece, ori, x, y, stamp);
                    InsertCandidate(top, new Candidate
                    {
                        Orientation = ori,
                        X = x,
                        Y = y,
                        Score = score
                    });
                }
            }

            for (int i = 0; i < top.Count; i++)
            {
                var c = top[i];
                if (options.Validator != null && !options.Validator(piece, c.Orientation, c.X, c.Y))
                    continue;
                return new PackPlacement
                {
                    Piece = piece,
                    Orientation = c.Orientation,
                    X = c.X,
                    Y = c.Y
                };
            }
            return null;
        }

        /// <summary>
        /// One repair attempt. When <paramref name="forcedFirst"/> is set, that piece is forced to
        /// cover the chosen gap first; the rest are inserted greedily in the given order.
        /// </summary>
        private static bool TryRepair(Worker worker, PackGrid work, List<PackPlacement> placed,
            PackPlacement forcedFirst, List<PackPlacement> order, int target, PackOptions options,
            List<PackPlacement> added, out long score, out int components, out int holes,
            out int gap, out int contact)
        {
            score = long.MaxValue;
            components = holes = gap = contact = 0;
            bool forcedPlaced = forcedFirst == null;

            for (int i = 0; i < order.Count; i++)
            {
                var p = order[i];

                // Insert the forced gap-covering piece once. It replaces exactly one item
                // in the order (itself), so every torn-out piece is reinserted exactly once.
                if (!forcedPlaced)
                {
                    var forced = FindCovering(worker, forcedFirst.Piece, target, options);
                    if (forced == null) return false;
                    work.Place(forced.Orientation, forced.X, forced.Y, forced.Piece.Id);
                    placed.Add(forced);
                    added.Add(forced);
                    forcedPlaced = true;
                }

                if (forcedFirst != null && ReferenceEquals(p, forcedFirst)) continue;

                var best = FindBest(worker, p.Piece, null, false);
                if (best == null) best = FindBest(worker, p.Piece, null, true);
                if (best == null) return false;

                work.Place(best.Orientation, best.X, best.Y, best.Piece.Id);
                placed.Add(best);
                added.Add(best);
            }

            work.RecalculateExtents();
            score = Score(work, options, out components, out holes, out gap, out contact);
            return true;
        }

        /// <summary>
        /// Large Neighborhood Search: pick a gap, tear out the handful of items around it, and
        /// reinsert them with the same best-position heuristic. Accept only improvements
        /// (plus a small deterministic chance of a sideways move to escape a local optimum).
        /// </summary>
        private static PackResult LnsImprove(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options,
            PackResult incumbent, Stopwatch sw)
        {
            if (incumbent == null || !incumbent.Success || incumbent.Placements.Count != pieces.Count)
                return incumbent;

            long bestScore = incumbent.Score;
            var bestPlacements = ClonePlacements(incumbent.Placements);
            int bestComponents = incumbent.EmptyComponents;
            int bestHoles = incumbent.IsolatedHoles;
            int bestGap = incumbent.BBoxGap;
            int bestContact = incumbent.Contact;

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(incumbent.Placements.Count);
            for (int i = 0; i < incumbent.Placements.Count; i++)
            {
                var p = incumbent.Placements[i];
                work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                placed.Add(p);
            }
            work.RecalculateExtents();
            var worker = new Worker(work, pieces, options);
            var rng = new Random(options.RandomSeed ^ unchecked((int)0x51ED270B));

            long lnsStart = sw.ElapsedMilliseconds;
            long lnsEnd = Math.Min(options.TimeBudgetMs, lnsStart + options.LnsBudgetMs);
            int iterations = 0;
            int improvements = 0;

            while (sw.ElapsedMilliseconds < lnsEnd)
            {
                work.RecalculateExtents();
                var gapCells = CollectGapCells(work);
                if (gapCells.Count == 0) break;
                int target = gapCells[rng.Next(gapCells.Count)];

                var removed = PickRuinPieces(work, placed, target, rng);
                if (removed.Count < 2) break;

                for (int i = 0; i < removed.Count; i++)
                {
                    var p = removed[i];
                    work.Remove(p.Orientation, p.X, p.Y);
                    placed.Remove(p);
                }

                var order = new List<PackPlacement>(removed);
                if (rng.Next(2) == 0)
                {
                    order.Sort((a, b) => b.Piece.Area.CompareTo(a.Piece.Area));
                    for (int i = 0; i < order.Count; i++)
                    {
                        int j = rng.Next(i, order.Count);
                        var tmp = order[i];
                        order[i] = order[j];
                        order[j] = tmp;
                    }
                }
                else
                {
                    Shuffle(order, rng);
                }

                // Attempt A: force the nearest torn-out item to cover the target gap.
                // Attempt B: plain greedy repair. Keep the better final layout.
                List<PackPlacement> bestAttempt = null;
                long bestAttemptScore = long.MaxValue;
                int bestComp = 0, bestHole = 0, bestGap2 = 0, bestContact2 = 0;

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var added = new List<PackPlacement>(removed.Count);
                    long score;
                    int comps, holes, gap, contact;
                    bool ok;
                    if (attempt == 0)
                    {
                        ok = TryRepair(worker, work, placed, removed[0], order, target, options,
                            added, out score, out comps, out holes, out gap, out contact);
                    }
                    else
                    {
                        ok = TryRepair(worker, work, placed, null, order, target, options,
                            added, out score, out comps, out holes, out gap, out contact);
                    }

                    if (ok && (bestAttempt == null ||
                               PackResult.IsTighter(holes, comps, gap, score,
                                   bestHole, bestComp, bestGap2, bestAttemptScore)))
                    {
                        bestAttemptScore = score;
                        bestAttempt = ClonePlacements(added);
                        bestComp = comps;
                        bestHole = holes;
                        bestGap2 = gap;
                        bestContact2 = contact;
                    }

                    for (int i = 0; i < added.Count; i++)
                    {
                        var p = added[i];
                        work.Remove(p.Orientation, p.X, p.Y);
                        placed.Remove(p);
                    }
                }

                if (options.Log != null && iterations < 12)
                {
                    options.Log("LNS target=(" + (target % work.Width) + "," + (target / work.Width) +
                                ") removed=" + removed.Count +
                                " attempt=" + (bestAttempt == null ? "fail" : bestAttemptScore.ToString()) +
                                " best=" + bestScore);
                }

                if (bestAttempt != null)
                {
                    bool accept = PackResult.IsTighter(bestHole, bestComp, bestGap2, bestAttemptScore,
                        bestHoles, bestComponents, bestGap, bestScore);
                    if (!accept && bestAttemptScore == bestScore)
                        accept = rng.NextDouble() < 0.35;
                    else if (!accept && bestAttemptScore <= bestScore + 30)
                        accept = rng.NextDouble() < 0.08;

                    if (accept)
                    {
                        for (int i = 0; i < bestAttempt.Count; i++)
                        {
                            var p = bestAttempt[i];
                            work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                            placed.Add(p);
                        }
                        work.RecalculateExtents();
                        if (PackResult.IsTighter(bestHole, bestComp, bestGap2, bestAttemptScore,
                                bestHoles, bestComponents, bestGap, bestScore))
                        {
                            bestScore = bestAttemptScore;
                            bestPlacements = ClonePlacements(placed);
                            bestComponents = bestComp;
                            bestHoles = bestHole;
                            bestGap = bestGap2;
                            bestContact = bestContact2;
                            improvements++;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < removed.Count; i++)
                        {
                            var p = removed[i];
                            work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                            placed.Add(p);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < removed.Count; i++)
                    {
                        var p = removed[i];
                        work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                        placed.Add(p);
                    }
                }

                iterations++;
                if (iterations >= 400) break;
            }

            return new PackResult
            {
                Success = true,
                TimedOut = incumbent.TimedOut,
                Score = bestScore,
                Placements = bestPlacements,
                EmptyComponents = bestComponents,
                IsolatedHoles = bestHoles,
                BBoxGap = bestGap,
                Contact = bestContact,
                LocalMoves = incumbent.LocalMoves,
                LocalImprovements = incumbent.LocalImprovements,
                LnsIterations = iterations,
                LnsImprovements = improvements
            };
        }

        private static int PickRuinCell(PackGrid grid, Random rng)
        {
            int chosen = -1;
            int total = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    if (grid.Wall[idx] || grid.Layers[idx] != 0) continue;

                    int occupied = 0;
                    if (IsSolid(grid, x - 1, y)) occupied++;
                    if (IsSolid(grid, x + 1, y)) occupied++;
                    if (IsSolid(grid, x, y - 1)) occupied++;
                    if (IsSolid(grid, x, y + 1)) occupied++;

                    int weight;
                    if (occupied == 4) weight = 12;
                    else if (occupied == 3) weight = 5;
                    else if (InsideBBox(grid, x, y)) weight = 3;
                    else weight = 1;

                    total += weight;
                    if (rng.Next(total) < weight) chosen = idx;
                }
            }
            return chosen;
        }

        private static List<PackPlacement> PickRuinPieces(PackGrid grid, List<PackPlacement> placed,
            int target, Random rng)
        {
            var result = new List<PackPlacement>();
            if (placed.Count < 2) return result;

            int tx = target % grid.Width;
            int ty = target / grid.Width;
            var distances = new int[placed.Count];
            var order = new List<int>(placed.Count);
            for (int i = 0; i < placed.Count; i++)
            {
                distances[i] = MinDistanceToCell(placed[i], tx, ty);
                order.Add(i);
            }
            order.Sort((a, b) => distances[a].CompareTo(distances[b]));

            int k = 2 + rng.Next(0, 3);
            if (k > placed.Count) k = placed.Count;
            if (k < 2) return result;

            for (int i = 0; i < k; i++)
                result.Add(placed[order[i]]);

            // Occasionally swap the last (farthest) pick for a slightly farther random piece.
            if (k > 1 && k < placed.Count && rng.Next(2) == 0)
            {
                int lo = k - 1;
                int hi = Math.Min(placed.Count, k + 5);
                int pick = lo + rng.Next(hi - lo);
                result[k - 1] = placed[order[pick]];
            }
            return result;
        }

        private static int MinDistanceToCell(PackPlacement p, int tx, int ty)
        {
            int best = int.MaxValue;
            var o = p.Orientation;
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int x = p.X + o.CellX[i];
                int y = p.Y + o.CellY[i];
                int d = Math.Abs(x - tx) + Math.Abs(y - ty);
                if (d < best) best = d;
            }
            return best;
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private static bool InsideBBox(PackGrid grid, int x, int y)
        {
            if (grid.MinX < 0) return false;
            return x >= grid.MinX && x <= grid.MaxX && y >= grid.MinY && y <= grid.MaxY;
        }

        private static PackResult TryOrder(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options,
            List<PackPiece> order, Stopwatch sw)
        {
            var work = baseGrid.Clone();
            var worker = new Worker(work, pieces, options);
            var placed = new List<PackPlacement>(pieces.Count);

            for (int i = 0; i < order.Count; i++)
            {
                if ((i & 3) == 0 && sw.ElapsedMilliseconds > options.TimeBudgetMs)
                    return null;

                var piece = order[i];
                var best = FindBest(worker, piece, null, false);
                if (best == null)
                    best = FindBest(worker, piece, null, true);
                if (best == null)
                    return null;

                work.Place(best.Orientation, best.X, best.Y, piece.Id);
                placed.Add(best);
            }

            // Gravity usually helps, but it can occasionally open a new gap. Keep the better one.
            int preHoles, preComponents, preGap, preContact;
            long preScore = Score(work, options, out preComponents, out preHoles, out preGap, out preContact);
            var preGrid = work.Clone();
            var prePlaced = ClonePlacements(placed);

            Compact(worker, placed);

            var result = new PackResult { Placements = placed };
            result.Score = Score(work, options, out result.EmptyComponents, out result.IsolatedHoles,
                out result.BBoxGap, out result.Contact);
            result.MaxEmptyRect = LargestEmptyRectangle(work);
            result.ReadingOrderDisorder = CountReadingOrderDisorder(work);
            result.SameItemBBoxWaste = CountSameItemBBoxWaste(work);
            result.CategoryContacts = CountCategoryContacts(work);
            result.SameItemContacts = CountSameItemContacts(work);
            result.ItemClusters = CountItemClusters(work);
            if (result.Score > preScore)
            {
                result.Placements = prePlaced;
                result.Score = preScore;
                result.EmptyComponents = preComponents;
                result.IsolatedHoles = preHoles;
                result.BBoxGap = preGap;
                result.Contact = preContact;
            }
            return result;
        }

        internal static PackPlacement FindPlacement(PackGrid grid, List<PackPiece> pieces,
            PackPiece piece, PackOptions options)
        {
            if (grid == null || pieces == null || piece == null || options == null) return null;
            grid.StrictOccupancy = options.StrictOccupancy;
            var worker = new Worker(grid, pieces, options);
            return FindBest(worker, piece, null, false) ?? FindBest(worker, piece, null, true);
        }

        private static PackPlacement FindBest(Worker w, PackPiece piece, PackPlacement limit, bool fullScan)
        {
            var list = FindTop(w, piece, limit, fullScan, 1);
            return list.Count > 0 ? list[0] : null;
        }

        private static List<PackPlacement> FindTop(Worker w, PackPiece piece, PackPlacement limit,
            bool fullScan, int maxResults)
        {
            if (maxResults < 1) maxResults = 1;
            var top = new List<Candidate>(TopCandidates);
            var grid = w.Grid;
            var options = w.Options;

            int minX = 0, maxX = grid.Width - 1;
            int minY = 0, maxY = grid.Height - 1;

            if (limit != null)
            {
                // Compaction may only move a piece closer to the chosen corner.
                if (options.FromEnd)
                {
                    minX = limit.X;
                    minY = limit.Y;
                }
                else
                {
                    maxX = limit.X;
                    maxY = limit.Y;
                }
            }
            else if (!fullScan && grid.MaxX >= 0)
            {
                // Normal packing: don't wander far outside the current packed mass.
                if (options.FromEnd)
                {
                    minX = Math.Max(0, grid.MinX - 2);
                    minY = Math.Max(0, grid.MinY - 2);
                }
                else
                {
                    maxX = Math.Min(grid.Width - 1, grid.MaxX + 2);
                    maxY = Math.Min(grid.Height - 1, grid.MaxY + 2);
                }
            }

            for (int oi = 0; oi < piece.Orientations.Count; oi++)
            {
                var ori = piece.Orientations[oi];
                if (ori.Width > grid.Width || ori.Height > grid.Height) continue;

                int oxMax = Math.Min(maxX, grid.Width - ori.Width);
                int oyMax = Math.Min(maxY, grid.Height - ori.Height);
                int oxMin = Math.Max(minX, 0);
                int oyMin = Math.Max(minY, 0);

                for (int y = oyMin; y <= oyMax; y++)
                {
                    for (int x = oxMin; x <= oxMax; x++)
                    {
                        if (!CanPlace(grid, ori, x, y)) continue;

                        int stamp = w.NextStamp();
                        long score = Evaluate(w, piece, ori, x, y, stamp);
                        InsertCandidate(top, new Candidate
                        {
                            Orientation = ori,
                            X = x,
                            Y = y,
                            Score = score
                        });
                    }
                }
            }

            if (options.LocalMaxRectPenalty > 0f && top.Count > 1)
            {
                int rectBefore = LargestEmptyRectangle(grid);
                for (int i = 0; i < top.Count; i++)
                {
                    var c = top[i];
                    grid.Place(c.Orientation, c.X, c.Y, piece.Id);
                    int rectAfter = LargestEmptyRectangle(grid);
                    grid.Remove(c.Orientation, c.X, c.Y);
                    c.Score += (long)((rectBefore - rectAfter) * options.LocalMaxRectPenalty);
                    top[i] = c;
                }
                top.Sort((a, b) => a.Score.CompareTo(b.Score));
            }

            var results = new List<PackPlacement>(maxResults);
            for (int i = 0; i < top.Count && results.Count < maxResults; i++)
            {
                var c = top[i];
                if (options.Validator != null && !options.Validator(piece, c.Orientation, c.X, c.Y))
                    continue;
                results.Add(new PackPlacement
                {
                    Piece = piece,
                    Orientation = c.Orientation,
                    X = c.X,
                    Y = c.Y
                });
            }

            // The adaptive candidate list only keeps the best 16 heuristic positions.
            // If a validator (the game's CheckShapePlacement) rejects all of them, scan
            // the legal range again and keep validator-approved candidates only. Without
            // this, a piece with one legal but low-heuristic spot looks unpackable.
            if (results.Count == 0 && options.Validator != null && top.Count > 0)
            {
                var valid = new List<Candidate>(TopCandidates);
                for (int oi = 0; oi < piece.Orientations.Count; oi++)
                {
                    var ori = piece.Orientations[oi];
                    if (ori.Width > grid.Width || ori.Height > grid.Height) continue;

                    int oxMax = Math.Min(maxX, grid.Width - ori.Width);
                    int oyMax = Math.Min(maxY, grid.Height - ori.Height);
                    int oxMin = Math.Max(minX, 0);
                    int oyMin = Math.Max(minY, 0);

                    for (int y = oyMin; y <= oyMax; y++)
                    {
                        for (int x = oxMin; x <= oxMax; x++)
                        {
                            if (!CanPlace(grid, ori, x, y)) continue;
                            if (!options.Validator(piece, ori, x, y)) continue;
                            int stamp = w.NextStamp();
                            long score = Evaluate(w, piece, ori, x, y, stamp);
                            InsertCandidate(valid, new Candidate
                            {
                                Orientation = ori,
                                X = x,
                                Y = y,
                                Score = score
                            });
                        }
                    }
                }

                for (int i = 0; i < valid.Count && results.Count < maxResults; i++)
                {
                    var c = valid[i];
                    results.Add(new PackPlacement
                    {
                        Piece = piece,
                        Orientation = c.Orientation,
                        X = c.X,
                        Y = c.Y
                    });
                }
            }

            // A restricted scan can miss a legal spot near a wall; retry once with everything.
            if (results.Count == 0 && !fullScan && limit == null &&
                (minX > 0 || minY > 0 || maxX < grid.Width - 1 || maxY < grid.Height - 1))
                return FindTop(w, piece, null, true, maxResults);

            return results;
        }

        private static void InsertCandidate(List<Candidate> top, Candidate c)
        {
            int i = top.Count - 1;
            if (top.Count == TopCandidates && c.Score >= top[i].Score) return;
            top.Add(c);
            i = top.Count - 1;
            while (i > 0 && top[i - 1].Score > top[i].Score)
            {
                var tmp = top[i - 1];
                top[i - 1] = top[i];
                top[i] = tmp;
                i--;
            }
            if (top.Count > TopCandidates) top.RemoveAt(top.Count - 1);
        }

        private static bool CanPlace(PackGrid grid, PackOrientation o, int x, int y)
        {
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

        private static long Evaluate(Worker w, PackPiece piece, PackOrientation o, int x, int y, int stamp)
        {
            var grid = w.Grid;
            int contact = 0;
            int same = 0;
            long distance = 0;
            int holes = 0;

            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                w.PieceStamp[gy * grid.Width + gx] = stamp;

                int dx = w.Options.FromEnd ? grid.Width - 1 - gx : gx;
                int dy = w.Options.FromEnd ? grid.Height - 1 - gy : gy;
                // Row-major preference: fill top rows before lower rows, then left to right.
                distance += (long)dy * (grid.Width + 1) + dx;
            }

            // Contact and same-item adjacency.
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                contact += ContactAt(w, piece, gx - 1, gy, stamp, ref same);
                contact += ContactAt(w, piece, gx + 1, gy, stamp, ref same);
                contact += ContactAt(w, piece, gx, gy - 1, stamp, ref same);
                contact += ContactAt(w, piece, gx, gy + 1, stamp, ref same);
            }

            // Count empty cells that would become fully enclosed 1-cell holes.
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                holes += HoleAt(w, gx - 1, gy, stamp);
                holes += HoleAt(w, gx + 1, gy, stamp);
                holes += HoleAt(w, gx, gy - 1, stamp);
                holes += HoleAt(w, gx, gy + 1, stamp);
            }

            int newMinX = grid.MinX < 0 ? x : Math.Min(grid.MinX, x);
            int newMinY = grid.MinY < 0 ? y : Math.Min(grid.MinY, y);
            int newMaxX = Math.Max(grid.MaxX, x + o.Width - 1);
            int newMaxY = Math.Max(grid.MaxY, y + o.Height - 1);
            int oldSpanX = w.Options.FromEnd
                ? (grid.MinX < 0 ? 0 : grid.Width - grid.MinX)
                : (grid.MaxX < 0 ? 0 : grid.MaxX + 1);
            int oldSpanY = w.Options.FromEnd
                ? (grid.MinY < 0 ? 0 : grid.Height - grid.MinY)
                : (grid.MaxY < 0 ? 0 : grid.MaxY + 1);
            int newSpanX = w.Options.FromEnd ? grid.Width - newMinX : newMaxX + 1;
            int newSpanY = w.Options.FromEnd ? grid.Height - newMinY : newMaxY + 1;
            int growth = newSpanX * newSpanY - oldSpanX * oldSpanY - piece.Area;
            if (growth < 0) growth = 0;

            int rotPenalty = RotationSteps(piece.OriginalRotation, o.Rotation) * 3;

            return (long)holes * 1500L
                   - (long)contact * 7L
                   - (long)same * 4L
                   + (long)growth * 5L
                   + (long)(distance * 0.03)
                   + rotPenalty;
        }

        private static int ContactAt(Worker w, PackPiece piece, int x, int y, int stamp, ref int same)
        {
            if (w.Grid.IsWall(x, y)) return 2;
            int idx = y * w.Grid.Width + x;
            if (w.PieceStamp[idx] == stamp) return 0;
            if (w.Grid.Layers[idx] == 0) return 0;
            int owner = w.Grid.Owner[idx];
            if (owner >= 0 && owner < w.Pieces.Count && w.Pieces[owner].Key == piece.Key)
            {
                same++;
                return 2;
            }
            return 1;
        }

        private static int HoleAt(Worker w, int x, int y, int stamp)
        {
            if (w.Grid.IsWall(x, y)) return 0;
            int idx = y * w.Grid.Width + x;
            if (w.Grid.Layers[idx] != 0 || w.PieceStamp[idx] == stamp) return 0;
            if (w.HoleStamp[idx] == stamp) return 0;

            if (w.IsOccupiedAfter(x - 1, y, stamp) && w.IsOccupiedAfter(x + 1, y, stamp)
                && w.IsOccupiedAfter(x, y - 1, stamp) && w.IsOccupiedAfter(x, y + 1, stamp))
            {
                w.HoleStamp[idx] = stamp;
                return 1;
            }
            return 0;
        }

        private static int RotationSteps(int from, int to)
        {
            int d = Math.Abs((from % 4 + 4) % 4 - (to % 4 + 4) % 4);
            return Math.Min(d, 4 - d);
        }

        private static void Compact(Worker w, List<PackPlacement> placed)
        {
            var order = new List<PackPlacement>(placed);
            for (int pass = 0; pass < 2; pass++)
            {
                order.Sort((a, b) =>
                {
                    int ay = w.Options.FromEnd ? -(a.Y + a.Orientation.Height) : a.Y;
                    int by = w.Options.FromEnd ? -(b.Y + b.Orientation.Height) : b.Y;
                    if (ay != by) return ay.CompareTo(by);
                    return a.X.CompareTo(b.X);
                });

                bool moved = false;
                foreach (var p in order)
                {
                    w.Grid.Remove(p.Orientation, p.X, p.Y);
                    var best = FindBest(w, p.Piece, p, false);
                    if (best != null && IsCloser(best, p, w.Options.FromEnd))
                    {
                        p.Orientation = best.Orientation;
                        p.X = best.X;
                        p.Y = best.Y;
                        moved = true;
                    }
                    w.Grid.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                }
                if (!moved) break;
            }
            w.Grid.RecalculateExtents();
        }

        private static List<PackPlacement> ClonePlacements(List<PackPlacement> src)
        {
            var list = new List<PackPlacement>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i];
                list.Add(new PackPlacement
                {
                    Piece = p.Piece,
                    Orientation = p.Orientation,
                    X = p.X,
                    Y = p.Y
                });
            }
            return list;
        }

        private static bool IsCloser(PackPlacement candidate, PackPlacement current, bool fromEnd)
        {
            if (fromEnd)
            {
                if (candidate.Y != current.Y) return candidate.Y > current.Y;
                return candidate.X > current.X;
            }
            if (candidate.Y != current.Y) return candidate.Y < current.Y;
            return candidate.X < current.X;
        }

        private static long Score(PackGrid grid, PackOptions options, out int components,
            out int isolated, out int bboxGap, out int contact)
        {
            components = CountEmptyComponents(grid);
            isolated = CountIsolated(grid);
            bboxGap = BBoxGap(grid);
            contact = CountContact(grid);

            long distance = 0;
            int occupied = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid.Layers[y * grid.Width + x] == 0) continue;
                    occupied++;
                    int dx = options.FromEnd ? grid.Width - 1 - x : x;
                    int dy = options.FromEnd ? grid.Height - 1 - y : y;
                    distance += (long)dy * (grid.Width + 1) + dx;
                }
            }
            if (occupied == 0) return 0;

            int maxRect = LargestEmptyRectangle(grid);
            int categoryContacts = CountCategoryContacts(grid);
            int sameItemContacts = CountSameItemContacts(grid);
            int itemClusters = CountItemClusters(grid);
            return (long)components * 500L
                   + (long)isolated * 2000L
                   + (long)bboxGap * 6L
                   - (long)contact * 2L
                   + (long)(distance * 0.02)
                   - (long)(maxRect * options.MaxRectWeight)
                   - (long)categoryContacts * 6L
                   - (long)sameItemContacts * 10L
                   + (long)itemClusters * 8L;
        }

        private static int CountCategoryContacts(PackGrid grid)
        {
            if (grid.OwnerCategories == null) return 0;
            int count = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (owner < 0 || owner >= grid.OwnerCategories.Length) continue;
                    string cat = grid.OwnerCategories[owner];
                    if (string.IsNullOrEmpty(cat)) continue;

                    if (x + 1 < grid.Width)
                    {
                        int o2 = grid.Owner[y * grid.Width + x + 1];
                        if (o2 >= 0 && o2 < grid.OwnerCategories.Length &&
                            grid.OwnerCategories[o2] == cat) count++;
                    }
                    if (y + 1 < grid.Height)
                    {
                        int o2 = grid.Owner[(y + 1) * grid.Width + x];
                        if (o2 >= 0 && o2 < grid.OwnerCategories.Length &&
                            grid.OwnerCategories[o2] == cat) count++;
                    }
                }
            }
            return count;
        }

        private static int CountSameItemContacts(PackGrid grid)
        {
            if (grid.OwnerKeys == null) return 0;
            int count = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (owner < 0 || owner >= grid.OwnerKeys.Length) continue;
                    string key = grid.OwnerKeys[owner];
                    if (string.IsNullOrEmpty(key)) continue;
                    if (x + 1 < grid.Width)
                    {
                        int o2 = grid.Owner[y * grid.Width + x + 1];
                        if (o2 >= 0 && o2 < grid.OwnerKeys.Length && grid.OwnerKeys[o2] == key) count++;
                    }
                    if (y + 1 < grid.Height)
                    {
                        int o2 = grid.Owner[(y + 1) * grid.Width + x];
                        if (o2 >= 0 && o2 < grid.OwnerKeys.Length && grid.OwnerKeys[o2] == key) count++;
                    }
                }
            }
            return count;
        }

        private static int CountItemClusters(PackGrid grid)
        {
            if (grid.OwnerKeys == null) return 0;
            int total = grid.Width * grid.Height;
            var seen = new bool[total];
            var stack = new int[total];
            int clusters = 0;

            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (seen[idx] || owner < 0 || owner >= grid.OwnerKeys.Length) continue;
                    string key = grid.OwnerKeys[owner];
                    if (string.IsNullOrEmpty(key)) continue;

                    clusters++;
                    int sp = 0;
                    stack[sp++] = idx;
                    seen[idx] = true;
                    while (sp > 0)
                    {
                        int cur = stack[--sp];
                        int cx = cur % grid.Width;
                        int cy = cur / grid.Width;
                        Push(cx - 1, cy);
                        Push(cx + 1, cy);
                        Push(cx, cy - 1);
                        Push(cx, cy + 1);
                    }

                    void Push(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height) return;
                        int ni = ny * grid.Width + nx;
                        if (seen[ni]) return;
                        int o2 = grid.Owner[ni];
                        if (o2 < 0 || o2 >= grid.OwnerKeys.Length) return;
                        if (grid.OwnerKeys[o2] != key) return;
                        seen[ni] = true;
                        stack[sp++] = ni;
                    }
                }
            }
            return clusters;
        }

        private sealed class ReadingItem
        {
            public int Owner;
            public int FirstCell;
            public string Key;
            public string Category;
        }

        /// <summary>
        /// Inversions between the desired semantic order (category -> identifier -> id)
        /// and the actual row-major reading order. Lower is better.
        /// </summary>
        private static int CountReadingOrderDisorder(PackGrid grid)
        {
            if (grid.OwnerKeys == null || grid.OwnerKeys.Length == 0) return 0;
            var map = new Dictionary<int, ReadingItem>();
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (owner < 0 || owner >= grid.OwnerKeys.Length) continue;
                    string key = grid.OwnerKeys[owner];
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!map.TryGetValue(owner, out var item))
                    {
                        item = new ReadingItem
                        {
                            Owner = owner,
                            FirstCell = y * grid.Width + x,
                            Key = key,
                            Category = grid.OwnerCategories != null &&
                                       owner < grid.OwnerCategories.Length
                                ? grid.OwnerCategories[owner]
                                : ""
                        };
                        map[owner] = item;
                    }
                    else
                    {
                        int cell = y * grid.Width + x;
                        if (cell < item.FirstCell) item.FirstCell = cell;
                    }
                }
            }

            if (map.Count < 2) return 0;
            var desired = new List<ReadingItem>(map.Values);
            desired.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Category ?? "", b.Category ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
                if (c != 0) return c;
                return a.Owner.CompareTo(b.Owner);
            });

            var actual = new List<ReadingItem>(desired);
            actual.Sort((a, b) =>
            {
                int c = a.FirstCell.CompareTo(b.FirstCell);
                return c != 0 ? c : a.Owner.CompareTo(b.Owner);
            });

            var rank = new Dictionary<int, int>();
            for (int i = 0; i < actual.Count; i++) rank[actual[i].Owner] = i;

            int inversions = 0;
            for (int i = 0; i < desired.Count; i++)
            {
                int ri = rank[desired[i].Owner];
                for (int j = i + 1; j < desired.Count; j++)
                {
                    if (ri > rank[desired[j].Owner]) inversions++;
                }
            }
            return inversions;
        }

        /// <summary>
        /// For each identifier that appears in at least two items, waste inside its
        /// item bounding box. A 2x2 block of four identical 1x1 items has waste 0;
        /// a staggered/L-shaped arrangement has positive waste. Lower is better.
        /// </summary>
        private static int CountSameItemBBoxWaste(PackGrid grid)
        {
            if (grid.OwnerKeys == null || grid.OwnerKeys.Length == 0) return 0;
            var stats = new Dictionary<string, ItemBBox>(StringComparer.Ordinal);
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    int owner = grid.Owner[idx];
                    if (owner < 0 || owner >= grid.OwnerKeys.Length) continue;
                    string key = grid.OwnerKeys[owner];
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!stats.TryGetValue(key, out var stat))
                    {
                        stat = new ItemBBox
                        {
                            MinX = x, MaxX = x, MinY = y, MaxY = y
                        };
                        stats[key] = stat;
                    }
                    else
                    {
                        if (x < stat.MinX) stat.MinX = x;
                        if (x > stat.MaxX) stat.MaxX = x;
                        if (y < stat.MinY) stat.MinY = y;
                        if (y > stat.MaxY) stat.MaxY = y;
                    }
                    stat.Cells++;
                    stat.Owners.Add(owner);
                }
            }

            int waste = 0;
            foreach (var pair in stats)
            {
                var stat = pair.Value;
                if (stat.Owners.Count < 2) continue;
                int bbox = (stat.MaxX - stat.MinX + 1) * (stat.MaxY - stat.MinY + 1);
                waste += Math.Max(0, bbox - stat.Cells);
            }
            return waste;
        }

        private sealed class ItemBBox
        {
            public int MinX;
            public int MaxX;
            public int MinY;
            public int MaxY;
            public int Cells;
            public readonly HashSet<int> Owners = new HashSet<int>();
        }

        /// <summary>Largest axis-aligned all-empty rectangle (area), via histogram stacks.</summary>
        private static int LargestEmptyRectangle(PackGrid grid)
        {
            int w = grid.Width;
            int h = grid.Height;
            var heights = new int[w];
            var stack = new int[w + 1];
            int best = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    heights[x] = (grid.Wall[idx] || grid.Layers[idx] != 0) ? 0 : heights[x] + 1;
                }

                int sp = 0;
                for (int x = 0; x <= w; x++)
                {
                    int cur = x < w ? heights[x] : 0;
                    while (sp > 0 && heights[stack[sp - 1]] >= cur)
                    {
                        int height = heights[stack[--sp]];
                        int left = sp > 0 ? stack[sp - 1] + 1 : 0;
                        int area = height * (x - left);
                        if (area > best) best = area;
                    }
                    stack[sp++] = x;
                }
            }
            return best;
        }

        private static int CountEmptyComponents(PackGrid grid)
        {
            int total = grid.Width * grid.Height;
            var seen = new bool[total];
            var stack = new int[total];
            int components = 0;
            int sp = 0;

            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    if (seen[idx] || grid.Wall[idx] || grid.Layers[idx] != 0) continue;

                    components++;
                    sp = 0;
                    stack[sp++] = idx;
                    seen[idx] = true;
                    while (sp > 0)
                    {
                        int cur = stack[--sp];
                        int cx = cur % grid.Width;
                        int cy = cur / grid.Width;
                        Push(cx - 1, cy);
                        Push(cx + 1, cy);
                        Push(cx, cy - 1);
                        Push(cx, cy + 1);
                    }
                }
            }
            return components;

            void Push(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height) return;
                int ni = ny * grid.Width + nx;
                if (seen[ni] || grid.Wall[ni] || grid.Layers[ni] != 0) return;
                seen[ni] = true;
                stack[sp++] = ni;
            }
        }

        private static int CountIsolated(PackGrid grid)
        {
            int count = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int idx = y * grid.Width + x;
                    if (grid.Wall[idx] || grid.Layers[idx] != 0) continue;
                    if (IsSolid(grid, x - 1, y) && IsSolid(grid, x + 1, y)
                        && IsSolid(grid, x, y - 1) && IsSolid(grid, x, y + 1))
                        count++;
                }
            }
            return count;
        }

        private static bool IsSolid(PackGrid grid, int x, int y)
        {
            return grid.IsWall(x, y) || grid.IsOccupied(x, y);
        }

        private static int BBoxGap(PackGrid grid)
        {
            if (grid.MinX < 0) return 0;
            // A cell blocked by the container mask (Wall) is not usable space and must
            // not be reported as a gap inside the occupied bounding box. This matters
            // for non-rectangular containers, where bbox-area minus occupied would
            // otherwise count the mask itself as fragmentation.
            int gap = 0;
            for (int y = grid.MinY; y <= grid.MaxY; y++)
            {
                for (int x = grid.MinX; x <= grid.MaxX; x++)
                {
                    int idx = y * grid.Width + x;
                    if (!grid.Wall[idx] && grid.Layers[idx] == 0) gap++;
                }
            }
            return gap;
        }

        private static int CountContact(PackGrid grid)
        {
            int contact = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid.Layers[y * grid.Width + x] == 0) continue;
                    if (grid.IsWall(x + 1, y) || grid.IsOccupied(x + 1, y)) contact++;
                    if (grid.IsWall(x, y + 1) || grid.IsOccupied(x, y + 1)) contact++;
                    if (x == 0 || grid.IsWall(x - 1, y)) contact++;
                    if (y == 0 || grid.IsWall(x, y - 1)) contact++;
                }
            }
            return contact;
        }

        private static List<List<PackPiece>> BuildOrders(List<PackPiece> pieces, PackOptions options)
        {
            var orders = new List<List<PackPiece>>();
            var areaDesc = new List<PackPiece>(pieces);
            areaDesc.Sort((a, b) =>
            {
                int c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                c = b.MaxDim.CompareTo(a.MaxDim);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Key, b.Key);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });
            orders.Add(areaDesc);

            var maxDim = new List<PackPiece>(pieces);
            maxDim.Sort((a, b) =>
            {
                int c = b.MaxDim.CompareTo(a.MaxDim);
                if (c != 0) return c;
                c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });
            orders.Add(maxDim);

            var height = new List<PackPiece>(pieces);
            height.Sort((a, b) =>
            {
                int c = MaxHeight(b).CompareTo(MaxHeight(a));
                if (c != 0) return c;
                c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });
            orders.Add(height);

            var categoryGroup = new List<PackPiece>(pieces);
            categoryGroup.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.CategoryKey ?? "", b.CategoryKey ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
                if (c != 0) return c;
                c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });
            if (options.CategoryFirst)
            {
                // Smart mode: construct only category-first orders, so "same category
                // together" is not overruled by a slightly tighter generic order.
                var smartOrders = new List<List<PackPiece>>();
                smartOrders.Add(categoryGroup);
                var smartRng = new Random(options.RandomSeed);
                int smartJitters = Math.Min(10, Math.Max(2, options.MaxStrategies - 1));
                for (int k = 0; k < smartJitters; k++)
                {
                    var jitter = new List<PackPiece>(categoryGroup);
                    int window = 3 + (k % 3);
                    for (int i = 0; i < jitter.Count; i += window)
                    {
                        int end = Math.Min(i + window - 1, jitter.Count - 1);
                        for (int j = i; j < end; j++)
                        {
                            int swap = smartRng.Next(j, end + 1);
                            var tmp = jitter[j];
                            jitter[j] = jitter[swap];
                            jitter[swap] = tmp;
                        }
                    }
                    smartOrders.Add(jitter);
                }
                return smartOrders;
            }

            var keyGroup = new List<PackPiece>(areaDesc);
            keyGroup.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                if (c != 0) return c;
                c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });
            orders.Add(keyGroup);

            var original = new List<PackPiece>(pieces);
            original.Sort((a, b) => a.Id.CompareTo(b.Id));
            orders.Add(original);

            if (!options.BigFirst)
            {
                var asc = new List<PackPiece>(areaDesc);
                asc.Reverse();
                orders.Add(asc);
            }

            var rng = new Random(options.RandomSeed);
            int jitters = Math.Min(10, Math.Max(2, options.MaxStrategies - orders.Count));
            for (int k = 0; k < jitters; k++)
            {
                var jitter = new List<PackPiece>(options.BigFirst ? areaDesc : areasAsc(areaDesc));
                int window = 3 + (k % 3);
                for (int i = 0; i < jitter.Count; i += window)
                {
                    int end = Math.Min(i + window - 1, jitter.Count - 1);
                    for (int j = i; j < end; j++)
                    {
                        int swap = rng.Next(j, end + 1);
                        var tmp = jitter[j];
                        jitter[j] = jitter[swap];
                        jitter[swap] = tmp;
                    }
                }
                orders.Add(jitter);
            }

            return orders;

            List<PackPiece> areasAsc(List<PackPiece> src)
            {
                var copy = new List<PackPiece>(src);
                copy.Reverse();
                return copy;
            }
        }

        private static int MaxHeight(PackPiece p)
        {
            int h = 0;
            for (int i = 0; i < p.Orientations.Count; i++)
                if (p.Orientations[i].Height > h) h = p.Orientations[i].Height;
            return h;
        }
    }
}
