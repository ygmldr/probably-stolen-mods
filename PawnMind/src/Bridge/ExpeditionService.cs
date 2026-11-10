using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class ExpeditionService
    {
        public static string UnlockJson(bool allow)
        {
            if (!allow) return "{\"ok\":false,\"error\":\"test_items_disabled\"}";
            try
            {
                ExpeditionData data = PlayerStore.Instance.expeditionData;
                data.UnlockExpedition();
                return "{\"ok\":true,\"hirelings\":" + data.hirelings.Count + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"unlock_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string DispatchJson(string hirelingId, string locationId)
        {
            ExpeditionData data = PlayerStore.Instance.expeditionData;
            if (data == null) return "{\"ok\":false,\"error\":\"no_expedition_data\"}";
            if (!data.isExpeditionUnlocked) return "{\"ok\":false,\"error\":\"expedition_locked\"}";

            Hireling hireling = null;
            for (int i = 0; i < data.hirelings.Count; i++)
            {
                if (data.hirelings[i] != null && data.hirelings[i].id == hirelingId) { hireling = data.hirelings[i]; break; }
            }
            if (hireling == null) return "{\"ok\":false,\"error\":\"hireling_not_found\"}";
            if (SafeInjured(hireling)) return "{\"ok\":false,\"error\":\"hireling_injured\"}";

            ExpeditionLocation location = null;
            for (int i = 0; i < data.expeditionLocations.Count; i++)
            {
                ExpeditionLocation candidate = data.expeditionLocations[i];
                if (candidate != null && candidate.id == locationId) { location = candidate; break; }
            }
            if (location == null) return "{\"ok\":false,\"error\":\"location_not_found\"}";
            if (!location.isUnlocked) return "{\"ok\":false,\"error\":\"location_locked\"}";

            try
            {
                data.CreateExpedition(hireling, location);
                Expedition expedition = data.ongoingExpeditions[data.ongoingExpeditions.Count - 1];
                if (string.IsNullOrEmpty(expedition.id)) expedition.id = "exp_" + DateTime.UtcNow.Ticks;
                expedition.StartExpedition();
                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "id", expedition.id },
                    { "location", location.id },
                    { "turn", expedition.turn }
                });
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"dispatch_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string AdvanceJson(string expeditionId)
        {
            ExpeditionData data = PlayerStore.Instance.expeditionData;
            if (data == null) return "{\"ok\":false,\"error\":\"no_expedition_data\"}";

            Expedition expedition = null;
            for (int i = 0; i < data.ongoingExpeditions.Count; i++)
            {
                if (data.ongoingExpeditions[i] != null && data.ongoingExpeditions[i].id == expeditionId)
                {
                    expedition = data.ongoingExpeditions[i];
                    break;
                }
            }
            if (expedition == null) return "{\"ok\":false,\"error\":\"expedition_not_found\"}";

            try
            {
                bool canContinue = expedition.CanContinue();
                if (canContinue) expedition.ContinueExpedition();
                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "can_continue", expedition.CanContinue() },
                    { "turn", expedition.turn },
                    { "logs", expedition.logs == null ? 0 : expedition.logs.Count }
                });
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"advance_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static bool SafeInjured(Hireling hireling)
        {
            try { return hireling.IsInjured(); } catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
