using System;
using System.Collections.Generic;

namespace CompactSort.Packing
{
    public enum PackAlgorithm
    {
        Greedy,
        GreedyLns,
        Beam,
        BeamLns,
        Hybrid,
        HybridSa,
        Annealing,
        Genetic,
        BranchAndBound,
        VisualFirst
    }

    /// <summary>One rotation of an item shape, normalized so its bounding box starts at (0,0).</summary>
    public sealed class PackOrientation
    {
        public int Width;
        public int Height;
        public int Rotation;          // absolute rotation 0..3
        public int[] CellX;
        public int[] CellY;
        public byte[] CellLayer;      // layer byte used by the game's stacked-slot rules

        public bool SameShape(PackOrientation other)
        {
            if (other == null || Width != other.Width || Height != other.Height ||
                CellX.Length != other.CellX.Length)
                return false;
            for (int i = 0; i < CellX.Length; i++)
            {
                if (CellX[i] != other.CellX[i] || CellY[i] != other.CellY[i] ||
                    CellLayer[i] != other.CellLayer[i])
                    return false;
            }
            return true;
        }
    }

    public sealed class PackPiece
    {
        public int Id;
        public string Key;            // item identifier; used only for a small grouping bonus
        public string CategoryKey;    // item directory/category, e.g. MedsItemDirectory
        public int Area;
        public int MaxDim;
        public int OriginalRotation;
        public object Tag;            // adapter payload (GameItem)
        public List<PackOrientation> Orientations = new List<PackOrientation>();
    }

    /// <summary>Mutable grid. Wall cells are outside the container mask and can never be used.</summary>
    public sealed class PackGrid
    {
        public int Width;
        public int Height;
        public bool[] Wall;
        public byte[] Layers;         // bit mask of occupied layers
        public int[] Owner;           // -1 empty, -2 anchored item, >=0 moving piece id
        public string[] OwnerCategories;
        public string[] OwnerKeys;
        public bool StrictOccupancy = true;
        public int MinX = -1;
        public int MinY = -1;
        public int MaxX = -1;
        public int MaxY = -1;

        public PackGrid(int width, int height)
        {
            Width = width;
            Height = height;
            Wall = new bool[width * height];
            Layers = new byte[width * height];
            Owner = new int[width * height];
            for (int i = 0; i < Owner.Length; i++) Owner[i] = -1;
        }

        public PackGrid Clone()
        {
            var g = new PackGrid(Width, Height);
            Array.Copy(Wall, g.Wall, Wall.Length);
            Array.Copy(Layers, g.Layers, Layers.Length);
            Array.Copy(Owner, g.Owner, Owner.Length);
            g.OwnerCategories = OwnerCategories;
            g.OwnerKeys = OwnerKeys;
            g.StrictOccupancy = StrictOccupancy;
            g.MinX = MinX; g.MinY = MinY; g.MaxX = MaxX; g.MaxY = MaxY;
            return g;
        }

        public int Index(int x, int y) { return y * Width + x; }

        public bool InBounds(int x, int y) { return x >= 0 && y >= 0 && x < Width && y < Height; }

        public bool IsWall(int x, int y)
        {
            return !InBounds(x, y) || Wall[y * Width + x];
        }

        public bool IsOccupied(int x, int y)
        {
            return !IsWall(x, y) && Layers[y * Width + x] != 0;
        }

        public void Place(PackOrientation o, int x, int y, int owner)
        {
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                int idx = gy * Width + gx;
                Layers[idx] = (byte)(Layers[idx] | (1 << o.CellLayer[i]));
                Owner[idx] = owner;
                if (MinX < 0 || gx < MinX) MinX = gx;
                if (MinY < 0 || gy < MinY) MinY = gy;
                if (gx > MaxX) MaxX = gx;
                if (gy > MaxY) MaxY = gy;
            }
        }

        public void Remove(PackOrientation o, int x, int y)
        {
            for (int i = 0; i < o.CellX.Length; i++)
            {
                int gx = x + o.CellX[i];
                int gy = y + o.CellY[i];
                int idx = gy * Width + gx;
                Layers[idx] = (byte)(Layers[idx] & ~(1 << o.CellLayer[i]));
                if (Layers[idx] == 0) Owner[idx] = -1;
            }
        }

        public void SetOwnerCategories(List<PackPiece> pieces)
        {
            if (pieces == null)
            {
                OwnerCategories = null;
                return;
            }
            OwnerCategories = new string[pieces.Count];
            OwnerKeys = new string[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                OwnerCategories[i] = pieces[i].CategoryKey;
                OwnerKeys[i] = pieces[i].Key;
            }
        }

        public void RecalculateExtents()
        {
            MinX = MinY = MaxX = MaxY = -1;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Layers[y * Width + x] == 0) continue;
                    if (MinX < 0 || x < MinX) MinX = x;
                    if (MinY < 0 || y < MinY) MinY = y;
                    if (x > MaxX) MaxX = x;
                    if (y > MaxY) MaxY = y;
                }
            }
        }
    }

    public sealed class PackPlacement
    {
        public PackPiece Piece;
        public PackOrientation Orientation;
        public int X;
        public int Y;
    }

    public sealed class PackOptions
    {
        public bool BigFirst = true;
        public bool FromEnd = false;
        public bool AllowRotation = true;
        public int TimeBudgetMs = 350;
        public int MaxStrategies = 24;
        public int RandomSeed = 20261101;
        public PackAlgorithm Algorithm = PackAlgorithm.Hybrid;
        public bool CategoryFirst = false;
        public bool StrictOccupancy = true;
        public bool EnableGroupingSwap = true;
        public bool EnableNeaten = true;
        public bool EnableLns = true;
        public int LnsBudgetMs = 220;
        public int BeamWidth = 20;
        public int BeamBranch = 6;
        public float MaxRectWeight = 20f;
        public float LocalMaxRectPenalty = 0f;
        public int AnnealingBudgetMs = 180;
        public int GeneticPopulation = 12;
        public int GeneticGenerations = 6;

        /// <summary>Final legality check supplied by the game adapter. Called only for the best candidates.</summary>
        public Func<PackPiece, PackOrientation, int, int, bool> Validator;

        /// <summary>Optional trace sink; used by the test harness, null in game.</summary>
        public Action<string> Log;
    }

    public sealed class PackResult
    {
        public bool Success;
        public bool TimedOut;
        public long Score;
        public int EmptyComponents;
        public int IsolatedHoles;
        public int BBoxGap;
        public int Contact;
        public int MaxEmptyRect;
        public int ReadingOrderDisorder;
        public int SameItemBBoxWaste;
        public int CategoryContacts;
        public int SameItemContacts;
        public int ItemClusters;
        public int LocalMoves;
        public int LocalImprovements;
        public int SearchIterations;
        public int SearchImprovements;
        public int[] Order;
        public int LnsIterations;
        public int LnsImprovements;
        public List<PackPlacement> Placements = new List<PackPlacement>();

        /// <summary>Experiment-only switch used by the weight sweep.</summary>
        public static bool PreferMaxRectFirst = false;

        /// <summary>Gap-first comparison: holes, then empty components, then bbox gaps, then score.</summary>
        public bool IsTighterThan(PackResult other)
        {
            if (other == null) return true;
            if (IsolatedHoles != other.IsolatedHoles)
                return IsolatedHoles < other.IsolatedHoles;
            if (EmptyComponents != other.EmptyComponents)
                return EmptyComponents < other.EmptyComponents;

            if (PreferMaxRectFirst)
            {
                if (MaxEmptyRect != other.MaxEmptyRect)
                    return MaxEmptyRect > other.MaxEmptyRect;
                if (BBoxGap != other.BBoxGap)
                    return BBoxGap < other.BBoxGap;
            }
            else
            {
                if (BBoxGap != other.BBoxGap)
                    return BBoxGap < other.BBoxGap;
                // Once the gaps are equal, keep the same identifier together before
                // spending anything on visual neatness.
                if (ItemClusters != other.ItemClusters)
                    return ItemClusters < other.ItemClusters;
                if (SameItemContacts != other.SameItemContacts)
                    return SameItemContacts > other.SameItemContacts;
                // Reading order and same-item rectangularity are then soft preferences.
                if (SameItemBBoxWaste != other.SameItemBBoxWaste)
                    return SameItemBBoxWaste < other.SameItemBBoxWaste;
                if (ReadingOrderDisorder != other.ReadingOrderDisorder)
                    return ReadingOrderDisorder < other.ReadingOrderDisorder;
                if (CategoryContacts != other.CategoryContacts)
                    return CategoryContacts > other.CategoryContacts;
                if (MaxEmptyRect != other.MaxEmptyRect)
                    return MaxEmptyRect > other.MaxEmptyRect;
            }
            return Score < other.Score;
        }

        /// <summary>
        /// Visual-first comparison: never make holes/components worse, allow a small
        /// bbox-gap cost, then prefer reading order / same-item neatness / clustering.
        /// </summary>
        public bool IsVisuallyBetterThan(PackResult other, int gapTolerance)
        {
            if (other == null) return true;
            if (IsolatedHoles > other.IsolatedHoles) return false;
            if (EmptyComponents > other.EmptyComponents) return false;
            if (BBoxGap > other.BBoxGap + gapTolerance) return false;

            if (ReadingOrderDisorder < other.ReadingOrderDisorder) return true;
            if (SameItemBBoxWaste < other.SameItemBBoxWaste) return true;
            if (ItemClusters < other.ItemClusters) return true;
            if (SameItemContacts > other.SameItemContacts) return true;
            if (CategoryContacts > other.CategoryContacts) return true;
            if (MaxEmptyRect > other.MaxEmptyRect) return true;
            return Score < other.Score;
        }

        public static bool IsTighter(int holesA, int componentsA, int gapA, long scoreA,
            int holesB, int componentsB, int gapB, long scoreB)
        {
            if (holesA != holesB) return holesA < holesB;
            if (componentsA != componentsB) return componentsA < componentsB;
            if (gapA != gapB) return gapA < gapB;
            return scoreA < scoreB;
        }
    }
}
