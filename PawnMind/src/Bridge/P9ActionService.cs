using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class P9ActionService
    {
        public static string DeliverRequestJson(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "{\"ok\":false,\"error\":\"missing_id\",\"dispatched\":false}";

            StoreRequest request = null;
            try
            {
                List<StoreRequest> requests = PlayerStore.Instance.storeRequests;
                for (int i = 0; i < requests.Count; i++)
                {
                    if (requests[i] != null && requests[i].id == id) { request = requests[i]; break; }
                }
            }
            catch { }

            bool fillable = false;
            try { fillable = request != null && request.CanFillRequest(); } catch { }
            RequestCheck check = RequestValidator.Validate(request != null, fillable);
            if (check != RequestCheck.Ok)
                return "{\"ok\":false,\"error\":\""
                    + (check == RequestCheck.NotFound ? "request_not_found" : "not_fillable")
                    + "\",\"dispatched\":false}";

            try
            {
                // R4/F4b: the native fill is issued from here on.
                request.FillRequest();
                return "{\"ok\":true,\"dispatched\":true,\"cash\":"
                    + GameState.GetCash() + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"deliver_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string ReportClientJson()
        {
            StoreClient client = GameState.GetCurrentClient();
            bool canReport = false;
            bool reported = false;
            try
            {
                if (client != null)
                {
                    canReport = client.CanReport();
                    reported = client.reported;
                }
            }
            catch { }

            ReportCheck check = ReportRules.Validate(client != null, canReport, reported);
            if (check != ReportCheck.Ok)
            {
                string error = check == ReportCheck.NoClient ? "no_client"
                    : check == ReportCheck.AlreadyReported ? "already_reported" : "cannot_report";
                return "{\"ok\":false,\"error\":\"" + error + "\",\"dispatched\":false}";
            }

            try
            {
                PlayerStore.Instance.OnCustomerReported();
                return "{\"ok\":true,\"dispatched\":true,\"is_wanted\":"
                    + (client.isWanted ? "true" : "false")
                    + ",\"report_reward\":" + client.reportReward + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"report_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string BarterAcceptJson()
        {
            StoreClient client = GameState.GetCurrentClient();
            BarterOffer offer = client == null ? null : client.barterOffer;
            bool acceptable = false;
            try { acceptable = offer != null && offer.IsBarterAcceptable(); } catch { }

            BarterCheck check = BarterValidator.Validate(offer != null, acceptable);
            if (check != BarterCheck.Ok)
                return "{\"ok\":false,\"error\":\""
                    + (check == BarterCheck.NoOffer ? "no_barter_offer" : "not_acceptable")
                    + "\",\"dispatched\":false}";

            try
            {
                offer.OnBarterAccepted();
                return "{\"ok\":true,\"dispatched\":true}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"barter_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string ArrestWantedJson(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "{\"ok\":false,\"error\":\"missing_id\",\"dispatched\":false}";
            try
            {
                if (!PlayerStore.Instance.wantedStatus.ContainsKey(id))
                    return "{\"ok\":false,\"error\":\"unknown_wanted\"}";
                WantedUIManager.Instance.OnArrested(id);
                return "{\"ok\":true}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"arrest_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
