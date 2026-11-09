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
