using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Read-only clientele orchestration. The adapter resolves only
    /// player-visible names (StoreClientListDict / TypeHelper) and this class
    /// normalizes, clamps and caps the rows.
    /// </summary>
    public sealed class ClienteleService
    {
        private readonly IClientelePort port;

        public ClienteleService(IClientelePort port)
        {
            this.port = port;
        }

        public Clientele Read()
        {
            ClienteleRaw raw = null;
            if (port != null)
            {
                try
                {
                    raw = port.ReadClientele();
                }
                catch (Exception)
                {
                    raw = new ClienteleRaw
                    {
                        Readable = false,
                        Error = ClienteleErrors.Unavailable
                    };
                }
            }

            if (raw == null)
            {
                raw = new ClienteleRaw
                {
                    Readable = false,
                    Error = ClienteleErrors.Unavailable
                };
            }

            return ClienteleRules.Build(raw, raw.Day);
        }

        public string Json()
        {
            Clientele clientele = Read();

            var row = new JObject();
            row["ok"] = true;
            row["readable"] = clientele.Readable;
            row["day"] = clientele.Day;
            row["cached"] = clientele.Cached;
            row["error"] = clientele.Readable
                ? JValue.CreateNull()
                : new JValue(clientele.Error);

            var types = new JArray();
            for (int i = 0; i < clientele.Types.Count; i++)
            {
                var type = new JObject();
                type["id"] = clientele.Types[i].Id;
                type["name"] = clientele.Types[i].Name;
                types.Add(type);
            }

            row["showcase_types"] = types;

            var discerning = new JObject();
            discerning["chance"] = clientele.DiscerningChance;
            discerning["cooldown_openings"] = clientele.DiscerningCooldownOpenings;
            row["discerning_shopper"] = discerning;

            var predictions = new JArray();
            for (int i = 0; i < clientele.Predictions.Count; i++)
            {
                ClientelePredictionRow prediction = clientele.Predictions[i];
                var item = new JObject();
                item["client_id"] = prediction.ClientId;
                item["client_name"] = prediction.ClientName;
                item["chance"] = prediction.Chance;
                item["item_id"] = string.IsNullOrEmpty(prediction.ItemId)
                    ? JValue.CreateNull()
                    : new JValue(prediction.ItemId);
                item["item_name"] = string.IsNullOrEmpty(prediction.ItemName)
                    ? JValue.CreateNull()
                    : new JValue(prediction.ItemName);
                predictions.Add(item);
            }

            row["predictions"] = predictions;
            row["prediction_count"] = predictions.Count;
            row["normal_customer_count"] = clientele.NormalCustomerCount;

            var warnings = new JArray();
            for (int i = 0; i < clientele.Warnings.Count; i++) warnings.Add(clientele.Warnings[i]);
            row["warnings"] = warnings;

            return row.ToString(Formatting.None);
        }
    }
}
