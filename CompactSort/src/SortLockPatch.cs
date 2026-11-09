using System;
using HarmonyLib;
using UnityEngine;

namespace CompactSort
{
    /// <summary>
    /// Re-labels the game's existing Pin action as a sort lock. The PINNED_TAG is already
    /// treated as anchored by both the vanilla sorter and our packer, so a pinned item is
    /// never moved by automatic sorting.
    /// </summary>
    [HarmonyPatch(typeof(ItemContextHandler), "SetButtonColor")]
    public static class SortLockPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RichTextElement button,
            RichTextElement ___buttonPin, GameItem ___currentItem)
        {
            try
            {
                if (button == null || button != ___buttonPin) return;
                var item = ___currentItem;
                if (item == null) return;

                bool locked = ItemPinHelper.IsPinned(item);
                button.SetText(locked ? "解锁排序" : "锁定排序", 10000,
                    RenderHandler.ColorPalette.LightGray);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] sort-lock label failed: " + e);
            }
        }
    }
}
