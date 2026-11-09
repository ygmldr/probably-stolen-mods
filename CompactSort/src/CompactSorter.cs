using System;
using System.Collections.Generic;
using System.Diagnostics;
using CompactSort.Packing;
using UnityEngine;

namespace CompactSort
{
    /// <summary>Bridges the game's grid inventory to the pure packer.</summary>
    internal static class CompactSorter
    {
        public static bool EnableDragAutoFit = true;

        public static bool TrySort(GameGridInventory inventory, bool bigFirst, bool fromEnd, bool categoryFirst = false, int budgetOverride = -1)
        {
            if (inventory == null) return false;
            if (inventory.IsInsertLocked())
            {
                ModLog.I("[CompactSort] skip: container insert is locked.");
                return false;
            }
            if (!InventorySortHelper.CanSort(inventory))
            {
                ModLog.I("[CompactSort] skip: container has unremovable items or no movable items.");
                return false;
            }

            var moving = new List<GameItem>();
            var anchored = new List<GameItem>();
            var all = inventory.childItems;
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (IsAnchored(item))
                {
                    anchored.Add(item);
                    continue;
                }
                if (!item.MayRemove() || item.MaxNumRemove() < item.unitCount)
                    return false;
                moving.Add(item);
            }

            if (moving.Count <= 1)
            {
                ModLog.I("[CompactSort] skip: only " + moving.Count + " movable item(s).");
                return true;
            }

            var grid = BuildGrid(inventory, anchored);
            var pieces = BuildPieces(moving);
            if (pieces == null || pieces.Count != moving.Count)
            {
                ModLog.W("[CompactSort] skip: could not build item shapes.");
                return false;
            }
            grid.SetOwnerCategories(pieces);

            var options = new PackOptions
            {
                BigFirst = bigFirst,
                FromEnd = fromEnd,
                AllowRotation = EmporiumOption.SortAutoRotate,
                TimeBudgetMs = budgetOverride > 0 ? budgetOverride : CompactSortSettings.TimeBudgetMs,
                MaxStrategies = 24,
                RandomSeed = 20261101,
                Algorithm = PackAlgorithm.Hybrid,
                CategoryFirst = categoryFirst,
                BeamWidth = 20,
                BeamBranch = 6,
                Validator = (piece, ori, x, y) => Validate(inventory, piece, ori, x, y, moving)
            };

            var result = GridPacker.Pack(grid, pieces, options);
            if (!result.Success && categoryFirst)
            {
                ModLog.I("[CompactSort] category-first layout failed; retrying generic order.");
                options.CategoryFirst = false;
                result = GridPacker.Pack(grid, pieces, options);
            }
            if (!result.Success)
            {
                ModLog.W("[CompactSort] no complete packing found for this container.");
                return false;
            }

            int usable = 0;
            for (int i = 0; i < grid.Wall.Length; i++)
                if (!grid.Wall[i]) usable++;
            int itemArea = 0;
            for (int i = 0; i < pieces.Count; i++) itemArea += pieces[i].Area;
            double fill = usable == 0 ? 1.0 : Math.Min(1.0, (double)itemArea / usable);
            int visualGapTolerance = fill < 0.80 ? Math.Max(6, usable / 3)
                : (fill < 0.90 ? 2 : 0);

            bool usedVisual = false;
            if (categoryFirst && !fromEnd && fill < 0.90)
            {
                var visualOptions = new PackOptions
                {
                    BigFirst = true,
                    FromEnd = false,
                    AllowRotation = options.AllowRotation,
                    TimeBudgetMs = Math.Min(150, options.TimeBudgetMs),
                    MaxStrategies = 12,
                    RandomSeed = options.RandomSeed,
                    Algorithm = PackAlgorithm.VisualFirst,
                    CategoryFirst = true,
                    BeamWidth = options.BeamWidth,
                    BeamBranch = options.BeamBranch,
                    Validator = options.Validator
                };
                var visual = GridPacker.Pack(grid, pieces, visualOptions);
                bool visualImproves = visual.Success &&
                    (visual.ReadingOrderDisorder < result.ReadingOrderDisorder ||
                     visual.SameItemBBoxWaste < result.SameItemBBoxWaste ||
                     visual.ItemClusters < result.ItemClusters ||
                     visual.SameItemContacts > result.SameItemContacts);
                if (visualImproves &&
                    visual.IsVisuallyBetterThan(result, visualGapTolerance))
                {
                    result = visual;
                    usedVisual = true;
                    ModLog.I("[CompactSort] visual-first layout selected (fill " +
                              (fill * 100).ToString("0.0") + "%, gap " + result.BBoxGap +
                              ", reading " + result.ReadingOrderDisorder +
                              ", waste " + result.SameItemBBoxWaste + ").");
                }
            }

            var current = BuildCurrentGrid(inventory, anchored, moving, pieces);
            var currentScore = GridPacker.ScoreLayout(current, options);
            bool better = usedVisual
                ? result.IsVisuallyBetterThan(currentScore, visualGapTolerance)
                : result.IsTighterThan(currentScore);
            if (!better && categoryFirst &&
                (result.ItemClusters < currentScore.ItemClusters ||
                 result.SameItemContacts > currentScore.SameItemContacts) &&
                result.IsolatedHoles <= currentScore.IsolatedHoles &&
                result.EmptyComponents <= currentScore.EmptyComponents &&
                result.BBoxGap <= currentScore.BBoxGap + 1)
            {
                better = true;
                ModLog.I("[CompactSort] accepting a grouping improvement with a small gap cost.");
            }
            if (!better)
            {
                ModLog.I("[CompactSort] current layout is already as tight (gap " + result.BBoxGap +
                          " vs " + currentScore.BBoxGap + ", holes " + result.IsolatedHoles +
                          " vs " + currentScore.IsolatedHoles + ", same-item " + result.SameItemContacts +
                          " vs " + currentScore.SameItemContacts + ", clusters " + result.ItemClusters +
                          " vs " + currentScore.ItemClusters + ", category " + result.CategoryContacts +
                          " vs " + currentScore.CategoryContacts + ", score " + result.Score +
                          " vs " + currentScore.Score + "), keeping it.");
                return true;
            }

            var shapes = new List<GridShape>(result.Placements.Count);
            for (int i = 0; i < result.Placements.Count; i++)
            {
                var p = result.Placements[i];
                var shape = BuildShape((GameItem)p.Piece.Tag, p.Orientation, p.X, p.Y);
                if (shape == null || !Validate(inventory, p.Piece, p.Orientation, p.X, p.Y, moving))
                {
                    ModLog.W("[CompactSort] final validation failed, falling back to vanilla.");
                    return false;
                }
                shapes.Add(shape);
            }

            if (!Commit(inventory, result.Placements, shapes, moving, allowUndo: true))
                return false;

            ModLog.I("[CompactSort] compacted " + moving.Count + " items: score " + currentScore.Score +
                      " -> " + result.Score + ", holes " + currentScore.IsolatedHoles + " -> " +
                      result.IsolatedHoles + ", empty components " + currentScore.EmptyComponents + " -> " +
                      result.EmptyComponents + ", bbox gap " + currentScore.BBoxGap + " -> " +
                      result.BBoxGap + ", maxRect " + currentScore.MaxEmptyRect + " -> " +
                      result.MaxEmptyRect + ", clusters " + currentScore.ItemClusters + " -> " +
                      result.ItemClusters + ", local " + result.LocalImprovements + "/" + result.LocalMoves +
                      ", lns " + result.LnsImprovements + "/" + result.LnsIterations + ".");
            return true;
        }

        public static bool TrySortSmart(GameGridInventory inventory)
        {
            return TrySort(inventory, bigFirst: true, fromEnd: false, categoryFirst: true);
        }

        /// <summary>
        /// Drag-and-drop fallback: try to fit the incoming items plus everything already in the
        /// target grid. Used after the vanilla drop has already failed for at least one item.
        /// </summary>
        public static bool TryPackIncoming(GameGridInventory inventory, List<GameItem> incoming)
        {
            if (!EnableDragAutoFit)
            {
                ModLog.I("[CompactSort] drag-fit disabled: manual sort only.");
                return false;
            }
            if (inventory == null || incoming == null || incoming.Count == 0) return false;
            if (inventory.IsInsertLocked()) return false;
            if (!IsHiddenStash(inventory) && !GeneralHelper.MayPlayerInsertInto(inventory)) return false;

            var moving = new List<GameItem>();
            var anchored = new List<GameItem>();
            var all = inventory.childItems;
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (IsAnchored(item))
                {
                    anchored.Add(item);
                    continue;
                }
                if (!item.MayRemove() || item.MaxNumRemove() < item.unitCount)
                    return false;
                moving.Add(item);
            }

            int incomingAdded = 0;
            for (int i = 0; i < incoming.Count; i++)
            {
                var item = incoming[i];
                if (item == null) continue;
                if (item is GameItemElement element && element.IsDestroyed()) continue;
                if (item.parentInventory == null)
                {
                    // Purchase/plant overflow items can be spawned without a parent.
                    // Never repack the target for an item we cannot detach safely.
                    ModLog.I("[CompactSort] drag-fit skipped: incoming overflow item has no parent.");
                    return false;
                }
                if (item.parentInventory == inventory) continue;
                if (!item.MayRemove() || item.MaxNumRemove() < item.unitCount)
                    return false;
                if (!inventory.MayHaveValidInventorySlot(item))
                    return false;
                if (moving.Contains(item)) continue;
                moving.Add(item);
                incomingAdded++;
            }

            if (incomingAdded == 0)
            {
                ModLog.I("[CompactSort] drag-fit skipped: no incoming item outside the target.");
                return false;
            }
            if (moving.Count == 0) return false;

            var grid = BuildGrid(inventory, anchored);
            var pieces = BuildPieces(moving);
            if (pieces == null || pieces.Count != moving.Count)
            {
                ModLog.W("[CompactSort] skip: could not build item shapes.");
                return false;
            }
            grid.SetOwnerCategories(pieces);

            var options = new PackOptions
            {
                BigFirst = true,
                FromEnd = false,
                AllowRotation = EmporiumOption.SortAutoRotate,
                TimeBudgetMs = CompactSortSettings.TimeBudgetMs,
                MaxStrategies = 24,
                RandomSeed = 20261101,
                Algorithm = PackAlgorithm.Hybrid,
                CategoryFirst = false,
                BeamWidth = 20,
                BeamBranch = 6,
                Validator = (piece, ori, x, y) => Validate(inventory, piece, ori, x, y, moving)
            };

            var result = GridPacker.Pack(grid, pieces, options);
            if (!result.Success)
            {
                ModLog.I("[CompactSort] drag-fit: no layout fits " + moving.Count + " items.");
                return false;
            }

            var shapes = new List<GridShape>(result.Placements.Count);
            for (int i = 0; i < result.Placements.Count; i++)
            {
                var p = result.Placements[i];
                var shape = BuildShape((GameItem)p.Piece.Tag, p.Orientation, p.X, p.Y);
                if (shape == null || !Validate(inventory, p.Piece, p.Orientation, p.X, p.Y, moving))
                {
                    ModLog.W("[CompactSort] drag-fit final validation failed.");
                    return false;
                }
                shapes.Add(shape);
            }

            if (!CommitInPlace(inventory, result.Placements, shapes, moving))
                return false;
            ModLog.I("[CompactSort] drag-fit placed " + moving.Count + " items (including " +
                      incomingAdded + " incoming), gap " + result.BBoxGap + ".");
            return true;
        }

        private static Dictionary<string, string> _categoryMap;

        internal static string CategoryOf(GameItem item)
        {
            if (_categoryMap == null) BuildCategoryMap();
            string id = item.identifier;
            if (!string.IsNullOrEmpty(id) && _categoryMap.TryGetValue(id, out var category))
                return category;
            if (item.itemTypes != null && item.itemTypes.Count > 0 && !string.IsNullOrEmpty(item.itemTypes[0]))
                return item.itemTypes[0];
            return "";
        }

        private static void BuildCategoryMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var classNames = DirectoryMaster.GetDictionaryClassNameList<GameItem>();
                for (int i = 0; i < classNames.Count; i++)
                {
                    var ids = DirectoryMaster.GetIdentifierList<GameItem>(classNames[i]);
                    for (int j = 0; j < ids.Count; j++)
                    {
                        string id = ids[j];
                        if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                            map[id] = classNames[i];
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.W("[CompactSort] category map failed: " + e.Message);
            }
            _categoryMap = map;
        }

        private sealed class PoolBin
        {
            public GameGridInventory Inv;
            public PackGrid Grid;
            public List<GameItem> Anchored;
            public List<GameItem> Movables;
            public List<PackPiece> Pieces = new List<PackPiece>();
            public List<PackPlacement> Placements = new List<PackPlacement>();
            public List<GameItem> PieceItems = new List<GameItem>();
            public Dictionary<string, int> KeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> CategoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public int Free;
        }

        private static PoolBin CloneBin(PoolBin source)
        {
            var copy = new PoolBin
            {
                Inv = source.Inv,
                Anchored = source.Anchored,
                Movables = source.Movables,
                Grid = source.Grid.Clone(),
                Free = source.Free,
                KeyCounts = new Dictionary<string, int>(source.KeyCounts, StringComparer.Ordinal),
                CategoryCounts = new Dictionary<string, int>(source.CategoryCounts, StringComparer.Ordinal)
            };
            copy.Pieces.AddRange(source.Pieces);
            copy.Placements.AddRange(source.Placements);
            copy.PieceItems.AddRange(source.PieceItems);
            return copy;
        }

        private static void AdoptBin(PoolBin target, PoolBin source)
        {
            target.Grid = source.Grid;
            target.Pieces = source.Pieces;
            target.Placements = source.Placements;
            target.PieceItems = source.PieceItems;
            target.KeyCounts = source.KeyCounts;
            target.CategoryCounts = source.CategoryCounts;
            target.Free = source.Free;
        }

        private static bool TryPlaceItemInBin(PoolBin bin, GameItem item, PackOptions placeOptions)
        {
            var one = BuildPieces(new List<GameItem> { item });
            if (one == null || one.Count != 1) return false;
            var piece = one[0];
            piece.Id = bin.Pieces.Count;
            bin.Pieces.Add(piece);

            var placement = GridPacker.FindPlacement(bin.Grid, bin.Pieces, piece, placeOptions);
            if (placement == null)
            {
                bin.Pieces.RemoveAt(bin.Pieces.Count - 1);
                return false;
            }

            bin.Grid.Place(placement.Orientation, placement.X, placement.Y, piece.Id);
            var shape = BuildShape(item, placement.Orientation, placement.X, placement.Y);
            if (shape == null ||
                !bin.Inv.MayHaveValidInventorySlot(item) ||
                !bin.Inv.CheckShapePlacement(item, shape, bin.Movables))
            {
                bin.Grid.Remove(placement.Orientation, placement.X, placement.Y);
                bin.Pieces.RemoveAt(bin.Pieces.Count - 1);
                return false;
            }

            bin.Placements.Add(placement);
            bin.PieceItems.Add(item);
            bin.Free = CountFree(bin.Grid);
            string key = item.identifier ?? "";
            string category = CategoryOf(item);
            bin.KeyCounts[key] = CountOf(bin.KeyCounts, key) + 1;
            bin.CategoryCounts[category] = CountOf(bin.CategoryCounts, category) + 1;
            return true;
        }

        private static int CountFree(PackGrid grid)
        {
            int count = 0;
            for (int i = 0; i < grid.Layers.Length; i++)
                if (!grid.Wall[i] && grid.Layers[i] == 0) count++;
            return count;
        }

        private static int ShapeArea(GameItem item)
        {
            var shape = item.modifiedShape;
            if (shape == null) return 1;
            int area = 0;
            for (int y = 0; y < shape.height; y++)
                for (int x = 0; x < shape.width; x++)
                    if (shape.GetLocal(x, y) != 0) area++;
            return Math.Max(1, area);
        }

        private static int CountOf(Dictionary<string, int> map, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            return map.TryGetValue(key, out var value) ? value : 0;
        }

        /// <summary>
        /// Global pool sort: take all movable items from the automatic-sort containers,
        /// order them globally by category/identifier, and greedily assign each item to the
        /// container that already has the most identical/category items and room for it.
        /// Locked items stay anchored. The commit is transactional: no item moves unless
        /// every assignment validates.
        /// </summary>
        public static int GlobalPoolSort()
        {
            if (!SortFeatures.GlobalPoolEnabled)
            {
                ModLog.I("[CompactSort] global pool: disabled in this build.");
                return 0;
            }
            var containers = CollectAutoContainers();
            if (containers.Count == 0)
            {
                ModLog.I("[CompactSort] global pool: no containers found.");
                return 0;
            }

            var bins = new List<PoolBin>();
            var allItems = new List<GameItem>();
            var originalBin = new Dictionary<GameItem, PoolBin>();

            for (int ci = 0; ci < containers.Count; ci++)
            {
                var inv = containers[ci];
                var anchored = new List<GameItem>();
                var movables = new List<GameItem>();
                bool skip = false;
                var children = inv.childItems;
                for (int i = 0; i < children.Count; i++)
                {
                    var item = children[i];
                    if (IsAnchored(item))
                    {
                        anchored.Add(item);
                        continue;
                    }
                    if (!item.MayRemove() || item.MaxNumRemove() < item.unitCount)
                    {
                        skip = true;
                        break;
                    }
                    movables.Add(item);
                }
                if (skip || movables.Count == 0) continue;

                var bin = new PoolBin
                {
                    Inv = inv,
                    Anchored = anchored,
                    Movables = movables,
                    Grid = BuildGrid(inv, anchored)
                };
                bin.Free = CountFree(bin.Grid);
                for (int i = 0; i < movables.Count; i++)
                {
                    var item = movables[i];
                    string key = item.identifier ?? "";
                    string category = CategoryOf(item);
                    bin.KeyCounts[key] = CountOf(bin.KeyCounts, key) + 1;
                    bin.CategoryCounts[category] = CountOf(bin.CategoryCounts, category) + 1;
                }
                for (int i = 0; i < anchored.Count; i++)
                {
                    var item = anchored[i];
                    string key = item.identifier ?? "";
                    string category = CategoryOf(item);
                    bin.KeyCounts[key] = CountOf(bin.KeyCounts, key) + 1;
                    bin.CategoryCounts[category] = CountOf(bin.CategoryCounts, category) + 1;
                }
                bins.Add(bin);
                for (int i = 0; i < movables.Count; i++)
                {
                    allItems.Add(movables[i]);
                    originalBin[movables[i]] = bin;
                }
            }

            if (bins.Count == 0)
            {
                ModLog.I("[CompactSort] global pool: no movable items in automatic containers.");
                return 0;
            }

            if (bins.Count == 1)
                return TrySort(bins[0].Inv, true, false, true, CompactSortSettings.TimeBudgetMs) ? 1 : 0;

            allItems.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(CategoryOf(a), CategoryOf(b));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.identifier ?? "", b.identifier ?? "");
                if (c != 0) return c;
                c = ShapeArea(b).CompareTo(ShapeArea(a));
                if (c != 0) return c;
                return 0;
            });

            var sw = Stopwatch.StartNew();
            int budget = Mathf.Clamp(CompactSortSettings.TimeBudgetMs * 2, 600, 1500);
            var placeOptions = new PackOptions
            {
                Algorithm = PackAlgorithm.Greedy,
                BigFirst = true,
                FromEnd = false,
                AllowRotation = EmporiumOption.SortAutoRotate,
                TimeBudgetMs = budget,
                Validator = null
            };

            var remaining = new List<GameItem>(allItems);
            var groups = new Dictionary<string, List<GameItem>>(StringComparer.Ordinal);
            for (int i = 0; i < allItems.Count; i++)
            {
                var item = allItems[i];
                string key = item.identifier ?? "";
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<GameItem>();
                    groups[key] = group;
                }
                group.Add(item);
            }

            var groupList = new List<List<GameItem>>();
            foreach (var pair in groups)
                if (pair.Value.Count > 1) groupList.Add(pair.Value);
            groupList.Sort((a, b) =>
            {
                int aa = 0, ba = 0;
                for (int i = 0; i < a.Count; i++) aa += ShapeArea(a[i]);
                for (int i = 0; i < b.Count; i++) ba += ShapeArea(b[i]);
                int c = ba.CompareTo(aa);
                if (c != 0) return c;
                return b.Count.CompareTo(a.Count);
            });

            int packedGroups = 0;
            for (int gi = 0; gi < groupList.Count; gi++)
            {
                if (sw.ElapsedMilliseconds > budget)
                {
                    ModLog.I("[CompactSort] global pool: timed out before commit.");
                    return 0;
                }

                var group = groupList[gi];
                string key = group[0].identifier ?? "";
                string category = CategoryOf(group[0]);
                originalBin.TryGetValue(group[0], out var original);
                bool sameOrigin = true;
                for (int i = 1; i < group.Count; i++)
                {
                    originalBin.TryGetValue(group[i], out var other);
                    if (other != original) { sameOrigin = false; break; }
                }

                var candidates = new List<PoolBin>(bins);
                candidates.Sort((a, b) =>
                {
                    int c = CountOf(b.CategoryCounts, category).CompareTo(CountOf(a.CategoryCounts, category));
                    if (c != 0) return c;
                    c = CountOf(b.KeyCounts, key).CompareTo(CountOf(a.KeyCounts, key));
                    if (c != 0) return c;
                    bool ao = sameOrigin && a == original;
                    bool bo = sameOrigin && b == original;
                    if (ao != bo) return ao ? -1 : 1;
                    return b.Free.CompareTo(a.Free);
                });

                bool placedGroup = false;
                for (int bi = 0; bi < candidates.Count && !placedGroup; bi++)
                {
                    var bin = candidates[bi];
                    var copy = CloneBin(bin);
                    var ordered = new List<GameItem>(group);
                    ordered.Sort((a, b) => ShapeArea(b).CompareTo(ShapeArea(a)));
                    bool ok = true;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        if (!TryPlaceItemInBin(copy, ordered[i], placeOptions))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok) continue;

                    AdoptBin(bin, copy);
                    for (int i = 0; i < group.Count; i++) remaining.Remove(group[i]);
                    placedGroup = true;
                    packedGroups++;
                }
            }

            remaining.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(CategoryOf(a), CategoryOf(b));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.identifier ?? "", b.identifier ?? "");
                if (c != 0) return c;
                c = ShapeArea(b).CompareTo(ShapeArea(a));
                return c;
            });

            for (int ii = 0; ii < remaining.Count; ii++)
            {
                if (sw.ElapsedMilliseconds > budget)
                {
                    ModLog.I("[CompactSort] global pool: timed out before commit.");
                    return 0;
                }

                var item = remaining[ii];
                string key = item.identifier ?? "";
                string category = CategoryOf(item);
                originalBin.TryGetValue(item, out var original);

                var candidates = new List<PoolBin>(bins);
                candidates.Sort((a, b) =>
                {
                    int c = CountOf(b.KeyCounts, key).CompareTo(CountOf(a.KeyCounts, key));
                    if (c != 0) return c;
                    c = CountOf(b.CategoryCounts, category).CompareTo(CountOf(a.CategoryCounts, category));
                    if (c != 0) return c;
                    bool ao = a == original, bo = b == original;
                    if (ao != bo) return ao ? -1 : 1;
                    return b.Free.CompareTo(a.Free);
                });

                bool placed = false;
                for (int bi = 0; bi < candidates.Count && !placed; bi++)
                {
                    if (TryPlaceItemInBin(candidates[bi], item, placeOptions))
                        placed = true;
                }

                if (!placed)
                {
                    ModLog.W("[CompactSort] global pool: could not place " + item.identifier + "; aborting.");
                    return 0;
                }
            }

            // Validate every target container before moving anything.
            for (int bi = 0; bi < bins.Count; bi++)
            {
                var bin = bins[bi];
                for (int i = 0; i < bin.PieceItems.Count; i++)
                {
                    var item = bin.PieceItems[i];
                    var placement = bin.Placements[i];
                    if (!bin.Inv.MayHaveValidInventorySlot(item))
                    {
                        ModLog.W("[CompactSort] global pool: target rejects " + item.identifier + "; aborting.");
                        return 0;
                    }
                    var shape = BuildShape(item, placement.Orientation, placement.X, placement.Y);
                    if (shape == null || !bin.Inv.CheckShapePlacement(item, shape, bin.Movables))
                    {
                        ModLog.W("[CompactSort] global pool: placement invalid for " + item.identifier + "; aborting.");
                        return 0;
                    }
                }
            }

            var snapshots = new List<SortUndoEntry>();
            for (int bi = 0; bi < bins.Count; bi++)
            {
                for (int i = 0; i < bins[bi].PieceItems.Count; i++)
                {
                    var item = bins[bi].PieceItems[i];
                    snapshots.Add(new SortUndoEntry
                    {
                        Item = item,
                        Parent = item.parentInventory,
                        Shape = item.modifiedShape.Clone()
                    });
                }
            }

            if (!CommitPoolInPlace(bins, snapshots))
                return 0;

            ModLog.I("[CompactSort] global pool: " + allItems.Count + " items across " +
                      bins.Count + " containers, " + packedGroups + " identifier groups kept together, budget " +
                      budget + "ms.");
            return allItems.Count;
        }

        private static bool CommitPoolInPlace(List<PoolBin> bins, List<SortUndoEntry> snapshots)
        {
            var moved = new List<GameItem>();
            bool success = true;
            try
            {
                for (int bi = 0; bi < bins.Count; bi++)
                {
                    var bin = bins[bi];
                    for (int i = 0; i < bin.PieceItems.Count; i++)
                    {
                        var item = bin.PieceItems[i];
                        var placement = bin.Placements[i];
                        var shape = BuildShape(item, placement.Orientation, placement.X, placement.Y);

                        if (item.parentInventory == bin.Inv)
                        {
                            item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                            item.ValidateShapeState();
                        }
                        else
                        {
                            var parent = item.parentInventory;
                            if (parent == null || !parent.Expel(item))
                                throw new InvalidOperationException("could not detach " + item.identifier);
                            if (item is GameItemElement element && element.IsDestroyed())
                                throw new InvalidOperationException("item destroyed on detach: " + item.identifier);
                            item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                            if (!bin.Inv.UncheckedAccept(item))
                                throw new InvalidOperationException("could not place " + item.identifier);
                            moved.Add(item);
                        }
                    }
                }

                for (int bi = 0; bi < bins.Count; bi++)
                {
                    bins[bi].Inv.Validate();
                    if (HasOverlap(bins[bi].Inv))
                        throw new InvalidOperationException("overlap detected after global pool commit");
                    ModUndo.Clear(bins[bi].Inv);
                }

                for (int bi = 0; bi < bins.Count; bi++)
                {
                    for (int i = 0; i < bins[bi].PieceItems.Count; i++)
                    {
                        var item = bins[bi].PieceItems[i];
                        if (item is GameItemElement element && element.IsDestroyed())
                            throw new InvalidOperationException("item disappeared: " + item.identifier);
                        if (item.parentInventory != bins[bi].Inv)
                            throw new InvalidOperationException("item not in target: " + item.identifier);
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.E("[CompactSort] global pool commit failed, rolling back: " + e);
                try
                {
                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        var entry = snapshots[i];
                        var item = entry.Item;
                        if (item == null) continue;
                        if (item.parentInventory != null && item.parentInventory != entry.Parent)
                            item.parentInventory.Expel(item);
                        item.modifiedShape = entry.Shape;
                        if (entry.Parent != null && item.parentInventory == null)
                            entry.Parent.UncheckedAccept(item);
                        item.ValidateShapeState();
                    }
                    for (int bi = 0; bi < bins.Count; bi++) bins[bi].Inv.Validate();
                }
                catch (Exception rollbackError)
                {
                    ModLog.E("[CompactSort] global pool rollback failed: " + rollbackError);
                }
                success = false;
            }
            return success;
        }

        internal static bool IsBlockedContainer(GameGridInventory inventory,
            params GameGridInventory[] blocked)
        {
            if (inventory == null || blocked == null) return false;
            for (int i = 0; i < blocked.Length; i++)
            {
                if (ReferenceEquals(inventory, blocked[i])) return true;
            }
            return false;
        }

        internal static bool IsAutoSortBlocked(GameGridInventory inventory,
            GameGridInventory[] blocked, GameGridInventory hidden)
        {
            if (inventory == null) return false;

            // The hidden stash is a legitimate storage area even though it lives inside
            // the trashcan. Allow it (and its descendants) while the trashcan itself
            // remains manual-only.
            if (hidden != null && ReferenceEquals(inventory, hidden)) return false;
            if (IsBlockedContainer(inventory, blocked)) return true;

            var ancestors = GraphUtils.FindParentsType<GameGridInventory>(inventory);
            for (int i = 0; i < ancestors.Count; i++)
            {
                if (hidden != null && ReferenceEquals(ancestors[i], hidden)) return false;
                if (IsBlockedContainer(ancestors[i], blocked)) return true;
            }
            return false;
        }

        public static bool IsAutoSortBlocked(GameGridInventory inventory)
        {
            if (inventory == null) return false;
            var entry = EmporiumEntry.Instance;
            if (entry == null) return false;

            // Counter surfaces are for the customer/sale flow, not storage. Global pool and
            // drag auto-fit must not move items onto or off the front/back counters.
            var blocked = new[]
            {
                entry.showcaseElement,
                entry.trashcanInvElement,
                entry.frontInvinvElement,
                entry.backInvinvElement,
                entry.backInvinvElementCounter
            };
            return IsAutoSortBlocked(inventory, blocked, entry.hiddenElement);
        }

        private static bool IsHiddenStash(GameGridInventory inventory)
        {
            var entry = EmporiumEntry.Instance;
            return entry != null && ReferenceEquals(inventory, entry.hiddenElement);
        }

        public static List<GameGridInventory> CollectAutoContainers()
        {
            var list = new List<GameGridInventory>();
            var seen = new HashSet<GameGridInventory>();

            void Add(GameGridInventory inv)
            {
                if (inv == null || inv.handler == null) return;
                if (!inv.handler.gameObject.scene.IsValid()) return;
                if (!seen.Add(inv)) return;
                if (IsAutoSortBlocked(inv)) return;
                if (!IsHiddenStash(inv) && !GeneralHelper.MayPlayerInsertInto(inv)) return;
                if (inv.childItems.Count == 0) return;
                list.Add(inv);
            }

            if (PlayerStore.Instance != null)
                Add(PlayerStore.Instance.GetStoreInventory());

            var entry = EmporiumEntry.Instance;
            if (entry != null)
            {
                // Player storage. The hidden stash is allowed even though it is nested
                // inside the trashcan; counter surfaces and the showcase stay manual-only.
                Add(entry.invElement);
                Add(entry.bazarLeftinvElement);
                Add(entry.hiddenElement);
            }

            var windows = WindowsHandler.current;
            if (windows != null && windows.visibleWindows != null)
            {
                for (int i = 0; i < windows.visibleWindows.Count; i++)
                {
                    var window = windows.visibleWindows[i];
                    if (window == null) continue;
                    // Only the window's direct grid. Recursing into nested inventories
                    // would also pick up hidden compartments inside containers.
                    var direct = window.child as GameGridInventory;
                    if (direct != null) Add(direct);
                }
            }

            return list;
        }

        public static int SortAllContainers()
        {
            if (!SortFeatures.PerContainerSortEnabled)
            {
                ModLog.I("[CompactSort] full sort: disabled in this build.");
                return 0;
            }
            var list = CollectAutoContainers();
            if (list.Count == 0)
            {
                ModLog.I("[CompactSort] full sort: no containers found.");
                return 0;
            }

            int budget = Mathf.Clamp(600 / list.Count, 60, 250);
            int ok = 0;
            int failed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    if (TrySort(list[i], true, false, true, budget)) ok++;
                    else failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    ModLog.W("[CompactSort] full sort failed for one container: " + e.Message);
                }
            }
            ModLog.I("[CompactSort] full sort: " + ok + "/" + list.Count + " containers, " +
                      failed + " skipped/failed, per-container budget " + budget + "ms.");
            return ok;
        }

        internal static bool HasOverlap(GameGridInventory inventory)
        {
            int w = inventory.inventoryShape.width;
            int h = inventory.inventoryShape.height;
            var seen = new bool[w * h];
            var items = inventory.childItems;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (item is GameItemElement element && element.IsDestroyed()) continue;
                var shape = item.modifiedShape;
                if (shape == null) continue;
                for (int y = shape.minY; y <= shape.maxY; y++)
                {
                    for (int x = shape.minX; x <= shape.maxX; x++)
                    {
                        if (shape.Get(x, y) == 0) continue;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int idx = y * w + x;
                        if (seen[idx]) return true;
                        seen[idx] = true;
                    }
                }
            }
            return false;
        }

        private static bool IsAnchored(GameItem item)
        {
            return ItemPinHelper.IsPinned(item) || DebrisHelper.IsDebris(item);
        }

        private static PackGrid BuildGrid(GameGridInventory inventory, List<GameItem> anchored)
        {
            var invShape = inventory.inventoryShape;
            var grid = new PackGrid(invShape.width, invShape.height);
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    // The game's container mask uses 0 = usable cell, non-zero = blocked.
                    if (invShape.Get(x, y) != 0)
                        grid.Wall[y * grid.Width + x] = true;
                }
            }

            for (int i = 0; i < anchored.Count; i++)
                MarkShape(grid, anchored[i].modifiedShape, -2);

            grid.RecalculateExtents();
            return grid;
        }

        private static void MarkShape(PackGrid grid, GridShape shape, int owner)
        {
            for (int y = shape.minY; y <= shape.maxY; y++)
            {
                for (int x = shape.minX; x <= shape.maxX; x++)
                {
                    byte layer = shape.Get(x, y);
                    if (layer == 0) continue;
                    int idx = y * grid.Width + x;
                    if (idx < 0 || idx >= grid.Layers.Length) continue;
                    grid.Layers[idx] = (byte)(grid.Layers[idx] | (1 << layer));
                    grid.Owner[idx] = owner;
                }
            }
        }

        private static List<PackPiece> BuildPieces(List<GameItem> items)
        {
            var pieces = new List<PackPiece>(items.Count);
            bool allowRotation = EmporiumOption.SortAutoRotate;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var shape = item.modifiedShape;
                if (shape == null) return null;

                int w = shape.width;
                int h = shape.height;
                bool flip = shape.flipped;
                var piece = new PackPiece
                {
                    Id = i,
                    Key = item.identifier ?? "",
                    CategoryKey = CategoryOf(item),
                    MaxDim = Math.Max(w, h),
                    OriginalRotation = shape.orientation,
                    Tag = item
                };

                // The vanilla game only ever uses the current orientation and one 90-degree turn.
                // Keeping the same two choices avoids upside-down items and invalid state checks.
                int first = shape.orientation % 4;
                int count = allowRotation ? 2 : 1;
                for (int ri = 0; ri < count; ri++)
                {
                    int rot = (first + ri) % 4;
                    var ori = BuildOrientation(shape, w, h, flip, rot);
                    if (ori == null || ori.CellX.Length == 0) continue;

                    bool duplicate = false;
                    for (int k = 0; k < piece.Orientations.Count; k++)
                    {
                        if (piece.Orientations[k].SameShape(ori)) { duplicate = true; break; }
                    }
                    if (!duplicate) piece.Orientations.Add(ori);
                }

                if (piece.Orientations.Count == 0) return null;
                piece.Area = piece.Orientations[0].CellX.Length;
                if (piece.Area <= 0) return null;
                pieces.Add(piece);
            }

            return pieces;
        }

        private static PackOrientation BuildOrientation(GridShape shape, int w, int h, bool flip, int rot)
        {
            rot = ((rot % 4) + 4) % 4;
            var xs = new List<int>();
            var ys = new List<int>();
            var layers = new List<byte>();

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    byte layer = shape.GetLocal(lx, ly);
                    if (layer == 0) continue;

                    int x = flip ? w - 1 - lx : lx;
                    int nx, ny;
                    switch (rot)
                    {
                        case 1: nx = ly; ny = w - 1 - x; break;
                        case 2: nx = w - 1 - x; ny = h - 1 - ly; break;
                        case 3: nx = h - 1 - ly; ny = x; break;
                        default: nx = x; ny = ly; break;
                    }
                    xs.Add(nx);
                    ys.Add(ny);
                    layers.Add(layer);
                }
            }

            return new PackOrientation
            {
                Width = rot % 2 == 0 ? w : h,
                Height = rot % 2 == 0 ? h : w,
                Rotation = rot,
                CellX = xs.ToArray(),
                CellY = ys.ToArray(),
                CellLayer = layers.ToArray()
            };
        }

        private static GridShape BuildShape(GameItem item, PackOrientation ori, int x, int y)
        {
            return new GridShapeBuilder(item.modifiedShape)
                .SetOrientation(ori.Rotation)
                .SetPosition(x, y)
                .Build();
        }

        private static bool Validate(GameGridInventory inventory, PackPiece piece, PackOrientation ori,
            int x, int y, List<GameItem> moving)
        {
            var item = piece.Tag as GameItem;
            if (item == null) return false;
            var shape = BuildShape(item, ori, x, y);
            if (shape == null) return false;
            if (shape.minX < 0 || shape.minY < 0 ||
                shape.maxX >= inventory.inventoryShape.width ||
                shape.maxY >= inventory.inventoryShape.height)
                return false;
            return inventory.CheckShapePlacement(item, shape, moving);
        }

        private static PackGrid BuildCurrentGrid(GameGridInventory inventory, List<GameItem> anchored,
            List<GameItem> moving, List<PackPiece> pieces)
        {
            var grid = BuildGrid(inventory, anchored);
            for (int i = 0; i < moving.Count; i++)
            {
                var shape = moving[i].modifiedShape;
                if (shape == null) continue;
                MarkShape(grid, shape, i);
            }
            grid.SetOwnerCategories(pieces);
            grid.RecalculateExtents();
            return grid;
        }

        /// <summary>
        /// Drag-safe commit: items that already live in the target only get a new shape;
        /// they are never expelled/re-accepted in bulk. Only the incoming items (from the
        /// drag source) are detached from their old parent and accepted into the target.
        /// </summary>
        private static bool CommitInPlace(GameGridInventory inventory, List<PackPlacement> placements,
            List<GridShape> shapes, List<GameItem> moving)
        {
            bool success = true;
            var originals = new List<SortUndoEntry>(moving.Count);
            for (int i = 0; i < moving.Count; i++)
            {
                originals.Add(new SortUndoEntry
                {
                    Item = moving[i],
                    Parent = moving[i].parentInventory,
                    Shape = moving[i].modifiedShape.Clone()
                });
            }
            ModUndo.Clear(inventory);

            var movedIncoming = new List<GameItem>();
            try
            {
                for (int i = 0; i < placements.Count; i++)
                {
                    var item = (GameItem)placements[i].Piece.Tag;
                    if (item is GameItemElement element && element.IsDestroyed())
                        throw new InvalidOperationException("destroyed item during drag commit");
                    var shape = shapes[i];

                    if (item.parentInventory == inventory)
                    {
                        // In-place reshape: no parent change, no add/remove callbacks.
                        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                        item.ValidateShapeState();
                    }
                    else
                    {
                        var parent = item.parentInventory;
                        if (parent == null)
                            throw new InvalidOperationException("incoming item has no parent: " + item.identifier);
                        if (!parent.Expel(item))
                            throw new InvalidOperationException("could not detach incoming " + item.identifier);
                        if (item is GameItemElement incomingElement && incomingElement.IsDestroyed())
                            throw new InvalidOperationException("incoming item destroyed on detach: " + item.identifier);
                        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                        if (!inventory.UncheckedAccept(item))
                            throw new InvalidOperationException("could not accept incoming " + item.identifier);
                        movedIncoming.Add(item);
                    }
                }

                inventory.Validate();
                if (HasOverlap(inventory))
                    throw new InvalidOperationException("overlap detected after drag commit");

                for (int i = 0; i < placements.Count; i++)
                {
                    var item = (GameItem)placements[i].Piece.Tag;
                    if (item is GameItemElement element && element.IsDestroyed())
                        throw new InvalidOperationException("item disappeared during drag commit: " + item.identifier);
                    if (item.parentInventory != inventory)
                        throw new InvalidOperationException("item not in target after drag commit: " + item.identifier);
                }
            }
            catch (Exception e)
            {
                ModLog.E("[CompactSort] drag commit failed, rolling back: " + e);
                try
                {
                    for (int i = 0; i < originals.Count; i++)
                    {
                        var entry = originals[i];
                        var item = entry.Item;
                        if (item == null) continue;
                        if (item.parentInventory == inventory && entry.Parent != inventory)
                            inventory.Expel(item);
                        item.modifiedShape = entry.Shape;
                        if (entry.Parent != null && item.parentInventory == null)
                            entry.Parent.UncheckedAccept(item);
                        item.ValidateShapeState();
                    }
                    inventory.Validate();
                }
                catch (Exception rollbackError)
                {
                    ModLog.E("[CompactSort] drag rollback failed: " + rollbackError);
                }
                success = false;
            }
            return success;
        }

        private static bool Commit(GameGridInventory inventory, List<PackPlacement> placements,
            List<GridShape> shapes, List<GameItem> moving, bool allowUndo)
        {
            bool success = true;
            var originals = new List<SortUndoEntry>(moving.Count);
            for (int i = 0; i < moving.Count; i++)
            {
                originals.Add(new SortUndoEntry
                {
                    Item = moving[i],
                    Parent = moving[i].parentInventory,
                    Shape = moving[i].modifiedShape.Clone()
                });
            }

            if (allowUndo)
            {
                var targetOriginals = new List<SortUndoEntry>();
                for (int i = 0; i < originals.Count; i++)
                {
                    if (originals[i].Parent == inventory)
                        targetOriginals.Add(originals[i]);
                }
                ModUndo.Store(inventory, targetOriginals);
            }
            else
            {
                ModUndo.Clear(inventory);
            }

            try
            {
                for (int i = 0; i < originals.Count; i++)
                {
                    var item = originals[i].Item;
                    var parent = item.parentInventory;
                    if (parent != null && !parent.Expel(item))
                        throw new InvalidOperationException("could not detach " + item.identifier);
                    if (item.parentInventory != null)
                        throw new InvalidOperationException("item still has a parent: " + item.identifier);
                }

                for (int i = 0; i < placements.Count; i++)
                {
                    var item = (GameItem)placements[i].Piece.Tag;
                    item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shapes[i]);
                    if (!inventory.UncheckedAccept(item))
                        throw new InvalidOperationException("could not place " + item.identifier);
                }

                inventory.Validate();
                if (HasOverlap(inventory))
                    throw new InvalidOperationException("overlap detected after sort commit");
            }
            catch (Exception e)
            {
                ModLog.E("[CompactSort] commit failed, rolling back: " + e);
                try
                {
                    for (int i = 0; i < originals.Count; i++)
                    {
                        var item = originals[i].Item;
                        if (item.parentInventory != null)
                            item.parentInventory.Expel(item);
                    }
                    for (int i = 0; i < originals.Count; i++)
                    {
                        var entry = originals[i];
                        entry.Item.modifiedShape = entry.Shape;
                        if (entry.Parent != null)
                            entry.Parent.UncheckedAccept(entry.Item);
                    }
                    inventory.Validate();
                }
                catch (Exception rollbackError)
                {
                    ModLog.E("[CompactSort] rollback failed: " + rollbackError);
                }
                success = false;
            }
            return success;
        }

        internal static bool TryUndo(GameGridInventory inventory)
        {
            if (inventory == null) return false;
            var entries = ModUndo.Get(inventory);
            if (entries == null || entries.Count == 0) return false;

            var eligible = new List<SortUndoEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var item = entry.Item;
                if (item == null || entry.Shape == null) continue;
                if (item.parentInventory != inventory) continue;
                if (item is GameItemElement element && element.IsDestroyed()) continue;
                if (!item.MayRemove() || item.MaxNumRemove() < item.unitCount)
                    return false;
                eligible.Add(entry);
            }
            if (eligible.Count == 0) return false;

            var moving = new List<GameItem>(eligible.Count);
            for (int i = 0; i < eligible.Count; i++) moving.Add(eligible[i].Item);
            for (int i = 0; i < eligible.Count; i++)
            {
                if (!inventory.CheckShapePlacement(eligible[i].Item, eligible[i].Shape, moving))
                    return false;
            }

            var current = new List<SortUndoEntry>(eligible.Count);
            for (int i = 0; i < eligible.Count; i++)
            {
                var item = eligible[i].Item;
                current.Add(new SortUndoEntry
                {
                    Item = item,
                    Parent = item.parentInventory,
                    Shape = item.modifiedShape.Clone()
                });
            }

            try
            {
                for (int i = 0; i < eligible.Count; i++)
                {
                    var item = eligible[i].Item;
                    var parent = item.parentInventory;
                    if (parent != null && !parent.Expel(item))
                        throw new InvalidOperationException("could not detach " + item.identifier);
                }
                for (int i = 0; i < eligible.Count; i++)
                {
                    var entry = eligible[i];
                    entry.Item.modifiedShape = entry.Shape.Clone();
                    if (entry.Parent != null && !entry.Parent.UncheckedAccept(entry.Item))
                        throw new InvalidOperationException("could not restore " + entry.Item.identifier);
                    entry.Item.ValidateShapeState();
                }
                inventory.Validate();
                if (HasOverlap(inventory))
                    throw new InvalidOperationException("overlap detected after undo");
                ModUndo.Remove(inventory);
                ModLog.I("[CompactSort] undo restored " + eligible.Count + " item(s).");
                return true;
            }
            catch (Exception e)
            {
                ModLog.E("[CompactSort] undo failed, restoring current layout: " + e);
                try
                {
                    for (int i = 0; i < current.Count; i++)
                    {
                        var item = current[i].Item;
                        if (item.parentInventory != null) item.parentInventory.Expel(item);
                    }
                    for (int i = 0; i < current.Count; i++)
                    {
                        var entry = current[i];
                        entry.Item.modifiedShape = entry.Shape;
                        if (entry.Parent != null) entry.Parent.UncheckedAccept(entry.Item);
                        entry.Item.ValidateShapeState();
                    }
                    inventory.Validate();
                }
                catch (Exception rollbackError)
                {
                    ModLog.E("[CompactSort] undo rollback failed: " + rollbackError);
                }
                return false;
            }
        }
    }
}
