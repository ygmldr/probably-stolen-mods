using System;
using HarmonyLib;

internal static class Program
{
    private static void Main()
    {
        Check(typeof(ItemContextHandler), "OnEventInit");
        Check(typeof(ItemContextHandler), "TriggerButton");
        Check(typeof(ItemContextHandler), "EqualizeSortButtonWidths");
        Check(typeof(ItemContextHandler), "UpdateSortSelectButtonColor");
        Check(typeof(ItemMultiSelectHandler), "EndGroupDrag");
        Check(typeof(ItemMouseDragHandler), "EndDrag");
        Check(typeof(ItemContextHandler), "OnEventReset");
        Check(typeof(GameOptionRegistry), "EnsureVanilla");
        Check(typeof(ItemContextHandler), "SetButtonColor");
        Check(typeof(EmporiumOption), "Update");
        Check(typeof(StoreStation), "StartDay");
        Check(typeof(InventorySortHelper), "MayUndo");
        Check(typeof(InventorySortHelper), "CanUndo");
        Check(typeof(InventorySortHelper), "Undo");
        Check(typeof(InventorySortHelper), "Sort");
        Check(typeof(ItemContextHandler), "Stop");
    }

    private static void Check(Type type, string name)
    {
        var method = AccessTools.Method(type, name);
        Console.WriteLine(type.Name + "." + name + " -> " + (method == null ? "NOT FOUND" : "OK"));
    }
}
