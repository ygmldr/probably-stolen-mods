using System;
using System.Collections.Generic;
using System.Text;

namespace PawnMind.Core
{
    /// <summary>
    /// How much is proven about one source read by the state snapshot.
    /// Unknown is deliberately not treated as Pure: a getter joins the hot read
    /// path only after its call chain was reviewed (see
    /// docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md).
    /// </summary>
    public enum SnapshotPurity
    {
        Pure = 0,
        Unknown = 1,
        Impure = 2
    }

    /// <summary>One snapshot field (or one game member the snapshot build reads).</summary>
    public sealed class SnapshotSource
    {
        public readonly string Section;
        public readonly string Key;
        public readonly string Origin;
        public readonly SnapshotPurity Purity;
        public readonly string Note;

        public SnapshotSource(string section, string key, string origin,
            SnapshotPurity purity, string note)
        {
            Section = section;
            Key = key;
            Origin = origin;
            Purity = purity;
            Note = note;
        }
    }

    /// <summary>
    /// Declarative map of the read-only state snapshot. The drift test parses
    /// src/Bridge/StateSnapshot.cs and refuses a snapshot that emits an
    /// unregistered key or calls an unregistered game member. The known
    /// Refresh()-backed sources are forbidden outright: see <see cref="ForbiddenTokens"/>.
    /// </summary>
    public static class SnapshotFieldMap
    {
        public const string SectionState = "state";
        public const string SectionClient = "client";
        public const string SectionRunSummary = "run_summary";
        public const string SectionItem = "item";
        public const string SectionLegalAction = "legal_action";

        private const SnapshotPurity Pure = SnapshotPurity.Pure;
        private const SnapshotPurity Unknown = SnapshotPurity.Unknown;
        private const SnapshotPurity Impure = SnapshotPurity.Impure;

        /// <summary>
        /// Native calls that may mutate game/UI state and must never appear in the
        /// snapshot build. BargainUIManager.Refresh() resets the per-client offer
        /// flags when the tracked client changes; StoreClient.CanReport() is one of
        /// the getters that call it.
        /// </summary>
        public static readonly string[] ForbiddenTokens =
        {
            "CanReport(",
            "CanOfferDiscount(",
            "CanOfferMarkup(",
            "CanOfferBuyingDiscount(",
            "CanOfferBuyingMarkup(",
            "Refresh(",
            "RNG.Roll",
            "ClearTradeRepMultiplierOverrides(",
            "GetBargainSuccessChance(",
            "GetBargainLeaveChance(",
        };

        /// <summary>
        /// The six MCP aliases that build the same snapshot. Frozen here so the
        /// generated policy catalog and the snapshot rules cannot drift apart.
        /// </summary>
        public static readonly string[] SnapshotReaderAliases =
        {
            "get_legal_actions",
            "get_run_summary",
            "get_state",
            "wait_for_change",
            "wait_for_events",
            "wait_until",
        };

        /// <summary>Top-level keys emitted by BuildState / BuildJson.</summary>
        public static readonly SnapshotSource[] StateKeys =
        {
            Row("in_game", "GameState.IsInGame", Pure,
                "PlayerStore.HasInstance existence probe; never creates the empty store"),
            Row("legal_actions", "LegalActionBuilder.Build (Core)", Pure,
                "pure Core projection; contains availability=available rows only"),
            Row("candidate_actions", "LegalActionBuilder.Build (Core)", Pure,
                "candidate rows with availability=unknown; never claimed as legal"),
            Row("prompt", "PromptJson.ForState(PromptPolicy.ReadSafe(PromptAdapter))", Unknown,
                "adapter read; native modal fields not exhaustively traced in this batch"),
            Row("report", "ReportService.State", Unknown,
                "DayReport/adapter read; not exhaustively traced in this batch"),
            Row("calendar_next", "CalendarService.StateNext", Unknown,
                "calendar adapter read; not exhaustively traced in this batch"),
            Row("tutorial_hints", "TutorialAdapter.ReadTutorial", Unknown,
                "tutorial adapter read; not exhaustively traced in this batch"),
            Row("day", "GameState.GetDay", Pure, "StoreStation.dayCounter field"),
            Row("shop_state", "GameState.GetShopState", Pure, "PlayerStore.storeState enum field"),
            Row("outside", "GameState.IsOutside", Pure, "MapUIManager.IsOutside => Current != null"),
            Row("location", "GameState.CurrentLocationId", Pure, "MapUIManager.Current.Id field"),
            Row("afterhour_count", "GameState.GetAfterhourItemCount", Pure,
                "EmporiumEntry.afterhourInventory.childItems.Count"),
            Row("cash", "GameState.GetCash", Pure, "PlayerStore.playerCash field"),
            Row("rent_days_left", "GameState.GetRentDaysLeft", Pure, "PlayerStore.dayUntilRent field"),
            Row("rent_value", "GameState.GetRentValue", Pure, "PlayerStore.rentValue field"),
            Row("starting_day", "GameState.GetStartingDay", Pure, "PlayerStore.startingDay field"),
            Row("client_arrived", "GameState.IsClientArrived", Pure, "PlayerStore.isClientArrived field"),
            Row("waiting_customers", "GameState.GetWaitingCustomerCount", Pure,
                "StoreClientManager.clientStack.Count"),
            Row("back_counter_count", "GameState.GetBackCounterItems", Pure,
                "EmporiumEntry.backInvinvElement.childItems.Count"),
            Row("back_counter", "StateSnapshot.DescribeItems", Unknown,
                "item projection reads GetNegociatedValue(), which writes debug UI when the price debug panel is active"),
            Row("front_counter_count", "GameState.GetFrontCounterItems", Pure,
                "EmporiumEntry.frontInvinvElement.childItems.Count"),
            Row("front_counter", "StateSnapshot.DescribeItems", Unknown,
                "same item value chain as back_counter"),
            Row("client_item_count", "GameState.GetClientItems", Pure,
                "StoreClientInstance.gridInv.childItems.Count (private field, read-only reflection)"),
            Row("client_items", "StateSnapshot.DescribeItems", Unknown,
                "same item value chain as back_counter"),
            Row("negotiation_open", "GameState.IsNegotiationOpen", Pure,
                "NegociationUIManager.IsOpen => uiObject.activeSelf"),
            Row("bargain", "GameState.GetBargainSnapshot", Pure,
                "BargainUIManager.IsOpen => uiObject.activeSelf; no Refresh()-backed flags"),
            Row("inventory_count", "GameState.GetInventoryItems", Pure,
                "EmporiumEntry.invElement.childItems.Count"),
            Row("inventory", "StateSnapshot.DescribeItems", Unknown,
                "same item value chain as back_counter"),
            Row("can_call_next", "GameState.GetCanNextClient", Pure,
                "PlayerStore.CanNextClient(): list count + bool fields only"),
            Row("can_end_day", "GameState.GetCanEndDay", Pure,
                "PlayerStore.CanEndDay(): dismissable flags only"),
            Row("dialogue", "FirstWeekJson.Dialogue(GameState.ReadDialogueView)", Unknown,
                "Dialogue.IsDisabled runs game-supplied predicate closures; not traced"),
            Row("rent", "FirstWeekService.RentState / FirstWeekJson.Rent", Pure,
                "ReadRentSnapshot: playerCash/rentValue/dayUntilRent/runStats field reads"),
            Row("client", "ClientSnapshotProjection.Describe", Pure,
                "only plain StoreClient fields; GetBudget() returns the clientBudget field"),
            Row("negotiated_item", "GameState.GetNegotiatedItem", Pure,
                "PlayerStore.currentNegociatedItem field"),
            Row("run_summary", "StateSnapshot.BuildRunSummary", Pure,
                "copies already-projected state values"),
            Row("rev", "RevisionTracker.CurrentRevision", Pure, "in-process counter"),
            Row("timestamp", "DateTime.UtcNow", Pure, "clock read"),
        };

        /// <summary>Keys of the run_summary section.</summary>
        public static readonly SnapshotSource[] RunSummaryKeys =
        {
            Summary("day", "state.day", Pure, "copy"),
            Summary("shop_state", "state.shop_state", Pure, "copy"),
            Summary("cash", "state.cash", Pure, "copy"),
            Summary("rent_days_left", "state.rent_days_left", Pure, "copy"),
            Summary("inventory_count", "state.inventory_count", Pure, "copy"),
            Summary("front_counter_count", "state.front_counter_count", Pure, "copy"),
            Summary("back_counter_count", "state.back_counter_count", Pure, "copy"),
            Summary("client_name", "state.client.name", Pure, "copy"),
            Summary("client_intent", "state.client.intent", Pure, "copy"),
        };

        /// <summary>Keys of the client section, with the exact StoreClient member read.</summary>
        public static readonly SnapshotSource[] ClientKeys =
        {
            Client("name", "StoreClient.displayName", Pure, "plain field"),
            Client("intent", "StoreClient.clientIntent", Pure, "enum field"),
            Client("uses_budget", "StoreClient.useClientBudget", Pure, "bool field"),
            Client("impatience", "StoreClient.clientImpatience", Pure, "int field"),
            Client("is_wanted", "StoreClient.isWanted", Pure, "bool field"),
            Client("report_reward", "StoreClient.reportReward", Pure, "int field"),
            Client("reported", "StoreClient.reported", Pure, "bool field"),
            Client("wanted_poster_id", "StoreClient.wantedPosterId", Pure, "string field"),
            Client("bargain_available", "StoreClient.isBargainAvailable", Pure, "bool field"),
            Client("faction", "StoreClient.clientFaction", Pure, "string field"),
            Client("budget", "StoreClient.GetBudget", Pure, "returns the clientBudget field"),
        };

        /// <summary>Keys of the item projection used by every item list.</summary>
        public static readonly SnapshotSource[] ItemKeys =
        {
            Item("id", "GameItem.GetUniqueID", Pure, "id field"),
            Item("name", "GameItem.name", Pure, "string field"),
            Item("value", "GameItem.GetNegociatedValue / GetCurrentValue", Unknown,
                "conditional debug-UI write when DebugPanel.priceDebugPanel is active; no game mutation found"),
            Item("owned", "GameState.IsOwned", Pure, "IS_OWNED_TAG read"),
            Item("sellable", "GameState.CanSellItem", Unknown,
                "player-store can-sell predicates; Debug.Log only, no Refresh()/RNG found in the excerpt"),
            Item("identifier", "GameItem.identifier", Pure, "string field"),
            Item("count", "GameItem.unitCount", Pure, "int field"),
        };

        /// <summary>Keys of one legal-action row.</summary>
        public static readonly SnapshotSource[] LegalActionKeys =
        {
            Legal("action", "LegalAction.Action", Pure, "Core string"),
            Legal("args", "LegalAction.Args", Pure, "Core dictionary"),
            Legal("hint", "LegalAction.Hint", Pure, "Core string"),
            Legal("availability", "LegalAction.Availability", Pure, "Core enum"),
            Legal("requires", "LegalAction.Requires", Pure, "Core string"),
        };

        /// <summary>GameState members the snapshot build may call.</summary>
        public static readonly SnapshotSource[] GameStateCalls =
        {
            Game("IsInGame", Pure, "PlayerStore.HasInstance"),
            Game("GetShopState", Pure, "storeState.ToString()"),
            Game("GetCanNextClient", Pure, "CanNextClient(): list count + bool fields"),
            Game("GetCanEndDay", Pure, "CanEndDay(): dismissable flags"),
            Game("GetCurrentClient", Pure, "currentClientInstance private field via reflection (read-only)"),
            Game("IsClientArrived", Pure, "isClientArrived field"),
            Game("GetBargainSnapshot", Pure, "BargainUIManager.IsOpen only"),
            Game("GetFrontCounterItems", Pure, "childItems list reference"),
            Game("GetBackCounterItems", Pure, "childItems list reference"),
            Game("GetClientItems", Pure, "gridInv private field via reflection (read-only)"),
            Game("GetInventoryItems", Pure, "childItems list reference"),
            Game("GetAfterhourItemCount", Pure, "childItems.Count"),
            Game("GetWaitingCustomerCount", Pure, "clientStack.Count"),
            Game("GetDay", Pure, "StoreStation.dayCounter"),
            Game("GetCash", Pure, "playerCash"),
            Game("GetRentDaysLeft", Pure, "dayUntilRent"),
            Game("GetRentValue", Pure, "rentValue"),
            Game("GetStartingDay", Pure, "startingDay"),
            Game("IsOutside", Pure, "MapUIManager.IsOutside"),
            Game("CurrentLocationId", Pure, "MapUIManager.Current.Id"),
            Game("IsNegotiationOpen", Pure, "NegociationUIManager.IsOpen"),
            Game("IsOwned", Pure, "IS_OWNED_TAG read"),
            Game("CanSellItem", Unknown, "player-store can-sell predicates; Debug.Log only"),
            Game("GetClientBudget", Pure, "returns clientBudget"),
            Game("GetNegotiatedItem", Pure, "currentNegociatedItem"),
            Game("ReadDialogueView", Unknown, "Dialogue.IsDisabled runs game-supplied predicates"),
            Game("ReadRentSnapshot", Pure, "playerCash/rentValue/dayUntilRent/runStats fields"),
        };

        /// <summary>Bridge/native services read while building the snapshot.</summary>
        public static readonly SnapshotSource[] ServiceCalls =
        {
            Service("LegalActionBuilder.Build", Pure, "pure Core projection"),
            Service("LegalActionBuilder.Available", Pure, "filters availability=available rows"),
            Service("LegalActionBuilder.Candidates", Pure, "filters candidate rows"),
            Service("PromptJson.ForState", Pure, "pure JSON shaping"),
            Service("PromptPolicy.ReadSafe", Unknown, "reads the pending native prompt"),
            Service("PromptAdapter.Instance", Unknown, "adapter singleton"),
            Service("ReportService.State", Unknown, "day-report adapter read"),
            Service("CalendarService.StateNext", Unknown, "calendar adapter read"),
            Service("TutorialAdapter.ReadTutorial", Unknown, "tutorial adapter read"),
            Service("FirstWeekService.RentState", Pure, "rent tracker + pure readback"),
            Service("FirstWeekJson.Rent", Pure, "pure JSON shaping"),
            Service("FirstWeekJson.Dialogue", Pure, "pure JSON shaping"),
            Service("Revision.Observe", Pure, "in-process fingerprint counter"),
            Service("Revision.Revision", Pure, "in-process counter read"),
            Service("ReportService.Current", Pure, "singleton reference"),
            Service("CalendarService.Current", Pure, "singleton reference"),
            Service("FirstWeekService.Current", Pure, "singleton reference"),
            Service("TutorialAdapter.Instance", Pure, "singleton reference"),
        };

        /// <summary>Null when the (section, key) pair is registered.</summary>
        public static SnapshotSource Find(string section, string key)
        {
            SnapshotSource[] rows = Rows(section);
            if (rows == null) return null;
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.Equals(rows[i].Key, key, StringComparison.Ordinal)) return rows[i];
            }

            return null;
        }

        /// <summary>Null when the GameState member is not registered.</summary>
        public static SnapshotSource FindGameCall(string member)
        {
            for (int i = 0; i < GameStateCalls.Length; i++)
            {
                if (string.Equals(GameStateCalls[i].Key, member, StringComparison.Ordinal))
                    return GameStateCalls[i];
            }

            return null;
        }

        /// <summary>Null when the service call is not registered.</summary>
        public static SnapshotSource FindServiceCall(string call)
        {
            for (int i = 0; i < ServiceCalls.Length; i++)
            {
                if (string.Equals(ServiceCalls[i].Key, call, StringComparison.Ordinal))
                    return ServiceCalls[i];
            }

            return null;
        }

        public static SnapshotSource[] Rows(string section)
        {
            switch (section)
            {
                case SectionState: return StateKeys;
                case SectionClient: return ClientKeys;
                case SectionRunSummary: return RunSummaryKeys;
                case SectionItem: return ItemKeys;
                case SectionLegalAction: return LegalActionKeys;
                default: return null;
            }
        }

        /// <summary>Every key that a pure snapshot must never emit.</summary>
        public static List<string> ImpureKeys()
        {
            var found = new List<string>();
            string[] sections = { SectionState, SectionClient, SectionRunSummary, SectionItem, SectionLegalAction };
            for (int s = 0; s < sections.Length; s++)
            {
                SnapshotSource[] rows = Rows(sections[s]);
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i].Purity == Impure) found.Add(rows[i].Key);
                }
            }

            return found;
        }

        /// <summary>Forbidden call tokens found in source text (comments are ignored).</summary>
        public static List<string> ForbiddenTokenHits(string sourceText)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(sourceText)) return hits;

            string code = StripLineComments(sourceText);
            for (int i = 0; i < ForbiddenTokens.Length; i++)
            {
                if (code.IndexOf(ForbiddenTokens[i], StringComparison.Ordinal) >= 0)
                    hits.Add(ForbiddenTokens[i]);
            }

            return hits;
        }

        /// <summary>True when the source text still references a forbidden call.</summary>
        public static bool ContainsForbiddenCall(string sourceText)
        {
            return ForbiddenTokenHits(sourceText).Count > 0;
        }

        private static string StripLineComments(string sourceText)
        {
            string[] lines = sourceText.Replace("\r\n", "\n").Split('\n');
            var builder = new StringBuilder(sourceText.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment);
                builder.Append(line).Append('\n');
            }

            return builder.ToString();
        }

        private static SnapshotSource Row(string key, string origin, SnapshotPurity purity, string note)
        {
            return new SnapshotSource(SectionState, key, origin, purity, note);
        }

        private static SnapshotSource Summary(string key, string origin, SnapshotPurity purity, string note)
        {
            return new SnapshotSource(SectionRunSummary, key, origin, purity, note);
        }

        private static SnapshotSource Client(string key, string origin, SnapshotPurity purity, string note)
        {
            return new SnapshotSource(SectionClient, key, origin, purity, note);
        }

        private static SnapshotSource Item(string key, string origin, SnapshotPurity purity, string note)
        {
            return new SnapshotSource(SectionItem, key, origin, purity, note);
        }

        private static SnapshotSource Legal(string key, string origin, SnapshotPurity purity, string note)
        {
            return new SnapshotSource(SectionLegalAction, key, origin, purity, note);
        }

        private static SnapshotSource Game(string member, SnapshotPurity purity, string note)
        {
            return new SnapshotSource("game", member, "GameState." + member, purity, note);
        }

        private static SnapshotSource Service(string call, SnapshotPurity purity, string note)
        {
            return new SnapshotSource("service", call, call, purity, note);
        }
    }
}
