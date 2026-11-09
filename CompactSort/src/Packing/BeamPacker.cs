using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CompactSort.Packing
{
    public static partial class GridPacker
    {
        private sealed class BeamState
        {
            public PackGrid Grid;
            public List<PackPlacement> Placed;
            public long Score;
            public int Components;
            public int Holes;
            public int Gap;
            public int Contact;
        }

        /// <summary>
        /// Beam search over partial layouts. At each item we expand the best few placements
        /// for every state, merge the children, and keep only the best <see cref="PackOptions.BeamWidth"/>.
        /// This keeps several plausible packings alive instead of committing to one greedy path.
        /// </summary>
        private static PackResult PackBeam(PackGrid baseGrid, List<PackPiece> pieces, PackOptions options,
            Stopwatch sw, bool runLns)
        {
            var best = new PackResult();
            long bestScore = long.MaxValue;
            var orders = BuildOrders(pieces, options);
            int orderLimit = Math.Min(3, orders.Count);

            for (int oi = 0; oi < orderLimit; oi++)
            {
                if (sw.ElapsedMilliseconds > options.TimeBudgetMs && bestScore != long.MaxValue)
                {
                    best.TimedOut = true;
                    break;
                }

                var states = new List<BeamState>(options.BeamWidth);
                states.Add(new BeamState
                {
                    Grid = baseGrid.Clone(),
                    Placed = new List<PackPlacement>(pieces.Count)
                });

                var order = orders[oi];
                bool failed = false;

                for (int pi = 0; pi < order.Count; pi++)
                {
                    if (sw.ElapsedMilliseconds > options.TimeBudgetMs)
                    {
                        failed = true;
                        break;
                    }

                    var piece = order[pi];
                    var next = new List<BeamState>(states.Count * options.BeamBranch);
                    var seen = new HashSet<int>();

                    for (int si = 0; si < states.Count; si++)
                    {
                        var state = states[si];
                        var worker = new Worker(state.Grid, pieces, options);
                        var candidates = FindTop(worker, piece, null, false, options.BeamBranch);
                        if (candidates.Count == 0)
                            candidates = FindTop(worker, piece, null, true, options.BeamBranch);

                        for (int ci = 0; ci < candidates.Count; ci++)
                        {
                            var cand = candidates[ci];
                            var childGrid = state.Grid.Clone();
                            childGrid.Place(cand.Orientation, cand.X, cand.Y, piece.Id);
                            childGrid.RecalculateExtents();

                            int signature = LayoutSignature(childGrid, state.Placed, cand);
                            if (!seen.Add(signature)) continue;

                            var placed = new List<PackPlacement>(state.Placed.Count + 1);
                            placed.AddRange(state.Placed);
                            placed.Add(cand);

                            int comps, holes, gap, contact;
                            long score = Score(childGrid, options, out comps, out holes, out gap, out contact);
                            next.Add(new BeamState
                            {
                                Grid = childGrid,
                                Placed = placed,
                                Score = score,
                                Components = comps,
                                Holes = holes,
                                Gap = gap,
                                Contact = contact
                            });
                        }
                    }

                    if (next.Count == 0)
                    {
                        failed = true;
                        break;
                    }

                    next.Sort(CompareBeamStates);
                    if (next.Count > options.BeamWidth)
                        next.RemoveRange(options.BeamWidth, next.Count - options.BeamWidth);
                    states = next;
                }

                if (failed) continue;

                for (int si = 0; si < states.Count; si++)
                {
                    var state = states[si];
                    if (state.Placed.Count != pieces.Count) continue;
                    if (bestScore == long.MaxValue ||
                        PackResult.IsTighter(state.Holes, state.Components, state.Gap, state.Score,
                            best.IsolatedHoles, best.EmptyComponents, best.BBoxGap, best.Score))
                    {
                        bestScore = state.Score;
                        best = new PackResult
                        {
                            Success = true,
                            Score = state.Score,
                            Placements = state.Placed,
                            EmptyComponents = state.Components,
                            IsolatedHoles = state.Holes,
                            BBoxGap = state.Gap,
                            Contact = state.Contact
                        };
                    }
                }
            }

            if (bestScore == long.MaxValue)
            {
                // Beam pruning can occasionally kill every branch; fall back to greedy.
                var fallback = PackGreedy(baseGrid, pieces, options, sw, runLns: false);
                if (runLns && fallback.Success && options.EnableLns)
                {
                    fallback = LocalMoveImprove(baseGrid, pieces, options, fallback, sw);
                    fallback = LnsImprove(baseGrid, pieces, options, fallback, sw);
                }
                return fallback;
            }

            if (runLns && options.EnableLns)
            {
                best = LocalMoveImprove(baseGrid, pieces, options, best, sw);
                best = LnsImprove(baseGrid, pieces, options, best, sw);
            }
            return best;
        }

        private static int CompareBeamStates(BeamState a, BeamState b)
        {
            if (a.Holes != b.Holes) return a.Holes.CompareTo(b.Holes);
            if (a.Components != b.Components) return a.Components.CompareTo(b.Components);
            if (a.Gap != b.Gap) return a.Gap.CompareTo(b.Gap);
            return a.Score.CompareTo(b.Score);
        }

        private static int LayoutSignature(PackGrid grid, List<PackPlacement> placed, PackPlacement newest)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < grid.Layers.Length; i++)
                {
                    if (grid.Layers[i] == 0) continue;
                    h = h * 31 + i;
                    h = h * 31 + grid.Layers[i];
                }
                h = h * 31 + newest.Piece.Id;
                h = h * 31 + newest.X;
                h = h * 31 + newest.Y;
                h = h * 31 + newest.Orientation.Rotation;
                return h;
            }
        }
    }
}
