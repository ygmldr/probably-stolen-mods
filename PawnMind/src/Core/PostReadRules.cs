namespace PawnMind.Core
{
    /// <summary>What the post-dispatch readback can say right now.</summary>
    public enum PostReadOutcome
    {
        /// <summary>The expected state change is already visible.</summary>
        Completed = 0,

        /// <summary>Readable, but the expected change is not there yet.</summary>
        Unchanged = 1,

        /// <summary>The state could not be read: say nothing.</summary>
        Pending = 2
    }

    /// <summary>
    /// R5.3/B — the post-dispatch readback rules for the four player actions that
    /// used to return success without proving anything.
    ///
    /// Each op gets a compact token before and after the native call. The rules are
    /// deliberately asymmetric: "the token did not change" is <see cref="Unchanged"/>,
    /// never failure, because these natives are asynchronous (the shutter animates,
    /// the customer walks in). A caller keeps a pending operation alive for a short
    /// window and only then decides.
    /// </summary>
    public static class PostReadRules
    {
        public const string OpenShop = "open_shop";
        public const string CallNextCustomer = "call_next_customer";
        public const string DismissCurrentClient = "dismiss_current_client";
        public const string AdvanceDialogue = "advance_dialogue";

        public const string NoClient = "none";
        public const string Client = "client";
        public const string NoDialogue = "none";
        public const string ShopOpen = "OPEN";

        public static bool IsCovered(string op)
        {
            return op == OpenShop || op == CallNextCustomer
                || op == DismissCurrentClient || op == AdvanceDialogue;
        }

        public static PostReadOutcome Evaluate(string op, string before, string after, bool readable)
        {
            if (!readable) return PostReadOutcome.Pending;
            if (before == null || after == null) return PostReadOutcome.Pending;

            switch (op)
            {
                case OpenShop:
                    // MORNING -> OPEN. Any other change is not what we asked for.
                    return string.Equals(after, ShopOpen, System.StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(before, ShopOpen, System.StringComparison.OrdinalIgnoreCase)
                        ? PostReadOutcome.Completed
                        : PostReadOutcome.Unchanged;

                case CallNextCustomer:
                    return before == NoClient && after == Client
                        ? PostReadOutcome.Completed
                        : PostReadOutcome.Unchanged;

                case DismissCurrentClient:
                    return before == Client && after == NoClient
                        ? PostReadOutcome.Completed
                        : PostReadOutcome.Unchanged;

                case AdvanceDialogue:
                    // Tokens are "none" or "dlg:<text hash>": either the bubble
                    // changed or the dialogue ended. Typing animates the text, so a
                    // changed token is the only thing this can honestly claim.
                    return after != before
                        ? PostReadOutcome.Completed
                        : PostReadOutcome.Unchanged;

                default:
                    return PostReadOutcome.Pending;
            }
        }
    }
}
