using System;
using System.Collections.Generic;
using CompactSort.Packing;

namespace CompactSort.GlobalBench
{
    internal sealed class GlobalMetrics
    {
        public int Items;
        public int BinsUsed;
        public int Holes;
        public int Components;
        public int BBoxGap;
        public int Clusters;
        public int SameItemContacts;
        public int CategoryContacts;
        public int MaxEmptyRect;
        public int ReadingOrderDisorder;
        public int SameItemBBoxWaste;
        public long Score;

        public static GlobalMetrics FromCase(BenchCase bench, PackOptions options)
        {
            var metrics = new GlobalMetrics();
            for (int i = 0; i < bench.Bins.Count; i++)
            {
                var bin = bench.Bins[i];
                if (bin.Items.Count == 0) continue;
                bin.SetOwnerKeys();
                var score = GridPacker.ScoreLayout(bin.Grid, options);
                metrics.Items += bin.Items.Count;
                metrics.BinsUsed++;
                metrics.Holes += score.IsolatedHoles;
                metrics.Components += score.EmptyComponents;
                metrics.BBoxGap += score.BBoxGap;
                metrics.Clusters += score.ItemClusters;
                metrics.SameItemContacts += score.SameItemContacts;
                metrics.CategoryContacts += score.CategoryContacts;
                metrics.MaxEmptyRect += score.MaxEmptyRect;
                metrics.ReadingOrderDisorder += score.ReadingOrderDisorder;
                metrics.SameItemBBoxWaste += score.SameItemBBoxWaste;
                metrics.Score += score.Score;
            }
            return metrics;
        }

        public bool BetterThan(GlobalMetrics other)
        {
            if (other == null) return true;
            if (Holes != other.Holes) return Holes < other.Holes;
            if (Components != other.Components) return Components < other.Components;
            if (BBoxGap != other.BBoxGap) return BBoxGap < other.BBoxGap;
            if (SameItemBBoxWaste != other.SameItemBBoxWaste)
                return SameItemBBoxWaste < other.SameItemBBoxWaste;
            if (ReadingOrderDisorder != other.ReadingOrderDisorder)
                return ReadingOrderDisorder < other.ReadingOrderDisorder;
            if (Clusters != other.Clusters) return Clusters < other.Clusters;
            if (SameItemContacts != other.SameItemContacts)
                return SameItemContacts > other.SameItemContacts;
            if (CategoryContacts != other.CategoryContacts)
                return CategoryContacts > other.CategoryContacts;
            if (MaxEmptyRect != other.MaxEmptyRect) return MaxEmptyRect > other.MaxEmptyRect;
            return Score < other.Score;
        }

        public static bool Validate(BenchCase bench, out string error)
        {
            var seen = new HashSet<BenchItem>();
            for (int i = 0; i < bench.Bins.Count; i++)
            {
                var bin = bench.Bins[i];
                if (!bin.Validate())
                {
                    error = "invalid bin " + bin.Id;
                    return false;
                }
                for (int j = 0; j < bin.Items.Count; j++)
                {
                    if (!seen.Add(bin.Items[j]))
                    {
                        error = "item assigned twice: " + bin.Items[j].Id;
                        return false;
                    }
                }
            }

            if (seen.Count != bench.Items.Count)
            {
                error = "assigned " + seen.Count + "/" + bench.Items.Count;
                return false;
            }
            for (int i = 0; i < bench.Items.Count; i++)
            {
                if (!seen.Contains(bench.Items[i]))
                {
                    error = "item missing: " + bench.Items[i].Id;
                    return false;
                }
            }
            error = null;
            return true;
        }
    }

    internal sealed class GlobalBenchResult
    {
        public string Algorithm;
        public int CaseId;
        public bool Success;
        public string Error;
        public long ElapsedMs;
        public GlobalMetrics Metrics;
    }
}
