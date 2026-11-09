using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CompactSort
{
    /// <summary>
    /// After a group drag finishes, the vanilla code may have placed only part of the selection.
    /// If the target is a grid and some selected items are still outside, retry with a full
    /// repack of the target contents plus the leftovers. This is the "3x3 + 1x1 into a 2x5 gap"
    /// fallback: the vanilla drop fails, then the whole container is repacked to make it fit.
    /// </summary>
    [HarmonyPatch(typeof(ItemMultiSelectHandler), "EndGroupDrag")]
    public static class GroupDragFitPatch
    {
        private static List<GameItemElement> _items;
        private static GameInventory _target;

        [HarmonyPrefix]
        private static void Prefix(List<GameItemElement> ___selectedItems)
        {
            _items = null;
            _target = null;
            try
            {
                if (___selectedItems == null || ___selectedItems.Count == 0) return;

                _items = new List<GameItemElement>(___selectedItems);
                var mouse = Input.mousePosition;
                GameItem hit = RenderHandler.RaycastElement<GameItem>(mouse);
                if (hit is GameItemElement element && _items.Contains(element))
                    hit = null;

                GameInventory target = null;
                if (hit == null)
                    target = RenderHandler.RaycastFindParentElement<GameInventory>(mouse);
                if (hit == null && target == null)
                    target = MachineryTabs.FindDropInventoryAt(mouse);
                _target = target;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] drag-fit capture failed: " + e.Message);
            }
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                if (_items == null || _target == null) return;
                var grid = _target as GameGridInventory;
                if (grid == null || !grid.SupportsGroupPlacement) return;

                var missing = new List<GameItem>();
                for (int i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    if (item == null || item.IsDestroyed()) continue;
                    if (item.parentInventory == grid) continue;
                    missing.Add(item);
                }

                if (missing.Count > 0)
                    CompactSorter.TryPackIncoming(grid, missing);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] drag-fit failed: " + e.Message);
            }
            finally
            {
                _items = null;
                _target = null;
            }
        }
    }

    /// <summary>Single-item equivalent of the group drag fallback.</summary>
    [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
    public static class SingleDragFitPatch
    {
        private static GameItemElement _item;
        private static int _remove;
        private static GameGridInventory _target;
        private static GameItem _lastItem;

        [HarmonyPrefix]
        private static void Prefix(GameItemElement ___currentItem, int ___currentRemove,
            GameInventory ___lastInventory, GameItem ___lastItem)
        {
            _item = ___currentItem;
            _remove = ___currentRemove;
            _target = ___lastInventory as GameGridInventory;
            _lastItem = ___lastItem;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                if (_item == null || _target == null || _item.IsDestroyed()) return;
                if (_lastItem != null) return;
                if (_remove < _item.unitCount) return;
                if (_item.parentInventory == _target) return;

                CompactSorter.TryPackIncoming(_target, new List<GameItem> { _item });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] single drag-fit failed: " + e.Message);
            }
            finally
            {
                _item = null;
                _target = null;
                _lastItem = null;
            }
        }
    }
}
