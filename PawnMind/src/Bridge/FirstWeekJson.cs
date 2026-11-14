using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S5 state projection. Only fields a player can see are serialized: dialogue
    /// text/enabled state and rent counters. Hidden dialogue conditions, appraisal
    /// answers and save internals are intentionally absent.
    /// </summary>
    public static class FirstWeekJson
    {
        public static Dictionary<string, object> Dialogue(DialogueView view)
        {
            var row = new Dictionary<string, object>();
            var choices = new List<Dictionary<string, object>>();

            if (view == null)
            {
                row["readable"] = false;
                row["error"] = DialogueChoiceErrors.DialogueUnavailable;
                row["available"] = false;
                row["choice"] = false;
                row["ended"] = true;
                row["typing"] = false;
                row["speaker"] = null;
                row["text"] = null;
                row["choices"] = choices;
                return row;
            }

            row["readable"] = view.Readable;
            if (!view.Readable)
            {
                row["error"] = string.IsNullOrEmpty(view.Error)
                    ? DialogueChoiceErrors.DialogueUnavailable
                    : view.Error;
            }
            else
            {
                row["error"] = null;
            }
            row["available"] = view.Available;
            row["choice"] = view.Available && view.Choice;
            row["ended"] = view.Ended;
            row["typing"] = view.Typing;
            row["speaker"] = view.Speaker;
            row["text"] = view.Text;

            for (int i = 0; i < view.Choices.Count; i++)
            {
                DialogueChoiceView choice = view.Choices[i];
                if (choice == null) continue;

                var cell = new Dictionary<string, object>();
                cell["index"] = choice.Index;
                cell["text"] = choice.Text ?? string.Empty;
                cell["enabled"] = choice.Enabled;
                if (choice.HasItem)
                {
                    cell["item_id"] = choice.ItemId;
                    cell["item_name"] = choice.ItemName;
                }

                choices.Add(cell);
            }

            row["choices"] = choices;
            return row;
        }

        public static Dictionary<string, object> Rent(RentObservation observation)
        {
            var row = new Dictionary<string, object>();
            RentSnapshot snapshot = observation == null ? null : observation.Snapshot;

            if (snapshot == null || !snapshot.Readable)
            {
                row["readable"] = false;
                row["error"] = RentErrors.RentUnavailable;
                row["receipt_error"] = RentErrors.ReceiptUnavailable;
                row["last_payment"] = null;
                return row;
            }

            row["readable"] = true;
            row["error"] = null;
            row["due"] = snapshot.Due;
            row["rent_value"] = snapshot.RentValue;
            row["temporary_rent"] = snapshot.TemporaryRent;
            row["days_left"] = snapshot.DaysLeft;
            row["days_until_rent"] = snapshot.DaysUntilRent;
            row["can_pay"] = snapshot.CanPay;
            row["rent_paid_total"] = snapshot.RentPaidTotal;
            row["rent_weeks_paid"] = snapshot.RentWeeksPaid;
            row["rent_weeks_missed"] = snapshot.RentWeeksMissed;
            row["starting_rent"] = snapshot.StartingRent;
            row["cash"] = snapshot.Cash;
            row["day"] = snapshot.Day;

            RentReceipt receipt = observation.LastReceipt;
            row["last_payment"] = receipt == null ? null : Receipt(receipt);
            row["receipt_error"] = observation.Error;
            return row;
        }

        public static Dictionary<string, object> Receipt(RentReceipt receipt)
        {
            var row = new Dictionary<string, object>();
            if (receipt == null) return row;

            row["id"] = receipt.Id;
            row["amount"] = receipt.Amount;
            row["cash_before"] = receipt.CashBefore;
            row["cash_after"] = receipt.CashAfter;
            row["cash_delta"] = receipt.CashDelta;
            row["rent_days_left_before"] = receipt.RentDaysLeftBefore;
            row["rent_days_left_after"] = receipt.RentDaysLeftAfter;
            row["paid_at_rev"] = receipt.PaidAtRev;
            row["source"] = receipt.Source;
            row["day"] = receipt.Day;
            row["weeks_paid_delta"] = receipt.WeeksPaidDelta;
            row["rent_weeks_paid"] = receipt.RentWeeksPaid;
            row["rent_weeks_missed"] = receipt.RentWeeksMissed;
            row["due_before"] = receipt.DueBefore;
            row["day_before"] = receipt.DayBefore;
            row["detection_complete"] = receipt.DetectionComplete;
            return row;
        }

        public static Dictionary<string, object> Readback(DayCycleReadback readback)
        {
            var row = new Dictionary<string, object>();
            if (readback == null)
            {
                row["readable"] = false;
                row["error"] = DayCycleErrors.ReadbackUnavailable;
                return row;
            }

            row["readable"] = readback.Readable;
            row["error"] = readback.Error;
            row["day_before"] = readback.DayBefore;
            row["day_after"] = readback.DayAfter;
            row["rent_days_left_before"] = readback.RentDaysLeftBefore;
            row["rent_days_left_after"] = readback.RentDaysLeftAfter;
            row["cash_before"] = readback.CashBefore;
            row["cash_after"] = readback.CashAfter;
            row["day_advanced"] = readback.DayAdvanced;
            row["changed"] = readback.Changed;
            return row;
        }
    }
}
