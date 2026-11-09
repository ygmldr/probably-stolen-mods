using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Monotonic state revision. Every observable state change increments it once,
    /// so actions can be rejected when they were decided against an older snapshot.
    /// </summary>
    public sealed class RevisionTracker
    {
        private string _lastFingerprint;

        public long Revision { get; private set; }

        public bool Observe(string fingerprint)
        {
            if (fingerprint == null) fingerprint = string.Empty;

            if (_lastFingerprint == null)
            {
                _lastFingerprint = fingerprint;
                return false;
            }

            if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal)) return false;

            _lastFingerprint = fingerprint;
            Revision++;
            return true;
        }

        public void Bump()
        {
            Revision++;
        }
    }
}
