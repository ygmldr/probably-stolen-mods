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
    /// dispatched. The cache is bounded and in-process only: it does not promise
    /// exactly-once delivery across a Mod restart.
    /// </summary>
    public sealed class ActionGate
    {
        private sealed class Entry
        {
            public string Fingerprint;
            public string Response;
        }

        private readonly Dictionary<string, Entry> _results =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;

        public ActionGate(int capacity = 1024)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
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
            if (string.IsNullOrEmpty(actionId)) return false;

            Entry existing;
            if (!_results.TryGetValue(actionId, out existing)) return false;
            if (!string.Equals(existing.Response, expectedResponse ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return false;
            }

            existing.Response = replacement ?? string.Empty;
            return true;
        }

        public void Remember(string actionId, string requestFingerprint, string response)
        {
            if (string.IsNullOrEmpty(actionId)) return;
            if (_results.ContainsKey(actionId)) return;

            _results[actionId] = new Entry
            {
                Fingerprint = requestFingerprint ?? string.Empty,
                Response = response ?? string.Empty
            };

            _order.Enqueue(actionId);

            while (_order.Count > _capacity)
            {
                string oldest = _order.Dequeue();
                _results.Remove(oldest);
            }
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
