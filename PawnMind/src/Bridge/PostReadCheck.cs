using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    /// <summary>
    /// R5.3/B: the post-dispatch readback for the four player actions that used to
    /// return success without proving anything.
    ///
    /// The natives are asynchronous (the shutter animates, the customer walks in),
    /// so "the token did not change yet" is not failure: the operation goes back as
    /// <c>running</c>, a short window is armed, and the operation verifier decides
    /// once the window closes. An unreadable state never claims a change.
    /// </summary>
    internal static class PostReadCheck
    {
        public const int WindowMs = 8000;

        private sealed class Pending
        {
            public string Op;
            public string Before;
            public DateTime Deadline;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Pending> ByActionId =
            new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <summary>Compact per-op token; null when the state is unreadable.</summary>
        public static string Token(string op)
        {
            try
            {
                switch (op)
                {
                    case PostReadRules.OpenShop:
                        return GameState.GetShopState();

                    case PostReadRules.CallNextCustomer:
                    case PostReadRules.DismissCurrentClient:
                        return GameState.GetCurrentClientHandle() != 0
                            ? PostReadRules.Client
                            : PostReadRules.NoClient;

                    case PostReadRules.AdvanceDialogue:
                    {
                        DialogueView view = GameState.ReadDialogueView();
                        if (view == null || !view.Readable) return null;
                        if (!view.Available) return PostReadRules.NoDialogue;
                        return "dlg:" + Hash(view.Speaker + "\u0000" + view.Text);
                    }

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Begin(string op)
        {
            return PostReadRules.IsCovered(op) ? Token(op) : null;
        }

        /// <summary>
        /// Turns a claimed success into a proven one, or downgrades it to running and
        /// arms the window. Refusals and failures pass through untouched.
        /// </summary>
        public static ActionResult Finish(string op, string before, string actionId,
            ActionResult result)
        {
            if (!PostReadRules.IsCovered(op) || result == null || !result.Ok) return result;

            string after = Token(op);
            PostReadOutcome outcome = PostReadRules.Evaluate(op, before, after, after != null);
            if (outcome == PostReadOutcome.Completed) return result;

            if (string.IsNullOrEmpty(actionId)) return result;   // nothing to track

            lock (Sync)
            {
                ByActionId[actionId] = new Pending
                {
                    Op = op,
                    Before = before,
                    Deadline = DateTime.UtcNow.AddMilliseconds(WindowMs)
                };
            }

            var row = new Newtonsoft.Json.Linq.JObject();
            row["ok"] = true;
            row["op"] = op;
            row["dispatched"] = true;
            row["execution_status"] = ExecutionStatus.Running;
            row["post_read"] = "unconfirmed";
            row["readback"] = new Newtonsoft.Json.Linq.JObject
            {
                ["before"] = before,
                ["after"] = after
            };
            row["next"] = "the native call was issued but the expected change is not"
                + " visible yet; read get_operation until it settles (never re-dispatch)";
            return ActionResult.SuccessRaw(op, row.ToString());
        }

        /// <summary>
        /// True when a decision was made. False means "still inside the window".
        /// </summary>
        public static bool TryResolve(string actionId, out string execution, out string error)
        {
            execution = null;
            error = null;
            if (string.IsNullOrEmpty(actionId)) return false;

            Pending pending;
            lock (Sync)
            {
                if (!ByActionId.TryGetValue(actionId, out pending)) return false;
            }

            string after = Token(pending.Op);
            PostReadOutcome outcome = PostReadRules.Evaluate(
                pending.Op, pending.Before, after, after != null);

            if (outcome == PostReadOutcome.Completed)
            {
                execution = ExecutionStatus.Completed;
                lock (Sync) ByActionId.Remove(actionId);
                return true;
            }

            if (DateTime.UtcNow < pending.Deadline) return false;

            // The window closed with a readable state that never changed.
            execution = after == null ? ExecutionStatus.Uncertain : ExecutionStatus.Failed;
            error = after == null ? "state_unreadable" : "no_observable_change";
            lock (Sync) ByActionId.Remove(actionId);
            return true;
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
