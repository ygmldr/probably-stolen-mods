namespace PawnMind.Core
{
    /// <summary>
    /// Stable S5 rent error codes. A missing or contradictory counter is never
    /// turned into a fabricated payment.
    /// </summary>
    public static class RentErrors
    {
        public const string RentUnavailable = "rent_unavailable";
        public const string ReceiptUnavailable = "receipt_unavailable";
    }

    /// <summary>
    /// Read-only rent projection. DaysLeft is the game's native
    /// GetRentDayCounter value; DaysUntilRent is the stored dayUntilRent value
    /// that the landlord payment resets. Both are player-visible countdowns.
    /// </summary>
    public sealed class RentSnapshot
    {
        public bool Readable;
        public string Error;
        public int Due;
        public int RentValue;
        public int TemporaryRent;
        public int DaysLeft;
        public int DaysUntilRent;
        public bool CanPay;
        public int RentPaidTotal;
        public int RentWeeksPaid;
        public int RentWeeksMissed;
        public int StartingRent;
        public int Cash;
        public int Day;
        // Opaque run identity used only to reset the tracker after a load/new
        // game. It is never serialized to the model.
        public string RunId;
    }

    /// <summary>
    /// One detected rent payment. The tracker creates this only when the
    /// RunStats counters increase after a readable baseline, so the same payment
    /// cannot be counted twice.
    /// </summary>
    public sealed class RentReceipt
    {
        public string Id;
        public int Amount;
        public int CashBefore;
        public int CashAfter;
        public int CashDelta;
        public int RentDaysLeftBefore;
        public int RentDaysLeftAfter;
        public long PaidAtRev;
        public string Source;
        public int Day;
        public int WeeksPaidDelta;
        public int RentWeeksPaid;
        public int RentWeeksMissed;
        public int DueBefore;
        public int DayBefore;
        public bool DetectionComplete;
    }

    public sealed class RentObservation
    {
        public RentSnapshot Snapshot;
        public RentReceipt Receipt;
        public RentReceipt LastReceipt;
        public string Error;
        public bool BaselineEstablished;
    }

    /// <summary>
    /// Pure detector for first-rent receipts. It observes rent snapshots over
    /// time; the first readable read is only a baseline. A later increase of
    /// RunStats.rentPaid/rentWeeksPaid creates exactly one receipt. A decrease
    /// (load/new run) resets the baseline instead of inventing a receipt.
    /// </summary>
    public sealed class RentReceiptTracker
    {
        private RentSnapshot last;
        private RentReceipt lastReceipt;
        private bool baseline;
        private string lastError;

        public bool BaselineEstablished
        {
            get { return baseline; }
        }

        public RentReceipt LastReceipt
        {
            get { return lastReceipt; }
        }

        public string LastError
        {
            get { return lastError; }
        }

        /// <summary>
        /// Refreshes the comparison baseline without creating a receipt. Used
        /// immediately before a native choice so only a counter increase caused
        /// by that call is attributed to it.
        /// </summary>
        public void ObserveBaseline(RentSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Readable) return;
            last = Clone(snapshot);
            baseline = true;
            lastError = null;
        }

        public RentObservation Observe(RentSnapshot snapshot, string source, long revision)
        {
            var observation = new RentObservation
            {
                Snapshot = snapshot,
                LastReceipt = lastReceipt,
                BaselineEstablished = baseline
            };

            if (snapshot == null || !snapshot.Readable)
            {
                lastError = RentErrors.ReceiptUnavailable;
                observation.Error = lastError;
                return observation;
            }

            bool runChanged = last != null
                && !string.IsNullOrEmpty(snapshot.RunId)
                && !string.IsNullOrEmpty(last.RunId)
                && !string.Equals(snapshot.RunId, last.RunId, System.StringComparison.Ordinal);
            bool reset = !baseline
                || runChanged
                || snapshot.RentWeeksPaid < last.RentWeeksPaid
                || snapshot.RentPaidTotal < last.RentPaidTotal
                || snapshot.Day < last.Day;

            if (reset)
            {
                last = Clone(snapshot);
                baseline = true;
                lastReceipt = null;
                lastError = null;
                observation.LastReceipt = null;
                observation.BaselineEstablished = true;
                return observation;
            }

            int paidDelta = snapshot.RentPaidTotal - last.RentPaidTotal;
            int weeksDelta = snapshot.RentWeeksPaid - last.RentWeeksPaid;
            RentReceipt receipt = null;
            string error = null;

            if (weeksDelta > 0 || paidDelta > 0)
            {
                if (paidDelta <= 0)
                {
                    // Weeks advanced without an observable amount. Do not guess
                    // and do not keep showing an older receipt as if it were new.
                    error = RentErrors.ReceiptUnavailable;
                    lastReceipt = null;
                }
                else
                {
                    receipt = new RentReceipt
                    {
                        Id = "rent_" + snapshot.RentWeeksPaid + "_" + snapshot.RentPaidTotal,
                        Amount = paidDelta,
                        CashBefore = last.Cash,
                        CashAfter = snapshot.Cash,
                        CashDelta = snapshot.Cash - last.Cash,
                        RentDaysLeftBefore = last.DaysUntilRent,
                        RentDaysLeftAfter = snapshot.DaysUntilRent,
                        PaidAtRev = revision,
                        Source = string.IsNullOrEmpty(source) ? "observed" : source,
                        Day = snapshot.Day,
                        WeeksPaidDelta = weeksDelta,
                        RentWeeksPaid = snapshot.RentWeeksPaid,
                        RentWeeksMissed = snapshot.RentWeeksMissed,
                        DueBefore = last.Due,
                        DayBefore = last.Day,
                        DetectionComplete = weeksDelta == 1
                    };
                    lastReceipt = receipt;
                }
            }

            last = Clone(snapshot);
            lastError = error;

            observation.Receipt = receipt;
            observation.LastReceipt = lastReceipt;
            observation.Error = error;
            observation.BaselineEstablished = true;
            return observation;
        }

        public void Reset()
        {
            last = null;
            lastReceipt = null;
            baseline = false;
            lastError = null;
        }

        private static RentSnapshot Clone(RentSnapshot source)
        {
            return new RentSnapshot
            {
                Readable = source.Readable,
                Error = source.Error,
                Due = source.Due,
                RentValue = source.RentValue,
                TemporaryRent = source.TemporaryRent,
                DaysLeft = source.DaysLeft,
                DaysUntilRent = source.DaysUntilRent,
                CanPay = source.CanPay,
                RentPaidTotal = source.RentPaidTotal,
                RentWeeksPaid = source.RentWeeksPaid,
                RentWeeksMissed = source.RentWeeksMissed,
                StartingRent = source.StartingRent,
                Cash = source.Cash,
                Day = source.Day,
                RunId = source.RunId
            };
        }
    }

    public interface IRentPort
    {
        RentSnapshot ReadRent();
    }
}
