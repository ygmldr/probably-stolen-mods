using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// One-shot tutorial hint dismissal. The write is idempotent at the game
    /// level (a bool assignment plus hiding the hint group) but the service
    /// never retries on an unknown readback: a caller that sees
    /// tutorial_hints_readback_mismatch must stop, exactly like other writes.
    /// </summary>
    public sealed class TutorialService
    {
        public const string Op = "dismiss_tutorial_hints";

        private readonly ITutorialPort port;

        public TutorialService(ITutorialPort port)
        {
            this.port = port;
        }

        public ActionResult Dismiss()
        {
            TutorialSnapshot before = ReadSafe();
            if (before == null || !before.Readable)
            {
                var failed = new JObject();
                failed["ok"] = false;
                failed["op"] = Op;
                failed["error"] = before == null || string.IsNullOrEmpty(before.Error)
                    ? TutorialRules.Unavailable
                    : before.Error;
                failed["dispatched"] = false;
                return ActionResult.SuccessRaw(Op, failed.ToString(Formatting.None));
            }

            if (!TutorialRules.NeedsWrite(before))
            {
                var unchanged = new JObject();
                unchanged["ok"] = true;
                unchanged["op"] = Op;
                unchanged["dispatched"] = false;
                unchanged["changed"] = false;
                unchanged["tutorial_enabled"] = before.Enabled;
                unchanged["hints_hidden"] = before.HintsHidden;
                unchanged["group_readable"] = before.GroupReadable;
                return ActionResult.SuccessRaw(Op, unchanged.ToString(Formatting.None));
            }

            try
            {
                port.DisableHints();
            }
            catch (Exception)
            {
                return Mismatch(before, "native_error");
            }

            TutorialSnapshot after = ReadSafe();
            TutorialOutcome outcome = TutorialRules.Evaluate(before, after, true);
            return Result(outcome);
        }

        private TutorialSnapshot ReadSafe()
        {
            if (port == null)
            {
                return new TutorialSnapshot
                {
                    Readable = false,
                    Error = TutorialRules.Unavailable
                };
            }

            try
            {
                return port.ReadTutorial();
            }
            catch (Exception)
            {
                return new TutorialSnapshot
                {
                    Readable = false,
                    Error = TutorialRules.Unavailable
                };
            }
        }

        private static ActionResult Mismatch(TutorialSnapshot before, string detail)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = Op;
            row["error"] = TutorialRules.ReadbackMismatch;
            row["detail"] = detail;
            row["dispatched"] = true;
            row["changed"] = before != null && before.Enabled;
            row["tutorial_enabled"] = before != null && before.Enabled;
            row["hints_hidden"] = before != null && before.HintsHidden;
            row["group_readable"] = before != null && before.GroupReadable;
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private static ActionResult Result(TutorialOutcome outcome)
        {
            var row = new JObject();
            row["ok"] = outcome.Ok;
            row["op"] = Op;
            row["dispatched"] = outcome.Dispatched;
            row["changed"] = outcome.Changed;
            row["tutorial_enabled"] = outcome.Enabled;
            row["hints_hidden"] = outcome.HintsHidden;
            row["group_readable"] = outcome.GroupReadable;
            row["error"] = outcome.Ok
                ? JValue.CreateNull()
                : new JValue(outcome.Error);
            if (!outcome.Ok)
            {
                row["detail"] = outcome.Detail;
            }

            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }
    }
}
