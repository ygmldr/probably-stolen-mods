using System;

internal static class Program
{
    private static void Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : @"D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\CompactInventorySort";
        string problem;
        var manifest = ModLoader.ParseManifestForTools(folder, out problem);
        Console.WriteLine(problem ?? "OK");
        if (manifest == null) return;
        Console.WriteLine("ID=" + manifest.ID);
        Console.WriteLine("Name=" + manifest.Name);
        Console.WriteLine("Version=" + manifest.ModVersion);
        Console.WriteLine("Caps=" + string.Join(",", manifest.Capabilities));
        Console.WriteLine("Assemblies=" + string.Join(",", manifest.Assemblies));
    }
}
