namespace PawnMind.Core
{
    /// <summary>Stable refusal / uncertainty codes for stage_negotiation_item (R5-B1).</summary>
    public static class StagingErrors
    {
        public const string NotInGame = "not_in_game";
        public const string NoClient = "no_client";

        /// <summary>The current client exists but its identity/intent cannot be read.</summary>
        public const string ClientUnreadable = "client_unreadable";

        /// <summary>The pre-read itself failed (staged pointer/container/ownership).</summary>
        public const string ReadFailed = "staging_read_failed";

        /// <summary>NegociationUIManager.uiObject is not wired: IsOpen would be a lie.</summary>
        public const string NegotiationUnreadable = "negotiation_unreadable";

        /// <summary>A blocking native/custom modal owns the screen; a player cannot drag.</summary>
        public const string ModalOpen = "modal_open";

        /// <summary>R5.1/F4: the blocking-modal read failed, so "clear" is unknown.</summary>
        public const string ModalUnknown = "modal_unknown";

        public const string ItemNotFound = "item_not_found";

        /// <summary>The item is already in the sold container: it is no longer ours to stage.</summary>
        public const string ItemSold = "item_sold";

        public const string ItemUnreadable = "item_unreadable";

        /// <summary>
        /// The item sits in an inventory that is not on screen during a shop
        /// negotiation (machine interior, night backpack, vending/fountain/employee
        /// storage). Moving it would be a bridge shortcut, not a player drag.
        /// </summary>
        public const string ItemUnreachable = "item_unreachable";

        /// <summary>Client intent BUY (the player sells): the staged item must be owned.</summary>
        public const string NotOwned = "not_owned";

        /// <summary>Client intent SELL (the player buys): the staged item must be the client's.</summary>
        public const string AlreadyOwned = "already_owned";

        public const string IntentUndefined = "intent_undefined";

        public const string BackCounterUnreadable = "back_counter_unreadable";

        /// <summary>The native accept predicate says the item cannot go on the counter.</summary>
        public const string BackCounterFull = "back_counter_full";

        /// <summary>Dispatched but the post-state readback does not prove the staging.</summary>
        public const string OutcomeUnverified = "outcome_unverified";

        /// <summary>The client instance at the counter changed across the staging call.</summary>
        public const string ClientChanged = "client_changed";
    }

    /// <summary>
    /// Read-only projection used by the staging pre-check. Every value has a
    /// native source in the Game adapter; unavailable reads fail closed to
    /// false/null and the policy refuses instead of guessing. The revision
    /// itself is checked by the ActionRouter before this policy runs.
    /// </summary>
    public sealed class StagingGuardInput
    {
        public bool InGame;
        public bool ClientPresent;
        public bool ClientReadable;
        public string ClientIntent;

        /// <summary>NegociationUIManager is present and its uiObject is readable.</summary>
        public bool NegotiationReadable;
        public bool NegotiationOpen;

        /// <summary>A native/custom modal covers the shop counters right now.</summary>
        /// <summary>
        /// R5.1/F4: three states, not a bool. "I could not read the modal stack"
        /// is not "nothing is blocking": an optimistic default let staging run
        /// behind an unknown overlay.
        /// </summary>
        public ModalState Modal = ModalState.Clear;

        public int RequestedItemId;
        public bool ItemExists;
        public bool ItemReadable;
        public string ItemContainer;
        public bool ItemOwned;

        /// <summary>The item is already a direct child of EmporiumEntry.backInvinvElement.</summary>
        public bool OnWeightedCounter;

        public bool BackCounterReadable;

        /// <summary>Native GameGridInventory.MayHaveValidInventorySlot(back counter, item).</summary>
        public bool BackCounterMayAccept;

        public bool StagedReadable;
        public int StagedItemId;
    }

    /// <summary>
    /// Pure pre-dispatch guard for stage_negotiation_item. Refuses when a
    /// trustworthy field is missing or when a player could not perform the same
    /// drag; never changes state and never guesses.
    /// </summary>
    public static class StagingGuardPolicy
    {
        /// <summary>Returns the refusal code, or null when the native staging path may run.</summary>
        public static string Evaluate(StagingGuardInput input)
        {
            if (input == null) return StagingErrors.ReadFailed;
            if (!input.InGame) return StagingErrors.NotInGame;

            if (!input.ClientPresent) return StagingErrors.NoClient;
            if (!input.ClientReadable) return StagingErrors.ClientUnreadable;

            string intent = Normalize(input.ClientIntent);
            if (intent.Length == 0 || intent == "UNDEFINED") return StagingErrors.IntentUndefined;

            if (!input.NegotiationReadable) return StagingErrors.NegotiationUnreadable;
            if (input.Modal == ModalState.Blocked) return StagingErrors.ModalOpen;
            if (input.Modal == ModalState.Unknown) return StagingErrors.ModalUnknown;

            if (input.RequestedItemId <= 0 || !input.ItemExists) return StagingErrors.ItemNotFound;

            string container = Normalize(input.ItemContainer);
            if (container == StagingContainers.Sold) return StagingErrors.ItemSold;
            if (!input.ItemReadable) return StagingErrors.ItemUnreadable;
            if (!StagingContainers.IsPlayerReachable(container)) return StagingErrors.ItemUnreachable;

            // Direction gate. Both facts are native: clientIntent and
            // GeneralHelper.IsItemOwned(item). Staging the wrong side would leave
            // the LLM with a staged item confirm_trade must then refuse.
            if (intent == "BUY" && !input.ItemOwned) return StagingErrors.NotOwned;
            if (intent == "SELL" && input.ItemOwned) return StagingErrors.AlreadyOwned;

            if (!input.StagedReadable) return StagingErrors.ReadFailed;
            if (!input.BackCounterReadable) return StagingErrors.BackCounterUnreadable;

            // The item is not on the weighted counter yet: the native accept
            // predicate must say it can go there. Already-staged items are
            // re-staged through the remove/add path and skip this pre-check.
            if (!input.OnWeightedCounter && !input.BackCounterMayAccept)
                return StagingErrors.BackCounterFull;

            return null;
        }

        public static string Normalize(string value)
        {
            return value == null ? string.Empty : value.Trim().ToUpperInvariant();
        }

        /// <summary>True when the client is the buying side (the player sells the item).</summary>
        public static bool IsPlayerSell(string intent)
        {
            return Normalize(intent) == "BUY" || Normalize(intent) == "SELLNBUY";
        }

        /// <summary>True when the client is the selling side (the player buys the item).</summary>
        public static bool IsPlayerBuy(string intent)
        {
            string normalized = Normalize(intent);
            return normalized == "SELL" || normalized == "SELLNBUY";
        }
    }

    /// <summary>Container ids that are on screen during a shop negotiation.</summary>
    public static class StagingContainers
    {
        public const string Sold = "SOLD";

        /// <summary>
        /// The store-scene inventories a player can drag from while negotiating.
        /// Machine interiors and the night backpack are deliberately absent: the
        /// bridge cannot prove those windows are open (residual risk, L3 NOT_RUN).
        /// </summary>
        public static bool IsPlayerReachable(string container)
        {
            string id = container == null ? string.Empty : container.Trim().ToLowerInvariant();
            switch (id)
            {
                case "main":
                case "front":
                case "back":
                case "showcase":
                case "hidden":
                case "trash":
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>The public MCP op name, single source for dispatch/pending scope/tests.</summary>
    public static class StagingOps
    {
        public const string Name = "stage_negotiation_item";
    }

    /// <summary>
    /// Which native primitive the adapter used; reported as evidence so a live
    /// session can tell a real weighted-area add from an in-place reselect.
    /// </summary>
    public static class StagingPrimitives
    {
        /// <summary>TryAcceptAll moved the item onto the weighted counter (add callback).</summary>
        public const string WeightedAreaAdd = "weighted_area_add";

        /// <summary>Expel + UncheckedAccept on the counter (remove + add callbacks).</summary>
        public const string WeightedAreaRemoveAdd = "weighted_area_remove_add";

        public const string None = "none";
    }
}
