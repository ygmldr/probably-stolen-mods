using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum ActionGateResult
    {
        Accepted,
        Duplicate,
        StaleRevision
    }

    /// <summary>
    /// Revision check plus a small idempotency cache. A duplicated action id returns
    /// the first response instead of running the action twice (safe retries after a
    /// timeout where the outcome is unknown).
    /// </summary>
    public sealed class ActionGate
    {
        private readonly Dictionary<string, string> _results =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;

        public ActionGate(int capacity = 64)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        public ActionGateResult Check(
            string actionId,
            long requestRevision,
            long currentRevision,
            out string cachedResponse)
        {
            cachedResponse = null;

            if (!string.IsNullOrEmpty(actionId) && _results.TryGetValue(actionId, out cachedResponse))
            {
                return ActionGateResult.Duplicate;
            }

            if (requestRevision >= 0 && requestRevision != currentRevision)
            {
                return ActionGateResult.StaleRevision;
            }

            return ActionGateResult.Accepted;
        }

        public void Remember(string actionId, string response)
        {
            if (string.IsNullOrEmpty(actionId)) return;

            if (_results.ContainsKey(actionId))
            {
                _results[actionId] = response ?? string.Empty;
                return;
            }

            _results[actionId] = response ?? string.Empty;
            _order.Enqueue(actionId);

            while (_order.Count > _capacity)
            {
                string oldest = _order.Dequeue();
                _results.Remove(oldest);
            }
        }
    }
}
