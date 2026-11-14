using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Classifies stable bridge error codes that justify automatic evidence.
    /// Ordinary business refusals (cannot_afford, choice_disabled, stale_rev,
    /// screenshot throttling, ...) are deliberately excluded.
    /// </summary>
    public static class FailureEvidence
    {
        private static readonly HashSet<string> HardFailures =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "outcome_unknown",
                "state_unavailable",
                "readback_mismatch",
                "native_error",
                "exception",
                "bad_response",
                "game_version_mismatch",
                "game_assembly_mvid_mismatch"
            };

        public static bool IsHardFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return false;

            string code = error.Trim().ToLowerInvariant();
            if (code.Length == 0) return false;

            if (HardFailures.Contains(code)) return true;
            if (code.StartsWith("version_", StringComparison.Ordinal)) return true;
            if (code.StartsWith("recording_", StringComparison.Ordinal)) return true;
            if (code.StartsWith("exception:", StringComparison.Ordinal)) return true;

            return false;
        }
    }
}
