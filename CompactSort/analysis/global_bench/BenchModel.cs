using System;
using System.Collections.Generic;
using CompactSort.Packing;

namespace CompactSort.GlobalBench
{
    internal sealed class BenchItem
    {
        public int Id;
        public string Key;
        public string Category;
        public int Width;
        public int Height;
        public byte[,] Mask;
        public int Area;
        public int OriginalBin;

        public PackPiece CreatePiece(int pieceId)
        {
            var piece = new PackPiece
            {
                Id = pieceId,
                Key = Key ?? "",
                CategoryKey = Category ?? "",
                MaxDim = Math.Max(Width, Height),
                OriginalRotation = 0,
                Area = Area,
                Tag = this
            };

            for (int rot = 0; rot < 4; rot++)
            {
                var orientation = BuildOrientation(rot);
                bool duplicate = false;
                for (int i = 0; i < piece.Orientations.Count; i++)
                {
                    if (piece.Orientations[i].SameShape(orientation))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) piece.Orientations.Add(orientation);
            }
            return piece;
        }

        private PackOrientation BuildOrientation(int rotation)
        {
            rotation = ((rotation % 4) + 4) % 4;
            int outW = rotation % 2 == 0 ? Width : Height;
            int outH = rotation % 2 == 0 ? Height : Width;
            var xs = new List<int>();
            var ys = new List<int>();
            var layers = new List<byte>();

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Mask[x, y] == 0) continue;
                    int nx;
                    int ny;
                    switch (rotation)
                    {
                        case 1: nx = y; ny = Width - 1 - x; break;
                        case 2: nx = Width - 1 - x; ny = Height - 1 - y; break;
                        case 3: nx = Height - 1 - y; ny = x; break;
                        default: nx = x; ny = y; break;
                    }
                    xs.Add(nx);
                    ys.Add(ny);
                    layers.Add(1);
                }
            }

            return new PackOrientation
            {
                Width = outW,
                Height = outH,
                Rotation = rotation,
                CellX = xs.ToArray(),
                CellY = ys.ToArray(),
                CellLayer = layers.ToArray()
            };
        }
    }

    internal sealed class BenchBin
    {
        public int Id;
        public int Width;
        public int Height;
        public bool[] Wall;
        public PackGrid BaseGrid;
        public PackGrid Grid;
        public List<BenchItem> Anchors = new List<BenchItem>();
        public List<BenchItem> Items = new List<BenchItem>();
        public List<PackPiece> Pieces = new List<PackPiece>();
        public List<PackPlacement> Placements = new List<PackPlacement>();
        public Dictionary<string, int> KeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> CategoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Free;
        public string Kind = "storage";

        public BenchBin(int id, int width, int height, bool[] wall)
        {
            Id = id;
            Width = width;
            Height = height;
            Wall = wall == null ? new bool[width * height] : (bool[])wall.Clone();
            BaseGrid = new PackGrid(width, height);
            Array.Copy(Wall, BaseGrid.Wall, Wall.Length);
            Grid = BaseGrid.Clone();
            Free = CountFree(Grid);
        }

        public BenchBin Clone()
        {
            var copy = new BenchBin(Id, Width, Height, Wall)
            {
                Anchors = new List<BenchItem>(Anchors),
                Items = new List<BenchItem>(Items),
                Pieces = new List<PackPiece>(Pieces),
                Placements = new List<PackPlacement>(Placements),
                KeyCounts = new Dictionary<string, int>(KeyCounts, StringComparer.Ordinal),
                CategoryCounts = new Dictionary<string, int>(CategoryCounts, StringComparer.Ordinal),
                Free = Free,
                BaseGrid = BaseGrid.Clone(),
                Grid = Grid.Clone(),
                Kind = Kind
            };
            return copy;
        }

        public void CopyFrom(BenchBin source)
        {
            Anchors = new List<BenchItem>(source.Anchors);
            Items = new List<BenchItem>(source.Items);
            Pieces = new List<PackPiece>(source.Pieces);
            Placements = new List<PackPlacement>(source.Placements);
            KeyCounts = new Dictionary<string, int>(source.KeyCounts, StringComparer.Ordinal);
            CategoryCounts = new Dictionary<string, int>(source.CategoryCounts, StringComparer.Ordinal);
            Free = source.Free;
            BaseGrid = source.BaseGrid.Clone();
            Grid = source.Grid.Clone();
            Kind = source.Kind;
        }

        public void AddAnchor(BenchItem anchor, int x, int y, int orientation = 0)
        {
            if (anchor == null) return;
            var piece = anchor.CreatePiece(-2);
            PackOrientation chosen = null;
            for (int i = 0; i < piece.Orientations.Count; i++)
            {
                var o = piece.Orientations[i];
                if (o.Rotation != orientation) continue;
                if (Fits(o, x, y)) { chosen = o; break; }
            }
            if (chosen == null)
            {
                for (int i = 0; i < piece.Orientations.Count; i++)
                {
                    var o = piece.Orientations[i];
                    if (Fits(o, x, y)) { chosen = o; break; }
                }
            }
            if (chosen == null) return;
            BaseGrid.Place(chosen, x, y, -2);
            Anchors.Add(anchor);
            AddCount(KeyCounts, anchor.Key);
            AddCount(CategoryCounts, anchor.Category);
            Grid = BaseGrid.Clone();
            Free = CountFree(Grid);
        }

        private bool Fits(PackOrientation o, int x, int y)
        {
            if (x < 0 || y < 0 || x + o.Width > Width || y + o.Height > Height) return false;
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int idx = (y + o.CellY[i]) * Width + (x + o.CellX[i]);
                if (Wall[idx] || BaseGrid.Layers[idx] != 0) return false;
            }
            return true;
        }

        public bool TryPlace(BenchItem item, PackOptions options)
        {
            if (item == null) return false;
            var piece = item.CreatePiece(Pieces.Count);
            piece.Tag = item;
            Pieces.Add(piece);

            var placement = GridPacker.FindPlacement(Grid, Pieces, piece, options);
            if (placement == null)
            {
                Pieces.RemoveAt(Pieces.Count - 1);
                return false;
            }

            Grid.Place(placement.Orientation, placement.X, placement.Y, piece.Id);
            Placements.Add(placement);
            Items.Add(item);
            AddCount(KeyCounts, item.Key);
            AddCount(CategoryCounts, item.Category);
            Free = CountFree(Grid);
            return true;
        }

        public bool Compact(PackOptions options)
        {
            if (Pieces.Count == 0) return true;
            var pieces = new List<PackPiece>(Pieces);
            var baseGrid = BaseGrid.Clone();
            var result = GridPacker.Pack(baseGrid, pieces, options);
            if (result == null || !result.Success || result.Placements.Count != pieces.Count)
                return false;
            ApplyResult(result);
            return true;
        }

        public void ApplyResult(PackResult result)
        {
            var grid = BaseGrid.Clone();
            var pieces = new List<PackPiece>();
            var placements = new List<PackPlacement>();
            var items = new List<BenchItem>();

            for (int i = 0; i < result.Placements.Count; i++)
            {
                var placement = result.Placements[i];
                placement.Piece.Id = i;
                grid.Place(placement.Orientation, placement.X, placement.Y, i);
                pieces.Add(placement.Piece);
                placements.Add(placement);
                items.Add((BenchItem)placement.Piece.Tag);
            }

            Grid = grid;
            Pieces = pieces;
            Placements = placements;
            Items = items;
            KeyCounts.Clear();
            CategoryCounts.Clear();
            for (int i = 0; i < Anchors.Count; i++)
            {
                AddCount(KeyCounts, Anchors[i].Key);
                AddCount(CategoryCounts, Anchors[i].Category);
            }
            for (int i = 0; i < Items.Count; i++)
            {
                AddCount(KeyCounts, Items[i].Key);
                AddCount(CategoryCounts, Items[i].Category);
            }
            Free = CountFree(Grid);
        }

        public bool Validate()
        {
            var occupied = new bool[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int idx = y * Width + x;
                    if (Wall[idx]) occupied[idx] = true;
                }
            }
            if (BaseGrid != null)
            {
                for (int i = 0; i < BaseGrid.Layers.Length; i++)
                    if (BaseGrid.Layers[i] != 0) occupied[i] = true;
            }

            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                var piece = FindPiece(item);
                if (piece == null) return false;
                var orientation = FindOrientation(piece);
                if (orientation == null) return false;
                var placement = FindPlacement(piece);
                if (placement == null) return false;
                for (int c = 0; c < orientation.CellX.Length; c++)
                {
                    int x = placement.X + orientation.CellX[c];
                    int y = placement.Y + orientation.CellY[c];
                    if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
                    int idx = y * Width + x;
                    if (occupied[idx]) return false;
                    occupied[idx] = true;
                }
            }
            return true;
        }

        private PackPiece FindPiece(BenchItem item)
        {
            for (int i = 0; i < Pieces.Count; i++)
                if (ReferenceEquals(Pieces[i].Tag, item)) return Pieces[i];
            return null;
        }

        private PackOrientation FindOrientation(PackPiece piece)
        {
            for (int i = 0; i < Placements.Count; i++)
                if (ReferenceEquals(Placements[i].Piece, piece)) return Placements[i].Orientation;
            return null;
        }

        private PackPlacement FindPlacement(PackPiece piece)
        {
            for (int i = 0; i < Placements.Count; i++)
                if (ReferenceEquals(Placements[i].Piece, piece)) return Placements[i];
            return null;
        }

        public void SetOwnerKeys()
        {
            Grid.SetOwnerCategories(Pieces);
        }

        public int CountFree(PackGrid grid)
        {
            int count = 0;
            for (int i = 0; i < grid.Layers.Length; i++)
                if (!grid.Wall[i] && grid.Layers[i] == 0) count++;
            return count;
        }

        private static void AddCount(Dictionary<string, int> map, string key)
        {
            key = key ?? "";
            map.TryGetValue(key, out int count);
            map[key] = count + 1;
        }
    }

    internal sealed class BenchCase
    {
        public int Seed;
        public List<BenchBin> Bins = new List<BenchBin>();
        public List<BenchItem> Items = new List<BenchItem>();

        public BenchCase Clone()
        {
            var copy = new BenchCase { Seed = Seed };
            for (int i = 0; i < Bins.Count; i++) copy.Bins.Add(Bins[i].Clone());
            copy.Items.AddRange(Items);
            return copy;
        }
    }

    internal static class BenchGenerator
    {
        private static readonly int[][] Sizes =
        {
            new[] { 10, 10 },
            new[] { 18, 4 },
            new[] { 24, 10 },
            new[] { 12, 8 },
            new[] { 16, 6 }
        };

        public static BenchCase Generate(int seed, int binCount, int itemCount)
        {
            var rng = new Random(seed);
            var bench = new BenchCase { Seed = seed };
            int totalCapacity = 0;

            for (int i = 0; i < binCount; i++)
            {
                var size = Sizes[rng.Next(Sizes.Length)];
                int w = size[0];
                int h = size[1];
                var wall = new bool[w * h];
                int wallCount = rng.Next(0, 5);
                for (int k = 0; k < wallCount; k++)
                    wall[rng.Next(wall.Length)] = true;

                var bin = new BenchBin(i, w, h, wall);
                // A few anchored items make the benchmark closer to a real container.
                if (rng.Next(100) < 35)
                {
                    var anchor = MakeItem(-1, "locked", "Locked", 1 + rng.Next(2), 1 + rng.Next(2), false, rng);
                    anchor.OriginalBin = i;
                    int ax = rng.Next(0, Math.Max(1, w - anchor.Width + 1));
                    int ay = rng.Next(0, Math.Max(1, h - anchor.Height + 1));
                    bin.AddAnchor(anchor, ax, ay);
                }
                bench.Bins.Add(bin);
                totalCapacity += bin.CountFree(bin.Grid);
            }

            int targetArea = (int)(totalCapacity * (0.50 + rng.NextDouble() * 0.15));
            int area = 0;
            int id = 0;
            int guard = 0;
            string[] categories = { "Meds", "Food", "Weapons", "Parts", "Junk" };

            while (area < targetArea && guard++ < itemCount * 20)
            {
                int w = 1 + rng.Next(3);
                int h = 1 + rng.Next(3);
                bool lShape = rng.Next(100) < 18 && w > 1 && h > 1;
                string category = categories[rng.Next(categories.Length)];
                string key = category + "_" + rng.Next(1, 5);
                var item = MakeItem(id, key, category, w, h, lShape, rng);
                if (area + item.Area > targetArea) continue;
                item.OriginalBin = rng.Next(binCount);
                bench.Items.Add(item);
                area += item.Area;
                id++;
                if (bench.Items.Count >= itemCount) break;
            }

            return bench;
        }

        private static BenchItem MakeItem(int id, string key, string category, int w, int h,
            bool lShape, Random rng)
        {
            var mask = new byte[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[x, y] = 1;

            if (lShape)
                mask[rng.Next(w), rng.Next(h)] = 0;

            int area = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[x, y] != 0) area++;

            return new BenchItem
            {
                Id = id,
                Key = key,
                Category = category,
                Width = w,
                Height = h,
                Mask = mask,
                Area = Math.Max(1, area)
            };
        }
    }
}
