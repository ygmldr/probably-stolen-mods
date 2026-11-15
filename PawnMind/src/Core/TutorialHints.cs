namespace PawnMind.Core
{
    /// <summary>
    /// Tutorial hint control. The only game switch involved is
    /// PlayerStore.tutorialEnable (the master switch read by TutorialUIManager)
    /// plus hiding the already-visible hint group. Deliberately unrelated to
    /// HandleSkipIntro, which unlocks services and changes story flags.
    /// </summary>
    public sealed class TutorialSnapshot
    {
        public bool Readable;
        public string Error;
        public bool Enabled;
        public bool HintsHidden;
        public bool GroupReadable;
    }

    public interface ITutorialPort
    {
        TutorialSnapshot ReadTutorial();

        /// <summary>
        /// Sets tutorialEnable = false once and hides the visible hint group.
        /// Never touches skip-intro, services, story clients or items.
        /// </summary>
        void DisableHints();
    }

    public sealed class TutorialOutcome
    {
        public bool Ok;
        public string Error;
        public string Detail;
        public bool Dispatched;
        public bool Changed;
        public bool Enabled;
        public bool HintsHidden;
        public bool GroupReadable;
    }

    public static class TutorialRules
    {
        public const string ReadbackMismatch = "tutorial_hints_readback_mismatch";
        public const string Unavailable = "tutorial_unavailable";

        /// <summary>
        /// A write is needed when the master switch is on, or when the hint
        /// group is still visible from before the switch was turned off.
        /// </summary>
        public static bool NeedsWrite(TutorialSnapshot before)
        {
            if (before == null || !before.Readable) return false;
            return before.Enabled || !before.HintsHidden;
        }

        public static TutorialOutcome Evaluate(
            TutorialSnapshot before,
            TutorialSnapshot after,
            bool dispatched)
        {
            if (before == null || !before.Readable)
            {
                return new TutorialOutcome
                {
                    Ok = false,
                    Error = before == null || string.IsNullOrEmpty(before.Error)
                        ? Unavailable
                        : before.Error,
                    Dispatched = false,
                    Enabled = before != null && before.Enabled,
                    HintsHidden = before != null && before.HintsHidden,
                    GroupReadable = before != null && before.GroupReadable
                };
            }

            if (!NeedsWrite(before))
            {
                return new TutorialOutcome
                {
                    Ok = true,
                    Dispatched = false,
                    Changed = false,
                    Enabled = before.Enabled,
                    HintsHidden = before.HintsHidden,
                    GroupReadable = before.GroupReadable
                };
            }

            if (after == null || !after.Readable)
            {
                return Mismatch(before, "readback_unavailable");
            }

            if (after.Enabled || !after.GroupReadable || !after.HintsHidden)
            {
                return Mismatch(before, "hints_still_enabled");
            }

            return new TutorialOutcome
            {
                Ok = true,
                Dispatched = dispatched,
                Changed = before.Enabled,
                Enabled = after.Enabled,
                HintsHidden = after.HintsHidden,
                GroupReadable = after.GroupReadable
            };
        }

        private static TutorialOutcome Mismatch(TutorialSnapshot before, string detail)
        {
            return new TutorialOutcome
            {
                Ok = false,
                Error = ReadbackMismatch,
                Detail = detail,
                Dispatched = true,
                Changed = before != null && before.Enabled,
                Enabled = before != null && before.Enabled,
                HintsHidden = before != null && before.HintsHidden,
                GroupReadable = before != null && before.GroupReadable
            };
        }
    }
}
