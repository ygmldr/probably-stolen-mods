using System;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// One dispatch-level guard for every legacy/new write path that could write
    /// a save or start a game. The act wrapper is refused for protected ops so
    /// there is no second, less auditable spelling of the same native action.
    /// </summary>
    public sealed class RecordingDispatchGuard
    {
        private readonly RecordingSaveService service;

        public RecordingDispatchGuard(RecordingSaveService service)
        {
            this.service = service;
        }

        public bool TryHandle(string op, JObject request, out ActionResult result)
        {
            result = null;

            string target = op;
            JObject args = request;
            bool viaAct = false;

            if (string.Equals(op, "act", StringComparison.Ordinal))
            {
                string inner = request == null ? null : (string)request["action"];
                if (string.IsNullOrEmpty(inner) || string.Equals(inner, "act", StringComparison.Ordinal))
                {
                    result = ActionResult.Failure("act", "missing_action");
                    return true;
                }

                target = inner;
                viaAct = true;
                args = request["args"] as JObject;
                if (args == null) args = new JObject();
            }

            // The generic act wrapper must never reach the native debug/cheat
            // operations: they mutate money and items directly and would make the
            // recorded run unauditable regardless of the recording configuration.
            if (viaAct && IsDebugOp(target))
            {
                result = ActionResult.Failure(target, RecordingSaveErrors.ActWrapperForbidden);
                return true;
            }

            if (!IsProtected(target))
            {
                if (!IsDebugOp(target)) return false;

                // Direct debug ops stay available for local development only while
                // the recording policy is disabled.
                if (service == null)
                {
                    result = ActionResult.Failure(target, RecordingSaveErrors.ServiceUnavailable);
                    return true;
                }
                if (!service.RecordingActive) return false;

                result = ActionResult.Failure(target, RecordingSaveErrors.DebugForbidden);
                return true;
            }

            if (viaAct)
            {
                result = ActionResult.Failure(target, RecordingSaveErrors.ActWrapperForbidden);
                return true;
            }

            if (service == null)
            {
                result = ActionResult.Failure(target, RecordingSaveErrors.ServiceUnavailable);
                return true;
            }

            switch (target)
            {
                case "new_game":
                    result = service.NewGame();
                    return true;

                case "load_game":
                    result = service.LoadGame(ReadInt(args, "slot", -1));
                    return true;

                case "save_game":
                    result = service.SaveGame();
                    return true;

                case "sleep":
                    result = service.Sleep(ReadBool(args, "dismiss_waiting"));
                    return true;

                case "press":
                    // The generic press invokes an arbitrary UI callback, so it stays
                    // refused everywhere except an explicit test-mode session. There
                    // the target must still be a whitelisted overlay-dismiss button
                    // (UiPressPolicy), enforced at the click site.
                    if (ModePolicy.Mode != RunMode.Test)
                    {
                        result = ActionResult.Failure(target, RecordingSaveErrors.PressForbidden);
                        return true;
                    }

                    return false;

                case "save_recording_game":
                    result = service.SaveRecordingGame();
                    return true;

                case "load_recording_game":
                    result = service.LoadRecordingGame();
                    return true;

                case "confirm_new_game":
                    result = service.ConfirmNewGame();
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsDebugOp(string op)
        {
            switch (op)
            {
                case "debug_pulse":
                case "debug_dump":
                case "debug_fill_water":
                case "debug_grant":
                case "debug_unlock_expedition":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsProtected(string op)
        {
            switch (op)
            {
                case "new_game":
                case "load_game":
                case "save_game":
                case "sleep":
                case "press":
                case "save_recording_game":
                case "load_recording_game":
                case "confirm_new_game":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ReadBool(JObject request, string name)
        {
            if (request == null) return false;
            JToken token = request[name];
            if (token == null) return false;

            try
            {
                return token.Type == JTokenType.Boolean && (bool)token;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int ReadInt(JObject request, string name, int fallback)
        {
            if (request == null) return fallback;
            JToken token = request[name];
            if (token == null) return fallback;

            try
            {
                return (int)token;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
