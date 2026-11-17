using System;

namespace PawnMind.Core
{
    /// <summary>
    /// R4/F1b: the run-changing operations are exactly the ones that may switch
    /// the timeline. The list lives in Core so the router (which snapshots the
    /// run identity) and the Mod (which dispatches) cannot drift apart.
    /// </summary>
    public static class RunChangeOps
    {
        public static bool IsRunChangeOp(string op)
        {
            return op == "load_game"
                || op == "load_recording_game"
                || op == "new_game"
                || op == "confirm_new_game";
        }

        /// <summary>
        /// The generic <c>act</c> wrapper hides the real op inside the body, so a
        /// wrapped load_game must not escape the run-change rules.
        /// </summary>
        public static string Resolve(string op, string innerAction)
        {
            if (op != "act") return op;
            return string.IsNullOrEmpty(innerAction) ? op : innerAction;
        }
    }

    /// <summary>
    /// The three facts a run switch can carry. They are deliberately separate:
    /// "the native load was accepted" is not "the run identity changed", and an
    /// unreadable identity is not "unchanged".
    /// </summary>
    public static class RunChangeStatus
    {
        /// <summary>Not a run-changing operation; nothing to report.</summary>
        public const string NotRequested = "not_requested";

        /// <summary>The request was dispatched; the switch is not observed yet.</summary>
        public const string Requested = "requested";

        /// <summary>A trusted readback proved the run identity changed.</summary>
        public const string Confirmed = "confirmed";

        /// <summary>The identity is unreadable or missing; never guess either way.</summary>
        public const string Unknown = "unknown";

        public static bool IsValid(string status)
        {
            return status == NotRequested || status == Requested
                || status == Confirmed || status == Unknown;
        }
    }

    public static class RunChangePolicy
    {
        /// <summary>
        /// Compares two opaque native run identities.
        ///
        /// Only two non-empty, different identities prove the switch. A readable
        /// identity that did not move keeps the request pending; a missing or
        /// unreadable one is unknown. Revision, day and scene changes are not
        /// identities and must never be substituted for this comparison.
        /// </summary>
        public static string Classify(string before, string after)
        {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after))
                return RunChangeStatus.Unknown;

            return string.Equals(before, after, StringComparison.Ordinal)
                ? RunChangeStatus.Requested
                : RunChangeStatus.Confirmed;
        }
    }
}
