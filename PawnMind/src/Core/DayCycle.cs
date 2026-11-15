namespace PawnMind.Core
{
    /// <summary>
    /// Stable S5 day-cycle readback codes. A field that cannot be read or that
    /// regresses must never be reported as a successful day transition.
    /// </summary>
    public static class DayCycleErrors
    {
        public const string ReadbackUnavailable = "readback_unavailable";
        public const string ReadbackRegression = "readback_regression";
        public const string NotInGame = "not_in_game";
        public const string CannotEndDay = "cannot_end_day";
        public const string OutcomeUnknown = "outcome_unknown";
    }

    public sealed class DayCycleReadback
    {
        public bool Readable;
        public string Error;
        public int DayBefore;
        public int DayAfter;
        public int RentDaysLeftBefore;
        public int RentDaysLeftAfter;
        public int CashBefore;
        public int CashAfter;
        public bool DayAdvanced;
        public bool Changed;
    }

    public static class DayCycleRules
    {
        /// <summary>
        /// Compares only fields that were actually readable. It intentionally
        /// does not require the day to advance: sleep starts an asynchronous
        /// blackscreen sequence in the game, so an immediate readback may still
        /// show the old day. Regression or unreadability fails closed.
        /// </summary>
        public static DayCycleReadback Evaluate(
            bool beforeReadable,
            int dayBefore,
            int rentDaysLeftBefore,
            int cashBefore,
            bool afterReadable,
            int dayAfter,
            int rentDaysLeftAfter,
            int cashAfter)
        {
            var readback = new DayCycleReadback
            {
                DayBefore = dayBefore,
                DayAfter = dayAfter,
                RentDaysLeftBefore = rentDaysLeftBefore,
                RentDaysLeftAfter = rentDaysLeftAfter,
                CashBefore = cashBefore,
                CashAfter = cashAfter
            };

            if (!beforeReadable || !afterReadable)
            {
                readback.Readable = false;
                readback.Error = DayCycleErrors.ReadbackUnavailable;
                return readback;
            }

            readback.Readable = true;
            readback.DayAdvanced = dayAfter > dayBefore;
            readback.Changed = dayAfter != dayBefore
                || rentDaysLeftAfter != rentDaysLeftBefore
                || cashAfter != cashBefore;

            if (dayAfter < dayBefore)
            {
                readback.Error = DayCycleErrors.ReadbackRegression;
            }
            else if (rentDaysLeftAfter < 0 || rentDaysLeftAfter > 7)
            {
                readback.Error = DayCycleErrors.ReadbackRegression;
            }
            else if (cashAfter < 0)
            {
                readback.Error = DayCycleErrors.ReadbackRegression;
            }

            return readback;
        }
    }

    /// <summary>Read-only projection used by end_day gating and readback.</summary>
    public sealed class ShopDaySnapshot
    {
        public bool Readable;
        public string Error;
        public bool InGame;
        public bool CanEndDay;
        public int Day;
        public int Cash;
        public int RentDaysLeft;
        public int RentDue;

        /// <summary>Customers already queued (the game asks before dismissing them).</summary>
        public int WaitingCustomers;
    }

    public interface IShopDayPort
    {
        ShopDaySnapshot ReadShopDay();

        /// <summary>Exactly one native EndDay call; never retried.</summary>
        void EndDay();
    }
}
