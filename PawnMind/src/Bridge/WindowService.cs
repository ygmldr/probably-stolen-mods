using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Closes the native panels that hide the counter: paper notes, books,
    /// contracts, deeds and floating windows. Reading a note used to leave it on
    /// screen forever, which both blocks the video and hides the shop, so the
    /// model gets one explicit op that clears exactly what is visible.
    ///
    /// The op is one shot: every close runs once, and a screen that is still
    /// dirty afterwards is reported as a mismatch instead of being retried.
    /// </summary>
    public sealed class WindowService
    {
        public const string Op = "close_windows";

        private readonly IWindowPort port;

        public WindowService(IWindowPort port)
        {
            this.port = port;
        }

        public ActionResult Close()
        {
            WindowSnapshot before = ReadSafe();

            if (before == null || !before.Readable)
            {
                var failed = new JObject();
                failed["ok"] = false;
                failed["op"] = Op;
                failed["error"] = before == null || string.IsNullOrEmpty(before.Error)
                    ? WindowErrors.Unavailable
                    : before.Error;
                failed["dispatched"] = false;
                return ActionResult.SuccessRaw(Op, failed.ToString(Formatting.None));
            }

            List<string> plan = WindowRules.PlanClosures(before);
            if (plan.Count == 0)
            {
                var noop = new JObject();
                noop["ok"] = true;
                noop["op"] = Op;
                noop["dispatched"] = false;
                noop["changed"] = false;
                noop["closed"] = new JArray();
                noop["remaining"] = Describe(before);
                return ActionResult.SuccessRaw(Op, noop.ToString(Formatting.None));
            }

            var closed = new JArray();
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    string kind = plan[i];
                    if (kind == WindowKinds.Movable)
                    {
                        port.HideMovableWindows();
                    }
                    else if (kind == WindowKinds.Custom)
                    {
                        port.CloseCustomWindows();
                    }
                    else if (kind == WindowKinds.Overlays)
                    {
                        // R5/B2: typed dismissal of the two adapter-less overlays.
                        // The port invokes their own native close/apply buttons.
                        port.CloseOverlays();
                    }
                    else
                    {
                        port.CloseWindow(kind);
                    }
                    closed.Add(kind);
                }
            }
            catch (Exception)
            {
                return Failure(WindowErrors.ReadbackMismatch, closed, ReadSafe(), "native_error");
            }

            WindowSnapshot after = ReadSafe();
            if (!WindowRules.IsClean(after))
            {
                return Failure(WindowErrors.ReadbackMismatch, closed, after, null);
            }

            var row = new JObject();
            row["ok"] = true;
            row["op"] = Op;
            row["dispatched"] = true;
            row["changed"] = true;
            row["closed"] = closed;
            row["remaining"] = Describe(after);
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private WindowSnapshot ReadSafe()
        {
            try
            {
                return port.ReadWindows();
            }
            catch (Exception)
            {
                return new WindowSnapshot { Readable = false, Error = WindowErrors.Unavailable };
            }
        }

        private ActionResult Failure(string error, JArray closed, WindowSnapshot after, string detail)
        {
            var row = new JObject();
            row["ok"] = false;
            row["op"] = Op;
            row["error"] = error;
            row["dispatched"] = true;
            row["closed"] = closed;
            row["remaining"] = Describe(after);
            if (detail != null) row["detail"] = detail;
            return ActionResult.SuccessRaw(Op, row.ToString(Formatting.None));
        }

        private static JObject Describe(WindowSnapshot snapshot)
        {
            var row = new JObject();
            if (snapshot == null)
            {
                row["readable"] = false;
                return row;
            }

            row["readable"] = snapshot.Readable;
            row["book"] = snapshot.BookOpen;
            row["paper"] = snapshot.PaperOpen;
            row["contract"] = snapshot.ContractOpen;
            row["deed"] = snapshot.DeedOpen;
            row["movable"] = snapshot.MovableVisible;
            return row;
        }
    }
}
