namespace PawnMind.Core
{
    /// <summary>Stable codes for opening the shutter.</summary>
    public static class OpenShopErrors
    {
        public const string NotInGame = "not_in_game";
        public const string Unavailable = "open_shop_unavailable";
        public const string ShutterBusy = "shutter_busy";
        public const string BusyArrest = "busy_arrest";
        public const string BusyShootout = "busy_shootout";
        public const string ShopNotMorning = "shop_not_morning";
        public const string CannotOpen = "cannot_open_shop";
        public const string Failed = "open_shop_failed";
    }

    /// <summary>
    /// P0-OPEN-01: the shutter button owns a 2.75s animation lock and only works
    /// from MORNING. The bridge mirrors the native predicates before clicking so
    /// a repeated open_shop during the animation is refused with a stable code
    /// instead of being silently swallowed by the game.
    /// </summary>
    public static class OpenShopPolicy
    {
        /// <summary>Returns the refusal code, or null when the click may be dispatched.</summary>
        public static string Evaluate(
            bool inGame,
            bool shutterLocked,
            bool busyArrest,
            bool busyShootout,
            string shopState,
            bool canStartDay)
        {
            if (!inGame) return OpenShopErrors.NotInGame;
            // Mirror the native button order: lock, arrest, shootout, state, gate.
            if (shutterLocked) return OpenShopErrors.ShutterBusy;
            if (busyArrest) return OpenShopErrors.BusyArrest;
            if (busyShootout) return OpenShopErrors.BusyShootout;

            if (shopState == null
                || !string.Equals(shopState.Trim(), "MORNING", System.StringComparison.OrdinalIgnoreCase))
            {
                return OpenShopErrors.ShopNotMorning;
            }

            if (!canStartDay) return OpenShopErrors.CannotOpen;
            return null;
        }
    }

    /// <summary>Stable save-evidence codes for the sleep path.</summary>
    public static class SaveEvidenceCodes
    {
        public const string Completed = "save_completed";
        public const string Unknown = "save_unknown";
    }

    /// <summary>Result of classifying one sleep dispatch.</summary>
    public sealed class SleepSemantics
    {
        public string Execution;
        public bool DayChanged;
        public string SaveEvidence;
        public int DayBefore;
        public int DayAfter;

        public bool SaveCompleted
        {
            get { return SaveEvidence == SaveEvidenceCodes.Completed; }
        }

        public bool SaveUnknown
        {
            get { return SaveEvidence != SaveEvidenceCodes.Completed; }
        }
    }

    /// <summary>
    /// D3: end_day / sleep completion rules. The native flows are asynchronous
    /// (shutter animation, blackscreen day transition, save file write), so an
    /// immediate readback that shows no change means "running", not "no_effect".
    /// A day change is also not proof that the save file was written.
    /// </summary>
    public static class ShopOperationSemantics
    {
        public const string SaveCompleted = SaveEvidenceCodes.Completed;
        public const string SaveUnknown = SaveEvidenceCodes.Unknown;

        /// <summary>
        /// end_day completes only when the post-dispatch readback shows the shop
        /// CLOSED. Regression/unreadable readback is uncertain, never success.
        /// </summary>
        public static string EndDayExecution(
            bool dispatched,
            bool readbackReadable,
            string readbackError,
            bool shopClosedAfter)
        {
            if (!dispatched) return ExecutionStatus.Rejected;
            if (!readbackReadable || !string.IsNullOrEmpty(readbackError))
                return ExecutionStatus.Uncertain;
            return shopClosedAfter ? ExecutionStatus.Completed : ExecutionStatus.Running;
        }

        /// <summary>
        /// Sleep classification. day_changed and save evidence are reported
        /// separately; neither implies the other.
        /// </summary>
        public static SleepSemantics Sleep(
            bool dispatched,
            bool readbackReadable,
            string readbackError,
            int dayBefore,
            int dayAfter,
            string saveEvidence)
        {
            var result = new SleepSemantics
            {
                DayBefore = dayBefore,
                DayAfter = dayAfter,
                DayChanged = dayAfter > dayBefore,
                SaveEvidence = string.IsNullOrEmpty(saveEvidence)
                    ? SaveUnknown
                    : saveEvidence
            };

            if (!dispatched)
            {
                result.Execution = ExecutionStatus.Rejected;
                return result;
            }

            if (!readbackReadable || !string.IsNullOrEmpty(readbackError))
            {
                result.Execution = ExecutionStatus.Uncertain;
                return result;
            }

            // The async blackscreen may not have advanced the day yet. Pending is
            // the honest answer: a single unchanged readback is not "no effect".
            result.Execution = result.DayChanged
                ? ExecutionStatus.Completed
                : ExecutionStatus.Running;
            return result;
        }
    }
}
