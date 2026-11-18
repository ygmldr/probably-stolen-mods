using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Which controls the generic <c>press</c> op may still click.
    ///
    /// <c>press</c> exists for windows without a typed adapter, but it invokes an
    /// arbitrary UI callback, so the recording policy refuses it outright. A few
    /// panels have no typed op and would otherwise block the player's view or the
    /// day transition with no way to dismiss them:
    ///   * the control-hint overlay (CloseOverlayButton),
    ///   * the dialogue/text settings dialog (CloseButton / ApplyButton),
    ///   * the day-end shutter confirmation (<c>NegotiationTablet/Confirm/Buttons/Button</c>),
    /// which is the fourth path and the one R5/B2 is migrating to a typed action.
    ///
    /// This allowlist is deliberately tiny and name-based.
    ///
    /// Test-mode only: every path here is reachable only when
    /// <c>run_mode=test</c> (see <see cref="ModePolicy"/> and the dispatch guard).
    /// None of these is a Play capability; a formal Play benchmark may not depend
    /// on <c>press</c>.
    /// </summary>
    public static class UiPressPolicy
    {
        public const string ControlNotAllowed = "control_not_allowed";

        private static readonly string[] Allowed =
        {
            "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton",
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/CloseButton",
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/ApplyButton",

            // The negotiation tablet confirm panel. R5/B2 made it a typed modal
            // through get_pending_prompt/answer_prompt (kind negotiation_confirm),
            // so this press entry is legacy/test-only fallback and must never be
            // the Play path. The snapshot carries the button text (是/否), so the
            // caller chooses explicitly instead of guessing an index.
            "NegotiationTablet/Confirm/Buttons/Button"
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
