// Dumps the 50 synthetic benchmark instances used by PackerTest.AlgorithmBench,
// together with the per-instance results and placements of every algorithm.
// The pieces are built with the exact same RNG sequence and the exact same
// Add/AddMask helpers as the C# test harness, so the instances are bit-identical.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using CompactSort.Packing;

internal static class Program
{
    private static readonly PackAlgorithm[] AllAlgorithms =
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

    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "instances.json";
        bool skipSlow = Array.IndexOf(args, "--fast") >= 0;

        var instances = new List<object>();
        for (int seed = 1; seed <= 50; seed++)
        {
            int w, h;
            double fill;
            var pieces = BuildInstance(seed, out w, out h, out fill);
            int area = 0;
            foreach (var p in pieces) area += p.Area;

            var runs = new List<object>();

            // 1) Exactly what the mod does in game: Hybrid + fixed seed 20261101 + 350ms.
            runs.Add(RunOne("mod", PackAlgorithm.Hybrid, 20261101, w, h, pieces));

            // 2) Each algorithm with the benchmark configuration (seed = instance seed).
            PackResult best = null;
            string bestName = "";
            foreach (var alg in AllAlgorithms)
            {
                if (skipSlow && (alg == PackAlgorithm.HybridSa || alg == PackAlgorithm.Annealing ||
                                 alg == PackAlgorithm.BranchAndBound || alg == PackAlgorithm.Genetic))
                    continue;
                var r = RunOne(alg.ToString(), alg, seed, w, h, pieces, out var raw);
                runs.Add(r);
                if (raw != null && raw.Success && (best == null || raw.IsTighterThan(best)))
                {
                    best = raw;
                    bestName = alg.ToString();
                }
            }
            if (best != null)
                runs.Add(ResultToDict("bestOf", bestName, 0, best));

            instances.Add(new Dictionary<string, object>
            {
                ["seed"] = seed,
                ["w"] = w,
                ["h"] = h,
                ["fill"] = Math.Round(fill, 4),
                ["targetArea"] = (int)(w * h * fill),
                ["pieceArea"] = area,
                ["pieces"] = PiecesToDict(pieces),
                ["runs"] = runs,
                ["bestOf"] = bestName
            });
            Console.WriteLine("seed " + seed + " " + w + "x" + h + " fill=" + fill.ToString("0.00") +
                              " n=" + pieces.Count + " best=" + bestName);
        }

        var payload = new Dictionary<string, object>
        {
            ["meta"] = new Dictionary<string, object>
            {
                ["generator"] = "PackerTest.AlgorithmBench (seed 1..50, rng = new Random(seed*7919))",
                ["modConfig"] = "Hybrid, RandomSeed=20261101, TimeBudgetMs=350, MaxStrategies=24, BeamWidth=20, BeamBranch=6",
                ["benchConfig"] = "alg, RandomSeed=seed, TimeBudgetMs=350, MaxStrategies=24",
                ["budgetMs"] = 350
            },
            ["instances"] = instances
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        System.IO.File.WriteAllText(outPath, json);
        Console.WriteLine("wrote " + outPath + " (" + json.Length + " bytes)");
    }

    private static object RunOne(string name, PackAlgorithm alg, int randomSeed,
        int w, int h, List<PackPiece> pieces)
    {
        return RunOne(name, alg, randomSeed, w, h, pieces, out _);
    }

    private static object RunOne(string name, PackAlgorithm alg, int randomSeed,
        int w, int h, List<PackPiece> pieces, out PackResult raw)
    {
        var grid = new PackGrid(w, h);
        var options = new PackOptions
        {
            Algorithm = alg,
            BigFirst = true,
            FromEnd = false,
            AllowRotation = true,
            TimeBudgetMs = 350,
            MaxStrategies = 24,
            RandomSeed = randomSeed,
            CategoryFirst = false,
            EnableGroupingSwap = true,
            EnableLns = true,
            LnsBudgetMs = 220,
            BeamWidth = 20,
            BeamBranch = 6,
            MaxRectWeight = 20f,
            LocalMaxRectPenalty = 0f
        };
        var sw = Stopwatch.StartNew();
        raw = GridPacker.Pack(grid, pieces, options);
        sw.Stop();
        return ResultToDict(name, alg.ToString(), sw.ElapsedMilliseconds, raw);
    }

    private static object ResultToDict(string name, string algorithm, long ms, PackResult r)
    {
        var placements = new List<object>();
        if (r != null && r.Placements != null)
        {
            foreach (var p in r.Placements)
            {
                placements.Add(new Dictionary<string, object>
                {
                    ["id"] = p.Piece.Id,
                    ["x"] = p.X,
                    ["y"] = p.Y,
                    ["rot"] = p.Orientation.Rotation,
                    ["w"] = p.Orientation.Width,
                    ["h"] = p.Orientation.Height
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["name"] = name,
            ["algorithm"] = algorithm,
            ["ms"] = ms,
            ["success"] = r != null && r.Success,
            ["score"] = r == null ? 0 : r.Score,
            ["holes"] = r == null ? -1 : r.IsolatedHoles,
            ["components"] = r == null ? -1 : r.EmptyComponents,
            ["bboxGap"] = r == null ? -1 : r.BBoxGap,
            ["contact"] = r == null ? -1 : r.Contact,
            ["maxEmptyRect"] = r == null ? -1 : r.MaxEmptyRect,
            ["categoryContacts"] = r == null ? -1 : r.CategoryContacts,
            ["sameItemContacts"] = r == null ? -1 : r.SameItemContacts,
            ["itemClusters"] = r == null ? -1 : r.ItemClusters,
            ["timedOut"] = r != null && r.TimedOut,
            ["placements"] = placements
        };
    }

    private static List<object> PiecesToDict(List<PackPiece> pieces)
    {
        var list = new List<object>();
        foreach (var p in pieces)
        {
            int pw = p.Orientations[0].Width;
            int ph = p.Orientations[0].Height;
            list.Add(new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["key"] = p.Key,
                ["w"] = pw,
                ["h"] = ph,
                ["area"] = p.Area,
                ["maxDim"] = p.MaxDim
            });
        }
        return list;
    }

    // ---- identical to PackerTest.AlgorithmBench ----

    private static List<PackPiece> BuildInstance(int seed, out int w, out int h, out double fill)
    {
        var rng = new Random(seed * 7919);
        w = 10 + rng.Next(0, 5);
        h = 10 + rng.Next(0, 4);
        fill = 0.72 + rng.Next(0, 24) / 100.0;
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
        return pieces;
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
