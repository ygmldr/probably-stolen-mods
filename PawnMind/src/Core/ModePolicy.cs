using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum RunMode
    {
        /// <summary>Fail-closed default: no configuration, no operation.</summary>
        Locked = 0,
        Bootstrap = 1,
        Play = 2,
        Test = 3,
    }

    /// <summary>
    /// Which operations the current run mode may execute. The default is Locked:
    /// a missing or malformed configuration never widens the gate. Test is only
    /// reachable through an explicit "run_mode=test" plus "allow_test_mode=true".
    /// </summary>
    public static class ModePolicy
    {
        /// <summary>Operation is denied by the current mode's allowlist.</summary>
        public const string Forbidden = "mode_forbidden";

        /// <summary>The bridge has no valid run mode; nothing is allowed.</summary>
        public const string Locked = "mode_locked";

        private static RunMode _mode = RunMode.Locked;
        private static HashSet<string> _allow = new HashSet<string>(StringComparer.Ordinal);
        private static string _error;

        public static RunMode Mode { get { return _mode; } }

        /// <summary>Why the mode is what it is; null when the configuration was valid.</summary>
        public static string Error { get { return _error; } }

        public static void SetError(string error)
        {
            _error = string.IsNullOrEmpty(error) ? null : error;
        }

        public static void Configure(RunMode mode, IEnumerable<string> allow)
        {
            _mode = mode;
            _allow = new HashSet<string>(StringComparer.Ordinal);
            if (allow == null) return;
            foreach (string op in allow)
            {
                if (!string.IsNullOrEmpty(op)) _allow.Add(op);
            }
        }

        /// <summary>Back to the fail-closed default (startup and tests).</summary>
        public static void Reset()
        {
            _mode = RunMode.Locked;
            _allow.Clear();
            _error = null;
        }

        /// <summary>Null when allowed, otherwise the stable refusal code.</summary>
        public static string Refusal(string op)
        {
            if (_mode == RunMode.Locked) return Locked;
            if (string.IsNullOrEmpty(op)) return Forbidden;
            if (_mode == RunMode.Test) return null;
            return _allow.Contains(op) ? null : Forbidden;
        }

        public static bool Allows(string op)
        {
            return Refusal(op) == null;
        }
    }
}
