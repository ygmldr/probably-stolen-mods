using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CompactSort.GlobalBench;
using CompactSort.Packing;
using SmartInventoryArrange.Core;

namespace CompactSort.CompetitorBench
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string path = null;
            int limit = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--snapshot" && i + 1 < args.Length) path = args[++i];
                else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
            }
            if (path == null)
            {
                Console.Error.WriteLine("usage: --snapshot <dir> [--limit N]");
                return 2;
            }

            var cases = BenchSnapshotLoader.Load(path);
            int loadedBins = cases.Sum(c => c.Bins.Count);
            int loadedItems = cases.Sum(c => c.Items.Count);
            int loadedAnchors = cases.Sum(c => c.Bins.Sum(b => b.Anchors.Count));
            Console.WriteLine("loaded cases=" + cases.Count + " bins=" + loadedBins +
                              " items=" + loadedItems + " anchors=" + loadedAnchors);
            var rows = new List<Row>();
            int binIndex = 0;
            foreach (var bench in cases)
            {
                foreach (var bin in bench.Bins)
                {
                    // Keep the comparison clean: no anchors/fixed items in this pass.
                    if (bin.Anchors.Count != 0) continue;
                    var binItems = bench.Items.Where(i => i.OriginalBin == bin.Id).ToList();
                    if (binItems.Count < 2) continue;
                    if (limit > 0 && binIndex >= limit) break;
                    binIndex++;
                    var row = Compare(bin, binItems, binIndex);
                    if (row != null) rows.Add(row);
                }
                if (limit > 0 && binIndex >= limit) break;
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("no eligible bins");
                return 1;
            }

            Console.WriteLine("bins=" + rows.Count);
            Console.WriteLine("algorithm,avgMs,avgHoles,avgComponents,avgGap,avgReading,avgWaste,avgClusters");
            Print("ours-hybrid", rows, r => r.Our);
            Print("ours-visual", rows, r => r.Visual);
            Print("competitor", rows, r => r.Theirs);
            int oursWin = 0, theirsWin = 0, tie = 0;
            int ourGapWins = 0, theirGapWins = 0;
            int theirReadingWins = 0, theirWasteWins = 0;
            foreach (var r in rows)
            {
                if (r.Our.IsTighterThan(r.Theirs)) oursWin++;
                else if (r.Theirs.IsTighterThan(r.Our)) theirsWin++;
                else tie++;
                if (r.Our.BBoxGap < r.Theirs.BBoxGap) ourGapWins++;
                else if (r.Theirs.BBoxGap < r.Our.BBoxGap) theirGapWins++;
                if (r.Theirs.ReadingOrderDisorder < r.Our.ReadingOrderDisorder) theirReadingWins++;
                if (r.Theirs.SameItemBBoxWaste < r.Our.SameItemBBoxWaste) theirWasteWins++;
            }
            Console.WriteLine("wins(our objective) ours=" + oursWin + " competitor=" + theirsWin + " tie=" + tie);
            Console.WriteLine("gap wins ours=" + ourGapWins + " competitor=" + theirGapWins +
                              " | competitor reading wins=" + theirReadingWins +
                              " waste wins=" + theirWasteWins);
            return 0;
        }

        private static void Print(string name, List<Row> rows, Func<Row, PackResult> pick)
        {
            var allValues = rows.Select(r => pick(r)).ToList();
            var values = allValues.Where(v => v.Success).ToList();
            Console.WriteLine(name + "," + values.Count + "/" + allValues.Count + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.LocalMoves)).ToString("0.0", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.IsolatedHoles)).ToString("0.00", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.EmptyComponents)).ToString("0.00", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.BBoxGap)).ToString("0.00", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.ReadingOrderDisorder)).ToString("0.00", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.SameItemBBoxWaste)).ToString("0.00", CultureInfo.InvariantCulture) + "," +
                (values.Count == 0 ? 0 : values.Average(v => v.ItemClusters)).ToString("0.00", CultureInfo.InvariantCulture));
        }

        private static Row Compare(BenchBin bin, List<BenchItem> binItems, int index)
        {
            var options = new PackOptions
            {
                Algorithm = PackAlgorithm.Hybrid,
                BigFirst = true,
                AllowRotation = true,
                TimeBudgetMs = 350,
                MaxStrategies = 24,
                RandomSeed = 20261104,
                CategoryFirst = true,
                BeamWidth = 20,
                BeamBranch = 6,
                EnableLns = true,
                LnsBudgetMs = 220,
                StrictOccupancy = true
            };

            var ourPieces = binItems.Select((item, i) => item.CreatePiece(i)).ToList();
            for (int i = 0; i < ourPieces.Count; i++) ourPieces[i].Tag = binItems[i];
            var ourWatch = Stopwatch.StartNew();
            var ourRaw = GridPacker.Pack(EmptyGrid(bin), ourPieces, options);
            ourWatch.Stop();
            PackResult ours = ScoreRaw(bin, ourRaw, options, ourWatch.ElapsedMilliseconds);

            var visualOptions = new PackOptions
            {
                Algorithm = PackAlgorithm.VisualFirst,
                BigFirst = true,
                AllowRotation = true,
                TimeBudgetMs = 350,
                MaxStrategies = 24,
                RandomSeed = 20261104,
                CategoryFirst = true,
                StrictOccupancy = true
            };
            var visualPieces = binItems.Select((item, i) => item.CreatePiece(i)).ToList();
            for (int i = 0; i < visualPieces.Count; i++) visualPieces[i].Tag = binItems[i];
            var visualWatch = Stopwatch.StartNew();
            var visualRaw = GridPacker.Pack(EmptyGrid(bin), visualPieces, visualOptions);
            visualWatch.Stop();
            if (!visualRaw.Success)
                Console.WriteLine("visual debug bin=" + index + " pieces=" + visualPieces.Count +
                                  " placements=" + visualRaw.Placements.Count +
                                  " holes=" + visualRaw.IsolatedHoles);
            var visual = ScoreRaw(bin, visualRaw, visualOptions, visualWatch.ElapsedMilliseconds);

            var problem = ToProblem(bin, binItems);
            var solver = new DeterministicSolver
            {
                BeamWidth = 32,
                CandidatesPerState = 24,
                TimeBudget = TimeSpan.FromMilliseconds(350)
            };
            var watch = Stopwatch.StartNew();
            LayoutResult competitors;
            try
            {
                competitors = solver.Solve(problem);
            }
            catch (Exception e)
            {
                Console.WriteLine("competitor failed bin=" + index + " " + e.GetType().Name + ": " + e.Message);
                competitors = null;
            }
            watch.Stop();
            if (competitors == null || !competitors.Complete)
                return null;

            var theirGrid = EmptyGrid(bin);
            var theirPieces = new List<PackPiece>();
            var placements = competitors.Placements
                .Where(p => p.StableId != null)
                .ToList();
            foreach (var p in placements)
            {
                var item = binItems.FirstOrDefault(x => x.Id.ToString(CultureInfo.InvariantCulture) == p.StableId);
                if (item == null) continue;
                var orientation = new PackOrientation
                {
                    Width = p.Mask.Width,
                    Height = p.Mask.Height,
                    Rotation = p.GameOrientation,
                    CellX = p.Mask.Cells.Select(c => c.X).ToArray(),
                    CellY = p.Mask.Cells.Select(c => c.Y).ToArray(),
                    CellLayer = p.Mask.Cells.Select(c => (byte)1).ToArray()
                };
                var piece = new PackPiece
                {
                    Id = theirPieces.Count,
                    Key = item.Key,
                    CategoryKey = item.Category,
                    Area = item.Area,
                    MaxDim = Math.Max(item.Width, item.Height),
                    Tag = item
                };
                piece.Orientations.Add(orientation);
                theirGrid.Place(orientation, p.X, p.Y, piece.Id);
                theirPieces.Add(piece);
            }
            if (theirPieces.Count != binItems.Count)
                return null;
            theirGrid.SetOwnerCategories(theirPieces);
            theirGrid.RecalculateExtents();
            var theirs = GridPacker.ScoreLayout(theirGrid, options);
            theirs.LocalMoves = (int)watch.ElapsedMilliseconds;
            return new Row { Index = index, Our = ours, Visual = visual, Theirs = theirs };
        }

        private static PackResult ScoreRaw(BenchBin bin, PackResult raw, PackOptions options, long ms)
        {
            if (raw == null || !raw.Success)
            {
                return new PackResult
                {
                    Success = false,
                    LocalMoves = (int)ms,
                    IsolatedHoles = int.MaxValue,
                    EmptyComponents = int.MaxValue,
                    BBoxGap = int.MaxValue,
                    ReadingOrderDisorder = int.MaxValue,
                    SameItemBBoxWaste = int.MaxValue,
                    ItemClusters = int.MaxValue
                };
            }

            var grid = EmptyGrid(bin);
            var pieces = new List<PackPiece>();
            for (int i = 0; i < raw.Placements.Count; i++)
            {
                var p = raw.Placements[i];
                var piece = new PackPiece
                {
                    Id = i,
                    Key = p.Piece.Key,
                    CategoryKey = p.Piece.CategoryKey,
                    Area = p.Piece.Area,
                    MaxDim = p.Piece.MaxDim,
                    OriginalRotation = p.Piece.OriginalRotation,
                    Tag = p.Piece.Tag
                };
                piece.Orientations.Add(p.Orientation);
                grid.Place(p.Orientation, p.X, p.Y, i);
                pieces.Add(piece);
            }
            grid.SetOwnerCategories(pieces);
            grid.RecalculateExtents();
            var score = GridPacker.ScoreLayout(grid, options);
            score.LocalMoves = (int)ms;
            return score;
        }

        private static LayoutProblem ToProblem(BenchBin bin, List<BenchItem> binItems)
        {
            var items = new List<LayoutItem>();
            foreach (var item in binItems)
            {
                var piece = item.CreatePiece(0);
                var orientations = new List<SmartInventoryArrange.Core.Orientation>();
                foreach (var o in piece.Orientations)
                {
                    var cells = new List<SmartInventoryArrange.Core.Cell>();
                    for (int i = 0; i < o.CellX.Length; i++)
                        cells.Add(new SmartInventoryArrange.Core.Cell(o.CellX[i], o.CellY[i]));
                    orientations.Add(new SmartInventoryArrange.Core.Orientation(
                        o.Rotation, new ShapeMask(cells), o.Rotation != 0));
                }
                items.Add(new LayoutItem
                {
                    StableId = item.Id.ToString(CultureInfo.InvariantCulture),
                    ItemId = item.Key,
                    PrimaryCategory = item.Category ?? "~unknown",
                    Categories = new[] { item.Category ?? "~unknown" },
                    Orientations = orientations,
                    OriginalMask = orientations[0].Mask,
                    OriginalOrientation = 0,
                    OriginalX = 0,
                    OriginalY = 0,
                    VisualPriority = 1000
                });
            }

            var usable = new bool[bin.Width, bin.Height];
            for (int y = 0; y < bin.Height; y++)
                for (int x = 0; x < bin.Width; x++)
                    usable[x, y] = !bin.Wall[y * bin.Width + x];

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in binItems)
            {
                string c = item.Category ?? "~unknown";
                if (!order.ContainsKey(c)) order[c] = order.Count;
            }
            return new LayoutProblem
            {
                Width = bin.Width,
                Height = bin.Height,
                Usable = usable,
                Fixed = Array.Empty<Placement>(),
                Items = items,
                CategoryOrder = order
            };
        }

        private static PackGrid EmptyGrid(BenchBin bin)
        {
            var grid = new PackGrid(bin.Width, bin.Height);
            for (int i = 0; i < bin.Wall.Length; i++) grid.Wall[i] = bin.Wall[i];
            return grid;
        }

        private sealed class Row
        {
            public int Index;
            public PackResult Our;
            public PackResult Visual;
            public PackResult Theirs;
        }
    }
}
