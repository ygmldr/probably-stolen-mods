using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>Completion evidence returned by exactly one native staging call.</summary>
    public sealed class StagingEvidence
    {
        /// <summary>The native staging primitive was invoked (not a pre-dispatch refusal).</summary>
        public bool Called;

        /// <summary>Native error, if the call reported one.</summary>
        public string Error;

        public int StagedItemIdBefore;
        public int StagedItemIdAfter;

        /// <summary>The post-dispatch staged pointer was readable.</summary>
        public bool AfterReadable;

        public bool NegotiationOpenAfter;
        public bool NegotiationReadableAfter;
        public bool ItemOnCounterAfter;

        /// <summary>In-process StoreClient identity (never serialized).</summary>
        public int ClientHandleBefore;
        public int ClientHandleAfter;
        public bool ClientReadableAfter;

        /// <summary>Which native primitive ran (see <see cref="StagingPrimitives"/>).</summary>
        public string NativePrimitive;
    }

    /// <summary>
    /// Read/dispatch boundary for one staging step. The real adapter is in
    /// src/Game; the offline suite injects a fake, so "dispatch zero times" is a
    /// countable fact.
    /// </summary>
    public interface INegotiationStagingPort
    {
        /// <summary>Read-only pre-dispatch snapshot. Must not move anything.</summary>
        StagingGuardInput Read(int itemId);

        /// <summary>
        /// Exactly one player-equivalent staging: a native accept onto the
        /// weighted/back counter, or a native remove+add when the item is already
        /// there. Never assigns currentNegociatedItem / ownership / price directly.
        /// </summary>
        StagingEvidence Stage(int itemId);
    }

    /// <summary>
    /// R5-B1: first-class player-equivalent negotiation staging.
    ///
    /// Rules:
    ///  - a rejected pre-check dispatches nothing and keeps the operation a safe
    ///    rejection (dispatched=false);
    ///  - the success criterion is the native readback: the requested item is the
    ///    staged item, it physically sits on the weighted counter, the negotiation
    ///    UI is open and the client identity did not change across the call;
    ///  - a silent native return / mismatched or unreadable readback is uncertain,
    ///    never completed: the LLM must read state instead of retrying blindly;
    ///  - ownership, the staged pointer, the client intent and every price are
    ///    only read here. The native callbacks do the staging.
    /// </summary>
    public sealed class NegotiationStagingService
    {
        private readonly INegotiationStagingPort _port;

        public NegotiationStagingService(INegotiationStagingPort port)
        {
            if (port == null) throw new ArgumentNullException("port");
            _port = port;
        }

        public ActionResult Stage(int itemId, long revision)
        {
            StagingGuardInput guard;
            try
            {
                guard = _port.Read(itemId);
            }
            catch (Exception)
            {
                return Refusal(StagingErrors.ReadFailed, "pre_read_failed", itemId, revision, null);
            }

            if (guard == null)
                return Refusal(StagingErrors.ReadFailed, "pre_read_missing", itemId, revision, null);

            string refusal = StagingGuardPolicy.Evaluate(guard);
            if (refusal != null)
                return Refusal(refusal, DetailFor(refusal), itemId, revision, guard);

            StagingEvidence evidence;
            try
            {
                evidence = _port.Stage(itemId);
            }
            catch (Exception)
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "native_error",
                    itemId, revision, guard, null);
            }

            if (evidence == null || !evidence.Called)
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "native_not_called",
                    itemId, revision, guard, evidence);
            }

            if (!string.IsNullOrEmpty(evidence.Error))
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "native_error",
                    itemId, revision, guard, evidence);
            }

            if (!evidence.AfterReadable)
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "readback_unreadable",
                    itemId, revision, guard, evidence);
            }

            bool? clientUnchanged = ClientUnchanged(evidence);
            if (clientUnchanged == null)
            {
                // A staging step must never be attributed across an unreadable
                // client identity: the counter may belong to another customer.
                return Uncertain(StagingErrors.OutcomeUnverified, "client_identity_unreadable",
                    itemId, revision, guard, evidence);
            }

            if (!clientUnchanged.Value)
            {
                return Uncertain(StagingErrors.ClientChanged, "client_identity_changed",
                    itemId, revision, guard, evidence);
            }

            if (evidence.StagedItemIdAfter != itemId)
            {
                // The native path returned without making the requested item the
                // staged one (silent refusal or a different pointer): honest
                // outcome is uncertain, never ok:true.
                return Uncertain(StagingErrors.OutcomeUnverified, "staged_item_mismatch",
                    itemId, revision, guard, evidence);
            }

            if (!evidence.ItemOnCounterAfter)
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "item_not_on_counter",
                    itemId, revision, guard, evidence);
            }

            if (!evidence.NegotiationReadableAfter)
            {
                return Uncertain(StagingErrors.OutcomeUnverified, "negotiation_unreadable",
                    itemId, revision, guard, evidence);
            }

            if (!evidence.NegotiationOpenAfter)
            {
                // The item is staged but the tablet is not visible: a player could
                // not trade from it and confirm_trade would refuse no_negotiation.
                return Uncertain(StagingErrors.OutcomeUnverified, "negotiation_not_open",
                    itemId, revision, guard, evidence);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = StagingOps.Name;
            row["dispatched"] = true;
            row["execution_status"] = ExecutionStatus.Completed;
            row["item_id"] = itemId;
            row["staged_item_id_before"] = evidence.StagedItemIdBefore;
            row["staged_item_id_after"] = evidence.StagedItemIdAfter;
            row["negotiation_open"] = true;
            row["client_identity_unchanged"] = true;
            row["item_on_weighted_counter"] = true;
            row["client_intent"] = guard.ClientIntent;
            row["native_primitive"] = Primitive(evidence);
            row["evidence"] = EvidenceJson(evidence);
            row["rev"] = revision;
            return ActionResult.SuccessRaw(StagingOps.Name, row.ToString(Formatting.None));
        }

        private static bool? ClientUnchanged(StagingEvidence evidence)
        {
            if (!evidence.ClientReadableAfter) return null;
            if (evidence.ClientHandleBefore == 0) return null;
            return evidence.ClientHandleBefore == evidence.ClientHandleAfter;
        }

        private ActionResult Refusal(string error, string detail, int itemId, long revision,
            StagingGuardInput guard)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = StagingOps.Name;
            row["error"] = error;
            row["detail"] = detail;
            row["dispatched"] = false;
            row["execution_status"] = ExecutionStatus.Rejected;
            row["item_id"] = itemId;
            row["staged_item_id_before"] = guard != null && guard.StagedReadable
                ? new JValue(guard.StagedItemId)
                : JValue.CreateNull();
            // Nothing was dispatched, so no post-state was observed: reporting a
            // guessed "after" would be a fabricated readback.
            row["staged_item_id_after"] = JValue.CreateNull();
            row["negotiation_open"] = guard != null && guard.NegotiationReadable
                ? new JValue(guard.NegotiationOpen)
                : JValue.CreateNull();
            row["client_identity_unchanged"] = JValue.CreateNull();
            if (guard != null && !string.IsNullOrEmpty(guard.ClientIntent))
                row["client_intent"] = guard.ClientIntent;
            row["rev"] = revision;
            return ActionResult.SuccessRaw(StagingOps.Name, row.ToString(Formatting.None));
        }

        private ActionResult Uncertain(string error, string detail, int itemId, long revision,
            StagingGuardInput guard, StagingEvidence evidence)
        {
            bool afterReadable = evidence != null && evidence.AfterReadable;

            var row = new JObject();
            row["ok"] = false;
            row["op"] = StagingOps.Name;
            row["error"] = error;
            row["detail"] = detail;
            row["dispatched"] = true;
            row["uncertain"] = true;
            row["execution_status"] = ExecutionStatus.Uncertain;
            row["item_id"] = itemId;

            int? before = null;
            if (evidence != null)
                before = evidence.StagedItemIdBefore;
            else if (guard != null && guard.StagedReadable)
                before = guard.StagedItemId;

            row["staged_item_id_before"] = before.HasValue
                ? new JValue(before.Value)
                : JValue.CreateNull();
            row["staged_item_id_after"] = afterReadable
                ? new JValue(evidence.StagedItemIdAfter)
                : JValue.CreateNull();
            row["negotiation_open"] = afterReadable && evidence.NegotiationReadableAfter
                ? new JValue(evidence.NegotiationOpenAfter)
                : JValue.CreateNull();
            row["client_identity_unchanged"] = afterReadable
                ? (ClientUnchanged(evidence).HasValue
                    ? new JValue(ClientUnchanged(evidence).Value)
                    : JValue.CreateNull())
                : JValue.CreateNull();

            if (evidence != null)
            {
                row["item_on_weighted_counter"] = evidence.ItemOnCounterAfter;
                row["native_primitive"] = Primitive(evidence);
            }

            if (guard != null && !string.IsNullOrEmpty(guard.ClientIntent))
                row["client_intent"] = guard.ClientIntent;

            row["evidence"] = EvidenceJson(evidence);
            row["next"] = "the staging attempt ran but its outcome is not proven: do not retry"
                + " the same item with a new action_id; read get_state.negotiated_item and"
                + " decide, or stage a different item";
            row["rev"] = revision;
            return ActionResult.SuccessRaw(StagingOps.Name, row.ToString(Formatting.None));
        }

        private static JObject EvidenceJson(StagingEvidence evidence)
        {
            if (evidence == null) return new JObject();

            var row = new JObject();
            row["called"] = evidence.Called;
            row["error"] = evidence.Error == null ? JValue.CreateNull() : new JValue(evidence.Error);
            row["staged_item_id_before"] = evidence.StagedItemIdBefore;
            row["staged_item_id_after_readable"] = evidence.AfterReadable;
            if (evidence.AfterReadable) row["staged_item_id_after"] = evidence.StagedItemIdAfter;
            row["item_on_weighted_counter"] = evidence.ItemOnCounterAfter;
            row["negotiation_readable_after"] = evidence.NegotiationReadableAfter;
            row["negotiation_open_after"] = evidence.NegotiationOpenAfter;
            row["client_identity_unchanged"] = ClientUnchanged(evidence).HasValue
                ? new JValue(ClientUnchanged(evidence).Value)
                : JValue.CreateNull();
            row["native_primitive"] = Primitive(evidence);
            return row;
        }

        private static string Primitive(StagingEvidence evidence)
        {
            if (evidence == null || string.IsNullOrEmpty(evidence.NativePrimitive))
                return StagingPrimitives.None;
            return evidence.NativePrimitive;
        }

        private static string DetailFor(string error)
        {
            switch (error)
            {
                case StagingErrors.NotInGame: return "no_player_store";
                case StagingErrors.NoClient: return "no_current_client";
                case StagingErrors.ClientUnreadable: return "client_intent_unreadable";
                case StagingErrors.ReadFailed: return "staged_item_unreadable";
                case StagingErrors.NegotiationUnreadable: return "negotiation_ui_unwired";
                case StagingErrors.ModalOpen: return "blocking_modal_open";
                case StagingErrors.ItemNotFound: return "item_not_found";
                case StagingErrors.ItemSold: return "item_in_sold_container";
                case StagingErrors.ItemUnreadable: return "item_state_unreadable";
                case StagingErrors.ItemUnreachable: return "item_container_not_reachable";
                case StagingErrors.NotOwned: return "client_buys_an_unowned_item";
                case StagingErrors.AlreadyOwned: return "client_sells_an_owned_item";
                case StagingErrors.IntentUndefined: return "client_intent_undefined";
                case StagingErrors.BackCounterUnreadable: return "weighted_counter_unreadable";
                case StagingErrors.BackCounterFull: return "weighted_counter_rejects_item";
                default: return error;
            }
        }
    }
}
