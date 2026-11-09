using System;
using HarmonyLib;
using UnityEngine;

namespace CompactSort
{
    /// <summary>Sort-lock hotkey and read-only F8 diagnostics. Global/per-container hotkeys are disabled.</summary>
    [HarmonyPatch(typeof(EmporiumOption), "Update")]
    public static class FullSortPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                if (ItemPinHelper.BlocksClicks) return;
                if (ItemMouseDragHandler.current != null && ItemMouseDragHandler.current.IsDraggingItem) return;
                if (StoreUIManager.Instance != null && StoreUIManager.Instance.IsTyping()) return;

                // Read-only in-game diagnostics; never moves items. Used by test/ingame.
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    SelfTest.Run();
                    return;
                }

                if (HotkeySettings.LockPressed)
                {
                    ToggleHoveredLock();
                    return;
                }

            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] hotkey failed: " + e.Message);
            }
        }

        private static void ToggleHoveredLock()
        {
            var item = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
            if (item == null) return;
            if (!ItemPinHelper.MayPin(item)) return;

            bool locked = ItemPinHelper.TogglePinned(item);
            Debug.Log("[CompactSort] sort-lock " + (locked ? "ON" : "OFF") + ": " +
                      (item.identifier ?? item.name));
        }
    }
}
