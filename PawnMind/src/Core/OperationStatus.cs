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
        /// Classifies one dispatch result from the flags the bridge puts in the
        /// response. Pending wins over ok: a running native flow may still emit a
        /// provisional ok, and that must never be reported as completed.
        /// </summary>
        public static string Classify(bool ok, bool pending, bool uncertain, bool dispatched)
        {
            if (pending) return Running;
            if (uncertain) return Uncertain;
            if (ok) return Completed;
            if (!dispatched) return Rejected;
            return Failed;
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

    /// <summary>Stable error codes for the operation query.</summary>
    public static class OperationErrors
    {
        public const string UnknownOperation = "unknown_operation";
        public const string BadOperationId = "bad_operation_id";
        public const string OperationPending = "operation_pending";
        public const string OperationUnavailable = "operation_unavailable";
        public const string MissingOperationId = "missing_operation_id";

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
        public bool Dispatched { get; set; }

        public OperationRecord()
        {
            Execution = ExecutionStatus.Prepared;
            Delivery = DeliveryStatus.PendingDelivery;
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
                Dispatched = Dispatched
            };
        }

        /// <summary>ok=true only for a trusted completion; used by the JSON envelope.</summary>
        public bool Ok
        {
            get { return ExecutionStatus.IsSuccess(Execution); }
        }
    }
}
