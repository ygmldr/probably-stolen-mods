using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Which controls the generic <c>press</c> op may still click.
    ///
    /// <c>press</c> exists for windows without a typed adapter, but it invokes an
    /// arbitrary UI callback, so the recording policy refuses it outright. A few
    /// overlay panels (the control-hint overlay and the dialogue/text settings
    /// dialog) have no typed op and would otherwise block the player's view with
    /// no way to close them.
    ///
    /// This allowlist is deliberately tiny and name-based: only the dismiss
    /// buttons of those two overlays. Everything else stays refused.
    /// </summary>
    public static class UiPressPolicy
    {
        public const string ControlNotAllowed = "control_not_allowed";

        private static readonly string[] Allowed =
        {
            "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton",
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/CloseButton",
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/ApplyButton"
        };

        /// <summary>
        /// True only for a whitelisted overlay-dismiss button. Unity appends
        /// " (1)", " (2)" to duplicate object names, so a trailing counter is
        /// ignored before matching.
        /// </summary>
        public static bool IsAllowedControl(string path)
        {
            string normalized = StripDuplicateSuffix((path ?? string.Empty).Trim());
            if (normalized.Length == 0) return false;

            for (int i = 0; i < Allowed.Length; i++)
            {
                if (string.Equals(normalized, Allowed[i], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string StripDuplicateSuffix(string path)
        {
            int length = path.Length;
            if (length < 4 || path[length - 1] != ')') return path;

            int open = path.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) return path;

            for (int i = open + 2; i < length - 1; i++)
            {
                if (path[i] < '0' || path[i] > '9') return path;
            }

            return path.Substring(0, open);
        }
    }
}
