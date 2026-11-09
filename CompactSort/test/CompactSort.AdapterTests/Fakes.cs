using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace CompactSort.AdapterTests
{
    /// <summary>
    /// Minimal in-memory GameItem used by adapter tests. It deliberately overrides the
    /// virtual validation/attachment surface so no Unity player loop is required.
    /// </summary>
    internal sealed class FakeItem : GameItem
    {
        public FakeItem(string identifier, int width, int height) : base(identifier, null)
        {
            var builder = new GridShapeBuilder(width, height);
            var cells = new char[width * height];
            for (int i = 0; i < cells.Length; i++) cells[i] = '1';
            builder.SetData(new string(cells), width);

            var built = builder.Build();
            shape = built;
            modifiedShape = built;
            unitCount = 1;
            unitValue = 1;

            var tags = MakeTags();
            state = tags;
            modifiedState = tags;
            itemTypes = new List<string> { "Test" };
        }

        public GridShape PositionedAt(int x, int y)
        {
            return new GridShapeBuilder(shape).SetPosition(x, y).Build();
        }

        public void MoveTo(int x, int y)
        {
            modifiedShape = PositionedAt(x, y);
        }

        public override void Destroy()
        {
        }

        public override void Validate()
        {
        }

        private static TagSystem MakeTags()
        {
            var tags = new TagSystem();
            var stateType = typeof(GameItem).Assembly.GetType("TagState");
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), stateType);
            var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
            typeof(TagSystem).GetProperty("dict").SetValue(tags, dictionary, null);
            return tags;
        }
    }

    /// <summary>
    /// In-memory grid container. GameGridInventory's constructor touches Unity, so the
    /// factory allocates an uninitialized object and then fills only the fields the
    /// adapter reads. All mutating methods used by CompactSorter are overridden.
    /// </summary>
    internal sealed class FakeGridInventory : GameGridInventory
    {
        private List<GameItem> _items;
        public int FailNextAccepts;

        public FakeGridInventory() : base(1, 1)
        {
        }

        public static FakeGridInventory Create(int width, int height)
        {
            var inventory = (FakeGridInventory)FormatterServices.GetUninitializedObject(
                typeof(FakeGridInventory));
            inventory._items = new List<GameItem>();
            inventory.overrideLockInsert = false;
            inventory.overrideLockRemove = false;

            var builder = new GridShapeBuilder(width, height);
            var cells = new char[width * height];
            for (int i = 0; i < cells.Length; i++) cells[i] = '0'; // container mask: 0 = usable
            builder.SetData(new string(cells), width);

            var shapeField = typeof(GameGridInventory).GetField(
                "<inventoryShape>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            shapeField.SetValue(inventory, builder.Build());
            return inventory;
        }

        public override List<GameItem> childItems
        {
            get { return _items; }
        }

        public void Place(FakeItem item, int x, int y)
        {
            item.MoveTo(x, y);
            UncheckedAccept(item);
        }

        public override bool CheckShapePlacement(GameItem item, GridShape shape,
            List<GameItem> moving)
        {
            if (item == null || shape == null) return false;

            var occupied = new bool[inventoryShape.width * inventoryShape.height];
            for (int i = 0; i < _items.Count; i++)
            {
                var existing = _items[i];
                if (existing == null || ReferenceEquals(existing, item)) continue;
                if (moving != null && moving.Contains(existing)) continue;
                Mark(occupied, existing.modifiedShape);
            }

            for (int y = shape.minY; y <= shape.maxY; y++)
            {
                for (int x = shape.minX; x <= shape.maxX; x++)
                {
                    if (shape.Get(x, y) == 0) continue;
                    if (x < 0 || y < 0 || x >= inventoryShape.width || y >= inventoryShape.height)
                        return false;
                    if (occupied[y * inventoryShape.width + x]) return false;
                }
            }
            return true;
        }

        public override bool UncheckedAccept(GameItem item)
        {
            if (item == null) return false;
            if (FailNextAccepts > 0)
            {
                FailNextAccepts--;
                throw new InvalidOperationException("injected accept failure");
            }
            if (!_items.Contains(item)) _items.Add(item);
            item.parentInventory = this;
            return true;
        }

        public override bool Expel(GameItem item)
        {
            if (item == null) return false;
            _items.Remove(item);
            if (ReferenceEquals(item.parentInventory, this)) item.parentInventory = null;
            return true;
        }

        public override bool MayHaveValidInventorySlot(GameItem item)
        {
            return true;
        }

        public override void Validate()
        {
        }

        private void Mark(bool[] occupied, GridShape shape)
        {
            if (shape == null) return;
            for (int y = shape.minY; y <= shape.maxY; y++)
            {
                for (int x = shape.minX; x <= shape.maxX; x++)
                {
                    if (shape.Get(x, y) == 0) continue;
                    if (x < 0 || y < 0 || x >= inventoryShape.width || y >= inventoryShape.height)
                        continue;
                    occupied[y * inventoryShape.width + x] = true;
                }
            }
        }
    }
}
