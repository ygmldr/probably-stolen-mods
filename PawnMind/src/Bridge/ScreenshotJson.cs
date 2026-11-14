using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>JSON envelopes for capture_screenshot and list_screenshots.</summary>
    public static class ScreenshotJson
    {
        public static string CaptureResult(ScreenshotRequest request, long revision)
        {
            var payload = new JObject();

            if (request == null || !request.Accepted || request.Entry == null)
            {
                payload["ok"] = false;
                payload["error"] = request == null || string.IsNullOrEmpty(request.Error)
                    ? "screenshot_unavailable"
                    : request.Error;
                payload["rev"] = revision;
                return payload.ToString(Formatting.None);
            }

            ScreenshotEntry entry = request.Entry;
            payload["ok"] = true;
            payload["path"] = entry.Path;
            payload["reason"] = entry.Reason;
            payload["op"] = entry.Op;
            payload["outcome"] = entry.Outcome;
            payload["rev"] = revision;
            payload["day"] = entry.Day;
            payload["cash"] = entry.Cash;
            payload["timestamp"] = entry.Timestamp;
            return payload.ToString(Formatting.None);
        }

        public static string ListResult(ScreenshotLedger ledger, int limit)
        {
            if (ledger == null)
            {
                return "{\"ok\":false,\"error\":\"screenshot_unavailable\"}";
            }

            ScreenshotStatus status = ledger.Status;
            var payload = new JObject();
            payload["ok"] = true;
            payload["total_count"] = status.TotalCount;
            payload["total_bytes"] = status.TotalBytes;
            payload["budget_bytes"] = status.BudgetBytes;
            payload["dropped"] = status.Dropped;
            payload["budget_exhausted"] = status.BudgetExhausted;
            payload["last_path"] = ledger.LastPath;
            payload["last_error"] = ledger.LastError;

            var rows = new JArray();
            IReadOnlyList<ScreenshotEntry> entries = ledger.Recent(limit);
            for (int i = 0; i < entries.Count; i++)
            {
                ScreenshotEntry entry = entries[i];
                var row = new JObject();
                row["path"] = entry.Path;
                row["reason"] = entry.Reason;
                row["op"] = entry.Op;
                row["outcome"] = entry.Outcome;
                row["error"] = entry.Error == null ? JValue.CreateNull() : new JValue(entry.Error);
                row["rev"] = entry.Rev;
                row["day"] = entry.Day;
                row["cash"] = entry.Cash;
                row["timestamp"] = entry.Timestamp;
                rows.Add(row);
            }

            payload["screenshots"] = rows;
            return payload.ToString(Formatting.None);
        }
    }
}
