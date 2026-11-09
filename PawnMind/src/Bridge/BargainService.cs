using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class BargainService
    {
        public static string PreviewJson(string direction, int percent)
        {
            if (percent != 10 && percent != 25 && percent != 50 && percent != 75)
                return "{\"ok\":false,\"error\":\"bad_percent\"}";

            BargainUIManager ui = BargainUIManager.Instance;
            StoreClient client = GameState.GetCurrentClient();
            if (ui == null) return "{\"ok\":false,\"error\":\"no_bargain_ui\"}";
            if (client == null) return "{\"ok\":false,\"error\":\"no_client\"}";

            BargainType type;
            bool markup;
            bool friendly;
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "sell_discount": type = BargainType.Discount; markup = false; friendly = true; break;
                case "sell_markup": type = BargainType.Markup; markup = true; friendly = false; break;
                case "buy_markup": type = BargainType.BuyingMarkup; markup = true; friendly = true; break;
                case "buy_discount": type = BargainType.BuyingDiscount; markup = false; friendly = false; break;
                default: return "{\"ok\":false,\"error\":\"bad_direction\"}";
            }

            GameItem item = GameState.GetNegotiatedItem() ?? GameState.GetFirstClientItem();
            int value = item == null ? 0 : (int)item.GetNegociatedValue();
            int pre = item == null ? 0 : BargainUIManager.GetPreBargainValue(item, value);

            int success = ui.GetBargainSuccessChance(type, percent);
            int leave = ui.GetBargainLeaveChance(type, percent);
            int refuse = Math.Max(0, 100 - success - leave);
            double factionFactor = FactionFactor(client.clientFaction);
            int rep = pre == 0 ? 0 : BargainMath.ReputationDelta(pre, friendly, percent, factionFactor);

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "direction", direction },
                { "percent", percent },
                { "type", type.ToString() },
                { "success_chance", success },
                { "leave_chance", leave },
                { "refuse_chance", refuse },
                { "price_before", value },
                { "price_after", pre == 0 ? 0 : BargainMath.AdjustPrice(pre, markup, percent) },
                { "rep_multiplier", BargainMath.ReputationMultiplier(friendly, percent) },
                { "rep_per_1000", Math.Round(BargainMath.ReputationPer1000(BargainMath.ReputationMultiplier(friendly, percent), factionFactor), 2) },
                { "rep_estimate", rep },
                { "rep_estimate_exact", Math.Round(pre * BargainMath.ReputationMultiplier(friendly, percent) * factionFactor / 400.0, 2) },
                { "faction", client.clientFaction },
                { "faction_factor", factionFactor },
                { "note", "estimate assumes the offer is accepted; values/difficulty perks may change the result" }
            });
        }

        private static double FactionFactor(string faction)
        {
            if (faction == "FACTION_LOWER_LEVEL") return 0.66;
            if (faction == "FACTION_UPPER_LEVEL") return 1.2;
            return 1.0;
        }
    }
}
