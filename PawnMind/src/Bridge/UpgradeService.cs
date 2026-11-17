using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class UpgradeService
    {
        public static string ListJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                foreach (KeyValuePair<string, NetworkUpgrade> pair in PlayerStore.Instance.networkUpgrade)
                {
                    NetworkUpgrade upgrade = pair.Value;
                    if (upgrade == null) continue;

                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", pair.Key },
                        { "cost", upgrade.cost },
                        { "state", upgrade.state },
                        { "unlocked", SafeBool(upgrade.IsUnlocked) },
                        { "prerequisite", upgrade.prerequisite == null || upgrade.prerequisite.Count == 0 },
                        { "title", upgrade.subtitle ?? pair.Key }
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"upgrade_list_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "cash", GameState.GetCash() },
                { "upgrades", rows }
            });
        }

        public static string BuyJson(string id)
        {
            if (string.IsNullOrEmpty(id)) return "{\"ok\":false,\"error\":\"missing_id\",\"dispatched\":false}";

            NetworkUpgrade upgrade;
            if (!PlayerStore.Instance.networkUpgrade.TryGetValue(id, out upgrade) || upgrade == null)
                return "{\"ok\":false,\"error\":\"unknown_upgrade\",\"dispatched\":false}";

            bool unlocked = SafeBool(upgrade.IsUnlocked);
            bool prerequisite = SafeBool(upgrade.IsHavePrerequisite);
            bool enoughCash = GameState.GetCash() >= upgrade.cost;
            UpgradeCheck check = UpgradeValidator.Validate(true, unlocked, prerequisite, enoughCash);
            if (check != UpgradeCheck.Ok) return "{\"ok\":false,\"error\":\"" + ErrorCode(check)
                + "\",\"dispatched\":false}";

            try
            {
                PlayerStore.Instance.playerCash -= upgrade.cost;
                upgrade.Unlock();
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"upgrade_failed\",\"dispatched\":true,\"detail\":\""
                    + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "dispatched", true },
                { "id", id },
                { "cost", upgrade.cost },
                { "cash", GameState.GetCash() }
            });
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static string ErrorCode(UpgradeCheck check)
        {
            switch (check)
            {
                case UpgradeCheck.NotFound: return "unknown_upgrade";
                case UpgradeCheck.AlreadyUnlocked: return "already_unlocked";
                case UpgradeCheck.Prerequisite: return "prerequisite_missing";
                case UpgradeCheck.NoCash: return "cannot_afford";
                default: return "ok";
            }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
