using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S8 bridge orchestration for the game's generic confirmation modal.
    ///
    /// The class owns no Unity objects. It reads through Core ports, validates
    /// the answer with PromptPolicy, calls exactly one native button callback,
    /// and only reports success after a readback proves the panel closed. A
    /// read failure before the click is no_prompt: the bridge never guesses at
    /// a modal it cannot see.
    /// </summary>
    public sealed class PromptService
    {
        public const string OpGetPending = "get_pending_prompt";
        public const string OpAnswer = "answer_prompt";

        private readonly IPromptPort promptPort;
        private readonly IPromptStatePort statePort;

        public PromptService(IPromptPort promptPort, IPromptStatePort statePort)
        {
            this.promptPort = promptPort;
            this.statePort = statePort;
        }

        /// <summary>
        /// Read-only: never dispatches, never needs a revision, and therefore
        /// also works while the version write gate is locked.
        /// </summary>
        public ActionResult GetPending()
        {
            PromptSnapshot snapshot = PromptPolicy.ReadSafe(promptPort);
            if (!snapshot.Readable)
            {
                return Failure(
                    OpGetPending,
                    string.IsNullOrEmpty(snapshot.Error)
                        ? PromptErrors.ReadFailed
                        : snapshot.Error,
                    false,
                    null);
            }

            if (snapshot.Open && snapshot.Prompt == null)
            {
                // A visible panel without readable text is not a safe prompt.
                return Failure(OpGetPending, PromptErrors.ReadFailed, false, null);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = OpGetPending;
            row["prompt"] = PromptPolicy.IsOpen(snapshot)
                ? PromptJson.PromptObject(snapshot.Prompt, null)
                : JValue.CreateNull();
            return ActionResult.SuccessRaw(OpGetPending, row.ToString(Formatting.None));
        }

        public ActionResult AnswerPrompt(string choice)
        {
            PromptSnapshot before = PromptPolicy.ReadSafe(promptPort);
            string validation = PromptPolicy.ValidateAnswer(before, choice);
            if (validation != null)
            {
                return Failure(OpAnswer, validation, false, before);
            }

            string normalized;
            PromptChoices.TryNormalize(choice, out normalized);

            PromptAnswerOutcome outcome;
            try
            {
                // Exactly one native callback. A throw is never retried.
                outcome = promptPort.Answer(before.Prompt, normalized);
            }
            catch (Exception)
            {
                PromptSnapshot afterError = PromptPolicy.ReadSafe(promptPort);
                JObject failure = FailureRow(
                    OpAnswer, PromptErrors.AnswerFailed, true, afterError);
                failure["detail"] = "native_error";
                return ActionResult.SuccessRaw(
                    OpAnswer, failure.ToString(Formatting.None));
            }

            if (outcome == null)
            {
                // A port that cannot say what happened must not be read as a
                // safe refusal: the answer may have reached the native callback.
                PromptSnapshot afterNull = PromptPolicy.ReadSafe(promptPort);
                JObject failure = FailureRow(
                    OpAnswer, PromptErrors.AnswerFailed, true, afterNull);
                failure["detail"] = "no_outcome";
                return ActionResult.SuccessRaw(
                    OpAnswer, failure.ToString(Formatting.None));
            }

            if (!outcome.Dispatched)
            {
                // R5/B2: the native modal changed between read and answer, so
                // nothing was clicked. This is a safe refusal, not a failure.
                PromptSnapshot afterRefuse = PromptPolicy.ReadSafe(promptPort);
                JObject refused = FailureRow(
                    OpAnswer, outcome.Error, false, afterRefuse);
                refused["detail"] = "modal_changed_before_dispatch";
                return ActionResult.SuccessRaw(
                    OpAnswer, refused.ToString(Formatting.None));
            }

            if (!string.IsNullOrEmpty(outcome.Error))
            {
                PromptSnapshot afterError = PromptPolicy.ReadSafe(promptPort);
                JObject failure = FailureRow(
                    OpAnswer, outcome.Error, true, afterError);
                failure["detail"] = "native_error";
                return ActionResult.SuccessRaw(
                    OpAnswer, failure.ToString(Formatting.None));
            }

            PromptSnapshot after = PromptPolicy.ReadSafe(promptPort);
            if (!PromptPolicy.IsClosed(after))
            {
                JObject failure = FailureRow(
                    OpAnswer, PromptErrors.ReadbackMismatch, true, after);
                failure["readback"] = PromptPolicy.IsOpen(after)
                    ? "still_open"
                    : "unreadable";
                return ActionResult.SuccessRaw(
                    OpAnswer, failure.ToString(Formatting.None));
            }

            PromptStateSummary summary = ReadStateSafe();
            var row = new JObject();
            row["ok"] = true;
            row["op"] = OpAnswer;
            row["dispatched"] = true;
            row["choice"] = normalized;
            row["prompt_closed"] = true;
            row["prompt"] = JValue.CreateNull();
            row["state"] = PromptJson.StateSummary(summary);
            return ActionResult.SuccessRaw(OpAnswer, row.ToString(Formatting.None));
        }

        private PromptStateSummary ReadStateSafe()
        {
            if (statePort == null)
            {
                return new PromptStateSummary
                {
                    Readable = false,
                    Error = PromptErrors.ReadFailed
                };
            }

            try
            {
                return statePort.ReadPromptState() ?? new PromptStateSummary
                {
                    Readable = false,
                    Error = PromptErrors.ReadFailed
                };
            }
            catch (Exception)
            {
                return new PromptStateSummary
                {
                    Readable = false,
                    Error = PromptErrors.ReadFailed
                };
            }
        }

        private static ActionResult Failure(
            string op,
            string error,
            bool dispatched,
            PromptSnapshot snapshot)
        {
            return ActionResult.SuccessRaw(
                op,
                FailureRow(op, error, dispatched, snapshot).ToString(Formatting.None));
        }

        private static JObject FailureRow(
            string op,
            string error,
            bool dispatched,
            PromptSnapshot snapshot)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = op;
            row["error"] = error;
            row["dispatched"] = dispatched;
            row["prompt"] = PromptPolicy.IsOpen(snapshot)
                ? PromptJson.PromptObject(snapshot.Prompt, null)
                : JValue.CreateNull();
            return row;
        }
    }
}
