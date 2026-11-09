using HarmonyLib;

namespace CompactSort
{
    [HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.MayUndo))]
    internal static class ModUndoMayPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameGridInventory inventory, ref bool __result)
        {
            if (!__result && ModUndo.Has(inventory)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.CanUndo))]
    internal static class ModUndoCanPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameGridInventory inventory, ref bool __result)
        {
            if (!__result && ModUndo.Has(inventory)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.Undo))]
    internal static class ModUndoPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameGridInventory inventory, ref bool __result)
        {
            if (!ModUndo.Has(inventory)) return true;
            __result = CompactSorter.TryUndo(inventory);
            return false;
        }

    }

    [HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.Sort))]
    internal static class ModUndoSortPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameGridInventory inventory)
        {
            ModUndo.Clear(inventory);
        }
    }
}
