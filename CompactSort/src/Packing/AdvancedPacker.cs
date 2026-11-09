using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CompactSort.Packing
{
    public static partial class GridPacker
    {
        /// <summary>
        /// Simulated annealing over complete layouts. Proposals are gap-targeted relocations,
        /// two-piece swaps, and in-place rotations; worse layouts are accepted with a
        /// temperature-dependent probability while the best layout is always kept.
        /// </summary>
        private static PackResult PackAnnealing(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, Stopwatch sw)
        {
            var start = PackGreedy(baseGrid, pieces, options, sw, runLns: true);
            return AnnealFromResult(baseGrid, pieces, options, start, sw);
        }

        private static PackResult AnnealFromResult(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, PackResult start, Stopwatch sw)
        {
            if (start == null || !start.Success) return start;

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(start.Placements.Count);
            for (int i = 0; i < start.Placements.Count; i++)
            {
                var p = start.Placements[i];
                work.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
                placed.Add(p);
            }
            work.RecalculateExtents();
            var worker = new Worker(work, pieces, options);
            var rng = new Random(options.RandomSeed ^ unchecked((int)0x2545F491));

            long bestScore = start.Score;
            var bestPlacements = ClonePlacements(start.Placements);
            int bestComponents = start.EmptyComponents;
            int bestHoles = start.IsolatedHoles;
            int bestGap = start.BBoxGap;
            int bestContact = start.Contact;

            long curScore = bestScore;
            int curComponents = bestComponents;
            int curHoles = bestHoles;
            int curGap = bestGap;
            int curContact = bestContact;

            long begin = sw.ElapsedMilliseconds;
            long end = Math.Min(options.TimeBudgetMs, begin + options.AnnealingBudgetMs);
            int iterations = 0;
            int improvements = 0;

            while (sw.ElapsedMilliseconds < end && iterations < 200000)
            {
                iterations++;
                double progress = (double)(sw.ElapsedMilliseconds - begin) / Math.Max(1, end - begin);
                double temperature = 200.0 * (1.0 - progress) + 1.0;

                int roll = rng.Next(100);
                bool changed;
                if (roll < 55)
                    changed = SaRelocate(work, placed, worker, options, rng, temperature,
                        ref curScore, ref curComponents, ref curHoles, ref curGap, ref curContact,
                        ref bestScore, ref bestPlacements, ref bestComponents, ref bestHoles,
                        ref bestGap, ref bestContact, ref improvements);
                else if (roll < 85)
                    changed = SaSwap(work, placed, worker, options, rng, temperature,
                        ref curScore, ref curComponents, ref curHoles, ref curGap, ref curContact,
                        ref bestScore, ref bestPlacements, ref bestComponents, ref bestHoles,
                        ref bestGap, ref bestContact, ref improvements);
                else
                    changed = SaRotate(work, placed, worker, options, rng, temperature,
                        ref curScore, ref curComponents, ref curHoles, ref curGap, ref curContact,
                        ref bestScore, ref bestPlacements, ref bestComponents, ref bestHoles,
                        ref bestGap, ref bestContact, ref improvements);

                if (!changed && sw.ElapsedMilliseconds >= end) break;
            }

            return new PackResult
            {
                Success = true,
                Score = bestScore,
                Placements = bestPlacements,
                EmptyComponents = bestComponents,
                IsolatedHoles = bestHoles,
                BBoxGap = bestGap,
                Contact = bestContact,
                SearchIterations = iterations,
                SearchImprovements = improvements
            };
        }

        private static bool AcceptSa(int holes, int comps, int gap, long score,
            int curHoles, int curComps, int curGap, long curScore, double temperature, Random rng)
        {
            if (PackResult.IsTighter(holes, comps, gap, score, curHoles, curComps, curGap, curScore))
                return true;
            double delta = (holes - curHoles) * 20000.0
                           + (comps - curComps) * 2000.0
                           + (gap - curGap) * 100.0
                           + (score - curScore) * 0.5;
            if (delta <= 0) return true;
            return rng.NextDouble() < Math.Exp(-delta / Math.Max(0.01, temperature));
        }

        private static bool SaRelocate(PackGrid work, List<PackPlacement> placed, Worker worker,
            PackOptions options, Random rng, double temperature,
            ref long curScore, ref int curComponents, ref int curHoles, ref int curGap, ref int curContact,
            ref long bestScore, ref List<PackPlacement> bestPlacements, ref int bestComponents,
            ref int bestHoles, ref int bestGap, ref int bestContact, ref int improvements)
        {
            var gaps = CollectGapCells(work);
            if (gaps.Count == 0) return false;
            int target = gaps[rng.Next(gaps.Count)];
            var nearby = NearbyPieces(work, placed, target, 4, 16);
            if (nearby.Count == 0) return false;

            int idx = nearby[rng.Next(nearby.Count)];
            var original = placed[idx];
            work.Remove(original.Orientation, original.X, original.Y);
            placed.RemoveAt(idx);

            var candidate = FindCovering(worker, original.Piece, target, options);
            if (candidate == null)
            {
                work.Place(original.Orientation, original.X, original.Y, original.Piece.Id);
                placed.Insert(idx, original);
                return false;
            }

            work.Place(candidate.Orientation, candidate.X, candidate.Y, candidate.Piece.Id);
            work.RecalculateExtents();
            int comps, holes, gap, contact;
            long score = Score(work, options, out comps, out holes, out gap, out contact);

            if (AcceptSa(holes, comps, gap, score, curHoles, curComponents, curGap, curScore, temperature, rng))
            {
                placed.Insert(idx, candidate);
                curScore = score; curComponents = comps; curHoles = holes; curGap = gap; curContact = contact;
                if (PackResult.IsTighter(holes, comps, gap, score, bestHoles, bestComponents, bestGap, bestScore))
                {
                    bestScore = score;
                    bestPlacements = ClonePlacements(placed);
                    bestComponents = comps; bestHoles = holes; bestGap = gap; bestContact = contact;
                    improvements++;
                }
                return true;
            }

            work.Remove(candidate.Orientation, candidate.X, candidate.Y);
            work.Place(original.Orientation, original.X, original.Y, original.Piece.Id);
            placed.Insert(idx, original);
            work.RecalculateExtents();
            return false;
        }

        private static bool SaSwap(PackGrid work, List<PackPlacement> placed, Worker worker,
            PackOptions options, Random rng, double temperature,
            ref long curScore, ref int curComponents, ref int curHoles, ref int curGap, ref int curContact,
            ref long bestScore, ref List<PackPlacement> bestPlacements, ref int bestComponents,
            ref int bestHoles, ref int bestGap, ref int bestContact, ref int improvements)
        {
            if (placed.Count < 2) return false;
            var gaps = CollectGapCells(work);
            int target = gaps.Count > 0 ? gaps[rng.Next(gaps.Count)] : rng.Next(work.Width * work.Height);
            var nearby = NearbyPieces(work, placed, target, 5, 12);
            if (nearby.Count < 2) return false;

            int ai = nearby[rng.Next(nearby.Count)];
            int bi = nearby[rng.Next(nearby.Count)];
            if (ai == bi) return false;

            var a = placed[ai];
            var b = placed[bi];
            work.Remove(a.Orientation, a.X, a.Y);
            work.Remove(b.Orientation, b.X, b.Y);

            var aAtB = new PackPlacement { Piece = a.Piece, Orientation = a.Orientation, X = b.X, Y = b.Y };
            var bAtA = new PackPlacement { Piece = b.Piece, Orientation = b.Orientation, X = a.X, Y = a.Y };
            bool ok = CanPlace(work, aAtB.Orientation, aAtB.X, aAtB.Y);
            if (ok)
            {
                work.Place(aAtB.Orientation, aAtB.X, aAtB.Y, aAtB.Piece.Id);
                ok = CanPlace(work, bAtA.Orientation, bAtA.X, bAtA.Y);
                work.Remove(aAtB.Orientation, aAtB.X, aAtB.Y);
            }
            if (ok && options.Validator != null)
            {
                ok = options.Validator(aAtB.Piece, aAtB.Orientation, aAtB.X, aAtB.Y)
                     && options.Validator(bAtA.Piece, bAtA.Orientation, bAtA.X, bAtA.Y);
            }

            if (!ok)
            {
                work.Place(a.Orientation, a.X, a.Y, a.Piece.Id);
                work.Place(b.Orientation, b.X, b.Y, b.Piece.Id);
                return false;
            }

            work.Place(aAtB.Orientation, aAtB.X, aAtB.Y, aAtB.Piece.Id);
            work.Place(bAtA.Orientation, bAtA.X, bAtA.Y, bAtA.Piece.Id);
            work.RecalculateExtents();
            int comps, holes, gap, contact;
            long score = Score(work, options, out comps, out holes, out gap, out contact);

            if (AcceptSa(holes, comps, gap, score, curHoles, curComponents, curGap, curScore, temperature, rng))
            {
                placed.Remove(a);
                placed.Remove(b);
                placed.Add(aAtB);
                placed.Add(bAtA);
                curScore = score; curComponents = comps; curHoles = holes; curGap = gap; curContact = contact;
                if (PackResult.IsTighter(holes, comps, gap, score, bestHoles, bestComponents, bestGap, bestScore))
                {
                    bestScore = score;
                    bestPlacements = ClonePlacements(placed);
                    bestComponents = comps; bestHoles = holes; bestGap = gap; bestContact = contact;
                    improvements++;
                }
                return true;
            }

            work.Remove(aAtB.Orientation, aAtB.X, aAtB.Y);
            work.Remove(bAtA.Orientation, bAtA.X, bAtA.Y);
            work.Place(a.Orientation, a.X, a.Y, a.Piece.Id);
            work.Place(b.Orientation, b.X, b.Y, b.Piece.Id);
            work.RecalculateExtents();
            return false;
        }

        private static bool SaRotate(PackGrid work, List<PackPlacement> placed, Worker worker,
            PackOptions options, Random rng, double temperature,
            ref long curScore, ref int curComponents, ref int curHoles, ref int curGap, ref int curContact,
            ref long bestScore, ref List<PackPlacement> bestPlacements, ref int bestComponents,
            ref int bestHoles, ref int bestGap, ref int bestContact, ref int improvements)
        {
            if (placed.Count == 0) return false;
            int idx = rng.Next(placed.Count);
            var original = placed[idx];
            var piece = original.Piece;
            work.Remove(original.Orientation, original.X, original.Y);
            placed.RemoveAt(idx);

            PackPlacement candidate = null;
            long candidateScore = long.MaxValue;
            int cc = 0, ch = 0, cg = 0, cct = 0;
            for (int oi = 0; oi < piece.Orientations.Count; oi++)
            {
                var ori = piece.Orientations[oi];
                if (ori.Rotation == original.Orientation.Rotation) continue;
                if (!CanPlace(work, ori, original.X, original.Y)) continue;
                if (options.Validator != null && !options.Validator(piece, ori, original.X, original.Y))
                    continue;

                work.Place(ori, original.X, original.Y, piece.Id);
                work.RecalculateExtents();
                int comps, holes, gap, contact;
                long score = Score(work, options, out comps, out holes, out gap, out contact);
                work.Remove(ori, original.X, original.Y);
                if (candidate == null ||
                    PackResult.IsTighter(holes, comps, gap, score, ch, cc, cg, candidateScore))
                {
                    candidateScore = score;
                    candidate = new PackPlacement
                    {
                        Piece = piece,
                        Orientation = ori,
                        X = original.X,
                        Y = original.Y
                    };
                    cc = comps; ch = holes; cg = gap; cct = contact;
                }
            }

            if (candidate == null)
            {
                work.Place(original.Orientation, original.X, original.Y, piece.Id);
                placed.Insert(idx, original);
                return false;
            }

            work.Place(candidate.Orientation, candidate.X, candidate.Y, piece.Id);
            work.RecalculateExtents();
            if (AcceptSa(ch, cc, cg, candidateScore, curHoles, curComponents, curGap, curScore, temperature, rng))
            {
                placed.Insert(idx, candidate);
                curScore = candidateScore; curComponents = cc; curHoles = ch; curGap = cg; curContact = cct;
                if (PackResult.IsTighter(ch, cc, cg, candidateScore, bestHoles, bestComponents, bestGap, bestScore))
                {
                    bestScore = candidateScore;
                    bestPlacements = ClonePlacements(placed);
                    bestComponents = cc; bestHoles = ch; bestGap = cg; bestContact = cct;
                    improvements++;
                }
                return true;
            }

            work.Remove(candidate.Orientation, candidate.X, candidate.Y);
            work.Place(original.Orientation, original.X, original.Y, piece.Id);
            placed.Insert(idx, original);
            work.RecalculateExtents();
            return false;
        }

        /// <summary>
        /// Time-boxed branch-and-bound / DFS. It uses the greedy result as the incumbent,
        /// orders items large-first, branches over the best few placements, and prunes by
        /// remaining-area feasibility plus an aggressive score cutoff. This is an experiment,
        /// not an exact solver.
        /// </summary>
        private static PackResult PackBranchAndBound(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, Stopwatch sw)
        {
            var incumbent = PackGreedy(baseGrid, pieces, options, sw, runLns: false);
            if (!incumbent.Success) return incumbent;

            var order = new List<PackPiece>(pieces);
            order.Sort((a, b) =>
            {
                int c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return b.MaxDim.CompareTo(a.MaxDim);
            });

            var work = baseGrid.Clone();
            var placed = new List<PackPlacement>(pieces.Count);
            var worker = new Worker(work, pieces, options);
            var best = incumbent;
            int totalArea = 0;
            for (int i = 0; i < pieces.Count; i++) totalArea += pieces[i].Area;

            int explored = 0;
            int improvements = 0;
            int branch = Math.Max(1, options.BeamBranch);

            void Search(int depth, int placedArea)
            {
                if (sw.ElapsedMilliseconds > options.TimeBudgetMs) return;
                if (depth == order.Count)
                {
                    int comps, holes, gap, contact;
                    long score = Score(work, options, out comps, out holes, out gap, out contact);
                    if (best == null ||
                        PackResult.IsTighter(holes, comps, gap, score,
                            best.IsolatedHoles, best.EmptyComponents, best.BBoxGap, best.Score))
                    {
                        best = new PackResult
                        {
                            Success = true,
                            Score = score,
                            Placements = ClonePlacements(placed),
                            EmptyComponents = comps,
                            IsolatedHoles = holes,
                            BBoxGap = gap,
                            Contact = contact
                        };
                        improvements++;
                    }
                    return;
                }

                int free = CountFreeCells(work);
                if (free < totalArea - placedArea) return;

                var piece = order[depth];
                var candidates = FindTop(worker, piece, null, false, branch);
                if (candidates.Count == 0)
                    candidates = FindTop(worker, piece, null, true, branch);

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (sw.ElapsedMilliseconds > options.TimeBudgetMs) return;
                    explored++;
                    var cand = candidates[i];
                    work.Place(cand.Orientation, cand.X, cand.Y, piece.Id);
                    placed.Add(cand);

                    int comps, holes, gap, contact;
                    long partial = Score(work, options, out comps, out holes, out gap, out contact);
                    bool cutoff = best != null && depth > order.Count / 2 &&
                                  partial > best.Score + 600;
                    if (!cutoff)
                        Search(depth + 1, placedArea + piece.Area);

                    placed.RemoveAt(placed.Count - 1);
                    work.Remove(cand.Orientation, cand.X, cand.Y);
                }
            }

            Search(0, 0);

            best.SearchIterations = explored;
            best.SearchImprovements = improvements;
            return best;
        }

        private static int CountFreeCells(PackGrid grid)
        {
            int count = 0;
            for (int i = 0; i < grid.Layers.Length; i++)
            {
                if (!grid.Wall[i] && grid.Layers[i] == 0) count++;
            }
            return count;
        }

        /// <summary>
        /// Order-based genetic algorithm: genes are the item insertion order, and a greedy
        /// decoder turns an order into a layout. Crossover is order crossover (OX); mutation
        /// swaps two items. Kept mainly as a comparison baseline.
        /// </summary>
        private static PackResult PackGenetic(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, Stopwatch sw)
        {
            int n = pieces.Count;
            int popSize = Math.Max(4, options.GeneticPopulation);
            int generations = Math.Max(1, options.GeneticGenerations);
            var rng = new Random(options.RandomSeed ^ unchecked((int)0x6D2B79F5));

            var seedOrders = BuildOrders(pieces, options);
            var population = new List<PackResult>(popSize);
            int decodes = 0;
            int improvements = 0;

            for (int i = 0; i < seedOrders.Count && population.Count < popSize; i++)
            {
                var order = ToIdArray(seedOrders[i], n);
                var decoded = DecodeOrder(baseGrid, pieces, options, order, sw, ref decodes);
                if (decoded != null && decoded.Success) population.Add(decoded);
            }
            while (population.Count < popSize && sw.ElapsedMilliseconds < options.TimeBudgetMs)
            {
                var order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                Shuffle(order, rng);
                var decoded = DecodeOrder(baseGrid, pieces, options, order, sw, ref decodes);
                if (decoded != null && decoded.Success) population.Add(decoded);
            }
            if (population.Count == 0)
                return PackGreedy(baseGrid, pieces, options, sw, runLns: true);

            PackResult best = BestOf(population);
            long bestScore = best.Score;

            for (int gen = 0; gen < generations; gen++)
            {
                if (sw.ElapsedMilliseconds > options.TimeBudgetMs) break;
                population.Sort((a, b) => a.IsTighterThan(b) ? -1 : (b.IsTighterThan(a) ? 1 : 0));

                var next = new List<PackResult>(popSize);
                for (int i = 0; i < Math.Min(2, population.Count); i++) next.Add(population[i]);

                while (next.Count < popSize && sw.ElapsedMilliseconds < options.TimeBudgetMs)
                {
                    var pa = population[rng.Next(population.Count)];
                    var pb = population[rng.Next(population.Count)];
                    var childOrder = Crossover(pa.Order, pb.Order, rng);
                    if (rng.Next(100) < 15) Mutate(childOrder, rng);

                    var decoded = DecodeOrder(baseGrid, pieces, options, childOrder, sw, ref decodes);
                    if (decoded == null || !decoded.Success) continue;
                    next.Add(decoded);
                    if (decoded.IsTighterThan(best))
                    {
                        best = decoded;
                        bestScore = decoded.Score;
                        improvements++;
                    }
                }
                population = next;
            }

            return new PackResult
            {
                Success = true,
                Score = best.Score,
                Placements = best.Placements,
                EmptyComponents = best.EmptyComponents,
                IsolatedHoles = best.IsolatedHoles,
                BBoxGap = best.BBoxGap,
                Contact = best.Contact,
                SearchIterations = decodes,
                SearchImprovements = improvements
            };
        }

        private static PackResult DecodeOrder(PackGrid baseGrid, List<PackPiece> pieces,
            PackOptions options, int[] order, Stopwatch sw, ref int decodes)
        {
            if (sw.ElapsedMilliseconds > options.TimeBudgetMs) return null;
            decodes++;
            var grid = baseGrid.Clone();
            var placed = new List<PackPlacement>(pieces.Count);
            var worker = new Worker(grid, pieces, options);
            for (int i = 0; i < order.Length; i++)
            {
                int id = order[i];
                if (id < 0 || id >= pieces.Count) return null;
                var piece = pieces[id];
                var best = FindBest(worker, piece, null, false);
                if (best == null) best = FindBest(worker, piece, null, true);
                if (best == null) return null;
                grid.Place(best.Orientation, best.X, best.Y, piece.Id);
                placed.Add(best);
            }
            grid.RecalculateExtents();
            int comps, holes, gap, contact;
            long score = Score(grid, options, out comps, out holes, out gap, out contact);
            return new PackResult
            {
                Success = true,
                Score = score,
                Placements = placed,
                EmptyComponents = comps,
                IsolatedHoles = holes,
                BBoxGap = gap,
                Contact = contact,
                Order = order
            };
        }

        private static int[] ToIdArray(List<PackPiece> order, int n)
        {
            var result = new int[n];
            for (int i = 0; i < n; i++) result[i] = order[i].Id;
            return result;
        }

        private static PackResult BestOf(List<PackResult> list)
        {
            var best = list[0];
            for (int i = 1; i < list.Count; i++)
                if (list[i].IsTighterThan(best)) best = list[i];
            return best;
        }

        private static int[] Crossover(int[] a, int[] b, Random rng)
        {
            int n = a.Length;
            var child = new int[n];
            var used = new bool[n];
            int lo = rng.Next(n);
            int hi = rng.Next(n);
            if (lo > hi) { int t = lo; lo = hi; hi = t; }
            for (int i = lo; i <= hi; i++)
            {
                child[i] = a[i];
                used[a[i]] = true;
            }
            int pos = (hi + 1) % n;
            for (int i = 0; i < n; i++)
            {
                int val = b[(hi + 1 + i) % n];
                if (used[val]) continue;
                child[pos] = val;
                used[val] = true;
                pos = (pos + 1) % n;
            }
            return child;
        }

        private static void Mutate(int[] order, Random rng)
        {
            if (order.Length < 2) return;
            int i = rng.Next(order.Length);
            int j = rng.Next(order.Length);
            if (i == j) return;
            int t = order[i];
            order[i] = order[j];
            order[j] = t;
        }

        private static void Shuffle(int[] list, Random rng)
        {
            for (int i = list.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }
    }
}
