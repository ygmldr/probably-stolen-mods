using System;
using System.Collections.Generic;
using CompactSort;
using CompactSort.Packing;
using CompactSort.Tests;

namespace CompactSort.AdapterTests
{
    internal static class AdapterTests
    {
        private static List<string> _logs;

        [Test]
        public static void TrySort_ConservesItemsAndLeavesNoOverlap()
        {
            BeginLog();
            var inventory = FakeGridInventory.Create(5, 5);

            var a = new FakeItem("rifle", 2, 2);
            var b = new FakeItem("medkit", 2, 2);
            var c = new FakeItem("box", 2, 2);
            var d = new FakeItem("crate", 2, 2);
            inventory.Place(a, 0, 0);
            inventory.Place(b, 3, 0);
            inventory.Place(c, 0, 3);
            inventory.Place(d, 3, 3);

            bool ok = CompactSorter.TrySort(inventory, true, false, false, 250);

            Assert.True(ok, "TrySort failed; logs=" + Logs());
            Assert.Equal(4, inventory.childItems.Count, "item count changed; logs=" + Logs());
            AssertParents(inventory);
            AssertNoOverlap(inventory);
            AssertInside(inventory);
            Assert.True(_logs.Exists(line => line.IndexOf("compacted", StringComparison.Ordinal) >= 0),
                "expected a compacted log line; logs=" + Logs());
        }

        [Test]
        public static void TrySort_PinnedItem_StaysAtItsPosition()
        {
            BeginLog();
            var inventory = FakeGridInventory.Create(5, 5);

            var pinned = new FakeItem("locked_rifle", 2, 2);
            pinned.state.EnableTag(ItemPinHelper.PINNED_TAG);
            Assert.True(ItemPinHelper.IsPinned(pinned), "test setup failed: item is not pinned");

            inventory.Place(pinned, 0, 0);
            var b = new FakeItem("medkit", 2, 2);
            var c = new FakeItem("box", 2, 2);
            var d = new FakeItem("crate", 2, 2);
            inventory.Place(b, 3, 0);
            inventory.Place(c, 0, 3);
            inventory.Place(d, 3, 3);

            int pinnedX = pinned.modifiedShape.minX;
            int pinnedY = pinned.modifiedShape.minY;
            bool ok = CompactSorter.TrySort(inventory, true, false, false, 250);

            Assert.True(ok, "TrySort failed; logs=" + Logs());
            Assert.Equal(4, inventory.childItems.Count, "item count changed; logs=" + Logs());
            Assert.Equal(pinnedX, pinned.modifiedShape.minX, "pinned item moved on x; logs=" + Logs());
            Assert.Equal(pinnedY, pinned.modifiedShape.minY, "pinned item moved on y; logs=" + Logs());
            Assert.Same(inventory, pinned.parentInventory, "pinned item changed parent");
            AssertNoOverlap(inventory);
        }

        [Test]
        public static void TryPackIncoming_KeepsTargetAndIncomingItems()
        {
            BeginLog();
            var target = FakeGridInventory.Create(4, 4);
            var source = FakeGridInventory.Create(4, 4);

            var existing = new FakeItem("existing_box", 2, 2);
            target.Place(existing, 0, 0);

            var incomingA = new FakeItem("incoming_can", 1, 1);
            var incomingB = new FakeItem("incoming_med", 1, 2);
            source.Place(incomingA, 0, 0);
            source.Place(incomingB, 1, 0);

            var incoming = new List<GameItem> { incomingA, incomingB };
            bool ok = CompactSorter.TryPackIncoming(target, incoming);

            Assert.True(ok, "drag-fit failed; logs=" + Logs());
            Assert.Equal(3, target.childItems.Count, "target item count changed; logs=" + Logs());
            Assert.Equal(0, source.childItems.Count, "source item count changed; logs=" + Logs());
            AssertParents(target);
            AssertNoOverlap(target);
            AssertInside(target);
        }

        [Test]
        public static void TryPackIncoming_OrphanOverflowItem_DoesNotRepackTarget()
        {
            BeginLog();
            var target = FakeGridInventory.Create(4, 4);
            var existing = new FakeItem("existing_box", 2, 2);
            target.Place(existing, 0, 0);
            int beforeX = existing.modifiedShape.minX;
            int beforeY = existing.modifiedShape.minY;

            // A purchase/plant overflow item that is not in any inventory yet.
            var orphan = new FakeItem("spawned_overflow", 1, 1);
            bool ok = CompactSorter.TryPackIncoming(target, new List<GameItem> { orphan });

            Assert.False(ok, "orphan overflow must not trigger a target repack; logs=" + Logs());
            Assert.Equal(1, target.childItems.Count, "target must be untouched");
            Assert.Equal(beforeX, existing.modifiedShape.minX, "existing item moved on x");
            Assert.Equal(beforeY, existing.modifiedShape.minY, "existing item moved on y");
        }

        [Test]
        public static void TryPackIncoming_AlreadyInsideItem_DoesNotRepackTarget()
        {
            BeginLog();
            var target = FakeGridInventory.Create(4, 4);
            var existing = new FakeItem("existing_box", 2, 2);
            target.Place(existing, 0, 0);
            int beforeX = existing.modifiedShape.minX;
            int beforeY = existing.modifiedShape.minY;

            bool ok = CompactSorter.TryPackIncoming(target, new List<GameItem> { existing });

            Assert.False(ok, "an item already in the target must not trigger a repack; logs=" + Logs());
            Assert.Equal(beforeX, existing.modifiedShape.minX, "existing item moved on x");
            Assert.Equal(beforeY, existing.modifiedShape.minY, "existing item moved on y");
        }

        [Test]
        public static void TryPackIncoming_PartialStack_PreservesUnitCount()
        {
            BeginLog();
            var target = FakeGridInventory.Create(4, 4);
            var source = FakeGridInventory.Create(4, 4);
            var existing = new FakeItem("existing_box", 2, 2);
            target.Place(existing, 0, 0);

            var incoming = new FakeItem("incoming_stack", 1, 1);
            incoming.unitCount = 3;
            source.Place(incoming, 0, 0);

            bool ok = CompactSorter.TryPackIncoming(target, new List<GameItem> { incoming });

            Assert.True(ok, "drag-fit failed; logs=" + Logs());
            Assert.Equal(3, incoming.unitCount, "partial stack unit count changed");
            Assert.Same(target, incoming.parentInventory, "incoming item has wrong parent");
            Assert.Equal(2, target.childItems.Count, "target item count changed");
        }

        [Test]
        public static void TrySort_CommitFailure_RollsBackAndReturnsFalse()
        {
            BeginLog();
            var inventory = FakeGridInventory.Create(5, 5);
            var a = new FakeItem("a", 2, 2);
            var b = new FakeItem("b", 2, 2);
            var c = new FakeItem("c", 2, 2);
            var d = new FakeItem("d", 2, 2);
            inventory.Place(a, 0, 0);
            inventory.Place(b, 3, 0);
            inventory.Place(c, 0, 3);
            inventory.Place(d, 3, 3);

            var before = new Dictionary<GameItem, string>();
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var item = inventory.childItems[i];
                before[item] = item.modifiedShape.minX + "," + item.modifiedShape.minY;
            }

            inventory.FailNextAccepts = 1;
            bool ok = CompactSorter.TrySort(inventory, true, false, false, 200);

            Assert.False(ok, "TrySort must report a failed commit, not success; logs=" + Logs());
            Assert.Equal(4, inventory.childItems.Count,
                "failed commit did not restore all items; logs=" + Logs());
            AssertParents(inventory);
            AssertNoOverlap(inventory);
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var item = inventory.childItems[i];
                Assert.Equal(before[item], item.modifiedShape.minX + "," + item.modifiedShape.minY,
                    "failed commit did not restore position of " + item.identifier);
            }
        }

        [Test]
        public static void TryPackIncoming_CommitFailure_RollsBackAndReturnsFalse()
        {
            BeginLog();
            var target = FakeGridInventory.Create(4, 4);
            var source = FakeGridInventory.Create(4, 4);
            var existing = new FakeItem("existing", 2, 2);
            target.Place(existing, 0, 0);
            var incoming = new FakeItem("incoming", 1, 1);
            source.Place(incoming, 0, 0);

            target.FailNextAccepts = 1;
            bool ok = CompactSorter.TryPackIncoming(target, new List<GameItem> { incoming });

            Assert.False(ok, "TryPackIncoming must report a failed commit; logs=" + Logs());
            Assert.Equal(1, target.childItems.Count,
                "target was not restored; logs=" + Logs());
            Assert.Equal(1, source.childItems.Count,
                "source was not restored; logs=" + Logs());
            Assert.Same(target, existing.parentInventory, "existing item changed parent");
            Assert.Same(source, incoming.parentInventory, "incoming item changed parent");
        }

        [Test]
        public static void ModUndo_RestoresOriginalShapeWithoutReflection()
        {
            BeginLog();
            var inventory = FakeGridInventory.Create(4, 4);
            var item = new FakeItem("undo_me", 1, 1);
            inventory.Place(item, 1, 1);
            var original = item.modifiedShape.Clone();

            ModUndo.Store(inventory, new List<SortUndoEntry>
            {
                new SortUndoEntry { Item = item, Parent = inventory, Shape = original }
            });
            Assert.True(ModUndo.Has(inventory), "snapshot was not stored");
            item.MoveTo(3, 3);

            bool ok = CompactSorter.TryUndo(inventory);

            Assert.True(ok, "mod undo failed; logs=" + Logs());
            Assert.Equal(1, item.modifiedShape.minX, "undo x mismatch");
            Assert.Equal(1, item.modifiedShape.minY, "undo y mismatch");
            Assert.Same(inventory, item.parentInventory, "undo parent mismatch");
            Assert.False(ModUndo.Has(inventory), "undo snapshot should be consumed");
        }

        [Test]
        public static void GlobalPool_AndFullSort_AreDisabled()
        {
            BeginLog();
            int pooled = CompactSorter.GlobalPoolSort();
            int full = CompactSorter.SortAllContainers();

            Assert.Equal(0, pooled, "GlobalPoolSort must be disabled");
            Assert.Equal(0, full, "SortAllContainers must be disabled");
            Assert.True(_logs.Exists(line => line.IndexOf("global pool: disabled", StringComparison.Ordinal) >= 0),
                "missing disabled log for global pool; logs=" + Logs());
            Assert.True(_logs.Exists(line => line.IndexOf("full sort: disabled", StringComparison.Ordinal) >= 0),
                "missing disabled log for full sort; logs=" + Logs());
        }

        [Test]
        public static void AutoSort_BlocksTrashcanCountersAndShowcase()
        {
            BeginLog();
            var normal = FakeGridInventory.Create(4, 4);
            var trashcan = FakeGridInventory.Create(4, 4);
            var frontCounter = FakeGridInventory.Create(4, 4);
            var backCounter = FakeGridInventory.Create(4, 4);
            var internalCounter = FakeGridInventory.Create(4, 4);
            var showcase = FakeGridInventory.Create(4, 4);

            var blocked = new GameGridInventory[]
            {
                trashcan, frontCounter, backCounter, internalCounter, showcase
            };

            Assert.False(CompactSorter.IsBlockedContainer(normal, blocked),
                "normal storage must not be blocked");
            Assert.True(CompactSorter.IsBlockedContainer(trashcan, blocked),
                "trashcan must be blocked from global/drag auto-sort");
            Assert.True(CompactSorter.IsBlockedContainer(frontCounter, blocked),
                "front counter/desktop must be blocked from global auto-sort");
            Assert.True(CompactSorter.IsBlockedContainer(backCounter, blocked),
                "back counter must be blocked from global auto-sort");
            Assert.True(CompactSorter.IsBlockedContainer(internalCounter, blocked),
                "counter inventory must be blocked from global auto-sort");
            Assert.True(CompactSorter.IsBlockedContainer(showcase, blocked),
                "showcase must be blocked from global auto-sort");
        }

        [Test]
        public static void AutoSort_HiddenStash_IsAllowed()
        {
            BeginLog();
            var hidden = FakeGridInventory.Create(2, 2);
            var trashcan = FakeGridInventory.Create(10, 10);
            var blocked = new GameGridInventory[] { trashcan };

            Assert.False(CompactSorter.IsAutoSortBlocked(hidden, blocked, hidden),
                "the hidden stash must be allowed even though its ancestor is the trashcan");
        }

        private static void BeginLog()
        {
            _logs = new List<string>();
            ModLog.Info = line => _logs.Add(line);
            ModLog.Warn = line => _logs.Add(line);
            ModLog.Error = line => _logs.Add(line);
        }

        private static string Logs()
        {
            return _logs == null ? "<no logs>" : string.Join(" | ", _logs.ToArray());
        }

        private static void AssertParents(FakeGridInventory inventory)
        {
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var item = inventory.childItems[i];
                Assert.Same(inventory, item.parentInventory,
                    "item " + item.identifier + " has the wrong parent");
            }
        }

        private static void AssertInside(FakeGridInventory inventory)
        {
            int width = inventory.inventoryShape.width;
            int height = inventory.inventoryShape.height;
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var shape = inventory.childItems[i].modifiedShape;
                Assert.NotNull(shape, "item " + inventory.childItems[i].identifier + " has no shape");
                Assert.GreaterOrEqual(shape.minX, 0,
                    "item " + inventory.childItems[i].identifier + " has negative x");
                Assert.GreaterOrEqual(shape.minY, 0,
                    "item " + inventory.childItems[i].identifier + " has negative y");
                Assert.LessOrEqual(shape.maxX, width - 1,
                    "item " + inventory.childItems[i].identifier + " exceeds width");
                Assert.LessOrEqual(shape.maxY, height - 1,
                    "item " + inventory.childItems[i].identifier + " exceeds height");
            }
        }

        private static void AssertNoOverlap(FakeGridInventory inventory)
        {
            int width = inventory.inventoryShape.width;
            int height = inventory.inventoryShape.height;
            var seen = new bool[width * height];
            for (int i = 0; i < inventory.childItems.Count; i++)
            {
                var item = inventory.childItems[i];
                var shape = item.modifiedShape;
                if (shape == null) continue;
                for (int y = shape.minY; y <= shape.maxY; y++)
                {
                    for (int x = shape.minX; x <= shape.maxX; x++)
                    {
                        if (shape.Get(x, y) == 0) continue;
                        if (x < 0 || y < 0 || x >= width || y >= height) continue;
                        int idx = y * width + x;
                        Assert.False(seen[idx],
                            "items overlap at (" + x + "," + y + "); logs=" + Logs());
                        seen[idx] = true;
                    }
                }
            }
        }
    }
}
