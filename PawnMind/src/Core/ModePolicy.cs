using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum RunMode
    {
        Bootstrap,
        Play,
        Test,
    }

    /// <summary>
    /// Which operations the current run mode may execute. Test allows everything
    /// (throwaway save only); Play allows only the reviewed allowlist; Bootstrap
    /// allows even less. Unknown operations are denied, never inherited.
    /// </summary>
    public static class ModePolicy
    {
        public const string Forbidden = "mode_forbidden";

        private static RunMode _mode = RunMode.Test;
        private static HashSet<string> _allow = new HashSet<string>(StringComparer.Ordinal);

        public static RunMode Mode { get { return _mode; } }

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

        /// <summary>Null when allowed, otherwise the stable refusal code.</summary>
        public static string Refusal(string op)
        {
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
