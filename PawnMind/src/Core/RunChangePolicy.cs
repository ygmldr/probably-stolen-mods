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
        /// The reader's answer when the game definitively has no run (main menu).
        /// It is distinct from <c>null</c>, which means "cannot tell" (loading
        /// screen, unreadable store). A NUL prefix can never collide with a real
        /// run id and is never serialized to a client.
        /// </summary>
        public const string NoRun = "\u0000no-run";

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
            // null/empty means "cannot tell" (loading screen, unreadable store).
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after))
                return RunChangeStatus.Unknown;

            bool beforeKnown = !string.Equals(before, NoRun, StringComparison.Ordinal);
            bool afterKnown = !string.Equals(after, NoRun, StringComparison.Ordinal);

            // Still at the main menu: the request was accepted, nothing switched.
            if (!beforeKnown && !afterKnown) return RunChangeStatus.Requested;

            // A run appeared where the game definitely had none. There is no
            // previous run to misattribute, so this is the one asymmetric case that
            // can be confirmed without a before-id.
            if (!beforeKnown) return RunChangeStatus.Confirmed;

            // A run disappeared: mid-swap (load) or back to the menu. Not proof of
            // a completed switch, so the old evidence stays put.
            if (!afterKnown) return RunChangeStatus.Unknown;

            return string.Equals(before, after, StringComparison.Ordinal)
                ? RunChangeStatus.Requested
                : RunChangeStatus.Confirmed;
        }
    }
}

/// <summary>
/// Which ops are identified by their arguments when deciding whether a second
/// action id would repeat the same native call.
///
/// Only ops where the arguments name the target belong here, and only because a
/// live session proved the coarse rule is actively harmful there: one uncertain
/// `confirm_trade` on an item the customer could not afford disabled every other
/// trade. Everything else keeps the conservative op-name match.
///
/// R5/B1 adds `stage_negotiation_item`: an uncertain staging attempt may have
/// moved the item onto the weighted counter, so retrying *that item* under a new
/// action id is a blind replay, while staging a different item is an independent
/// step the workflow must keep.
/// </summary>
public static class PendingScope
{
    public static bool IsCallScoped(string op)
    {
        // PendingScope lives outside the PawnMind.Core namespace (historical),
        // so the staging op name is referenced fully-qualified to keep one source.
        return op == "confirm_trade" || op == PawnMind.Core.StagingOps.Name;
    }
}
