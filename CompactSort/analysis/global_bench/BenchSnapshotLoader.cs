using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CompactSort.Packing;

namespace CompactSort.GlobalBench
{
    internal static class BenchSnapshotLoader
    {
        public static List<BenchCase> Load(string path)
        {
            var files = new List<string>();
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*.json"));
                files.Sort(StringComparer.Ordinal);
            }
            else
            {
                files.Add(path);
            }

            var cases = new List<BenchCase>();
            for (int i = 0; i < files.Count; i++)
            {
                var bench = LoadFile(files[i]);
                if (bench != null) cases.Add(bench);
            }
            return cases;
        }

        private static BenchCase LoadFile(string path)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                var bench = new BenchCase();
                if (root.TryGetProperty("day", out var day)) bench.Seed = day.GetInt32();
                if (!root.TryGetProperty("containers", out var containers) ||
                    containers.ValueKind != JsonValueKind.Array)
                    return null;

                int nextItemId = 0;
                int containerIndex = 0;
                foreach (var container in containers.EnumerateArray())
                {
                    int width = container.GetProperty("width").GetInt32();
                    int height = container.GetProperty("height").GetInt32();
                    var wall = ParseMask(container.TryGetProperty("wall", out var wallElement)
                        ? wallElement.GetString() : null, width, height);
                    var bin = new BenchBin(containerIndex, width, height, wall);
                    if (container.TryGetProperty("kind", out var kind) &&
                        kind.ValueKind == JsonValueKind.String)
                        bin.Kind = kind.GetString();

                    if (container.TryGetProperty("items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var itemElement in items.EnumerateArray())
                        {
                            var item = ParseItem(itemElement, ref nextItemId);
                            if (item == null) continue;
                            bool anchored = itemElement.TryGetProperty("anchored", out var anchoredElement) &&
                                            anchoredElement.ValueKind == JsonValueKind.True;
                            if (anchored)
                            {
                                int x = GetInt(itemElement, "x", 0);
                                int y = GetInt(itemElement, "y", 0);
                                int orientation = GetInt(itemElement, "orientation", 0);
                                bin.AddAnchor(item, x, y, orientation);
                            }
                            else
                            {
                                item.OriginalBin = containerIndex;
                                bench.Items.Add(item);
                            }
                        }
                    }

                    bench.Bins.Add(bin);
                    containerIndex++;
                }
                return bench;
            }
        }

        private static BenchItem ParseItem(JsonElement element, ref int nextId)
        {
            if (!element.TryGetProperty("width", out var wElement) ||
                !element.TryGetProperty("height", out var hElement))
                return null;
            int width = wElement.GetInt32();
            int height = hElement.GetInt32();
            if (width <= 0 || height <= 0) return null;

            string maskText = element.TryGetProperty("mask", out var maskElement)
                ? maskElement.GetString()
                : null;
            var mask = ParseMaskByte(maskText, width, height);
            int area = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (mask[x, y] != 0) area++;

            string key = element.TryGetProperty("key", out var keyElement)
                ? keyElement.GetString()
                : (element.TryGetProperty("identifier", out var idElement) ? idElement.GetString() : "");
            string category = element.TryGetProperty("category", out var catElement)
                ? catElement.GetString()
                : "";
            int id = element.TryGetProperty("id", out var idValue)
                ? idValue.GetInt32()
                : nextId;
            nextId = Math.Max(nextId, id + 1);

            return new BenchItem
            {
                Id = id,
                Key = key ?? "",
                Category = category ?? "",
                Width = width,
                Height = height,
                Mask = mask,
                Area = Math.Max(1, area)
            };
        }

        private static bool[] ParseMask(string text, int width, int height)
        {
            var result = new bool[width * height];
            if (string.IsNullOrEmpty(text)) return result;
            for (int i = 0; i < result.Length && i < text.Length; i++)
                result[i] = text[i] != '0';
            return result;
        }

        private static byte[,] ParseMaskByte(string text, int width, int height)
        {
            var result = new byte[width, height];
            if (string.IsNullOrEmpty(text)) return result;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    result[x, y] = i < text.Length && text[i] != '0' ? (byte)1 : (byte)0;
                }
            }
            return result;
        }

        private static int GetInt(JsonElement element, string name, int fallback)
        {
            return element.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
        }
    }
}
