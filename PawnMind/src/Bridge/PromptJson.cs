using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// JSON shapes shared by get_pending_prompt, answer_prompt and /state. The
    /// same object is emitted in all three places so an agent can compare them.
    /// </summary>
    internal static class PromptJson
    {
        public static JObject PromptObject(PendingPrompt prompt, string sourceOp)
        {
            var row = new JObject();
            if (prompt == null) return row;

            row["kind"] = string.IsNullOrEmpty(prompt.Kind)
                ? PromptKinds.Confirmation
                : prompt.Kind;
            // R5/B2: the caller compares identities, never button positions.
            row["target"] = string.IsNullOrEmpty(prompt.Target)
                ? PromptTargets.GenericConfirmation
                : prompt.Target;
            row["body"] = prompt.Body ?? string.Empty;
            row["confirm_text"] = prompt.ConfirmText ?? string.Empty;
            row["cancel_text"] = prompt.CancelText ?? string.Empty;
            row["single_button"] = prompt.SingleButton;
            row["confirm_enabled"] = prompt.ConfirmEnabled;
            row["cancel_enabled"] = prompt.CancelEnabled && !prompt.SingleButton;

            string source = string.IsNullOrEmpty(sourceOp) ? prompt.SourceOp : sourceOp;
            row["source_op"] = string.IsNullOrEmpty(source)
                ? JValue.CreateNull()
                : new JValue(source);
            return row;
        }

        /// <summary>
        /// /state.prompt: null when no panel is open, the prompt object when it
        /// is, and {"readable":false} when the adapter could not read it.
        /// </summary>
        public static JToken ForState(PromptSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Readable) return Unreadable();
            if (!PromptPolicy.IsOpen(snapshot)) return JValue.CreateNull();
            return PromptObject(snapshot.Prompt, null);
        }

        public static JObject Unreadable()
        {
            var row = new JObject();
            row["readable"] = false;
            return row;
        }

        public static JObject StateSummary(PromptStateSummary summary)
        {
            var row = new JObject();
            if (summary == null || !summary.Readable)
            {
                row["readable"] = false;
                return row;
            }

            row["readable"] = true;
            row["day"] = summary.Day;
            row["cash"] = summary.Cash;
            row["outside"] = summary.Outside;
            row["location"] = string.IsNullOrEmpty(summary.Location)
                ? JValue.CreateNull()
                : new JValue(summary.Location);
            row["shop_state"] = string.IsNullOrEmpty(summary.ShopState)
                ? JValue.CreateNull()
                : new JValue(summary.ShopState);
            return row;
        }
    }
}
