using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum ActionGateResult
    {
        Accepted,
        Duplicate,
        Conflict,
        MissingActionId,
        InvalidActionId,
        MissingRevision,
        InvalidRevision,
        StaleRevision
    }

    /// <summary>
    /// Revision check plus an idempotency cache. A write action id may only be
    /// replayed with the same normalized op/args; the first response is returned.
    /// A different payload for the same id is a protocol conflict and must not be
    /// dispatched.
    ///
    /// D module: every cached write also carries an in-process
    /// <see cref="OperationRecord"/> with independent execution and delivery axes.
    /// The cache is bounded and in-process only: it does not promise exactly-once
    /// delivery across a Mod restart, and a restart must make callers re-read state
    /// instead of replaying an unknown outcome.
    /// </summary>
    public sealed class ActionGate
    {
        private sealed class Entry
        {
            public string Fingerprint;
            public string Response;
            public OperationRecord Operation;
        }

        private readonly Dictionary<string, Entry> _results =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;
        private int _cleared;

        public ActionGate(int capacity = 1024)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        /// <summary>How many times the operation cache was cleared (run change).</summary>
        public int ClearCount
        {
            get { return _cleared; }
        }

        public ActionGateResult Check(
            string actionId,
            long? requestRevision,
            long currentRevision,
            string requestFingerprint,
            out string cachedResponse)
        {
            cachedResponse = null;

            if (string.IsNullOrEmpty(actionId) || actionId.Trim().Length == 0)
                return ActionGateResult.MissingActionId;

            if (actionId.Length > 128 || HasControlCharacter(actionId))
                return ActionGateResult.InvalidActionId;

            if (!requestRevision.HasValue) return ActionGateResult.MissingRevision;
            if (requestRevision.Value < 0) return ActionGateResult.InvalidRevision;

            Entry existing;
            if (_results.TryGetValue(actionId, out existing))
            {
                if (string.Equals(existing.Fingerprint, requestFingerprint ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    cachedResponse = existing.Response;
                    return ActionGateResult.Duplicate;
                }

                return ActionGateResult.Conflict;
            }

            if (requestRevision.Value != currentRevision) return ActionGateResult.StaleRevision;

            return ActionGateResult.Accepted;
        }

        /// <summary>
        /// Replaces an already recorded first response, for example when the
        /// transport could not deliver it and the real outcome is unknown. The
        /// replacement only happens when the caller still holds the exact cached
        /// response, so a refusal (which is never cached) cannot be rewritten and
        /// an already-replaced entry is left alone.
        /// </summary>
        public bool Replace(string actionId, string expectedResponse, string replacement)
        {
            Entry existing;
            if (!TryFind(actionId, out existing)) return false;
            if (!string.Equals(existing.Response, expectedResponse ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return false;
            }

            existing.Response = replacement ?? string.Empty;
            return true;
        }

        /// <summary>Legacy cache write: derives the execution axis from the response.</summary>
        public void Remember(string actionId, string requestFingerprint, string response)
        {
            bool ok = ResponseOk(response);
            var operation = new OperationRecord
            {
                OperationId = actionId,
                Op = null,
                Fingerprint = requestFingerprint ?? string.Empty,
                Execution = ok ? ExecutionStatus.Completed : ExecutionStatus.Rejected,
                Delivery = DeliveryStatus.Delivered,
                Dispatched = ok,
                Error = ok ? null : ResponseError(response)
            };
            RememberResult(actionId, requestFingerprint, response, operation);
        }

        /// <summary>Cache write with an explicit two-axis operation record.</summary>
        public void RememberResult(string actionId, string requestFingerprint,
            string response, OperationRecord operation)
        {
            if (string.IsNullOrEmpty(actionId)) return;
            if (_results.ContainsKey(actionId)) return;

            var entry = new Entry
            {
                Fingerprint = requestFingerprint ?? string.Empty,
                Response = response ?? string.Empty,
                Operation = operation ?? new OperationRecord()
            };
            if (string.IsNullOrEmpty(entry.Operation.OperationId))
                entry.Operation.OperationId = actionId;

            _results[actionId] = entry;
            _order.Enqueue(actionId);

            while (_order.Count > _capacity)
            {
                string oldest = _order.Dequeue();
                _results.Remove(oldest);
            }
        }

        /// <summary>
        /// Registers a write as prepared right before dispatch. The record exists so
        /// a crash/timeout inside dispatch is observable instead of silently absent.
        /// </summary>
        public OperationRecord Begin(string actionId, string op, long revision,
            string requestFingerprint = null)
        {
            if (string.IsNullOrEmpty(actionId)) return null;

            Entry existing;
            if (TryFind(actionId, out existing)) return existing.Operation;

            var operation = new OperationRecord
            {
                OperationId = actionId,
                Op = op,
                Fingerprint = requestFingerprint ?? string.Empty,
                Revision = revision,
                ResultRevision = revision,
                Execution = ExecutionStatus.Prepared,
                Delivery = DeliveryStatus.PendingDelivery,
                Dispatched = false
            };
            RememberResult(actionId, requestFingerprint, null, operation);
            return operation;
        }

        /// <summary>Completes a prepared operation; only the router calls this.</summary>
        public void Complete(string actionId, string response, string execution,
            string delivery, long resultRevision, string error, bool dispatched)
        {
            Entry entry;
            if (!TryFind(actionId, out entry)) return;

            entry.Response = response ?? entry.Response;
            entry.Operation.Execution = ExecutionStatus.IsValid(execution)
                ? execution
                : ExecutionStatus.Failed;
            entry.Operation.Delivery = DeliveryStatus.IsValid(delivery)
                ? delivery
                : DeliveryStatus.Delivered;
            entry.Operation.ResultRevision = resultRevision;
            entry.Operation.Error = error;
            // F4: dispatched is the service's own claim, never inferred from the
            // execution axis. A pending/failed result that says nothing reached the
            // game must stay dispatched=false.
            entry.Operation.Dispatched = dispatched;
        }

        /// <summary>
        /// D1: a lost response changes only the delivery axis. The completed
        /// execution evidence and the cached response body are left untouched.
        /// </summary>
        public bool MarkUndelivered(string actionId)
        {
            Entry entry;
            if (!TryFind(actionId, out entry)) return false;
            if (string.IsNullOrEmpty(entry.Response)) return false;

            entry.Operation.Delivery = DeliveryStatus.Undelivered;
            return true;
        }

        /// <summary>Reads the two-axis record; null when the id is unknown.</summary>
        public OperationRecord Find(string actionId)
        {
            Entry entry;
            if (!TryFind(actionId, out entry)) return null;
            return entry.Operation == null ? null : entry.Operation.Copy();
        }

        /// <summary>The raw cached response body (never rewritten by delivery).</summary>
        public string CachedResponse(string actionId)
        {
            Entry entry;
            if (!TryFind(actionId, out entry)) return null;
            return entry.Response;
        }

        /// <summary>Finds a non-terminal operation for the same op name.</summary>
        public OperationRecord FindPendingFor(string op)
        {
            if (string.IsNullOrEmpty(op)) return null;
            foreach (KeyValuePair<string, Entry> pair in _results)
            {
                OperationRecord operation = pair.Value.Operation;
                if (operation == null) continue;
                if (!string.Equals(operation.Op, op, StringComparison.Ordinal)) continue;
                if (!operation.Dispatched) continue;
                if (ExecutionStatus.IsTerminal(operation.Execution)) continue;
                return operation.Copy();
            }

            return null;
        }

        /// <summary>Stores an updated record (the operation verifier).</summary>
        public bool Update(OperationRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.OperationId)) return false;
            Entry entry;
            if (!TryFind(record.OperationId, out entry)) return false;
            entry.Operation = record.Copy();
            return true;
        }

        /// <summary>
        /// A run change (new game / loaded save / different timeline) invalidates
        /// every old operation record: evidence from another run must never be
        /// attributed to the current one. Returns how many records were dropped.
        /// </summary>
        public int ClearOperations()
        {
            int count = _results.Count;
            _results.Clear();
            _order.Clear();
            _cleared++;
            return count;
        }

        /// <summary>
        /// Evicts the previous run's records but keeps the operation that triggered
        /// the switch, so its response, evidence and idempotency survive (F1).
        /// </summary>
        public int ClearOperationsExcept(string keepActionId)
        {
            if (string.IsNullOrEmpty(keepActionId)) return ClearOperations();

            var drop = new System.Collections.Generic.List<string>();
            foreach (string id in _results.Keys)
            {
                if (!string.Equals(id, keepActionId, System.StringComparison.Ordinal))
                    drop.Add(id);
            }

            for (int i = 0; i < drop.Count; i++) _results.Remove(drop[i]);

            var kept = new System.Collections.Generic.Queue<string>();
            while (_order.Count > 0)
            {
                string id = _order.Dequeue();
                if (_results.ContainsKey(id)) kept.Enqueue(id);
            }
            while (kept.Count > 0) _order.Enqueue(kept.Dequeue());

            if (drop.Count > 0) _cleared++;
            return drop.Count;
        }

        private bool TryFind(string actionId, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(actionId)) return false;
            return _results.TryGetValue(actionId, out entry);
        }

        private static bool ResponseOk(string response)
        {
            if (string.IsNullOrEmpty(response)) return false;
            return response.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
        }

        private static string ResponseError(string response)
        {
            if (string.IsNullOrEmpty(response)) return null;
            const string marker = "\"error\":\"";
            int start = response.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = response.IndexOf('"', start);
            return end <= start ? null : response.Substring(start, end - start);
        }

        private static bool HasControlCharacter(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i])) return true;
            }

            return false;
        }
    }
}
