using System;

namespace PawnMind.Core
{
    /// <summary>
    /// D module: the write path has two independent axes.
    ///
    /// execution_status describes what happened to the game/business action;
    /// delivery_status describes whether the caller received the answer. A lost
    /// HTTP response must only change the delivery axis: the completed business
    /// fact stays completed.
    /// </summary>
    public static class ExecutionStatus
    {
        /// <summary>Registered, not dispatched yet. Only visible inside one HTTP turn.</summary>
        public const string Prepared = "prepared";

        /// <summary>Dispatched; the native flow is still running (animation/async).</summary>
        public const string Running = "running";

        /// <summary>Trusted evidence that the action did what it promised.</summary>
        public const string Completed = "completed";

        /// <summary>A guard refused before dispatch: nothing ran.</summary>
        public const string Rejected = "rejected";

        /// <summary>Dispatched and the action failed; no business completion.</summary>
        public const string Failed = "failed";

        /// <summary>Dispatched but the outcome cannot be observed: never retry blindly.</summary>
        public const string Uncertain = "uncertain";

        public static bool IsValid(string status)
        {
            return status == Prepared || status == Running || status == Completed
                || status == Rejected || status == Failed || status == Uncertain;
        }

        /// <summary>
        /// Terminal states are the only ones that need no further verification.
        /// Uncertain is deliberately not terminal: it still needs a human/verifier
        /// decision, just not an automatic replay.
        /// </summary>
        public static bool IsTerminal(string status)
        {
            return status == Completed || status == Rejected || status == Failed;
        }

        /// <summary>True only for a trusted successful completion.</summary>
        public static bool IsSuccess(string status)
        {
            return status == Completed;
        }

        /// <summary>
        /// Classifies one dispatch result from the facts the bridge puts in the
        /// response. Pending wins over ok: a running native flow may still emit a
        /// provisional ok, and that must never be reported as completed.
        ///
        /// A failed action with an unknown dispatch fact is uncertain, not
        /// rejected: "we could not tell whether the native call ran" is exactly
        /// the case where a blind retry is forbidden.
        /// </summary>
        public static string Classify(bool ok, bool pending, bool uncertain, string dispatch)
        {
            string claim = DispatchStatus.Normalize(dispatch);
            if (pending) return Running;
            if (uncertain) return Uncertain;
            if (ok) return Completed;
            if (claim == DispatchStatus.NotDispatched) return Rejected;
            if (claim == DispatchStatus.Dispatched) return Failed;
            return Uncertain;
        }
    }

    public static class DeliveryStatus
    {
        public const string PendingDelivery = "pending_delivery";
        public const string Delivered = "delivered";
        public const string Undelivered = "undelivered";

        public static bool IsValid(string status)
        {
            return status == PendingDelivery || status == Delivered
                || status == Undelivered;
        }
    }

    /// <summary>
    /// R4/F4b: whether the native call ran is its own fact, produced by the
    /// service that knows. It is never inferred from ok, from the presence of a
    /// JSON body or from an exception: "we do not know" is a real answer and must
    /// not be serialized as false (a safe refusal) or as true (a failed attempt).
    /// </summary>
    public static class DispatchStatus
    {
        /// <summary>A guard refused before the native call; nothing ran.</summary>
        public const string NotDispatched = "not_dispatched";

        /// <summary>The native call was issued.</summary>
        public const string Dispatched = "dispatched";

        /// <summary>No trustworthy statement exists; treat as possibly dispatched.</summary>
        public const string Unknown = "unknown";

        public static bool IsValid(string status)
        {
            return status == NotDispatched || status == Dispatched
                || status == Unknown;
        }

        /// <summary>Normalizes an absent/invalid claim to the conservative value.</summary>
        public static string Normalize(string status)
        {
            return IsValid(status) ? status : Unknown;
        }

        /// <summary>True when a second dispatch of the same op must be refused.</summary>
        public static bool MayHaveRun(string status)
        {
            return status != NotDispatched;
        }
    }

    /// <summary>Stable error codes for the operation query.</summary>
    public static class OperationErrors
    {
        public const string UnknownOperation = "unknown_operation";
        public const string BadOperationId = "bad_operation_id";
        public const string OperationPending = "operation_pending";
        public const string OperationUnavailable = "operation_unavailable";
        public const string MissingOperationId = "missing_operation_id";

        /// <summary>R4/F1b: a run-changing action is already in flight.</summary>
        public const string RunChangePending = "run_change_pending";

        public const string ProcessRestarted = "process_restarted";
    }

    /// <summary>
    /// One write action's two-axis record. Kept in-process only: there is no
    /// exactly-once promise across a Mod restart, and a restarted process reports
    /// old ids as unknown instead of replaying them.
    /// </summary>
    public sealed class OperationRecord
    {
        public string OperationId { get; set; }
        public string Op { get; set; }
        public string Fingerprint { get; set; }
        public string Execution { get; set; }
        public string Delivery { get; set; }
        public long Revision { get; set; }
        public long ResultRevision { get; set; }
        public string Error { get; set; }

        /// <summary>R4/F4b: the service's own dispatch claim (tri-state).</summary>
        public string Dispatch { get; set; }

        /// <summary>R4/F1b: the request was a run-changing operation.</summary>
        public bool RunChangeRequested { get; set; }

        /// <summary>
        /// R4/F1b: requested, confirmed, unknown or not_requested. Only "confirmed"
        /// may evict the previous run's evidence.
        /// </summary>
        public string RunChange { get; set; }

        /// <summary>
        /// The opaque native run identity observed right before dispatch. Never
        /// serialized to the client; only used to prove the switch happened.
        /// </summary>
        public string RunIdBefore { get; set; }

        /// <summary>Set once, so the eviction and the receipt reset happen once.</summary>
        public bool RunChangeApplied { get; set; }

        /// <summary>
        /// R4/F3b: what the verifier actually observed when it promoted the
        /// operation. Null when the promotion has no readback evidence.
        /// </summary>
        public string CompletionEvidence { get; set; }

        public bool RunChangeConfirmed
        {
            get { return RunChange == RunChangeStatus.Confirmed; }
        }

        public OperationRecord()
        {
            Execution = ExecutionStatus.Prepared;
            Delivery = DeliveryStatus.PendingDelivery;
            Dispatch = DispatchStatus.Unknown;
            RunChange = RunChangeStatus.NotRequested;
        }

        public OperationRecord Copy()
        {
            return new OperationRecord
            {
                OperationId = OperationId,
                Op = Op,
                Fingerprint = Fingerprint,
                Execution = Execution,
                Delivery = Delivery,
                Revision = Revision,
                ResultRevision = ResultRevision,
                Error = Error,
                Dispatch = Dispatch,
                RunChangeRequested = RunChangeRequested,
                RunChange = RunChange,
                RunIdBefore = RunIdBefore,
                RunChangeApplied = RunChangeApplied,
                CompletionEvidence = CompletionEvidence
            };
        }

        /// <summary>ok=true only for a trusted completion; used by the JSON envelope.</summary>
        public bool Ok
        {
            get { return ExecutionStatus.IsSuccess(Execution); }
        }
    }
}
