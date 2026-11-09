using System;
using System.IO;
using System.Linq;

internal static class Program
{
    private static void Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : @"D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\CompactInventorySort";
        string dll = System.IO.Path.Combine(folder, "CompactInventorySort.dll");
        string managed = @"D:\Program Files\steam\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed";
        var allowed = System.IO.Directory.GetFiles(managed, "*.dll")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Concat(new[] { "CompactInventorySort" })
            .ToArray();
        var result = ModScanner.Scan(new[] { dll }, allowed, folder, true);
        Console.WriteLine("error=" + (result.Error ?? "none"));
        Console.WriteLine("refused=" + result.Refused);
        Console.WriteLine("caps=" + string.Join(",", result.Capabilities));
        Console.WriteLine("methods=" + result.MethodsScanned);
        Console.WriteLine("hard=" + result.HardFlags.Count + " findings=" + result.Findings.Count);
        Console.WriteLine(result.FormatFindings(20));

        var asm = System.Reflection.Assembly.LoadFrom(dll);
        var modType = asm.GetTypes().FirstOrDefault(t => typeof(IMod).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
        Console.WriteLine("IMod type: " + (modType == null ? "NULL" : modType.FullName));
    }
}
