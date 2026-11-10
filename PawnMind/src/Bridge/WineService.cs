using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class WineService
    {
        public static string StatusJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["id"] = itemId;
            payload["name"] = item.name;
            payload["identifier"] = item.identifier;
            payload["fermenting_or_finished"] = SafeBool(delegate { return WineHelper.IsFermentingOrFinished(item); });
            payload["finished"] = SafeBool(delegate { return WineHelper.IsFinishedWine(item); });
            payload["can_modify"] = SafeBool(delegate { return WineHelper.CanModifyLiquid(item); });
            payload["quality_tier"] = SafeInt(delegate { return WineHelper.GetWineQualityTier(item); });
            payload["water_quality"] = SafeInt(delegate { return WineHelper.GetWaterQualityScore(item); });
            payload["extract_per_mille"] = SafeInt(delegate { return WineHelper.GetWineExtractPerMille(item); });
            payload["contaminant_per_mille"] = SafeInt(delegate { return WineHelper.GetWineContaminantPerMille(item); });
            return JsonConvert.SerializeObject(payload);
        }

        public static string StartJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";
            try
            {
                bool started = WineHelper.TryStartFermentation(item);
                return "{\"ok\":" + (started ? "true" : "false") + ",\"started\":" + (started ? "true" : "false") + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"fermentation_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string ListJson()
        {
            var rows = new List<Dictionary<string, object>>();
            List<string> containers = GameState.GetContainerIds();
            for (int c = 0; c < containers.Count; c++)
            {
                GameInventory inventory = GameState.GetContainer(containers[c]);
                if (inventory == null) continue;
                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null || !SafeTag(item, "WINE_CONTAINER_TAG")) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "container", containers[c] },
                        { "finished", SafeBool(delegate { return WineHelper.IsFinishedWine(item); }) }
                    });
                }
            }
            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "items", rows } });
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); } catch { return false; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        private static int SafeInt(Func<int> read)
        {
            try { return read(); } catch { return 0; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
