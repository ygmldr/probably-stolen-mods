using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Stable S8 modal-prompt error codes. Callers branch on these strings;
    /// changing one is a protocol change.
    /// </summary>
    public static class PromptErrors
    {
        /// <summary>
        /// A modal owns the input but the requested operation did not answer it.
        /// Reserved for callers that want to refuse while a prompt is pending.
        /// </summary>
        public const string PromptPending = "prompt_pending";

        public const string NoPrompt = "no_prompt";
        public const string AnswerFailed = "prompt_answer_failed";
        public const string ReadbackMismatch = "prompt_readback_mismatch";
        public const string ReadFailed = "prompt_read_failed";
        public const string BadChoice = "bad_choice";
        public const string ChoiceUnavailable = "choice_unavailable";
        public const string ConfirmationRequired = "confirmation_required";

        /// <summary>
        /// R5/B2: more than one known modal owns the input at once. The bridge
        /// refuses to guess which one an answer belongs to.
        /// </summary>
        public const string PromptAmbiguous = "prompt_ambiguous";

        /// <summary>
        /// R5/B2: the native modal changed between the read and the answer (for
        /// example another window took the input). Refused before any dispatch.
        /// </summary>
        public const string PromptChanged = "prompt_changed";
    }

    public static class PromptKinds
    {
        /// <summary>The game's GenericConfirmationUIManager.</summary>
        public const string Confirmation = "confirmation";

        /// <summary>
        /// R5/B2: the negotiation tablet's confirm panel. Native flows that raise
        /// it: PlayerStore.SellItemFromTable / NegotiationTablet multi-sell when the
        /// client's budget cannot cover the sale. It is the panel the test-only
        /// <c>press</c> whitelist used to click (see R5_B_GAMEPLAY_REVIEW W-B2-3:
        /// end_day itself does not raise it).
        /// </summary>
        public const string NegotiationConfirm = "negotiation_confirm";
    }

    /// <summary>
    /// R5/B2: stable identities of the native modal windows. A caller compares
    /// the target instead of guessing a button index, so an answer can never be
    /// delivered to a different panel that happens to have a similar button.
    /// </summary>
    public static class PromptTargets
    {
        /// <summary>GenericConfirmationUIManager.panel.</summary>
        public const string GenericConfirmation = "generic_confirmation";

        /// <summary>
        /// NegotiationTablet/Confirm: the panel built by
        /// NegociacionUIManager.OpenConfirmPanel.
        /// </summary>
        public const string NegotiationTabletConfirm = "negotiation_tablet_confirm";
    }

    /// <summary>
    /// Player-visible content of an open confirmation modal. Only text the
    /// player can also read on screen is projected; the native callbacks and
    /// their captured closures are never touched.
    /// </summary>
    public sealed class PendingPrompt
    {
        public string Kind = PromptKinds.Confirmation;
        public string Body = string.Empty;
        public string ConfirmText = string.Empty;
        public string CancelText = string.Empty;

        /// <summary>
        /// True when the native cancel button is hidden. In that case the only
        /// legal answer is confirm, exactly like a player can only press one
        /// button.
        /// </summary>
        public bool SingleButton;

        /// <summary>
        /// The write op that produced the prompt when it is known (for example
        /// return_home); null for a plain /state or get_pending_prompt read.
        /// </summary>
        public string SourceOp;

        /// <summary>
        /// R5/B2: stable identity of the native window that owns this prompt
        /// (see <see cref="PromptTargets"/>). An answer is routed by identity,
        /// never by button position.
        /// </summary>
        public string Target;

        /// <summary>R5/B2: the native confirm option is interactable.</summary>
        public bool ConfirmEnabled = true;

        /// <summary>R5/B2: the native cancel option is interactable.</summary>
        public bool CancelEnabled = true;
    }

    /// <summary>
    /// One read of the modal. Unreadable is distinct from closed: a bridge that
    /// cannot see the panel must not claim there is no prompt.
    /// </summary>
    public sealed class PromptSnapshot
    {
        public bool Readable = true;
        public string Error;
        public bool Open;
        public PendingPrompt Prompt;
    }

    /// <summary>
    /// The /state summary read after a prompt answer. A failure leaves Readable
    /// false; it never fabricates day/cash/outside/location values.
    /// </summary>
    public sealed class PromptStateSummary
    {
        public bool Readable;
        public string Error;
        public int Day;
        public long Cash;
        public bool Outside;
        public string Location;

        /// <summary>
        /// R5/B2: PlayerStore.storeState after the answer (OPEN / CLOSED /
        /// MORNING). The caller needs it to tell "the modal closed" from "the
        /// action the modal confirmed actually happened".
        /// </summary>
        public string ShopState;
    }

    public static class PromptChoices
    {
        public const string Confirm = "confirm";
        public const string Cancel = "cancel";

        /// <summary>
        /// Only the two literal lower-case choices are accepted. Whitespace is
        /// tolerated, case variants and free-form text are not: an answer is a
        /// native button click and must be unambiguous.
        /// </summary>
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            if (value == null) return false;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, Confirm, StringComparison.Ordinal))
            {
                normalized = Confirm;
                return true;
            }

            if (string.Equals(trimmed, Cancel, StringComparison.Ordinal))
            {
                normalized = Cancel;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Pure prompt rules. Reading the modal is an adapter concern; deciding
    /// whether an answer may reach a native button is not, so the Bridge can be
    /// tested with fakes and never guesses.
    /// </summary>
    public static class PromptPolicy
    {
        public static bool IsOpen(PromptSnapshot snapshot)
        {
            return snapshot != null
                && snapshot.Readable
                && snapshot.Open
                && snapshot.Prompt != null;
        }

        public static bool IsClosed(PromptSnapshot snapshot)
        {
            return snapshot != null && snapshot.Readable && !snapshot.Open;
        }

        /// <summary>
        /// Returns null when the answer may be dispatched exactly once, or the
        /// stable failure code. An unreadable/missing/closed modal and a modal
        /// without details all map to no_prompt: nothing safe can be clicked.
        /// </summary>
        public static string ValidateAnswer(PromptSnapshot snapshot, string choice)
        {
            if (!IsOpen(snapshot)) return PromptErrors.NoPrompt;

            string normalized;
            if (!PromptChoices.TryNormalize(choice, out normalized)) return PromptErrors.BadChoice;

            if (string.Equals(normalized, PromptChoices.Confirm, StringComparison.Ordinal)
                && !snapshot.Prompt.ConfirmEnabled)
            {
                // A greyed-out native option is not a legal player action.
                return PromptErrors.ChoiceUnavailable;
            }

            if (string.Equals(normalized, PromptChoices.Cancel, StringComparison.Ordinal)
                && (snapshot.Prompt.SingleButton || !snapshot.Prompt.CancelEnabled))
            {
                return PromptErrors.ChoiceUnavailable;
            }

            return null;
        }

        /// <summary>
        /// Never throws. A null port or a throwing read is an unreadable panel,
        /// not a closed one.
        /// </summary>
        public static PromptSnapshot ReadSafe(IPromptPort port)
        {
            if (port == null) return Unreadable();

            try
            {
                PromptSnapshot snapshot = port.Read();
                if (snapshot == null) return Unreadable();
                if (!snapshot.Readable
                    && string.IsNullOrEmpty(snapshot.Error))
                {
                    // An unreadable panel must always carry a reason; never let a
                    // blank error read as "closed".
                    snapshot.Error = PromptErrors.ReadFailed;
                }

                return snapshot;
            }
            catch (Exception)
            {
                return Unreadable();
            }
        }

        private static PromptSnapshot Unreadable()
        {
            return new PromptSnapshot
            {
                Readable = false,
                Open = false,
                Error = PromptErrors.ReadFailed
            };
        }
    }

    /// <summary>
    /// R5/B2: what one answer attempt actually did. "I refused because the modal
    /// changed" is not "the native callback threw": only the former is a safe
    /// non-dispatch, so the two facts stay separate.
    /// </summary>
    public sealed class PromptAnswerOutcome
    {
        /// <summary>Exactly one native answer callback was invoked.</summary>
        public bool Dispatched;

        /// <summary>Null on success; a stable code otherwise.</summary>
        public string Error;

        public static PromptAnswerOutcome Called()
        {
            return new PromptAnswerOutcome { Dispatched = true };
        }

        public static PromptAnswerOutcome CalledWithError(string error)
        {
            return new PromptAnswerOutcome
            {
                Dispatched = true,
                Error = string.IsNullOrEmpty(error)
                    ? PromptErrors.AnswerFailed
                    : error
            };
        }

        public static PromptAnswerOutcome NotDispatched(string error)
        {
            return new PromptAnswerOutcome
            {
                Dispatched = false,
                Error = string.IsNullOrEmpty(error)
                    ? PromptErrors.PromptChanged
                    : error
            };
        }
    }

    public interface IPromptPort
    {
        /// <summary>Read-only projection of the native modal.</summary>
        PromptSnapshot Read();

        /// <summary>
        /// Exactly one native answer callback for the modal described by
        /// <paramref name="prompt"/>. The caller validates the choice first; this
        /// method never retries. It refuses (Dispatched=false) when the modal it
        /// would answer is no longer the one that was read.
        /// </summary>
        PromptAnswerOutcome Answer(PendingPrompt prompt, string choice);
    }

    public interface IPromptStatePort
    {
        PromptStateSummary ReadPromptState();
    }

    /// <summary>
    /// Minimal port for the native return-home action. It exists so the
    /// post-Return prompt check can be tested without Unity.
    /// </summary>
    public interface INightReturnPort
    {
        bool IsOutside { get; }
        int ItemsOnGround { get; }
        void Return();
    }
}
