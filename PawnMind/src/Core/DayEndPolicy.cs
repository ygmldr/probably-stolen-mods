namespace PawnMind.Core
{
    /// <summary>Stable codes for refusing to end the business day.</summary>
    public static class DayEndErrors
    {
        /// <summary>The game itself refuses: an important client (landlord) is waiting.</summary>
        public const string CannotEndDay = "cannot_end_day";

        /// <summary>Customers are queued; the player must confirm dismissing them first.</summary>
        public const string CustomersWaiting = "customers_waiting";

        /// <summary>Sleep is only legal after the shop was actually closed for the day.</summary>
        public const string NotClosed = "not_closed";

        public const string ReadbackUnavailable = "day_readback_unavailable";
    }

    /// <summary>
    /// Shared rule for the two paths that can end a business day: the explicit
    /// end_day op and the sleep op. The game's own shutter only advances the day
    /// when PlayerStore.CanEndDay() is true (no non-dismissable client, e.g. the
    /// landlord) and asks for a confirmation when the queue is not empty. Both
    /// rules must hold here, otherwise the bridge lets the model skip the rent
    /// collection that the game would have forced.
    /// </summary>
    public static class DayEndPolicy
    {
        public static string EvaluateEndDay(
            bool readable,
            bool canEndDay,
            int waitingCustomers,
            bool dismissWaiting)
        {
            if (!readable) return DayEndErrors.ReadbackUnavailable;
            if (!canEndDay) return DayEndErrors.CannotEndDay;
            if (waitingCustomers > 0 && !dismissWaiting) return DayEndErrors.CustomersWaiting;
            return null;
        }

        public static string EvaluateSleep(
            bool readable,
            bool canEndDay,
            int waitingCustomers,
            bool dismissWaiting,
            string shopState)
        {
            string endDay = EvaluateEndDay(readable, canEndDay, waitingCustomers, dismissWaiting);
            if (endDay != null) return endDay;

            if (!IsClosed(shopState)) return DayEndErrors.NotClosed;
            return null;
        }

        /// <summary>
        /// Only the game's own CLOSED state allows sleeping. A missing or unknown
        /// state string is treated as not closed: the bridge must not advance the
        /// day on a guess.
        /// </summary>
        public static bool IsClosed(string shopState)
        {
            return shopState != null
                && string.Equals(shopState.Trim(), "CLOSED", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
