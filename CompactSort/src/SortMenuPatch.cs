using System;
using HarmonyLib;
using UnityEngine;

namespace CompactSort
{
    /// <summary>
    /// Adds a "compact sort" entry below the four vanilla sort options. The four vanilla
    /// buttons keep their original behavior; only the new row calls our packer.
    /// </summary>
    [HarmonyPatch(typeof(ItemContextHandler))]
    public static class SortMenuPatch
    {
        private static RichTextElement _smartButton;
        private static RichTextElement _bigStart;
        private static RichTextElement _smallStart;
        private static RichTextElement _bigEnd;
        private static RichTextElement _smallEnd;
        private static RichTextElement _undo;

        [HarmonyPostfix]
        [HarmonyPatch("OnEventInit")]
        private static void OnEventInitPostfix(PixelWindow ___sortWindow,
            RichTextElement ___buttonSortBigStart, RichTextElement ___buttonSortSmallStart,
            RichTextElement ___buttonSortBigEnd, RichTextElement ___buttonSortSmallEnd,
            RichTextElement ___buttonSortUndo)
        {
            try
            {
                var sortWindow = ___sortWindow;
                if (sortWindow == null) return;

                var oldGrid = sortWindow.child as GridPixelElement;
                if (oldGrid == null) return;

                _bigStart = ___buttonSortBigStart;
                _smallStart = ___buttonSortSmallStart;
                _bigEnd = ___buttonSortBigEnd;
                _smallEnd = ___buttonSortSmallEnd;
                _undo = ___buttonSortUndo;
                if (_bigStart == null || _smallStart == null || _bigEnd == null || _smallEnd == null || _undo == null)
                {
                    Debug.LogWarning("[CompactSort] sort menu buttons not found; extra entry skipped.");
                    return;
                }

                var grid = new GridPixelElement(1, 6);
                grid.Attach(_bigStart);
                grid.Attach(_smallStart);
                grid.Attach(_bigEnd);
                grid.Attach(_smallEnd);

                _smartButton = new RichTextElement();
                _smartButton.handler.tag = "ContextMenuNode";
                _smartButton.handler.gameObject.name = "nodeButtonSmartSort";
                _smartButton.SetText("紧凑整理", 10000, RenderHandler.ColorPalette.LightGray);
                grid.Attach(_smartButton);
                grid.Attach(_undo);

                grid.Validate();
                sortWindow.Attach(grid);
                sortWindow.Validate();
                Debug.Log("[CompactSort] added compact-sort entry below vanilla options.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] could not add sort menu entry: " + e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("EqualizeSortButtonWidths")]
        private static void EqualizePostfix()
        {
            try
            {
                if (_smartButton == null || _bigStart == null) return;
                _smartButton.SetFixedSize(-1, -1);
                int width = _bigStart.widthPixels;
                if (_smartButton.widthPixels > width) width = _smartButton.widthPixels;
                if (width <= 0) return;
                _bigStart.SetFixedSize(width, -1);
                if (_smallStart != null) _smallStart.SetFixedSize(width, -1);
                if (_bigEnd != null) _bigEnd.SetFixedSize(width, -1);
                if (_smallEnd != null) _smallEnd.SetFixedSize(width, -1);
                if (_undo != null) _undo.SetFixedSize(width, -1);
                _smartButton.SetFixedSize(width, -1);
            }
            catch
            {
                // never break the vanilla menu for a cosmetic width
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateSortSelectButtonColor")]
        private static void ColorPostfix(RichTextElement selectedButton, bool ___canSort)
        {
            try
            {
                if (_smartButton == null) return;
                _smartButton.SetText("紧凑整理", 10000,
                    ___canSort ? RenderHandler.ColorPalette.LightGray : RenderHandler.ColorPalette.DarkGray);
            }
            catch
            {
                // cosmetic only
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("TriggerButton")]
        private static bool TriggerPrefix(ItemContextHandler __instance, RichTextElement button,
            GameGridInventory ___currentInventory, bool ___canSort)
        {
            if (_smartButton == null || button != _smartButton) return true;
            try
            {
                var inventory = ___currentInventory;
                if (inventory != null && ___canSort)
                {
                    __instance.Stop();
                    CompactSorter.TrySortSmart(inventory);
                    if (TutorialUIManager.Instance != null)
                        TutorialUIManager.Instance.OnInventorySorted();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] smart sort entry failed: " + e);
            }
            return false;
        }
    }
}
