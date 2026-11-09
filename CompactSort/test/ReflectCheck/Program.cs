using System;
using System.Reflection;

internal static class Program
{
    private static void Main()
    {
        var f = typeof(InventorySortHelper).GetField("undoSnapshots",
            BindingFlags.NonPublic | BindingFlags.Static);
        Console.WriteLine("undo field: " + (f == null ? "NULL" : f.FieldType.FullName));
        var sort = typeof(InventorySortHelper).GetMethod("Sort");
        Console.WriteLine("sort: " + sort);
        Console.WriteLine("canSort: " + typeof(InventorySortHelper).GetMethod("CanSort"));
        Console.WriteLine("maySort: " + typeof(InventorySortHelper).GetMethod("MaySort"));
        var b = typeof(GridShapeBuilder);
        Console.WriteLine("setTransform: " + b.GetMethod("SetTransform", new[] { typeof(GridShape) }));
    }
}
