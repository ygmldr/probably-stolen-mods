using System;

namespace PawnMind.Core
{
    /// <summary>
    /// The bell is the player's "next customer" button, but it also refuses while
    /// the bell animation lock is held and while the shop is not open
    /// (StoreBell.RingBell). The bridge used to call TryCallNextClient directly and
    /// so could ring during the animation or outside opening hours.
    /// </summary>
    public static class ShopBellPolicy
    {
        public const string BellBusy = "bell_busy";
        public const string ShopNotOpen = "shop_not_open";
        public const string CannotCallNext = "cannot_call_next";

        /// <summary>Returns the refusal code, or null when the ring is legal.</summary>
        public static string Evaluate(bool readable, bool bellLocked, string shopState, bool canCallNext)
        {
            if (!readable) return DayEndErrors.ReadbackUnavailable;
            // RingBell checks the lock before its own state check.
            if (bellLocked) return BellBusy;
            if (!DayEndPolicy.IsOpenState(shopState)) return ShopNotOpen;
            if (!canCallNext) return CannotCallNext;
            return null;
        }
    }
}
