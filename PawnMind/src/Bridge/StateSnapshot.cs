using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class StateSnapshot
    {
        private static readonly RevisionTracker Revision = new RevisionTracker();

        public static long CurrentRevision
        {
            get { return Revision.Revision; }
        }

        public static string BuildJson()
        {
            Dictionary<string, object> state = BuildState();

            string fingerprint = JsonConvert.SerializeObject(state);
            Revision.Observe(fingerprint);

            state["rev"] = Revision.Revision;
            state["timestamp"] = DateTime.UtcNow.ToString("o");

            return JsonConvert.SerializeObject(state);
        }

        private static Dictionary<string, object> BuildState()
        {
            var state = new Dictionary<string, object>();
            bool inGame = GameState.IsInGame;
            state["in_game"] = inGame;

            GameFlags flags = BuildFlags();
            state["legal_actions"] = DescribeLegalActions(LegalActionBuilder.Build(flags));

            if (!inGame)
            {
                state["client"] = null;
                state["negotiated_item"] = null;
                state["run_summary"] = null;
                return state;
            }

            state["day"] = GameState.GetDay();
            state["shop_state"] = GameState.GetShopState();
            state["outside"] = GameState.IsOutside;
            state["location"] = GameState.CurrentLocationId;
            state["afterhour_count"] = GameState.GetAfterhourItemCount();
            state["cash"] = GameState.GetCash();
            state["rent_days_left"] = GameState.GetRentDaysLeft();
            state["rent_value"] = GameState.GetRentValue();
            state["starting_day"] = GameState.GetStartingDay();
            state["client_arrived"] = GameState.IsClientArrived();

            System.Collections.Generic.List<GameItem> backItems = GameState.GetBackCounterItems();
            state["back_counter_count"] = backItems.Count;
            state["back_counter"] = DescribeItems(backItems);

            System.Collections.Generic.List<GameItem> frontItems = GameState.GetFrontCounterItems();
            state["front_counter_count"] = frontItems.Count;
            state["front_counter"] = DescribeItems(frontItems);

            System.Collections.Generic.List<GameItem> clientItems = GameState.GetClientItems();
            state["client_item_count"] = clientItems.Count;
            state["client_items"] = DescribeItems(clientItems);
            state["negotiation_open"] = GameState.IsNegotiationOpen();
            state["bargain"] = GameState.GetBargainSnapshot();

            System.Collections.Generic.List<GameItem> inventory = GameState.GetInventoryItems();
            state["inventory_count"] = inventory.Count;
            state["inventory"] = DescribeItems(inventory);
            state["can_call_next"] = GameState.GetCanNextClient();
            state["can_end_day"] = GameState.GetCanEndDay();
            Dictionary<string, object> dialogue = GameState.GetDialogueSnapshot();
            if (dialogue != null)
            {
                dialogue["choices"] = GameState.GetDialogueChoices();
            }

            state["dialogue"] = dialogue;


            StoreClient client = GameState.GetCurrentClient();
            state["client"] = client == null ? null : DescribeClient(client);

            GameItem item = GameState.GetNegotiatedItem();
            state["negotiated_item"] = item == null ? null : DescribeItem(item);

            state["run_summary"] = BuildRunSummary(state);
            return state;
        }

        private static GameFlags BuildFlags()
        {
            var flags = new GameFlags();
            flags.InGame = GameState.IsInGame;
            if (!flags.InGame) return flags;

            flags.ShopState = GameState.GetShopState();
            flags.CanCallNext = GameState.GetCanNextClient();
            flags.CanEndDay = GameState.GetCanEndDay();

            StoreClient client = GameState.GetCurrentClient();
            flags.HasClient = client != null;
            flags.ClientArrived = GameState.IsClientArrived();

            Dictionary<string, object> dialogue = GameState.GetDialogueSnapshot();
            if (dialogue != null)
            {
                bool ended = dialogue.ContainsKey("ended") && dialogue["ended"] is bool && (bool)dialogue["ended"];
                flags.DialogueActive = !ended;
                flags.DialogueTyping = dialogue.ContainsKey("typing") && dialogue["typing"] is bool && (bool)dialogue["typing"];
                flags.DialogueChoice = dialogue.ContainsKey("choice") && dialogue["choice"] is bool && (bool)dialogue["choice"];
                if (flags.DialogueChoice)
                {
                    List<Dictionary<string, object>> choices = GameState.GetDialogueChoices();
                    for (int i = 0; i < choices.Count; i++)
                    {
                        object index;
                        if (choices[i].TryGetValue("index", out index)) flags.ChoiceIndices.Add(System.Convert.ToInt32(index));
                    }
                }
            }

            Dictionary<string, object> bargain = GameState.GetBargainSnapshot();
            if (bargain != null)
            {
                flags.BargainOpen = bargain.ContainsKey("open") && bargain["open"] is bool && (bool)bargain["open"];
                flags.CanSellDiscount = IsTrue(bargain, "can_sell_discount");
                flags.CanSellMarkup = IsTrue(bargain, "can_sell_markup");
                flags.CanBuyMarkup = IsTrue(bargain, "can_buy_markup");
                flags.CanBuyDiscount = IsTrue(bargain, "can_buy_discount");
            }

            List<GameItem> front = GameState.GetFrontCounterItems();
            List<GameItem> back = GameState.GetBackCounterItems();
            CollectTradeItems(front, flags);
            CollectTradeItems(back, flags);
            return flags;
        }

        private static void CollectTradeItems(List<GameItem> items, GameFlags flags)
        {
            for (int i = 0; i < items.Count && i < 8; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;

                if (!GameState.IsOwned(item))
                {
                    if (!flags.BuyItemIds.Contains(item.GetUniqueID())) flags.BuyItemIds.Add(item.GetUniqueID());
                }
                else if (GameState.CanSellItem(item))
                {
                    if (!flags.SellItemIds.Contains(item.GetUniqueID())) flags.SellItemIds.Add(item.GetUniqueID());
                }
            }
        }

        private static bool IsTrue(Dictionary<string, object> source, string key)
        {
            return source.ContainsKey(key) && source[key] is bool && (bool)source[key];
        }

        private static List<Dictionary<string, object>> DescribeLegalActions(List<LegalAction> actions)
        {
            var described = new List<Dictionary<string, object>>();
            for (int i = 0; i < actions.Count; i++)
            {
                LegalAction action = actions[i];
                var row = new Dictionary<string, object>();
                row["action"] = action.Action;
                row["args"] = action.Args;
                if (!string.IsNullOrEmpty(action.Hint)) row["hint"] = action.Hint;
                described.Add(row);
            }

            return described;
        }

        private static Dictionary<string, object> BuildRunSummary(Dictionary<string, object> state)
        {
            var summary = new Dictionary<string, object>();
            summary["day"] = state.ContainsKey("day") ? state["day"] : 0;
            summary["shop_state"] = state.ContainsKey("shop_state") ? state["shop_state"] : "UNKNOWN";
            summary["cash"] = state.ContainsKey("cash") ? state["cash"] : 0;
            summary["rent_days_left"] = state.ContainsKey("rent_days_left") ? state["rent_days_left"] : 0;
            summary["inventory_count"] = state.ContainsKey("inventory_count") ? state["inventory_count"] : 0;
            summary["front_counter_count"] = state.ContainsKey("front_counter_count") ? state["front_counter_count"] : 0;
            summary["back_counter_count"] = state.ContainsKey("back_counter_count") ? state["back_counter_count"] : 0;

            object client = state.ContainsKey("client") ? state["client"] : null;
            var clientRow = client as Dictionary<string, object>;
            summary["client_name"] = clientRow != null && clientRow.ContainsKey("name") ? clientRow["name"] : null;
            summary["client_intent"] = clientRow != null && clientRow.ContainsKey("intent") ? clientRow["intent"] : null;
            return summary;
        }

        private static List<Dictionary<string, object>> DescribeItems(List<GameItem> items)
        {
            var described = new List<Dictionary<string, object>>();
            int limit = Math.Min(items.Count, 30);
            for (int i = 0; i < limit; i++)
            {
                GameItem item = items[i];
                if (item == null) continue;

                var row = new Dictionary<string, object>();
                row["id"] = item.GetUniqueID();
                row["name"] = item.name;
                row["value"] = item.GetNegociatedValue();
                row["owned"] = GameState.IsOwned(item);
                row["sellable"] = GameState.IsOwned(item) && GameState.CanSellItem(item);
                described.Add(row);
            }

            return described;
        }

        private static Dictionary<string, object> DescribeClient(StoreClient client)
        {
            var description = new Dictionary<string, object>();
            description["name"] = client.displayName;
            description["intent"] = client.clientIntent.ToString();
            description["cash"] = client.clientCash;
            description["uses_budget"] = client.useClientBudget;
            description["impatience"] = client.clientImpatience;
            description["is_wanted"] = client.isWanted;
            description["report_reward"] = client.reportReward;
            description["stealth_wanted"] = client.isStealthWanted;
            description["reported"] = client.reported;
            description["can_report"] = SafeCanReport(client);
            description["wanted_poster_id"] = client.wantedPosterId;
            description["bargain_available"] = client.isBargainAvailable;
            description["faction"] = client.clientFaction;
            description["budget"] = GameState.GetClientBudget(client);
            return description;
        }

        private static bool SafeCanReport(StoreClient client)
        {
            try { return client.CanReport(); } catch { return false; }
        }

        private static Dictionary<string, object> DescribeItem(GameItem item)
        {
            var description = new Dictionary<string, object>();
            description["name"] = item.name;
            description["identifier"] = item.identifier;
            description["count"] = item.unitCount;
            description["value"] = item.GetCurrentValue();
            return description;
        }
    }
}
