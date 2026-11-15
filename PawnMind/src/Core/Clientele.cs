using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Clientele projection. Mirrors what ClienteleUIManager.RefreshUI shows:
    /// the display-case types that attract buyers, the discerning-shopper chance
    /// and cooldown, ShowcaseHelper.GetDisplayCasePrediction (clientId, chance %,
    /// itemId), and the number of random normal customers.
    /// </summary>
    public static class ClienteleErrors
    {
        public const string Unavailable = "clientele_unavailable";
    }

    public sealed class ClienteleTypeRow
    {
        public string Id;
        public string Name;
    }

    public sealed class ClientelePredictionRow
    {
        public string ClientId;
        public string ClientName;
        public int Chance;
        public string ItemId;
        public string ItemName;
    }

    public sealed class ClienteleRaw
    {
        public bool Readable;
        public string Error;
        public int Day;
        public List<ClienteleTypeRow> Types = new List<ClienteleTypeRow>();
        public int DiscerningChance;
        public int DiscerningCooldownOpenings;
        public List<ClientelePredictionRow> Predictions = new List<ClientelePredictionRow>();
        public int NormalCustomerCount;
        public List<string> Warnings = new List<string>();

        /// <summary>True when this reading came from the same-day cache.</summary>
        public bool Cached;
    }

    public sealed class Clientele
    {
        public bool Readable;
        public string Error;
        public int Day;
        public List<ClienteleTypeRow> Types = new List<ClienteleTypeRow>();
        public int DiscerningChance;
        public int DiscerningCooldownOpenings;
        public List<ClientelePredictionRow> Predictions = new List<ClientelePredictionRow>();
        public int NormalCustomerCount;
        public List<string> Warnings = new List<string>();

        /// <summary>True when this reading came from the same-day cache.</summary>
        public bool Cached;
    }

    public interface IClientelePort
    {
        /// <summary>Read-only; names are resolved from the visible id dictionaries.</summary>
        ClienteleRaw ReadClientele();
    }

    public static class ClienteleRules
    {
        public const int MaxTypes = 12;

        /// <summary>
        /// Reading clientele is not free: GetNormalCustomerCount rolls RNG and
        /// GetDisplayCasePrediction caches a random supplier pick. Reuse one
        /// snapshot per game day so repeated observation cannot keep advancing
        /// the RNG stream.
        /// </summary>
        public static bool ShouldReuse(bool hasCache, int cachedDay, int currentDay)
        {
            return hasCache && cachedDay == currentDay;
        }
        public const int MaxPredictions = 24;
        public const int MaxWarnings = 8;

        public static int ClampChance(int value)
        {
            if (value < 0) return 0;
            return value > 100 ? 100 : value;
        }

        public static Clientele Build(ClienteleRaw raw, int day)
        {
            if (raw == null) return Unreadable(day, ClienteleErrors.Unavailable);
            if (!raw.Readable)
            {
                return Unreadable(day, string.IsNullOrEmpty(raw.Error)
                    ? ClienteleErrors.Unavailable
                    : raw.Error);
            }

            return new Clientele
            {
                Readable = true,
                Day = day,
                Types = CleanTypes(raw.Types),
                DiscerningChance = ClampChance(raw.DiscerningChance),
                DiscerningCooldownOpenings = raw.DiscerningCooldownOpenings < 0
                    ? 0
                    : raw.DiscerningCooldownOpenings,
                Predictions = CleanPredictions(raw.Predictions),
                NormalCustomerCount = raw.NormalCustomerCount < 0 ? 0 : raw.NormalCustomerCount,
                Warnings = CleanWarnings(raw.Warnings),
                Cached = raw.Cached
            };
        }

        private static List<ClienteleTypeRow> CleanTypes(List<ClienteleTypeRow> raw)
        {
            var rows = new List<ClienteleTypeRow>();
            if (raw == null) return rows;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < raw.Count && rows.Count < MaxTypes; i++)
            {
                ClienteleTypeRow row = raw[i];
                if (row == null || string.IsNullOrEmpty(row.Id) || row.Id.Trim().Length == 0) continue;
                if (!seen.Add(row.Id)) continue;

                rows.Add(new ClienteleTypeRow
                {
                    Id = row.Id,
                    Name = string.IsNullOrEmpty(row.Name) ? row.Id : row.Name
                });
            }

            return rows;
        }

        private static List<ClientelePredictionRow> CleanPredictions(
            List<ClientelePredictionRow> raw)
        {
            var rows = new List<ClientelePredictionRow>();
            if (raw == null) return rows;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < raw.Count; i++)
            {
                ClientelePredictionRow row = raw[i];
                if (row == null
                    || string.IsNullOrEmpty(row.ClientId)
                    || row.ClientId.Trim().Length == 0)
                {
                    continue;
                }

                string itemId = row.ItemId ?? string.Empty;
                string key = row.ClientId + "\n" + itemId + "\n" + row.Chance;
                if (!seen.Add(key)) continue;

                rows.Add(new ClientelePredictionRow
                {
                    ClientId = row.ClientId,
                    ClientName = string.IsNullOrEmpty(row.ClientName) ? row.ClientId : row.ClientName,
                    Chance = ClampChance(row.Chance),
                    ItemId = itemId,
                    ItemName = string.IsNullOrEmpty(row.ItemName)
                        ? (itemId.Length == 0 ? null : itemId)
                        : row.ItemName
                });
            }

            rows.Sort(ComparePredictions);
            if (rows.Count > MaxPredictions) rows.RemoveRange(MaxPredictions, rows.Count - MaxPredictions);
            return rows;
        }

        private static int ComparePredictions(ClientelePredictionRow left, ClientelePredictionRow right)
        {
            int byChance = right.Chance.CompareTo(left.Chance);
            if (byChance != 0) return byChance;

            int byClient = string.CompareOrdinal(left.ClientId, right.ClientId);
            if (byClient != 0) return byClient;

            return string.CompareOrdinal(left.ItemId, right.ItemId);
        }

        private static List<string> CleanWarnings(List<string> raw)
        {
            var warnings = new List<string>();
            if (raw == null) return warnings;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < raw.Count && warnings.Count < MaxWarnings; i++)
            {
                string warning = raw[i];
                if (string.IsNullOrEmpty(warning) || warning.Trim().Length == 0) continue;
                if (!seen.Add(warning)) continue;
                warnings.Add(warning);
            }

            return warnings;
        }

        private static Clientele Unreadable(int day, string error)
        {
            return new Clientele
            {
                Readable = false,
                Error = string.IsNullOrEmpty(error) ? ClienteleErrors.Unavailable : error,
                Day = day
            };
        }
    }
}
