using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>Resolved startup mode plus the allowlist it implies.</summary>
    public sealed class RunModePlan
    {
        public RunMode Mode;
        public string Error;
        public readonly List<string> Allow = new List<string>();
    }

    /// <summary>
    /// Turns the raw "run_mode" value into a plan. Every failure path returns
    /// <see cref="RunMode.Locked"/>: there is no fallback to Test, and no
    /// allowlist is inherited from a previous run.
    /// </summary>
    public static class RunModeBinding
    {
        public const string Missing = "run_mode_missing";
        public const string Invalid = "run_mode_invalid";
        public const string TestNotAuthorized = "test_mode_not_authorized";

        public static RunModePlan Evaluate(string rawMode, bool allowTestMode)
        {
            var plan = new RunModePlan();
            string value = (rawMode ?? string.Empty).Trim().ToLowerInvariant();

            if (value.Length == 0) return Locked(plan, Missing);

            switch (value)
            {
                case "bootstrap":
                    plan.Mode = RunMode.Bootstrap;
                    plan.Allow.AddRange(PolicyCatalog.BootstrapOps);
                    return plan;

                case "play":
                    plan.Mode = RunMode.Play;
                    // Never the candidate allowlist: only reviewed live evidence counts.
                    plan.Allow.AddRange(PolicyCatalog.PlayOps);
                    return plan;

                case "test":
                    if (!allowTestMode) return Locked(plan, TestNotAuthorized);
                    plan.Mode = RunMode.Test;
                    return plan;

                default:
                    return Locked(plan, Invalid);
            }
        }

        /// <summary>Applies a plan to the runtime gate and returns the plan.</summary>
        public static RunModePlan Apply(string rawMode, bool allowTestMode)
        {
            RunModePlan plan = Evaluate(rawMode, allowTestMode);
            ModePolicy.Configure(plan.Mode, plan.Allow);
            ModePolicy.SetError(plan.Error);
            return plan;
        }

        public static string ModeName(RunMode mode)
        {
            switch (mode)
            {
                case RunMode.Bootstrap: return "bootstrap";
                case RunMode.Play: return "play";
                case RunMode.Test: return "test";
                default: return "locked";
            }
        }

        private static RunModePlan Locked(RunModePlan plan, string error)
        {
            plan.Mode = RunMode.Locked;
            plan.Error = error;
            plan.Allow.Clear();
            return plan;
        }
    }
}
