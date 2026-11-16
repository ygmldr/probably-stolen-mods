namespace PawnMind.Core
{
    /// <summary>Stable refusal codes for confirm_trade (B10-B16).</summary>
    public static class TradeErrors
    {
        public const string NotInGame = "not_in_game";
        public const string NoClient = "no_client";
        public const string ReadFailed = "trade_read_failed";

        /// <summary>B10: arrest / shootout / shot state; the native click is ignored.</summary>
        public const string TradeLocked = "trade_locked";

        public const string NoNegotiation = "no_negotiation";
        public const string ItemNotFound = "item_not_found";

        /// <summary>B12: the accepted item must be the one staged on the table.</summary>
        public const string ItemNotStaged = "item_not_staged";
        public const string AlreadyOwned = "already_owned";
        public const string NotOwned = "not_owned";
        public const string BadSide = "bad_side";

        /// <summary>B11: the client's business is already complete or intent undefined.</summary>
        public const string BusinessComplete = "business_complete";
        public const string IntentUndefined = "intent_undefined";

        /// <summary>B13: barter offer missing/unacceptable or client not introduced.</summary>
        public const string BarterNotAcceptable = "barter_not_acceptable";
        public const string NotIntroduced = "not_introduced";

        /// <summary>B14: the information-dealer service cost cannot be paid.</summary>
        public const string CannotAfford = "cannot_afford";

        /// <summary>B15: the gunsmith order was not accepted.</summary>
        public const string GunOrderNotAccepted = "gun_order_not_accepted";

        /// <summary>B16: native buy/sell guards refuse this item.</summary>
        public const string CannotBuyFromTable = "cannot_buy_from_table";
        public const string ClientRefusesItem = "client_refuses_item";

        /// <summary>Dispatched but no trusted completion evidence (silent native return).</summary>
        public const string OutcomeUnverified = "outcome_unverified";

        /// <summary>The trade may have completed but for a different client instance.</summary>
        public const string ClientChanged = "client_changed";
    }

    /// <summary>
    /// Read-only projection used by the confirm_trade pre-check. Every member has a
    /// native source listed in docs/audit/PLAYER_ENTRY_MATRIX.csv (B10-B16); the
    /// adapter reads it without dispatching anything.
    /// </summary>
    public sealed class TradeGuardInput
    {
        public bool InGame;
        public bool ClientPresent;
        public bool TradeLocked;
        public bool NegotiationOpen;
        public int RequestedItemId;
        public int StagedItemId;
        public bool StagedItemOwned;
        public string Side;
        public string ClientIntent;
        public bool BusinessComplete;
        public bool Introduced;
        public bool BarterOfferPresent;
        public bool BarterAcceptable;
        public int ServiceCost;
        public bool CanPay;
        public bool GunOrderPresent;
        public bool GunOrderAccepted;
        public bool CanBuyFromTable;
        public bool CanSellItem;
    }

    /// <summary>
    /// C module: the seven silent native refusal branches of
    /// NegociationUIManager.OnOfferAcceptClick. Every branch returns a stable code
    /// before the click is dispatched, so the bridge can never report a trade that
    /// the game silently ignored. Branches whose native predicate cannot be read
    /// safely are represented by a pre-computed bool; the adapter fails closed
    /// (false) when it cannot read them.
    /// </summary>
    public static class TradeGuardPolicy
    {
        /// <summary>Returns the refusal code, or null when the accept click may be dispatched.</summary>
        public static string Evaluate(TradeGuardInput input)
        {
            if (input == null) return TradeErrors.ReadFailed;
            if (!input.InGame) return TradeErrors.NotInGame;

            // The native click starts with the same two checks.
            if (input.TradeLocked) return TradeErrors.TradeLocked;
            if (!input.ClientPresent) return TradeErrors.NoClient;

            if (!input.NegotiationOpen) return TradeErrors.NoNegotiation;
            if (input.RequestedItemId <= 0) return TradeErrors.ItemNotFound;
            if (input.StagedItemId != input.RequestedItemId) return TradeErrors.ItemNotStaged;

            bool selling = IsSell(input.Side);
            bool validSide = selling || IsBuy(input.Side);
            if (!validSide) return TradeErrors.BadSide;

            string intent = Normalize(input.ClientIntent);
            if (intent == "UNDEFINED") return TradeErrors.IntentUndefined;
            if (input.BusinessComplete) return TradeErrors.BusinessComplete;

            // B12: direction comes from the native staged-item ownership check, not
            // from a second hand-written ownership test.
            if (selling && !input.StagedItemOwned) return TradeErrors.NotOwned;
            if (!selling && input.StagedItemOwned) return TradeErrors.AlreadyOwned;

            switch (intent)
            {
                case "BARTER":
                    if (!input.BarterOfferPresent || !input.BarterAcceptable)
                        return TradeErrors.BarterNotAcceptable;
                    if (!input.Introduced) return TradeErrors.NotIntroduced;
                    break;

                case "INFORMATION_DEALER":
                    if (!input.Introduced) return TradeErrors.NotIntroduced;
                    if (input.ServiceCost < 0 || !input.CanPay) return TradeErrors.CannotAfford;
                    break;

                case "GUNSMITH":
                    if (!input.GunOrderPresent || !input.GunOrderAccepted)
                        return TradeErrors.GunOrderNotAccepted;
                    break;
            }

            // B16: the native buy/sell gate for the requested direction.
            if (selling)
            {
                if (!input.CanSellItem) return TradeErrors.ClientRefusesItem;
            }
            else if (!input.CanBuyFromTable)
            {
                return TradeErrors.CannotBuyFromTable;
            }

            return null;
        }

        public static bool IsBuy(string side)
        {
            return string.Equals(Normalize(side), "BUY", System.StringComparison.Ordinal);
        }

        public static bool IsSell(string side)
        {
            return string.Equals(Normalize(side), "SELL", System.StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }
    }
}
