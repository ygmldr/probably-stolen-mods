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
            List<LegalAction> legal = LegalActionBuilder.Build(flags);
            // legal_actions only carries rows whose native guards already accepted.
            // Bargain directions are candidates: the panel being open does not prove
            // that a given direction is still available, and reading the per-direction
            // flags would call BargainUIManager.Refresh() on every snapshot.
            state["legal_actions"] = DescribeLegalActions(legal, false);
            state["candidate_actions"] = DescribeLegalActions(legal, true);

            // A native confirmation owns the input while it is open. Exposing it
            // here means the model can never silently ignore a modal (losing
            // ground items, discarding a trade) because the prompt is part of the
            // state it reads before every action.
            state["prompt"] = PromptJson.ForState(PromptPolicy.ReadSafe(PromptAdapter.Instance));

            if (!inGame)
            {
                state["client"] = null;
                state["negotiated_item"] = null;
                state["run_summary"] = null;
                state["rent"] = null;
                state["report"] = Unreadable();
                state["calendar_next"] = Unreadable();
                state["tutorial_hints"] = null;
                return state;
            }

            // Read-only intel: both projections tolerate a failed read. They
            // must never make the state snapshot itself unavailable.
            state["report"] = SafeReport();
            state["calendar_next"] = SafeCalendarNext();
            state["tutorial_hints"] = SafeTutorialHints();

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
            state["waiting_customers"] = GameState.GetWaitingCustomerCount();

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
            state["dialogue"] = FirstWeekJson.Dialogue(GameState.ReadDialogueView());
            state["rent"] = BuildRentState();

            StoreClient client = GameState.GetCurrentClient();
            state["client"] = client == null ? null : DescribeClient(client);

            GameItem item = GameState.GetNegotiatedItem();
            state["negotiated_item"] = item == null ? null : DescribeItem(item);

            state["run_summary"] = BuildRunSummary(state);
            return state;
        }

        private static Dictionary<string, object> Unreadable()
        {
            return new Dictionary<string, object> { { "readable", false } };
        }

        private static Dictionary<string, object> SafeReport()
        {
            try
            {
                ReportService service = ReportService.Current;
                return service == null ? Unreadable() : service.State();
            }
            catch (Exception)
            {
                return Unreadable();
            }
        }

        private static Dictionary<string, object> SafeCalendarNext()
        {
            try
            {
                CalendarService service = CalendarService.Current;
                return service == null ? Unreadable() : service.StateNext();
            }
            catch (Exception)
            {
                return Unreadable();
            }
        }

        private static object SafeTutorialHints()
        {
            try
            {
                TutorialSnapshot snapshot = TutorialAdapter.Instance.ReadTutorial();
                if (snapshot == null || !snapshot.Readable) return null;
                return snapshot.Enabled;
            }
            catch (Exception)
            {
                return null;
            }
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

            DialogueView dialogue = GameState.ReadDialogueView();
            flags.DialogueActive = dialogue.Available && !dialogue.Ended;
            flags.DialogueTyping = dialogue.Typing;
            flags.DialogueChoice = dialogue.Available && dialogue.Choice;
            if (flags.DialogueChoice)
            {
                for (int i = 0; i < dialogue.Choices.Count; i++)
                {
                    DialogueChoiceView choice = dialogue.Choices[i];
                    flags.ChoiceIndices.Add(choice.Index);
                    if (!choice.Enabled) flags.DisabledChoiceIndices.Add(choice.Index);
                }
            }

            Dictionary<string, object> bargain = GameState.GetBargainSnapshot();
            if (bargain != null)
            {
                // Only "open" is safe to read; the per-direction flags come from
                // Refresh()-backed getters and are no longer part of the snapshot.
                flags.BargainOpen = bargain.ContainsKey("open") && bargain["open"] is bool && (bool)bargain["open"];
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

        private static List<Dictionary<string, object>> DescribeLegalActions(
            List<LegalAction> actions, bool candidates)
        {
            List<LegalAction> selected = candidates
                ? LegalActionBuilder.Candidates(actions)
                : LegalActionBuilder.Available(actions);

            var described = new List<Dictionary<string, object>>();
            for (int i = 0; i < selected.Count; i++)
            {
                LegalAction action = selected[i];
                var row = new Dictionary<string, object>();
                row["action"] = action.Action;
                row["args"] = action.Args;
                // Every row states its availability: legal rows are "available",
                // candidates are "unknown" and additionally carry the native guard
                // they still need. This matches the SKILL/schema contract.
                row["availability"] = action.ReportedAvailability;
                if (candidates) row["requires"] = action.Requires;

                if (!string.IsNullOrEmpty(action.Hint)) row["hint"] = action.Hint;
                described.Add(row);
            }

            return described;
        }

        private static Dictionary<string, object> BuildRentState()
        {
            FirstWeekService service = FirstWeekService.Current;
            if (service != null) return service.RentState(CurrentRevision);

            // Even without the action service, show the visible rent state; the
            // absent tracker means a receipt cannot be asserted.
            return FirstWeekJson.Rent(new RentObservation
            {
                Snapshot = GameState.ReadRentSnapshot(),
                Error = RentErrors.ReceiptUnavailable
            });
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

        /// <summary>
        /// Read-only view of the current client for
        /// <see cref="ClientSnapshotProjection"/>. Every member is a plain field or
        /// a field getter; StoreClient.CanReport() is deliberately not exposed
        /// because it calls BargainUIManager.Refresh(), which resets the bargain
        /// state when the tracked client changes.
        /// </summary>
        private sealed class StoreClientSnapshot : IClientSnapshotSource
        {
            private readonly StoreClient _client;

            public StoreClientSnapshot(StoreClient client)
            {
                _client = client;
            }

            public string DisplayName { get { return _client.displayName; } }
            public string Intent { get { return _client.clientIntent.ToString(); } }
            public bool UsesBudget { get { return _client.useClientBudget; } }
            public int Impatience { get { return _client.clientImpatience; } }
            public bool IsWanted { get { return _client.isWanted; } }
            public int ReportReward { get { return _client.reportReward; } }
            public bool Reported { get { return _client.reported; } }
            public string WantedPosterId { get { return _client.wantedPosterId; } }
            public bool BargainAvailable { get { return _client.isBargainAvailable; } }
            public string Faction { get { return _client.clientFaction; } }
            public int Budget { get { return GameState.GetClientBudget(_client); } }
        }

        private static Dictionary<string, object> DescribeClient(StoreClient client)
        {
            return ClientSnapshotProjection.Describe(new StoreClientSnapshot(client));
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
