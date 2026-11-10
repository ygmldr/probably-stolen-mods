using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class PlayerStatusService
    {
        public static string FactionsJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                List<StoreReputation> reps = PlayerStore.Instance.storeReputations;
                for (int i = 0; i < reps.Count; i++)
                {
                    StoreReputation rep = reps[i];
                    if (rep == null) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", rep.factionId },
                        { "name", rep.factionDisplay },
                        { "amount", rep.GetReputation() },
                        { "exact", Math.Round(rep.GetReputationExact(), 2) },
                        { "perks", rep.perks == null ? 0 : rep.perks.Count },
                        { "strength", rep.strength },
                        { "influence", rep.influence },
                        { "max_influence", rep.maxInfluence }
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"faction_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "factions", rows } });
        }

        public static string HealthJson()
        {
            HealthData health = null;
            try { health = PlayerStore.Instance.healthData; } catch { }
            if (health == null) return "{\"ok\":false,\"error\":\"no_health_data\"}";

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "wound_state", health.woundState },
                { "seriously_wounded", HealthRules.IsSeriouslyWounded(health.woundState) },
                { "wound_stable", health.isWoundStable },
                { "wounded_fresh", health.isWoundedFresh },
                { "luck", health.luck },
                { "liquid_luck_days", health.liquidLuckDaysLeft },
                { "days_nicotine", health.daysSinceNicotineConsumption },
                { "days_alcohol", health.daysSinceAlcoholConsumption },
                { "days_narcotic", health.daysSinceNarcoticConsumption },
                { "sentinel_days", health.daysSinceSentinelInjection }
            });
        }

        public static string RequestsJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                List<StoreRequest> requests = PlayerStore.Instance.storeRequests;
                for (int i = 0; i < requests.Count; i++)
                {
                    StoreRequest request = requests[i];
                    if (request == null) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", request.id },
                        { "title", request.title },
                        { "description", request.description },
                        { "faction", request.factionId },
                        { "required_item", request.requiredItem },
                        { "required_count", request.requiredItemCount },
                        { "credit_reward", request.creditReward },
                        { "reputation_reward", request.reputationReward },
                        { "duration", request.duration }
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"request_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "requests", rows } });
        }

        public static string WantedJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                foreach (KeyValuePair<string, int> pair in PlayerStore.Instance.wantedStatus)
                {
                    rows.Add(new Dictionary<string, object> { { "id", pair.Key }, { "status", pair.Value } });
                }

                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "visited_today", PlayerStore.Instance.wantedPersonVisitedToday },
                    { "visited_name", PlayerStore.Instance.wantedPersonVisitedName },
                    { "visited_id", PlayerStore.Instance.wantedPersonVisitedId },
                    { "wanted", rows }
                });
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"wanted_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string BarterJson()
        {
            StoreClient client = GameState.GetCurrentClient();
            if (client == null || client.barterOffer == null)
                return "{\"ok\":false,\"error\":\"no_barter_offer\"}";

            BarterOffer offer = client.barterOffer;
            var conditions = new List<Dictionary<string, object>>();
            for (int i = 0; i < offer.conditions.Count; i++)
            {
                BarterCondition condition = offer.conditions[i];
                if (condition == null) continue;
                conditions.Add(new Dictionary<string, object>
                {
                    { "required_item_id", condition.requiredItemId },
                    { "required_tag", condition.requiredItemTag },
                    { "required_display", condition.requiredDisplay },
                    { "offered_display", condition.offeredDisplay }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "client", client.displayName },
                { "text", offer.barterTextBody },
                { "acceptable", SafeBool(offer.IsBarterAcceptable) },
                { "conditions", conditions }
            });
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
