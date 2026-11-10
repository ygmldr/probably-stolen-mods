using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    /// <summary>Test-only item grants, gated by allow_test_items=true in bridge.conf.</summary>
    internal static class DebugService
    {
        public static string GrantJson(bool allow, string identifier, int count)
        {
            if (!allow) return "{\"ok\":false,\"error\":\"test_items_disabled\"}";
            if (string.IsNullOrEmpty(identifier)) return "{\"ok\":false,\"error\":\"missing_identifier\"}";
            if (count < 1) count = 1;
            if (count > 10) count = 10;

            var granted = new List<string>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    GameItem item = Create(identifier);
                    if (item == null) return "{\"ok\":false,\"error\":\"unknown_item\"}";
                    GeneralHelper.SetItemOwned(item, true);
                    GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, item);
                    if (item.parentInventory != null) granted.Add(identifier);
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"grant_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "identifier", identifier },
                { "granted", granted.Count },
                { "requested", count }
            });
        }

        public static string FillWaterJson(bool allow, int itemId)
        {
            if (!allow) return "{\"ok\":false,\"error\":\"test_items_disabled\"}";
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";
            try
            {
                WaterHelper.FillWithPureWater(item);
                return "{\"ok\":true,\"ml\":" + WaterHelper.GetCurrentCapacityML(item) + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"fill_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static GameItem Create(string identifier)
        {
            if (identifier == "fish_tilapia") return AquacultureItems.Tilapia();
            if (identifier == "fish_eel") return AquacultureItems.Eel();
            return DirectoryMaster.Item(identifier);
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
