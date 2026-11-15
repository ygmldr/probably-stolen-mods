using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Faction perk projection for list_factions. The existing response keeps
    /// the perk count; perks_detail adds the threshold bookkeeping the
    /// ReputationUIManager shows.
    /// </summary>
    public sealed class FactionPerkRaw
    {
        public string Id;
        public int Threshold;
        public bool Unlocked;
        public int Type;
        public string Icon;
        public bool AlwaysActive;
    }

    public sealed class FactionPerkView
    {
        public string Id;
        public int Threshold;
        public bool Unlocked;
        // Literal max(0, threshold - reputation), kept for the documented
        // response contract.
        public int Remaining;
        // Direction-aware distance. Negative thresholds unlock by losing
        // reputation, where the literal formula above would read 0 while the
        // perk is still locked (e.g. LLAVOIDED at -80).
        public int RemainingToUnlock;
        public string Direction;
        public int Type;
        public string Icon;
        public bool AlwaysActive;
    }

    public static class FactionPerkRules
    {
        public const int MaxPerks = 64;

        public static int Remaining(int threshold, int reputation)
        {
            int remaining = threshold - reputation;
            return remaining < 0 ? 0 : remaining;
        }

        public static int RemainingToUnlock(int threshold, int reputation)
        {
            int distance = threshold >= 0
                ? threshold - reputation
                : reputation - threshold;
            return distance < 0 ? 0 : distance;
        }

        public static string DirectionFor(int threshold)
        {
            return threshold < 0 ? "at_or_below" : "at_or_above";
        }

        public static List<FactionPerkView> Build(List<FactionPerkRaw> perks, int reputation)
        {
            var rows = new List<FactionPerkView>();
            if (perks == null) return rows;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < perks.Count; i++)
            {
                FactionPerkRaw perk = perks[i];
                if (perk == null || string.IsNullOrEmpty(perk.Id) || perk.Id.Trim().Length == 0) continue;
                if (!seen.Add(perk.Id)) continue;

                rows.Add(new FactionPerkView
                {
                    Id = perk.Id,
                    Threshold = perk.Threshold,
                    Unlocked = perk.Unlocked,
                    Remaining = Remaining(perk.Threshold, reputation),
                    RemainingToUnlock = RemainingToUnlock(perk.Threshold, reputation),
                    Direction = DirectionFor(perk.Threshold),
                    Type = perk.Type,
                    Icon = perk.Icon,
                    AlwaysActive = perk.AlwaysActive
                });
            }

            rows.Sort(Compare);
            if (rows.Count > MaxPerks) rows.RemoveRange(MaxPerks, rows.Count - MaxPerks);
            return rows;
        }

        private static int Compare(FactionPerkView left, FactionPerkView right)
        {
            int byThreshold = left.Threshold.CompareTo(right.Threshold);
            if (byThreshold != 0) return byThreshold;
            return string.CompareOrdinal(left.Id, right.Id);
        }
    }
}
