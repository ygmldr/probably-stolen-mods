using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CompactSort.GlobalBench
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int cases = 20;
            int seed = 20261104;
            int bins = 4;
            int items = 42;
            string outPath = "analysis/global_bench/results.csv";
            bool selftest = false;
            string algorithmArg = "greedy,greedy+compact,regret,regret+compact";
            string snapshotPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--selftest": selftest = true; break;
                    case "--cases": cases = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--bins": bins = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--items": items = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out": outPath = args[++i]; break;
                    case "--algorithms": algorithmArg = args[++i]; break;
                    case "--snapshot": snapshotPath = args[++i]; break;
                    case "--help":
                        PrintHelp();
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown argument: " + args[i]);
                        return 2;
                }
            }

            var allocators = BuildAllocators(algorithmArg);
            if (selftest) return RunSelfTest(allocators);

            var benchCases = new List<BenchCase>();
            if (!string.IsNullOrEmpty(snapshotPath))
            {
                benchCases.AddRange(BenchSnapshotLoader.Load(snapshotPath));
                if (benchCases.Count == 0)
                {
                    Console.Error.WriteLine("no snapshot cases loaded from " + snapshotPath);
                    return 2;
                }
                cases = benchCases.Count;
            }
            else
            {
                cases = Math.Max(1, cases);
                for (int ci = 0; ci < cases; ci++)
                    benchCases.Add(BenchGenerator.Generate(seed + ci * 7919, bins, items));
            }

            var all = new List<GlobalBenchResult>();
            Console.WriteLine("Global benchmark: cases=" + cases +
                              (snapshotPath == null ? " seed=" + seed + " bins=" + bins + " items=" + items
                                                    : " snapshot=" + snapshotPath));
            Console.WriteLine("algorithms=" + string.Join(",", allocators.Select(a => a.Name)));
            Console.WriteLine();

            for (int ci = 0; ci < benchCases.Count; ci++)
            {
                var bench = benchCases[ci];
                for (int ai = 0; ai < allocators.Count; ai++)
                {
                    var copy = bench.Clone();
                    GlobalBenchResult result;
                    try
                    {
                        result = allocators[ai].Run(copy, ci);
                    }
                    catch (Exception e)
                    {
                        result = new GlobalBenchResult
                        {
                            Algorithm = allocators[ai].Name,
                            CaseId = ci,
                            Success = false,
                            Error = e.GetType().Name + ": " + e.Message
                        };
                    }
                    all.Add(result);
                }
            }

            WriteCsv(outPath, all);
            PrintSummary(all, cases, allocators);
            Console.WriteLine("CSV: " + Path.GetFullPath(outPath));
            return 0;
        }

        private static List<GlobalAllocator> BuildAllocators(string arg)
        {
            var list = new List<GlobalAllocator>();
            var names = (arg ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i].Trim().ToLowerInvariant();
                if (name == "greedy") list.Add(new GreedyAllocator(false));
                else if (name == "greedy+compact") list.Add(new GreedyAllocator(true));
                else if (name == "regret") list.Add(new RegretAllocator(false));
                else if (name == "regret+compact") list.Add(new RegretAllocator(true));
                else throw new ArgumentException("unknown algorithm: " + name);
            }
            if (list.Count == 0) list.Add(new GreedyAllocator(false));
            return list;
        }

        private static int RunSelfTest(List<GlobalAllocator> allocators)
        {
            Console.WriteLine("Global benchmark selftest: " + allocators.Count + " algorithm(s)");
            int failures = 0;
            for (int seed = 1; seed <= 5; seed++)
            {
                var bench = BenchGenerator.Generate(seed, 3, 18);
                for (int ai = 0; ai < allocators.Count; ai++)
                {
                    var copy = bench.Clone();
                    GlobalBenchResult result;
                    try
                    {
                        result = allocators[ai].Run(copy, seed);
                    }
                    catch (Exception e)
                    {
                        result = new GlobalBenchResult
                        {
                            Algorithm = allocators[ai].Name,
                            CaseId = seed,
                            Success = false,
                            Error = e.GetType().Name + ": " + e.Message
                        };
                    }

                    if (!result.Success)
                    {
                        failures++;
                        Console.WriteLine("FAIL seed=" + seed + " " + result.Algorithm +
                                          " error=" + result.Error);
                        continue;
                    }
                    string error;
                    if (!GlobalMetrics.Validate(copy, out error))
                    {
                        failures++;
                        Console.WriteLine("FAIL seed=" + seed + " " + result.Algorithm +
                                          " invalid=" + error);
                        continue;
                    }
                    Console.WriteLine("PASS seed=" + seed + " " + result.Algorithm +
                                      " items=" + result.Metrics.Items +
                                      " bins=" + result.Metrics.BinsUsed +
                                      " holes=" + result.Metrics.Holes +
                                      " components=" + result.Metrics.Components +
                                      " gap=" + result.Metrics.BBoxGap +
                                      " clusters=" + result.Metrics.Clusters +
                                      " ms=" + result.ElapsedMs);
                }
            }
            Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void WriteCsv(string path, List<GlobalBenchResult> results)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("case,algorithm,success,ms,items,bins,holes,components,bboxGap," +
                                 "reading,waste,clusters,sameItem,category,score,error");
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    var m = r.Metrics;
                    writer.WriteLine(string.Join(",",
                        r.CaseId.ToString(CultureInfo.InvariantCulture),
                        r.Algorithm,
                        r.Success ? "1" : "0",
                        r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.Items.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.BinsUsed.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.Holes.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.Components.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.BBoxGap.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.ReadingOrderDisorder.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.SameItemBBoxWaste.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.Clusters.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.SameItemContacts.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.CategoryContacts.ToString(CultureInfo.InvariantCulture),
                        m == null ? "" : m.Score.ToString(CultureInfo.InvariantCulture),
                        Csv(r.Error)));
                }
            }
        }

        private static string Csv(string value)
        {
            if (value == null) return "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void PrintSummary(List<GlobalBenchResult> results, int cases,
            List<GlobalAllocator> allocators)
        {
            Console.WriteLine();
            Console.WriteLine("Summary");
            Console.WriteLine("-------");
            for (int ai = 0; ai < allocators.Count; ai++)
            {
                string name = allocators[ai].Name;
                var rows = results.Where(r => r.Algorithm == name).ToList();
                int ok = rows.Count(r => r.Success);
                var good = rows.Where(r => r.Success && r.Metrics != null).ToList();
                double avgMs = rows.Count == 0 ? 0 : rows.Average(r => r.ElapsedMs);
                double avgHoles = good.Count == 0 ? 0 : good.Average(r => r.Metrics.Holes);
                double avgComp = good.Count == 0 ? 0 : good.Average(r => r.Metrics.Components);
                double avgGap = good.Count == 0 ? 0 : good.Average(r => r.Metrics.BBoxGap);
                double avgClusters = good.Count == 0 ? 0 : good.Average(r => r.Metrics.Clusters);
                double avgReading = good.Count == 0 ? 0 : good.Average(r => r.Metrics.ReadingOrderDisorder);
                double avgWaste = good.Count == 0 ? 0 : good.Average(r => r.Metrics.SameItemBBoxWaste);
                long totalGap = good.Sum(r => (long)r.Metrics.BBoxGap);
                Console.WriteLine(name.PadRight(15) +
                                  " ok=" + ok + "/" + cases +
                                  " ms=" + avgMs.ToString("0.0", CultureInfo.InvariantCulture) +
                                  " holes=" + avgHoles.ToString("0.00", CultureInfo.InvariantCulture) +
                                  " comp=" + avgComp.ToString("0.00", CultureInfo.InvariantCulture) +
                                  " gap=" + avgGap.ToString("0.00", CultureInfo.InvariantCulture) +
                                  " totalGap=" + totalGap +
                                  " reading=" + avgReading.ToString("0.00", CultureInfo.InvariantCulture) +
                                  " waste=" + avgWaste.ToString("0.00", CultureInfo.InvariantCulture) +
                                  " clusters=" + avgClusters.ToString("0.00", CultureInfo.InvariantCulture));
            }

            // Pairwise quality wins against the first algorithm, using the same
            // lexicographic objective as the packer.
            if (allocators.Count > 1)
            {
                string baseline = allocators[0].Name;
                Console.WriteLine();
                Console.WriteLine("Wins vs " + baseline + " (holes->components->gap->clusters):");
                for (int ai = 1; ai < allocators.Count; ai++)
                {
                    string name = allocators[ai].Name;
                    int wins = 0, losses = 0, ties = 0;
                    for (int ci = 0; ci < cases; ci++)
                    {
                        var a = results.FirstOrDefault(r => r.CaseId == ci && r.Algorithm == baseline);
                        var b = results.FirstOrDefault(r => r.CaseId == ci && r.Algorithm == name);
                        if (a == null || b == null || !a.Success || !b.Success) continue;
                        if (b.Metrics.BetterThan(a.Metrics)) wins++;
                        else if (a.Metrics.BetterThan(b.Metrics)) losses++;
                        else ties++;
                    }
                    Console.WriteLine(name.PadRight(15) + " win=" + wins +
                                      " loss=" + losses + " tie=" + ties);
                }
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("GlobalBench [--cases N] [--seed N] [--bins N] [--items N]");
            Console.WriteLine("           [--algorithms greedy,greedy+compact,regret,regret+compact]");
            Console.WriteLine("           [--out results.csv] [--snapshot path] [--selftest]");
        }
    }
}
