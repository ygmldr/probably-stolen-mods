using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class LotteryService
    {
        public static string StatusJson()
        {
            bool unlocked = SafeBool(LotteryHelper.IsLotteryUnlocked);
            int edition = 0;
            int day = 0;
            try { edition = LotteryHelper.GetCurrentEdition(); } catch { }
            try { day = GameState.GetDay(); } catch { }

            int drawIn = unlocked ? ((5 - day % 7) + 7) % 7 : -1;
            var prizes = new List<Dictionary<string, object>>();
            for (int m = 2; m <= 6; m++)
            {
                prizes.Add(new Dictionary<string, object>
                {
                    { "matches", m },
                    { "prize", LotteryPrizeTable.ForMatches(m) }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "unlocked", unlocked },
                { "edition", edition },
                { "day", day },
                { "draw_in_days", drawIn },
                { "prizes", prizes }
            });
        }

        public static string TicketsJson()
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
                    if (item == null || !SafeTag(item, "LOTTERY_TICKET_TAG")) continue;
                    rows.Add(Describe(item, containers[c]));
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "count", rows.Count },
                { "tickets", rows }
            });
        }

        public static string InspectJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";
            if (!SafeTag(item, "LOTTERY_TICKET_TAG")) return "{\"ok\":false,\"error\":\"not_a_ticket\"}";
            return JsonConvert.SerializeObject(Describe(item, ContainerOf(item)));
        }

        public static string RedeemJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";
            if (!SafeTag(item, "LOTTERY_TICKET_TAG")) return "{\"ok\":false,\"error\":\"not_a_ticket\"}";
            if (!SafeBool(delegate { return LotteryHelper.CanRedeemPrize(item); }))
                return "{\"ok\":false,\"error\":\"cannot_redeem\"}";

            try
            {
                LotteryHelper.TryRedeemPrize(item);
                return "{\"ok\":true,\"cash\":" + GameState.GetCash() + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"redeem_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static Dictionary<string, object> Describe(GameItem item, string container)
        {
            int matches = TagInt(item, "LOTTERY_MATCH_COUNT_TAG");
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "id", item.GetUniqueID() },
                { "name", item.name },
                { "container", container },
                { "number", TagString(item, "LOTTERY_NUMBER_TAG") },
                { "edition", TagInt(item, "LOTTERY_EDITION_TAG") },
                { "winning_number", TagString(item, "LOTTERY_WINNING_NUMBER_TAG") },
                { "matches", matches },
                { "prize", LotteryPrizeTable.ForMatches(matches) },
                { "can_redeem", SafeBool(delegate { return LotteryHelper.CanRedeemPrize(item); }) }
            };
        }

        private static string ContainerOf(GameItem item)
        {
            List<string> ids = GameState.GetContainerIds();
            for (int i = 0; i < ids.Count; i++)
            {
                GameInventory inventory = GameState.GetContainer(ids[i]);
                if (inventory == null) continue;
                if (inventory.childItems.Contains(item)) return ids[i];
            }

            return "";
        }

        private static string TagString(GameItem item, string tag)
        {
            try { return item.GetTagReadonly(tag).GetString() ?? ""; }
            catch { return ""; }
        }

        private static int TagInt(GameItem item, string tag)
        {
            try { return item.GetTagReadonly(tag).GetInt(); }
            catch { return 0; }
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); }
            catch { return false; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
