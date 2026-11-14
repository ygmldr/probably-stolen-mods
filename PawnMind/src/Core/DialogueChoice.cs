using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Stable S5 dialogue error codes. Callers branch on these strings; changing
    /// one is a protocol change.
    /// </summary>
    public static class DialogueChoiceErrors
    {
        public const string NoDialogue = "no_dialogue";
        public const string NoChoice = "no_choice";
        public const string BadIndex = "bad_index";
        public const string ChoiceDisabled = "choice_disabled";
        public const string NotApplied = "choice_not_applied";
        public const string DialogueUnavailable = "dialogue_unavailable";
        public const string OutcomeUnknown = "outcome_unknown";
    }

    /// <summary>
    /// One player-visible dialogue choice. Enabled mirrors the native button
    /// interactable state; no hidden condition function or disabled-reason text
    /// is projected here.
    /// </summary>
    public sealed class DialogueChoiceView
    {
        public int Index;
        public string Text = string.Empty;
        public bool Enabled;
        public bool HasItem;
        public int ItemId;
        public string ItemName;
    }

    /// <summary>
    /// Read-only dialogue projection. Identity is an opaque in-process handle
    /// used only for the post-select readback; it is not exposed in JSON.
    /// </summary>
    public sealed class DialogueView
    {
        public bool Readable = true;
        public string Error;
        public bool Available;
        public string Identity;
        public string Speaker;
        public string Text;
        public bool Ended;
        public bool Choice;
        public bool Typing;
        public List<DialogueChoiceView> Choices = new List<DialogueChoiceView>();
    }

    public static class DialogueChoicePolicy
    {
        /// <summary>
        /// Returns the stable failure code for a choice, or null when it may be
        /// dispatched. The order matters: no dialogue, then no choice set, then
        /// index range, then the native enabled state. Nothing here calls into
        /// the game.
        /// </summary>
        public static string Validate(DialogueView dialogue, int index)
        {
            if (dialogue == null || !dialogue.Readable)
                return DialogueChoiceErrors.DialogueUnavailable;
            if (!dialogue.Available)
                return DialogueChoiceErrors.NoDialogue;
            if (!dialogue.Choice)
                return DialogueChoiceErrors.NoChoice;
            if (index < 0 || index >= dialogue.Choices.Count)
                return DialogueChoiceErrors.BadIndex;
            if (!dialogue.Choices[index].Enabled)
                return DialogueChoiceErrors.ChoiceDisabled;
            return null;
        }

        /// <summary>
        /// True only when the dialogue observably moved. An unreadable after
        /// state is not "changed"; the caller must map it to outcome_unknown.
        /// </summary>
        public static bool Changed(DialogueView before, DialogueView after)
        {
            if (after == null || !after.Readable) return false;
            if (before == null || !before.Readable) return true;
            if (!after.Available) return before.Available;
            if (!before.Available) return true;

            if (!string.Equals(before.Identity, after.Identity, StringComparison.Ordinal)) return true;
            if (!string.Equals(before.Speaker, after.Speaker, StringComparison.Ordinal)) return true;
            if (!string.Equals(before.Text, after.Text, StringComparison.Ordinal)) return true;
            if (before.Ended != after.Ended) return true;
            if (before.Choice != after.Choice) return true;

            List<DialogueChoiceView> left = before.Choices;
            List<DialogueChoiceView> right = after.Choices;
            if (left.Count != right.Count) return true;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].Text, right[i].Text, StringComparison.Ordinal)) return true;
                if (left[i].Enabled != right[i].Enabled) return true;
            }

            return false;
        }
    }

    public interface IDialoguePort
    {
        DialogueView ReadDialogue();

        /// <summary>Exactly one native SelectChoice call; never retried.</summary>
        void SelectChoice(int index);
    }
}
