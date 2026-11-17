using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S8 return_home. MapUIManager.Return() may open a native confirmation
    /// modal instead of leaving, so the bridge reads the modal immediately
    /// after the call and refuses to report a completed return while it is
    /// open. The native action did run, hence dispatched=true.
    /// </summary>
    public sealed class NightReturnService
    {
        public const string ResponseConfirmationRequired = "confirmation_required";

        private readonly INightReturnPort nightPort;
        private readonly IPromptPort promptPort;

        public NightReturnService(INightReturnPort nightPort, IPromptPort promptPort)
        {
            this.nightPort = nightPort;
            this.promptPort = promptPort;
        }

        public ActionResult ReturnHome()
        {
            const string op = "return_home";

            if (nightPort == null)
            {
                return Failure(op, "not_initialized", false, 0);
            }

            if (!nightPort.IsOutside)
            {
                return Failure(op, "not_outside", false, 0);
            }

            int itemsOnGround = nightPort.ItemsOnGround;

            try
            {
                nightPort.Return();
            }
            catch (Exception e)
            {
                // The native call may have partially run. Do not retry it and
                // do not read the panel as if nothing happened.
                JObject failure = FailureRow(op, "return_failed", true, itemsOnGround);
                failure["detail"] = e.Message ?? string.Empty;
                return ActionResult.SuccessRaw(
                    op, failure.ToString(Formatting.None));
            }

            PromptSnapshot prompt = PromptPolicy.ReadSafe(promptPort);
            if (!prompt.Readable)
            {
                // The native Return ran but the modal state cannot be observed.
                // Never claim success in that case.
                JObject failure = FailureRow(op, PromptErrors.ReadFailed, true, itemsOnGround);
                failure["note"] = "the native return ran but the confirmation panel could not be read";
                return ActionResult.SuccessRaw(
                    op, failure.ToString(Formatting.None));
            }

            if (PromptPolicy.IsOpen(prompt))
            {
                JObject failure = FailureRow(
                    op, PromptErrors.ConfirmationRequired, true, itemsOnGround);
                failure["prompt"] = PromptJson.PromptObject(prompt.Prompt, op);
                failure["note"] = "the native return is waiting on a confirmation prompt; "
                    + "call get_pending_prompt, then answer_prompt";
                return ActionResult.SuccessRaw(
                    op, failure.ToString(Formatting.None));
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = op;
            // R4/F4b: the native Return ran; say so instead of leaving it implicit.
            row["dispatched"] = true;
            row["return_requested"] = true;
            row["items_on_ground"] = itemsOnGround;
            row["note"] = itemsOnGround > 0
                ? "items on the ground may block the return"
                : "return is asynchronous; poll /state.outside";
            return ActionResult.SuccessRaw(op, row.ToString(Formatting.None));
        }

        private static ActionResult Failure(
            string op,
            string error,
            bool dispatched,
            int itemsOnGround)
        {
            return ActionResult.SuccessRaw(
                op,
                FailureRow(op, error, dispatched, itemsOnGround).ToString(Formatting.None));
        }

        private static JObject FailureRow(
            string op,
            string error,
            bool dispatched,
            int itemsOnGround)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = op;
            row["error"] = error;
            row["dispatched"] = dispatched;
            row["items_on_ground"] = itemsOnGround;
            row["prompt"] = JValue.CreateNull();
            return row;
        }
    }
}
