using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S5 bridge orchestration for the first week: guarded dialogue choices with
    /// a post-select readback, rent receipt detection, and end_day readback.
    ///
    /// The class owns no Unity objects. Every game access goes through a Core
    /// port so the fake-game tests can prove that a disabled/invalid choice never
    /// reaches the native SelectChoice and that a failed readback never reports
    /// success.
    /// </summary>
    public sealed class FirstWeekService
    {
        public const string SourceDialogueChoice = "dialogue_choice";
        public const string SourceStateRead = "state_read";

        private static FirstWeekService current;

        private readonly IDialoguePort dialoguePort;
        private readonly IRentPort rentPort;
        private readonly IShopDayPort shopPort;
        private readonly RentReceiptTracker receipts = new RentReceiptTracker();

        public FirstWeekService(
            IDialoguePort dialoguePort,
            IRentPort rentPort,
            IShopDayPort shopPort)
        {
            this.dialoguePort = dialoguePort;
            this.rentPort = rentPort;
            this.shopPort = shopPort;
        }

        /// <summary>
        /// Wired once by PawnMindMod so /state and the action path share exactly
        /// one receipt tracker (and therefore cannot double-count a payment).
        /// </summary>
        public static FirstWeekService Current
        {
            get { return current; }
        }

        public static void SetCurrent(FirstWeekService service)
        {
            current = service;
        }

        /// <summary>
        /// Called after a controlled load/new game so a pre-load payment cannot
        /// be mistaken for a new payment on the next read.
        /// </summary>
        public void ResetRentReceipts()
        {
            receipts.Reset();
        }

        public Dictionary<string, object> RentState(long revision)
        {
            RentSnapshot snapshot = ReadRentSafe();
            RentObservation observation = receipts.Observe(snapshot, SourceStateRead, revision);
            return FirstWeekJson.Rent(observation);
        }

        public ActionResult ChooseDialogue(int index, long revision)
        {
            const string op = "choose_dialogue";

            DialogueView before = ReadDialogueSafe();
            string validation = DialogueChoicePolicy.Validate(before, index);
            if (validation != null)
            {
                return Failure(op, validation, false, index, null, null, null);
            }

            RentSnapshot rentBefore = ReadRentSafe();
            if (rentBefore.Readable)
            {
                // Establish/refresh the baseline before the native callback. A
                // payment that happened earlier must not be attributed to this
                // choice; only the post-select increase creates a receipt.
                receipts.ObserveBaseline(rentBefore);
            }

            DialogueView after;
            try
            {
                dialoguePort.SelectChoice(index);
            }
            catch (Exception)
            {
                // The native callback may have partially run. Never retry it;
                // report an unknown outcome, with any evidence we can still read.
                after = ReadDialogueSafe();
                bool changedAfterThrow = DialogueChoicePolicy.Changed(before, after);
                RentObservation rentAfterThrow = ObserveRentAfter(rentBefore, revision);
                return OutcomeUnknown(op, DialogueChoiceErrors.OutcomeUnknown, "native_error", true, index,
                    changedAfterThrow, rentAfterThrow, null, null);
            }

            after = ReadDialogueSafe();
            if (after == null || !after.Readable)
            {
                RentObservation rentAfterReadFailure = ObserveRentAfter(rentBefore, revision);
                return OutcomeUnknown(op, DialogueChoiceErrors.OutcomeUnknown,
                    DialogueChoiceErrors.DialogueUnavailable, true, index, false,
                    rentAfterReadFailure, null, null);
            }

            bool changed = DialogueChoicePolicy.Changed(before, after);
            RentObservation rentObservation = ObserveRentAfter(rentBefore, revision);
            string receiptError = rentObservation.Error;
            RentReceipt receipt = string.IsNullOrEmpty(receiptError)
                ? rentObservation.Receipt
                : null;

            if (!changed)
            {
                // The bridge could still read the dialogue and it is exactly the
                // same line. Do not report success; surface any payment evidence
                // separately so the caller can reconcile it.
                return Failure(op, DialogueChoiceErrors.NotApplied, true, index,
                    receipt, receiptError, rentObservation, changed);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["index"] = index;
            row["choice_text"] = before.Choices[index].Text;
            row["dialogue_changed"] = true;
            row["dialogue"] = JObject.FromObject(FirstWeekJson.Dialogue(after));
            row["last_rent_payment"] = receipt == null
                ? JValue.CreateNull()
                : JObject.FromObject(FirstWeekJson.Receipt(receipt));
            row["receipt_error"] = string.IsNullOrEmpty(receiptError)
                ? JValue.CreateNull()
                : new JValue(receiptError);
            row["rent"] = JObject.FromObject(FirstWeekJson.Rent(rentObservation));
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        public ActionResult EndDay(long revision, bool dismissWaiting = false)
        {
            const string op = "end_day";

            ShopDaySnapshot before = ReadShopSafe();
            if (before == null || !before.Readable)
            {
                return Failure(op, DayCycleErrors.ReadbackUnavailable, false, null, null, null, null);
            }
            if (!before.InGame)
            {
                return Failure(op, DayCycleErrors.NotInGame, false, null, null, null, null);
            }
            // The shutter button only works while the shop is open and idle; the
            // inner PlayerStore.EndDay() would silently clear the whole queue.
            if (before.ShutterBusy)
            {
                return Failure(op, "shutter_busy", false, null, null, null, null);
            }
            if (!DayEndPolicy.IsOpenState(before.ShopState))
            {
                return Failure(op, "shop_not_open", false, null, null, null, null);
            }
            if (before.ClientPresent && !dismissWaiting)
            {
                return Failure(op, "client_still_present", false, null, null, null, null);
            }

            string blocked = DayEndPolicy.EvaluateEndDay(
                before.Readable, before.CanEndDay, before.WaitingCustomers, dismissWaiting);
            if (blocked != null)
            {
                return Failure(op, blocked, false, null, null, null, null);
            }

            ShopDaySnapshot after;
            try
            {
                shopPort.EndDay(dismissWaiting);
            }
            catch (Exception)
            {
                after = ReadShopSafe();
                DayCycleReadback thrown = CompareShopDay(before, after);
                return OutcomeUnknown(op, DayCycleErrors.OutcomeUnknown, "native_error", true, null,
                    false, null, null, thrown);
            }

            after = ReadShopSafe();
            DayCycleReadback readback = CompareShopDay(before, after);
            if (!readback.Readable || !string.IsNullOrEmpty(readback.Error))
            {
                return OutcomeUnknown(op, DayCycleErrors.OutcomeUnknown, readback.Error, true, null,
                    false, null, null, readback);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["readback"] = JObject.FromObject(FirstWeekJson.Readback(readback));
            row["can_end_day_after"] = after.CanEndDay;
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        // -----------------------------------------------------------------
        // Read helpers (never throw out of the action path)
        // -----------------------------------------------------------------

        private DialogueView ReadDialogueSafe()
        {
            if (dialoguePort == null)
            {
                return new DialogueView
                {
                    Readable = false,
                    Error = DialogueChoiceErrors.DialogueUnavailable
                };
            }

            try
            {
                return dialoguePort.ReadDialogue();
            }
            catch (Exception)
            {
                return new DialogueView
                {
                    Readable = false,
                    Error = DialogueChoiceErrors.DialogueUnavailable
                };
            }
        }

        private RentSnapshot ReadRentSafe()
        {
            if (rentPort == null)
            {
                return new RentSnapshot
                {
                    Readable = false,
                    Error = RentErrors.RentUnavailable
                };
            }

            try
            {
                return rentPort.ReadRent();
            }
            catch (Exception)
            {
                return new RentSnapshot
                {
                    Readable = false,
                    Error = RentErrors.RentUnavailable
                };
            }
        }

        private ShopDaySnapshot ReadShopSafe()
        {
            if (shopPort == null)
            {
                return new ShopDaySnapshot
                {
                    Readable = false,
                    Error = DayCycleErrors.ReadbackUnavailable
                };
            }

            try
            {
                return shopPort.ReadShopDay();
            }
            catch (Exception)
            {
                return new ShopDaySnapshot
                {
                    Readable = false,
                    Error = DayCycleErrors.ReadbackUnavailable
                };
            }
        }

        private RentObservation ObserveRentAfter(RentSnapshot before, long revision)
        {
            RentSnapshot after = ReadRentSafe();
            if (before == null || !before.Readable)
            {
                // Without a readable pre-payment baseline this choice cannot be
                // credited with a payment. Leave the tracker untouched so the
                // next state read can detect it against the older baseline.
                return new RentObservation
                {
                    Snapshot = after,
                    LastReceipt = receipts.LastReceipt,
                    Error = RentErrors.ReceiptUnavailable,
                    BaselineEstablished = receipts.BaselineEstablished
                };
            }

            return receipts.Observe(after, SourceDialogueChoice, revision);
        }

        private static DayCycleReadback CompareShopDay(ShopDaySnapshot before, ShopDaySnapshot after)
        {
            return DayCycleRules.Evaluate(
                before != null && before.Readable,
                before == null ? 0 : before.Day,
                before == null ? 0 : before.RentDaysLeft,
                before == null ? 0 : before.Cash,
                after != null && after.Readable,
                after == null ? 0 : after.Day,
                after == null ? 0 : after.RentDaysLeft,
                after == null ? 0 : after.Cash);
        }

        // -----------------------------------------------------------------
        // Response helpers: all failures are explicit ok:false JSON
        // -----------------------------------------------------------------

        private static ActionResult Failure(
            string op,
            string error,
            bool dispatched,
            int? index,
            RentReceipt receipt,
            string receiptError,
            RentObservation rent)
        {
            return Failure(op, error, dispatched, index, receipt, receiptError, rent, null);
        }

        private static ActionResult Failure(
            string op,
            string error,
            bool dispatched,
            int? index,
            RentReceipt receipt,
            string receiptError,
            RentObservation rent,
            bool? dialogueChanged)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = op;
            row["error"] = error;
            row["dispatched"] = dispatched;
            if (index.HasValue)
            {
                row["index"] = index.Value;
                if (dialogueChanged.HasValue) row["dialogue_changed"] = dialogueChanged.Value;
            }

            row["last_rent_payment"] = receipt == null
                ? JValue.CreateNull()
                : JObject.FromObject(FirstWeekJson.Receipt(receipt));
            row["receipt_error"] = string.IsNullOrEmpty(receiptError)
                ? JValue.CreateNull()
                : new JValue(receiptError);
            if (rent != null) row["rent"] = JObject.FromObject(FirstWeekJson.Rent(rent));
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private static ActionResult OutcomeUnknown(
            string op,
            string errorCode,
            string detail,
            bool dispatched,
            int? index,
            bool? dialogueChanged,
            RentObservation rent,
            string receiptError,
            DayCycleReadback readback)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = op;
            row["error"] = string.IsNullOrEmpty(errorCode)
                ? DialogueChoiceErrors.OutcomeUnknown
                : errorCode;
            row["detail"] = string.IsNullOrEmpty(detail)
                ? row["error"]
                : detail;
            row["unknown"] = true;
            row["dispatched"] = dispatched;
            if (index.HasValue)
            {
                row["index"] = index.Value;
                if (dialogueChanged.HasValue) row["dialogue_changed"] = dialogueChanged.Value;
            }

            row["last_rent_payment"] = rent == null || rent.Receipt == null
                ? JValue.CreateNull()
                : JObject.FromObject(FirstWeekJson.Receipt(rent.Receipt));
            row["receipt_error"] = string.IsNullOrEmpty(receiptError)
                ? JValue.CreateNull()
                : new JValue(receiptError);
            if (rent != null) row["rent"] = JObject.FromObject(FirstWeekJson.Rent(rent));
            if (readback != null) row["readback"] = JObject.FromObject(FirstWeekJson.Readback(readback));
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }
    }
}
