using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>Completion evidence returned by exactly one native accept click.</summary>
    public sealed class TradeEvidence
    {
        /// <summary>The native call was invoked (not a pre-dispatch refusal).</summary>
        public bool Called;

        /// <summary>Native error, if the call reported one.</summary>
        public string Error;

        /// <summary>StoreClient.isBusinessComplete after the click.</summary>
        public bool BusinessComplete;

        /// <summary>True when the same StoreClient instance is still at the counter.</summary>
        public bool ClientUnchanged;

        /// <summary>Supporting evidence: the staged item left the table / ownership flipped.</summary>
        public bool ItemsTransferred;

        public long CashBefore;
        public long CashAfter;
        public int StagedItemIdAfter;
    }

    /// <summary>
    /// Read/dispatch boundary for a trade. The real adapter is in src/Game; the
    /// offline suite injects a fake, so "dispatch zero times" is a countable fact.
    /// </summary>
    public interface ITradePort
    {
        /// <summary>Read-only pre-dispatch snapshot. Must not click anything.</summary>
        TradeGuardInput Read(int itemId, string side);

        /// <summary>Exactly one native "accept offer" click.</summary>
        TradeEvidence Accept();
    }

    /// <summary>
    /// C module: confirm_trade with a complete pre-dispatch guard (B10-B16) and
    /// layered completion evidence. The trade operation ends at the commit; the
    /// closing dialogue is a separate workflow (advance_dialogue) and is never
    /// required for the trade to be reported completed.
    ///
    /// Evidence rules (R5/A3):
    ///  - ok:true only when the native accept was called without error, the same
    ///    client is still at the counter, and this trade left per-trade evidence
    ///    (the staged item moved OR cash moved);
    ///  - business_complete / visit_complete are attribution context only: a
    ///    finished visit with zero item/cash movement is uncertain, never success;
    ///  - business_complete=false does not block a real partial trade (R4 verified:
    ///    a customer buying several items is visit-open after every sale);
    ///  - a silent native return stays uncertain, never success;
    ///  - a cash delta proves a purchase; barter completes on the item with no cash.
    /// </summary>
    public sealed class TradeService
    {
        private const string Op = "confirm_trade";

        private readonly ITradePort _port;

        public TradeService(ITradePort port)
        {
            if (port == null) throw new ArgumentNullException("port");
            _port = port;
        }

        public ActionResult Confirm(int itemId, string side, long revision)
        {
            TradeGuardInput guard;
            try
            {
                guard = _port.Read(itemId, side);
            }
            catch (Exception)
            {
                return Refusal(TradeErrors.ReadFailed, itemId, side, revision);
            }

            if (guard == null) return Refusal(TradeErrors.ReadFailed, itemId, side, revision);

            string refusal = TradeGuardPolicy.Evaluate(guard);
            if (refusal != null) return Refusal(refusal, itemId, side, revision);

            TradeEvidence evidence;
            try
            {
                evidence = _port.Accept();
            }
            catch (Exception)
            {
                return Uncertain(TradeErrors.OutcomeUnverified, "native_error",
                    itemId, side, revision, guard, null);
            }

            if (evidence == null || !evidence.Called)
            {
                return Uncertain(TradeErrors.OutcomeUnverified, "native_not_called",
                    itemId, side, revision, guard, evidence);
            }

            if (!string.IsNullOrEmpty(evidence.Error))
            {
                return Uncertain(TradeErrors.OutcomeUnverified, "native_error",
                    itemId, side, revision, guard, evidence);
            }

            // R5/A3: two different facts.
            //   business_complete = the customer finished their whole visit;
            //   this-trade evidence  = the item left the table OR money moved.
            // A finished visit does not prove that this trade happened: the native
            // accept can finish the visit with nothing moving (R5_TRADE_01). A
            // customer buying several items is "visit open" after each sale, so
            // requiring business_complete made a real sale look unproven (and, being
            // non-terminal, it then blocked every later confirm_trade). R4 live
            // behavior stays: business_complete is attribution, per-trade evidence
            // is the success criterion.
            //
            // A purchase keeps `currentNegociatedItem` on the bought item, so the
            // staged-id pointer alone is not enough for the buy direction: the cash
            // delta is the other half of the same fact.
            bool cashMoved = evidence.CashBefore != evidence.CashAfter;
            bool tradeProven = evidence.ItemsTransferred || cashMoved;

            if (!evidence.ClientUnchanged)
            {
                // PP-TRADE-08: another client's state change must not back this trade.
                return Uncertain(TradeErrors.ClientChanged, "client_identity_changed",
                    itemId, side, revision, guard, evidence);
            }

            if (!tradeProven)
            {
                // No per-trade evidence. The visit fact (if any) stays attribution
                // context; the honest outcome is "uncertain", never ok:true.
                string detail = evidence.BusinessComplete
                    ? "no_trade_evidence"
                    : "business_not_complete";
                return Uncertain(TradeErrors.OutcomeUnverified, detail,
                    itemId, side, revision, guard, evidence);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = Op;
            row["dispatched"] = true;
            row["execution_status"] = ExecutionStatus.Completed;
            row["item_id"] = itemId;
            row["side"] = TradeGuardPolicy.IsSell(side) ? "sell" : "buy";
            row["client_intent"] = guard.ClientIntent;
            row["business_complete"] = evidence.BusinessComplete;
            row["visit_complete"] = evidence.BusinessComplete;
            row["items_transferred"] = evidence.ItemsTransferred;
            row["cash_moved"] = evidence.CashBefore != evidence.CashAfter;
            row["client_unchanged"] = true;
            row["staged_item_id"] = guard.StagedItemId;
            row["cash_before"] = evidence.CashBefore;
            row["cash_after"] = evidence.CashAfter;
            // Barter can complete with zero cash movement: the cash delta is
            // supporting evidence, never the success criterion.
            row["cash_changed"] = evidence.CashBefore != evidence.CashAfter;
            row["evidence"] = EvidenceJson(evidence);
            row["rev"] = revision;
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private ActionResult Refusal(string error, int itemId, string side, long revision)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = Op;
            row["error"] = error;
            row["dispatched"] = false;
            row["execution_status"] = ExecutionStatus.Rejected;
            row["item_id"] = itemId;
            row["side"] = side;
            row["rev"] = revision;
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private ActionResult Uncertain(string error, string detail, int itemId, string side,
            long revision, TradeGuardInput guard, TradeEvidence evidence)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = Op;
            row["error"] = error;
            row["detail"] = detail;
            row["dispatched"] = true;
            row["unknown"] = true;
            row["execution_status"] = ExecutionStatus.Uncertain;
            row["item_id"] = itemId;
            row["side"] = side;
            if (guard != null)
            {
                row["client_intent"] = guard.ClientIntent;
                row["staged_item_id"] = guard.StagedItemId;
            }

            if (evidence != null)
            {
                // Same evidence contract as a completed trade: the visit facts stay
                // attribution context and the per-trade booleans are reported
                // truthfully at the top level, but none of them is promoted to
                // success (ok stays false, execution_status stays uncertain).
                row["business_complete"] = evidence.BusinessComplete;
                row["visit_complete"] = evidence.BusinessComplete;
                row["client_unchanged"] = evidence.ClientUnchanged;
                row["items_transferred"] = evidence.ItemsTransferred;
                row["cash_moved"] = evidence.CashBefore != evidence.CashAfter;
            }

            row["evidence"] = EvidenceJson(evidence);
            row["next"] = "the native accept click returned without trusted completion"
                + " evidence: do not retry with a new action id. Read get_state"
                + " (business_complete / client_items) and decide.";
            row["rev"] = revision;
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private static JObject EvidenceJson(TradeEvidence evidence)
        {
            if (evidence == null) return new JObject();

            var row = new JObject();
            row["called"] = evidence.Called;
            row["error"] = evidence.Error == null ? JValue.CreateNull() : new JValue(evidence.Error);
            row["business_complete"] = evidence.BusinessComplete;
            row["client_unchanged"] = evidence.ClientUnchanged;
            row["items_transferred"] = evidence.ItemsTransferred;
            row["cash_moved"] = evidence.CashBefore != evidence.CashAfter;
            row["cash_before"] = evidence.CashBefore;
            row["cash_after"] = evidence.CashAfter;
            row["staged_item_id_after"] = evidence.StagedItemIdAfter;
            return row;
        }
    }
}
