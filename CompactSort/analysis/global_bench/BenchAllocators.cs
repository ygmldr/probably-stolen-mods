using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CompactSort.Packing;

namespace CompactSort.GlobalBench
{
    internal abstract class GlobalAllocator
    {
        public abstract string Name { get; }

        public abstract GlobalBenchResult Run(BenchCase bench, int caseId);

        protected PackOptions PlacementOptions()
        {
            return new PackOptions
            {
                Algorithm = PackAlgorithm.Greedy,
                BigFirst = true,
                FromEnd = false,
                AllowRotation = true,
                TimeBudgetMs = 1000,
                MaxStrategies = 12,
                EnableGroupingSwap = false,
                EnableLns = false,
                StrictOccupancy = true
            };
        }

        protected PackOptions CompactOptions()
        {
            return new PackOptions
            {
                Algorithm = PackAlgorithm.Greedy,
                BigFirst = true,
                FromEnd = false,
                AllowRotation = true,
                TimeBudgetMs = 120,
                MaxStrategies = 12,
                EnableGroupingSwap = false,
                EnableLns = false,
                StrictOccupancy = true
            };
        }

        protected void CompactBins(BenchCase bench)
        {
            var options = CompactOptions();
            for (int i = 0; i < bench.Bins.Count; i++)
            {
                if (bench.Bins[i].Items.Count <= 1) continue;
                if (bench.Bins[i].Compact(options)) continue;

                // Category-first packing can fail on a bin where a generic order
                // still fits; keep the original layout if both fail.
                var fallback = CompactOptions();
                fallback.CategoryFirst = false;
                fallback.EnableGroupingSwap = false;
                bench.Bins[i].Compact(fallback);
            }
        }

        protected static List<List<BenchItem>> BuildGroups(List<BenchItem> items)
        {
            var map = new Dictionary<string, List<BenchItem>>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!map.TryGetValue(item.Key ?? "", out var group))
                {
                    group = new List<BenchItem>();
                    map[item.Key ?? ""] = group;
                }
                group.Add(item);
            }

            var groups = new List<List<BenchItem>>();
            foreach (var pair in map)
                if (pair.Value.Count > 1) groups.Add(pair.Value);
            groups.Sort((a, b) =>
            {
                int c = TotalArea(b).CompareTo(TotalArea(a));
                if (c != 0) return c;
                return b.Count.CompareTo(a.Count);
            });
            return groups;
        }

        protected static int TotalArea(List<BenchItem> items)
        {
            int area = 0;
            for (int i = 0; i < items.Count; i++) area += items[i].Area;
            return area;
        }

        protected static int CountOf(Dictionary<string, int> map, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            return map.TryGetValue(key, out int value) ? value : 0;
        }

        protected static bool SameOrigin(List<BenchItem> items, BenchBin bin)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].OriginalBin != bin.Id) return false;
            return true;
        }

        protected static long CandidateScore(BenchBin bin, List<BenchItem> unit)
        {
            string key = unit[0].Key ?? "";
            string category = unit[0].Category ?? "";
            bool same = SameOrigin(unit, bin);
            return CountOf(bin.CategoryCounts, category) * 100000L
                   + CountOf(bin.KeyCounts, key) * 1000L
                   + (same ? 500L : 0L)
                   + bin.Free;
        }

        protected static bool TryPlaceUnit(BenchBin bin, List<BenchItem> unit, PackOptions options)
        {
            var ordered = new List<BenchItem>(unit);
            ordered.Sort((a, b) => b.Area.CompareTo(a.Area));
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!bin.TryPlace(ordered[i], options)) return false;
            }
            return true;
        }

        protected static BenchBin BestGroupBin(BenchCase bench, List<BenchItem> group,
            PackOptions options, out long bestScore, out BenchBin best)
        {
            bestScore = long.MinValue;
            best = null;
            for (int i = 0; i < bench.Bins.Count; i++)
            {
                var bin = bench.Bins[i];
                var copy = bin.Clone();
                if (!TryPlaceUnit(copy, group, options)) continue;
                long score = CandidateScore(bin, group);
                if (best == null || score > bestScore)
                {
                    best = copy;
                    bestScore = score;
                }
            }
            return best;
        }

        protected GlobalBenchResult Finish(string algorithm, int caseId, BenchCase bench,
            Stopwatch sw, bool success, string error)
        {
            sw.Stop();
            var result = new GlobalBenchResult
            {
                Algorithm = algorithm,
                CaseId = caseId,
                Success = success,
                Error = error,
                ElapsedMs = sw.ElapsedMilliseconds
            };

            if (success)
            {
                string validationError;
                if (!GlobalMetrics.Validate(bench, out validationError))
                {
                    result.Success = false;
                    result.Error = validationError;
                    return result;
                }
                result.Metrics = GlobalMetrics.FromCase(bench, PlacementOptions());
            }
            return result;
        }
    }

    internal sealed class GreedyAllocator : GlobalAllocator
    {
        private readonly bool _compact;

        public GreedyAllocator(bool compact)
        {
            _compact = compact;
        }

        public override string Name
        {
            get { return _compact ? "greedy+compact" : "greedy"; }
        }

        public override GlobalBenchResult Run(BenchCase bench, int caseId)
        {
            var sw = Stopwatch.StartNew();
            var options = PlacementOptions();
            var remaining = new HashSet<BenchItem>(bench.Items);
            var groups = BuildGroups(bench.Items);

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                var candidates = new List<BenchBin>(bench.Bins);
                string category = group[0].Category ?? "";
                string key = group[0].Key ?? "";
                candidates.Sort((a, b) =>
                {
                    int c = CountOf(b.CategoryCounts, category).CompareTo(
                        CountOf(a.CategoryCounts, category));
                    if (c != 0) return c;
                    c = CountOf(b.KeyCounts, key).CompareTo(CountOf(a.KeyCounts, key));
                    if (c != 0) return c;
                    bool ao = SameOrigin(group, a);
                    bool bo = SameOrigin(group, b);
                    if (ao != bo) return ao ? -1 : 1;
                    return b.Free.CompareTo(a.Free);
                });

                for (int bi = 0; bi < candidates.Count; bi++)
                {
                    var bin = candidates[bi];
                    var copy = bin.Clone();
                    if (!TryPlaceUnit(copy, group, options)) continue;
                    bin.CopyFrom(copy);
                    for (int i = 0; i < group.Count; i++) remaining.Remove(group[i]);
                    break;
                }
            }

            var rest = new List<BenchItem>(remaining);
            rest.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Category ?? "", b.Category ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
                if (c != 0) return c;
                return b.Area.CompareTo(a.Area);
            });

            for (int ii = 0; ii < rest.Count; ii++)
            {
                var item = rest[ii];
                string category = item.Category ?? "";
                string key = item.Key ?? "";
                var candidates = new List<BenchBin>(bench.Bins);
                candidates.Sort((a, b) =>
                {
                    int c = CountOf(b.KeyCounts, key).CompareTo(CountOf(a.KeyCounts, key));
                    if (c != 0) return c;
                    c = CountOf(b.CategoryCounts, category).CompareTo(
                        CountOf(a.CategoryCounts, category));
                    if (c != 0) return c;
                    bool ao = item.OriginalBin == a.Id;
                    bool bo = item.OriginalBin == b.Id;
                    if (ao != bo) return ao ? -1 : 1;
                    return b.Free.CompareTo(a.Free);
                });

                bool placed = false;
                for (int bi = 0; bi < candidates.Count && !placed; bi++)
                {
                    if (candidates[bi].TryPlace(item, options)) placed = true;
                }
                if (!placed)
                    return Finish(Name, caseId, bench, sw, false, "could not place " + item.Key);
            }

            if (_compact) CompactBins(bench);
            return Finish(Name, caseId, bench, sw, true, null);
        }
    }

    internal sealed class RegretAllocator : GlobalAllocator
    {
        private readonly bool _compact;

        public RegretAllocator(bool compact)
        {
            _compact = compact;
        }

        public override string Name
        {
            get { return _compact ? "regret+compact" : "regret"; }
        }

        public override GlobalBenchResult Run(BenchCase bench, int caseId)
        {
            var sw = Stopwatch.StartNew();
            var options = PlacementOptions();
            var remaining = new HashSet<BenchItem>(bench.Items);
            var groups = BuildGroups(bench.Items);

            while (groups.Count > 0)
            {
                List<BenchItem> chosen = null;
                BenchBin chosenBin = null;
                long chosenRegret = long.MinValue;

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];
                    long bestScore;
                    BenchBin best;
                    BestGroupBin(bench, group, options, out bestScore, out best);
                    if (best == null)
                    {
                        // Whole group cannot fit anywhere; leave its items for the
                        // individual fallback phase.
                        chosen = null;
                        chosenBin = null;
                        chosenRegret = long.MinValue;
                        break;
                    }

                    long second = SecondGroupScore(bench, group, options, best);
                    long regret = second == long.MinValue ? long.MaxValue / 4 : bestScore - second;
                    if (regret > chosenRegret)
                    {
                        chosenRegret = regret;
                        chosen = group;
                        chosenBin = best;
                    }
                }

                if (chosen == null)
                {
                    // Remove all groups that cannot fit whole; their items stay in
                    // remaining for individual placement.
                    groups.RemoveAll(g => BestGroupBin(bench, g, options, out _, out _) == null);
                    if (groups.Count == 0) break;
                    continue;
                }

                var target = FindBinById(bench, chosenBin.Id);
                target.CopyFrom(chosenBin);
                for (int i = 0; i < chosen.Count; i++) remaining.Remove(chosen[i]);
                groups.Remove(chosen);
            }

            var rest = new List<BenchItem>(remaining);
            while (rest.Count > 0)
            {
                BenchItem chosenItem = null;
                BenchBin chosenBin = null;
                long chosenRegret = long.MinValue;

                for (int i = 0; i < rest.Count; i++)
                {
                    var item = rest[i];
                    var scores = new List<long>();
                    var bins = new List<BenchBin>();
                    for (int bi = 0; bi < bench.Bins.Count; bi++)
                    {
                        var copy = bench.Bins[bi].Clone();
                        if (!copy.TryPlace(item, options)) continue;
                        bins.Add(copy);
                        scores.Add(CandidateScore(bench.Bins[bi], new List<BenchItem> { item }));
                    }

                    if (bins.Count == 0)
                    {
                        return Finish(Name, caseId, bench, sw, false, "no bin for " + item.Key);
                    }

                    long regret;
                    if (bins.Count == 1)
                    {
                        regret = long.MaxValue / 4;
                    }
                    else
                    {
                        long best = long.MinValue;
                        long second = long.MinValue;
                        for (int s = 0; s < scores.Count; s++)
                        {
                            if (scores[s] > best)
                            {
                                second = best;
                                best = scores[s];
                            }
                            else if (scores[s] > second)
                            {
                                second = scores[s];
                            }
                        }
                        regret = best - second;
                    }

                    if (regret > chosenRegret)
                    {
                        chosenRegret = regret;
                        chosenItem = item;
                        chosenBin = bins[BestScoreIndex(scores)];
                    }
                }

                if (chosenItem == null)
                    return Finish(Name, caseId, bench, sw, false, "regret could not choose item");

                var target = FindBinById(bench, chosenBin.Id);
                target.CopyFrom(chosenBin);
                rest.Remove(chosenItem);
            }

            if (_compact) CompactBins(bench);
            return Finish(Name, caseId, bench, sw, true, null);
        }

        private static long SecondGroupScore(BenchCase bench, List<BenchItem> group,
            PackOptions options, BenchBin best)
        {
            long second = long.MinValue;
            long bestScore = long.MinValue;
            for (int i = 0; i < bench.Bins.Count; i++)
            {
                var copy = bench.Bins[i].Clone();
                if (!TryPlaceUnit(copy, group, options)) continue;
                long score = CandidateScore(bench.Bins[i], group);
                if (score > bestScore)
                {
                    second = bestScore;
                    bestScore = score;
                }
                else if (score > second)
                {
                    second = score;
                }
            }
            return second;
        }

        private static int BestScoreIndex(List<long> scores)
        {
            int best = 0;
            for (int i = 1; i < scores.Count; i++)
                if (scores[i] > scores[best]) best = i;
            return best;
        }

        private static BenchBin FindBinById(BenchCase bench, int id)
        {
            for (int i = 0; i < bench.Bins.Count; i++)
                if (bench.Bins[i].Id == id) return bench.Bins[i];
            return null;
        }
    }
}
