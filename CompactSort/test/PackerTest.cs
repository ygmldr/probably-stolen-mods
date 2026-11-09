using System;
using System.Collections.Generic;
using CompactSort.Packing;

internal static class PackerTest
{
    private static void Main(string[] args)
    {
        if (args != null && args.Length > 0 && args[0] == "weights")
        {
            WeightSweep(false, false);
            return;
        }
        if (args != null && args.Length > 0 && args[0] == "weights-rect")
        {
            WeightSweep(true, false);
            return;
        }
        if (args != null && args.Length > 0 && args[0] == "weights-local")
        {
            WeightSweep(false, true);
            return;
        }

        RunCase("mixed 12x10", 12, 10, BuildMixedPieces(), new[] { 11, 2, 11, 3, 0, 9 });
        RunCase("dense 14x12", 14, 12, BuildDensePieces(), new[] { 13, 0, 13, 1, 0, 11 });
        RunCase("irregular 10x9", 10, 9, BuildIrregularPieces(), new[] { 9, 0, 9, 8 });
        Stress(20, 20, 60, 5, 123);
        Stress(30, 30, 100, 6, 456);
        StressDense(12, 12, 0.90, 11);
        StressDense(16, 16, 0.92, 22);
        StressDense(20, 20, 0.90, 33);
        Benchmark();
        RandomBenchmark();
        AlgorithmBench();
        CategoryCheck();
    }

    private sealed class WeightCase
    {
        public int W;
        public int H;
        public List<PackPiece> Pieces;
    }

    private static void WeightSweep(bool rectFirst, bool localPenalty)
    {
        PackResult.PreferMaxRectFirst = rectFirst;
        var cases = new List<WeightCase>();
        for (int seed = 1; seed <= 30; seed++)
        {
            var rng = new Random(seed * 7919);
            int w = 10 + rng.Next(0, 5);
            int h = 10 + rng.Next(0, 4);
            double fill = 0.72 + rng.Next(0, 24) / 100.0;
            var pieces = new List<PackPiece>();
            int id = 0, area = 0, target = (int)(w * h * fill);
            int guard = 0;
            while (area < target && guard++ < 300)
            {
                int pw = rng.Next(1, 5);
                int ph = rng.Next(1, 5);
                int a = pw * ph;
                if (area + a > target) continue;
                Add(pieces, ref id, "r" + (id % 4), pw, ph);
                area += a;
            }
            cases.Add(new WeightCase { W = w, H = h, Pieces = pieces });
        }

        Console.WriteLine("SWEEP,mode,weight,avgRect,avgGap,avgHoles,avgComp,avgMs");
        var weights = new[] { 0f, 5f, 10f, 20f, 40f, 80f, 160f, 320f };
        foreach (var weight in weights)
        {
            double rect = 0, gap = 0, holes = 0, comp = 0, ms = 0;
            for (int ci = 0; ci < cases.Count; ci++)
            {
                var c = cases[ci];
                var grid = new PackGrid(c.W, c.H);
                var opts = new PackOptions
                {
                    Algorithm = PackAlgorithm.Greedy,
                    BigFirst = true,
                    AllowRotation = true,
                    TimeBudgetMs = 350,
                    LnsBudgetMs = 220,
                    MaxStrategies = 24,
                    RandomSeed = 991 + ci,
                    MaxRectWeight = localPenalty ? 0f : weight,
                    LocalMaxRectPenalty = localPenalty ? weight : 0f,
                    CategoryFirst = false
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = GridPacker.Pack(grid, c.Pieces, opts);
                sw.Stop();
                rect += r.MaxEmptyRect;
                gap += r.BBoxGap;
                holes += r.IsolatedHoles;
                comp += r.EmptyComponents;
                ms += sw.ElapsedMilliseconds;
            }
            int n = cases.Count;
            Console.WriteLine("SWEEP," +
                              (localPenalty ? "local-penalty" : (rectFirst ? "rect-first" : "gap-first")) + "," +
                              weight.ToString("0") + "," +
                              (rect / n).ToString("0.00") + "," +
                              (gap / n).ToString("0.00") + "," +
                              (holes / n).ToString("0.00") + "," +
                              (comp / n).ToString("0.00") + "," +
                              (ms / n).ToString("0.0"));
        }
        PackResult.PreferMaxRectFirst = false;
    }

    private static void CategoryCheck()
    {
        Console.WriteLine();
        Console.WriteLine("=== category grouping ===");
        var pieces = new List<PackPiece>();
        int id = 0;
        for (int i = 0; i < 6; i++) AddCat(pieces, ref id, "med" + i, "Meds", 1, 2);
        for (int i = 0; i < 6; i++) AddCat(pieces, ref id, "food" + i, "Food", 1, 2);
        for (int i = 0; i < 4; i++) AddCat(pieces, ref id, "gun" + i, "Guns", 2, 2);
        Console.WriteLine("cats: " + string.Join(",", pieces.ConvertAll(x => x.CategoryKey)));
        var grid = new PackGrid(12, 6);
        var plain = GridPacker.Pack(grid, pieces, new PackOptions
        {
            Algorithm = PackAlgorithm.Greedy,
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 500,
            RandomSeed = 7,
            CategoryFirst = false
        });
        var grouped = GridPacker.Pack(grid, pieces, new PackOptions
        {
            Algorithm = PackAlgorithm.Greedy,
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 500,
            RandomSeed = 7,
            CategoryFirst = true
        });
        Console.WriteLine("plain: catContacts=" + plain.CategoryContacts + " itemContacts=" + plain.SameItemContacts +
                          " gap=" + plain.BBoxGap + " score=" + plain.Score);
        Console.WriteLine("grouped: catContacts=" + grouped.CategoryContacts + " itemContacts=" + grouped.SameItemContacts +
                          " gap=" + grouped.BBoxGap + " score=" + grouped.Score);
        var pg = grid.Clone();
        foreach (var p in plain.Placements) pg.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
        Print(pg, pieces);
        var gg = grid.Clone();
        foreach (var p in grouped.Placements) gg.Place(p.Orientation, p.X, p.Y, p.Piece.Id);
        Print(gg, pieces);
    }

    private static void AddCat(List<PackPiece> list, ref int id, string key, string category, int w, int h)
    {
        Add(list, ref id, key, w, h);
        list[list.Count - 1].CategoryKey = category;
    }

    private static void AlgorithmBench()
    {
        Console.WriteLine();
        Console.WriteLine("=== algorithm A/B (50 cases) ===");
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
            PackAlgorithm.BranchAndBound
        };
        var wins = new int[algorithms.Length];
        var losses = new int[algorithms.Length];
        var ties = new int[algorithms.Length];
        var sumScore = new long[algorithms.Length];
        var sumGap = new long[algorithms.Length];
        var sumMs = new long[algorithms.Length];
        var sumRect = new long[algorithms.Length];

        for (int seed = 1; seed <= 50; seed++)
        {
            var rng = new Random(seed * 7919);
            int w = 10 + rng.Next(0, 5);
            int h = 10 + rng.Next(0, 4);
            double fill = 0.72 + rng.Next(0, 24) / 100.0;
            var pieces = new List<PackPiece>();
            int id = 0, area = 0, target = (int)(w * h * fill);
            int guard = 0;
            while (area < target && guard++ < 300)
            {
                int pw = rng.Next(1, 5);
                int ph = rng.Next(1, 5);
                int a = pw * ph;
                if (area + a > target) continue;
                Add(pieces, ref id, "r" + (id % 4), pw, ph);
                area += a;
            }
            var grid = new PackGrid(w, h);

            var results = new PackResult[algorithms.Length];
            for (int ai = 0; ai < algorithms.Length; ai++)
            {
                var opts = new PackOptions
                {
                    Algorithm = algorithms[ai],
                    BigFirst = true,
                    AllowRotation = true,
                    TimeBudgetMs = 350,
                    MaxStrategies = 24,
                    RandomSeed = seed,
                    EnableLns = true,
                    LnsBudgetMs = 220,
                    BeamWidth = 20,
                    BeamBranch = 6
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                results[ai] = GridPacker.Pack(grid, pieces, opts);
                sumMs[ai] += sw.ElapsedMilliseconds;
                sumScore[ai] += results[ai].Score;
                sumGap[ai] += results[ai].BBoxGap;
                sumRect[ai] += results[ai].MaxEmptyRect;
            }

            for (int ai = 0; ai < algorithms.Length; ai++)
            {
                if (results[ai].IsTighterThan(results[1])) wins[ai]++;
                else if (results[1].IsTighterThan(results[ai])) losses[ai]++;
                else ties[ai]++;
            }
        }

        for (int ai = 0; ai < algorithms.Length; ai++)
        {
            Console.WriteLine(algorithms[ai].ToString().PadRight(10) +
                              " win/loss/tie=" + wins[ai] + "/" + losses[ai] + "/" + ties[ai] +
                              " avgScore=" + (sumScore[ai] / 50.0).ToString("0.0") +
                              " avgGap=" + (sumGap[ai] / 50.0).ToString("0.0") +
                              " avgMs=" + (sumMs[ai] / 50.0).ToString("0.0") +
                              " avgRect=" + (sumRect[ai] / 50.0).ToString("0.0"));
        }
    }

    private static void RandomBenchmark()
    {
        Console.WriteLine();
        Console.WriteLine("=== random A/B (50 cases) ===");
        int wins = 0, losses = 0, ties = 0;
        long sumGreedy = 0, sumLns = 0;
        int holesGreedy = 0, holesLns = 0;
        int gapGreedy = 0, gapLns = 0, gapWins = 0;
        for (int seed = 1; seed <= 50; seed++)
        {
            var rng = new Random(seed * 7919);
            int w = 10 + rng.Next(0, 5);
            int h = 10 + rng.Next(0, 4);
            double fill = 0.72 + rng.Next(0, 24) / 100.0;
            var pieces = new List<PackPiece>();
            int id = 0, area = 0, target = (int)(w * h * fill);
            int guard = 0;
            while (area < target && guard++ < 300)
            {
                int pw = rng.Next(1, 5);
                int ph = rng.Next(1, 5);
                int a = pw * ph;
                if (area + a > target) continue;
                Add(pieces, ref id, "r" + (id % 4), pw, ph);
                area += a;
            }
            var grid = new PackGrid(w, h);
            var greedy = GridPacker.Pack(grid, pieces, new PackOptions
            {
                BigFirst = true, AllowRotation = true, TimeBudgetMs = 1000,
                MaxStrategies = 24, RandomSeed = seed, EnableLns = false
            });
            var lns = GridPacker.Pack(grid, pieces, new PackOptions
            {
                BigFirst = true, AllowRotation = true, TimeBudgetMs = 1000,
                LnsBudgetMs = 400, MaxStrategies = 24, RandomSeed = seed, EnableLns = true
            });
            sumGreedy += greedy.Score;
            sumLns += lns.Score;
            holesGreedy += greedy.IsolatedHoles;
            holesLns += lns.IsolatedHoles;
            gapGreedy += greedy.BBoxGap;
            gapLns += lns.BBoxGap;
            if (lns.BBoxGap < greedy.BBoxGap) gapWins++;
            if (lns.Score < greedy.Score)
            {
                wins++;
                Console.WriteLine("  win seed=" + seed + " " + w + "x" + h + " fill=" + fill.ToString("0.00") +
                                  " score " + greedy.Score + " -> " + lns.Score +
                                  " gap " + greedy.BBoxGap + " -> " + lns.BBoxGap +
                                  " holes " + greedy.IsolatedHoles + " -> " + lns.IsolatedHoles +
                                  " comp " + greedy.EmptyComponents + " -> " + lns.EmptyComponents +
                                  " (local=" + lns.LocalImprovements + ", lns=" + lns.LnsImprovements + ")");
            }
            else if (lns.Score > greedy.Score) losses++;
            else ties++;
        }
        Console.WriteLine("wins=" + wins + " losses=" + losses + " ties=" + ties);
        Console.WriteLine("avg greedy=" + (sumGreedy / 50.0).ToString("0.0") +
                          " avg lns=" + (sumLns / 50.0).ToString("0.0") +
                          " holes " + holesGreedy + " -> " + holesLns +
                          " bboxGap " + gapGreedy + " -> " + gapLns +
                          " gapWins=" + gapWins);
    }

    private static void Benchmark()
    {
        Console.WriteLine();
        Console.WriteLine("=== LNS A/B ===");
        Bench("mixed 12x10", 12, 10, BuildMixedPieces(), new[] { 11, 2, 11, 3, 0, 9 });
        Bench("dense 14x12", 14, 12, BuildDensePieces(), new[] { 13, 0, 13, 1, 0, 11 });
        Bench("irregular 10x9", 10, 9, BuildIrregularPieces(), new[] { 9, 0, 9, 8 });
    }

    private static void Bench(string name, int w, int h, List<PackPiece> pieces, int[] wallCells)
    {
        var grid = new PackGrid(w, h);
        for (int i = 0; i + 1 < wallCells.Length; i += 2)
            grid.Wall[wallCells[i + 1] * w + wallCells[i]] = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var greedy = GridPacker.Pack(grid, pieces, new PackOptions
        {
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 1000,
            MaxStrategies = 24,
            RandomSeed = 12345,
            EnableLns = false
        });
        long greedyMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var lns = GridPacker.Pack(grid, pieces, new PackOptions
        {
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 1000,
            LnsBudgetMs = 500,
            MaxStrategies = 24,
            RandomSeed = 12345,
            EnableLns = true,
            Log = Console.WriteLine
        });
        long lnsMs = sw.ElapsedMilliseconds;

        Console.WriteLine(name + ":");
        Console.WriteLine("  greedy: score=" + greedy.Score + " holes=" + greedy.IsolatedHoles +
                          " comp=" + greedy.EmptyComponents + " gap=" + greedy.BBoxGap +
                          " ms=" + greedyMs);
        Console.WriteLine("  LNS   : score=" + lns.Score + " holes=" + lns.IsolatedHoles +
                          " comp=" + lns.EmptyComponents + " gap=" + lns.BBoxGap +
                          " local=" + lns.LocalImprovements + "/" + lns.LocalMoves +
                          " lns=" + lns.LnsImprovements + "/" + lns.LnsIterations +
                          " ms=" + lnsMs);
    }

    private static void StressDense(int w, int h, double targetFill, int seed)
    {
        Console.WriteLine();
        Console.WriteLine("=== dense stress " + w + "x" + h + " target=" + (targetFill * 100) + "% ===");
        var rng = new Random(seed);
        var pieces = new List<PackPiece>();
        int id = 0;
        int area = 0;
        int target = (int)(w * h * targetFill);
        int guard = 0;
        while (area < target && guard++ < 500)
        {
            int pw = rng.Next(1, 5);
            int ph = rng.Next(1, 5);
            int a = pw * ph;
            if (area + a > target) continue;
            Add(pieces, ref id, "r" + (id % 5), pw, ph);
            area += a;
        }
        Console.WriteLine("pieces=" + pieces.Count + " area=" + area + "/" + (w * h));

        var grid = new PackGrid(w, h);
        var options = new PackOptions
        {
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 500,
            MaxStrategies = 24,
            RandomSeed = seed
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = GridPacker.Pack(grid, pieces, options);
        sw.Stop();
        Console.WriteLine("success=" + result.Success + " ms=" + sw.ElapsedMilliseconds +
                          " score=" + result.Score + " holes=" + result.IsolatedHoles +
                          " components=" + result.EmptyComponents + " bboxGap=" + result.BBoxGap +
                          " timeout=" + result.TimedOut);
    }

    private static void Stress(int w, int h, int count, int avgArea, int seed)
    {
        Console.WriteLine();
        Console.WriteLine("=== stress " + w + "x" + h + " pieces=" + count + " ===");
        var rng = new Random(seed);
        var pieces = new List<PackPiece>();
        int id = 0;
        int area = 0;
        for (int i = 0; i < count; i++)
        {
            int pw = rng.Next(1, 4);
            int ph = rng.Next(1, 4);
            Add(pieces, ref id, "r" + (i % 7), pw, ph);
            area += pw * ph;
        }
        Console.WriteLine("fill=" + (area * 100.0 / (w * h)).ToString("0.0") + "%");

        var grid = new PackGrid(w, h);
        var options = new PackOptions
        {
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 350,
            MaxStrategies = 24,
            RandomSeed = seed
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = GridPacker.Pack(grid, pieces, options);
        sw.Stop();
        Console.WriteLine("success=" + result.Success + " ms=" + sw.ElapsedMilliseconds +
                          " score=" + result.Score + " holes=" + result.IsolatedHoles +
                          " components=" + result.EmptyComponents + " bboxGap=" + result.BBoxGap +
                          " timeout=" + result.TimedOut);
    }

    private static void RunCase(string name, int w, int h, List<PackPiece> pieces, int[] wallCells)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + name + " ===");
        var grid = new PackGrid(w, h);
        for (int i = 0; i + 1 < wallCells.Length; i += 2)
            grid.Wall[wallCells[i + 1] * w + wallCells[i]] = true;

        var options = new PackOptions
        {
            BigFirst = true,
            AllowRotation = true,
            TimeBudgetMs = 1500,
            MaxStrategies = 24,
            RandomSeed = 12345
        };

        var result = GridPacker.Pack(grid, pieces, options);
        Console.WriteLine("success=" + result.Success + " score=" + result.Score +
                          " holes=" + result.IsolatedHoles + " components=" + result.EmptyComponents +
                          " bboxGap=" + result.BBoxGap + " contact=" + result.Contact +
                          " timeout=" + result.TimedOut);
        if (!result.Success)
        {
            Console.WriteLine("PACK FAILED");
            return;
        }

        var final = grid.Clone();
        foreach (var p in result.Placements)
            final.Place(p.Orientation, p.X, p.Y, p.Piece.Id);

        Print(final, pieces);

        // Compare with a naive shelf layout built from the same pieces.
        var shelf = ShelfPack(grid, pieces);
        if (shelf != null)
        {
            var metrics = GridPacker.ScoreLayout(shelf, options);
            Console.WriteLine("shelf: score=" + metrics.Score + " holes=" + metrics.IsolatedHoles +
                              " components=" + metrics.EmptyComponents + " bboxGap=" + metrics.BBoxGap);
            Print(shelf, pieces);
        }
    }

    private static PackGrid ShelfPack(PackGrid baseGrid, List<PackPiece> pieces)
    {
        var grid = baseGrid.Clone();
        var sorted = new List<PackPiece>(pieces);
        sorted.Sort((a, b) =>
        {
            int ah = a.Orientations[0].Height;
            int bh = b.Orientations[0].Height;
            int c = bh.CompareTo(ah);
            if (c != 0) return c;
            return b.Area.CompareTo(a.Area);
        });

        int shelfY = 0;
        int shelfH = 0;
        int cursorX = 0;
        foreach (var piece in sorted)
        {
            var o = piece.Orientations[0];
            bool placed = false;
            for (int y = shelfY; y <= shelfY + shelfH - o.Height && !placed; y++)
            {
                for (int x = cursorX; x <= grid.Width - o.Width; x++)
                {
                    if (CanPlace(grid, o, x, y))
                    {
                        grid.Place(o, x, y, piece.Id);
                        cursorX = x + o.Width;
                        placed = true;
                        break;
                    }
                }
            }
            if (!placed)
            {
                shelfY += shelfH;
                shelfH = o.Height;
                cursorX = 0;
                if (shelfY + o.Height > grid.Height) return null;
                if (!CanPlace(grid, o, 0, shelfY)) return null;
                grid.Place(o, 0, shelfY, piece.Id);
                cursorX = o.Width;
            }
            else if (o.Height > shelfH)
            {
                shelfH = o.Height;
            }
        }
        grid.RecalculateExtents();
        return grid;
    }

    private static bool CanPlace(PackGrid grid, PackOrientation o, int x, int y)
    {
        for (int i = 0; i < o.CellX.Length; i++)
        {
            int gx = x + o.CellX[i];
            int gy = y + o.CellY[i];
            if (grid.IsWall(gx, gy)) return false;
            if ((grid.Layers[gy * grid.Width + gx] & (1 << o.CellLayer[i])) != 0) return false;
        }
        return true;
    }

    private static void Print(PackGrid grid, List<PackPiece> pieces)
    {
        var chars = new char[grid.Width * grid.Height];
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
                chars[y * grid.Width + x] = grid.Wall[y * grid.Width + x] ? '#' : '.';

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int idx = y * grid.Width + x;
                if (grid.Layers[idx] == 0) continue;
                int owner = grid.Owner[idx];
                char c = owner >= 0 && owner < pieces.Count
                    ? (char)('A' + (owner % 26))
                    : '?';
                chars[idx] = c;
            }
        }

        for (int y = 0; y < grid.Height; y++)
        {
            Console.WriteLine(new string(chars, y * grid.Width, grid.Width));
        }
    }

    private static List<PackPiece> BuildMixedPieces()
    {
        var list = new List<PackPiece>();
        int id = 0;
        Add(list, ref id, "rifle", 2, 4);
        Add(list, ref id, "rifle", 2, 4);
        Add(list, ref id, "pistol", 2, 2);
        Add(list, ref id, "pistol", 2, 2);
        Add(list, ref id, "medkit", 2, 3);
        Add(list, ref id, "medkit", 2, 3);
        Add(list, ref id, "can", 1, 1);
        Add(list, ref id, "can", 1, 1);
        Add(list, ref id, "can", 1, 1);
        Add(list, ref id, "box", 3, 2);
        Add(list, ref id, "box", 3, 2);
        Add(list, ref id, "plate", 3, 1);
        Add(list, ref id, "plate", 3, 1);
        Add(list, ref id, "cpu", 2, 1);
        Add(list, ref id, "cpu", 2, 1);
        Add(list, ref id, "bottle", 1, 2);
        Add(list, ref id, "bottle", 1, 2);
        Add(list, ref id, "gear", 2, 2);
        Add(list, ref id, "gear", 2, 2);
        return list;
    }

    private static List<PackPiece> BuildDensePieces()
    {
        var list = new List<PackPiece>();
        int id = 0;
        for (int i = 0; i < 3; i++) Add(list, ref id, "crate", 3, 3);
        for (int i = 0; i < 4; i++) Add(list, ref id, "box", 3, 2);
        for (int i = 0; i < 4; i++) Add(list, ref id, "med", 2, 3);
        for (int i = 0; i < 6; i++) Add(list, ref id, "small", 2, 2);
        for (int i = 0; i < 8; i++) Add(list, ref id, "tiny", 1, 1);
        for (int i = 0; i < 4; i++) Add(list, ref id, "rod", 1, 4);
        return list;
    }

    private static List<PackPiece> BuildIrregularPieces()
    {
        var list = new List<PackPiece>();
        int id = 0;
        AddMask(list, ref id, "lshape", new byte[,]
        {
            { 1, 0 },
            { 1, 1 }
        });
        AddMask(list, ref id, "lshape", new byte[,]
        {
            { 1, 0 },
            { 1, 1 }
        });
        AddMask(list, ref id, "tshape", new byte[,]
        {
            { 1, 1, 1 },
            { 0, 1, 0 }
        });
        AddMask(list, ref id, "tshape", new byte[,]
        {
            { 1, 1, 1 },
            { 0, 1, 0 }
        });
        Add(list, ref id, "box", 3, 2);
        Add(list, ref id, "med", 2, 3);
        Add(list, ref id, "small", 2, 2);
        Add(list, ref id, "can", 1, 1);
        Add(list, ref id, "can", 1, 1);
        return list;
    }

    private static void Add(List<PackPiece> list, ref int id, string key, int w, int h)
    {
        var mask = new byte[w, h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[x, y] = 1;
        AddMask(list, ref id, key, mask);
    }

    private static void AddMask(List<PackPiece> list, ref int id, string key, byte[,] mask)
    {
        int w = mask.GetLength(0);
        int h = mask.GetLength(1);
        var piece = new PackPiece
        {
            Id = id++,
            Key = key,
            MaxDim = Math.Max(w, h),
            OriginalRotation = 0
        };

        for (int rot = 0; rot < 4; rot++)
        {
            var xs = new List<int>();
            var ys = new List<int>();
            var layers = new List<byte>();
            int ow = rot % 2 == 0 ? w : h;
            int oh = rot % 2 == 0 ? h : w;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte layer = mask[x, y];
                    if (layer == 0) continue;
                    int nx, ny;
                    Rotate(x, y, w, h, rot, out nx, out ny);
                    xs.Add(nx);
                    ys.Add(ny);
                    layers.Add(layer);
                }
            }
            var o = new PackOrientation
            {
                Width = ow,
                Height = oh,
                Rotation = rot,
                CellX = xs.ToArray(),
                CellY = ys.ToArray(),
                CellLayer = layers.ToArray()
            };
            bool duplicate = false;
            foreach (var existing in piece.Orientations)
            {
                if (existing.SameShape(o)) { duplicate = true; break; }
            }
            if (!duplicate) piece.Orientations.Add(o);
        }

        piece.Area = piece.Orientations[0].CellX.Length;
        list.Add(piece);
    }

    private static void Rotate(int x, int y, int w, int h, int rot, out int nx, out int ny)
    {
        switch (rot % 4)
        {
            case 1:
                nx = y;
                ny = w - 1 - x;
                break;
            case 2:
                nx = w - 1 - x;
                ny = h - 1 - y;
                break;
            case 3:
                nx = h - 1 - y;
                ny = x;
                break;
            default:
                nx = x;
                ny = y;
                break;
        }
    }
}
