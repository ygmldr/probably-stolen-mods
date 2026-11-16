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
    /// Evidence rules:
    ///  - ok:true only when business_complete is observed for the same client;
    ///  - a silent native return stays uncertain, never success;
    ///  - a cash delta is supporting evidence only: barter completes with no cash.
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

            if (!evidence.BusinessComplete)
            {
                // PP-TRADE-06: the native button returned without completing the
                // business. Never report ok:true on that.
                return Uncertain(TradeErrors.OutcomeUnverified, "business_not_complete",
                    itemId, side, revision, guard, evidence);
            }

            if (!evidence.ClientUnchanged)
            {
                // PP-TRADE-08: another client's state change must not back this trade.
                return Uncertain(TradeErrors.ClientChanged, "client_identity_changed",
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
            row["business_complete"] = true;
            row["client_unchanged"] = true;
            row["staged_item_id"] = guard.StagedItemId;
            row["items_transferred"] = evidence.ItemsTransferred;
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
            row["cash_before"] = evidence.CashBefore;
            row["cash_after"] = evidence.CashAfter;
            row["staged_item_id_after"] = evidence.StagedItemIdAfter;
            return row;
        }
    }
}
