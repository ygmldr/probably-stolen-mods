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

            if (!IsProtected(target)) return false;

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
                    result = service.Sleep();
                    return true;

                case "press":
                    result = service.Press();
                    return true;

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
