using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class GoodsService
    {
        public static string ListJson(string scopeText)
        {
            string scopeId = (scopeText ?? "all").ToLowerInvariant();
            if (scopeId == "commissary") return CommissaryJson();
            if (scopeId == "jackson") return OutsideJson("dr_jackson");
            if (scopeId == "wholesale") return WholesaleJson();

            GoodsScope scope = Parse(scopeText);
            var records = new List<ItemRecord>();

            Collect("front", records);
            Collect("back", records);

            List<ItemRecord> filtered = GoodsFilter.Filter(records, scope);
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < filtered.Count; i++)
            {
                ItemRecord record = filtered[i];
                var row = new Dictionary<string, object>();
                row["id"] = record.Id;
                row["name"] = record.Name;
                row["identifier"] = record.Identifier;
                row["container"] = record.Container;
                row["value"] = record.Value;
                row["count"] = record.Count;
                row["owned"] = record.Owned;
                row["sellable"] = record.Sellable;
                rows.Add(row);
            }

            StoreClient client = GameState.GetCurrentClient();
            var clientRow = new Dictionary<string, object>();
            if (client != null)
            {
                clientRow["name"] = client.displayName;
                clientRow["intent"] = client.clientIntent.ToString();
                clientRow["budget"] = GameState.GetClientBudget(client);
                clientRow["bargain_available"] = client.isBargainAvailable;
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "scope", scope.ToString().ToLowerInvariant() },
                { "items", rows },
                { "client", client == null ? null : clientRow }
            });
        }

        private static string CommissaryJson()
        {
            List<StockItem> stock = StockFilter.InStock(GameState.GetCommissaryStock());
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < stock.Count; i++)
            {
                string identifier = stock[i].Identifier;
                GameItem template = null;
                try { template = DirectoryMaster.Item(identifier); } catch { }

                var row = new Dictionary<string, object>();
                row["identifier"] = identifier;
                row["name"] = template == null ? identifier : template.name;
                row["price"] = stock[i].Price;
                row["stock"] = stock[i].Stock;
                rows.Add(row);
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "scope", "commissary" },
                { "items", rows },
                { "redeemed_credit", PlayerStore.Instance.commissaryData.redeemedCredit },
                { "outside", GameState.IsOutside },
                { "location", GameState.CurrentLocationId }
            });
        }

        private static string OutsideJson(string requiredLocation)
        {
            if (!GameState.IsOutside || GameState.CurrentLocationId != requiredLocation)
            {
                return "{\"ok\":false,\"error\":\"not_at_location\",\"location\":\""
                    + (GameState.CurrentLocationId ?? "") + "\"}";
            }

            var records = new List<ItemRecord>();
            Collect("front", records);
            Collect("back", records);
            return RowsJson(requiredLocation, records, null);
        }

        private static string WholesaleJson()
        {
            StoreClient client = GameState.GetCurrentClient();
            if (client == null || !client.isOfferWholesale)
                return "{\"ok\":false,\"error\":\"no_wholesale_offer\"}";

            var records = new List<ItemRecord>();
            Collect("front", records);
            Collect("back", records);

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["scope"] = "wholesale";
            payload["discount"] = client.wholesaleDiscount;
            payload["wholesale_price"] = PlayerStore.Instance.GetWholesalePrice();
            payload["items"] = Rows(records);
            return JsonConvert.SerializeObject(payload);
        }

        private static string RowsJson(string scope, List<ItemRecord> records, StoreClient client)
        {
            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["scope"] = scope;
            payload["items"] = Rows(records);
            return JsonConvert.SerializeObject(payload);
        }

        private static List<Dictionary<string, object>> Rows(List<ItemRecord> records)
        {
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < records.Count; i++)
            {
                ItemRecord record = records[i];
                var row = new Dictionary<string, object>();
                row["id"] = record.Id;
                row["name"] = record.Name;
                row["identifier"] = record.Identifier;
                row["container"] = record.Container;
                row["value"] = record.Value;
                row["count"] = record.Count;
                row["owned"] = record.Owned;
                row["sellable"] = record.Sellable;
                rows.Add(row);
            }

            return rows;
        }

        private static void Collect(string container, List<ItemRecord> records)
        {
            List<GameItem> items = GameState.GetContainer(container) == null
                ? new List<GameItem>()
                : GameState.GetContainer(container).childItems;

            for (int i = 0; i < items.Count; i++)
            {
                ItemRecord record = InventoryService.Describe(container, items[i]);
                if (record != null) records.Add(record);
            }
        }

        private static GoodsScope Parse(string scope)
        {
            switch ((scope ?? "all").ToLowerInvariant())
            {
                case "client": return GoodsScope.Client;
                case "sellable": return GoodsScope.Sellable;
                default: return GoodsScope.All;
            }
        }
    }
}
