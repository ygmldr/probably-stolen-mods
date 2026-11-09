using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Transport-independent action protocol: parse, revision check, idempotency,
    /// dispatch. The game-facing dispatch table lives in PawnMindMod so this class
    /// can be unit tested without Unity.
    /// </summary>
    public sealed class ActionRouter
    {
        private readonly ActionGate _gate;
        private readonly Func<long> _revision;
        private readonly Func<string, JObject, ActionResult> _dispatch;
        private readonly ActionHistory _history;

        public ActionRouter(
            ActionGate gate,
            Func<long> revision,
            Func<string, JObject, ActionResult> dispatch,
            ActionHistory history = null)
        {
            if (gate == null) throw new ArgumentNullException("gate");
            if (revision == null) throw new ArgumentNullException("revision");
            if (dispatch == null) throw new ArgumentNullException("dispatch");

            _gate = gate;
            _revision = revision;
            _dispatch = dispatch;
            _history = history ?? new ActionHistory();
        }

        public string Handle(string body)
        {
            JObject request;
            try
            {
                request = JObject.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            }
            catch (Exception)
            {
                return Error("bad_json");
            }

            string op = (string)request["op"];
            if (string.IsNullOrEmpty(op)) return Error("missing_op");

            if (op == "ping")
            {
                return "{\"ok\":true,\"pong\":true,\"rev\":" + _revision() + "}";
            }

            if (op == "get_action_history")
            {
                int limit = 20;
                JToken limitToken = request["limit"];
                try
                {
                    if (limitToken != null) limit = (int)limitToken;
                }
                catch (Exception)
                {
                }

                return _history.ToJson(limit);
            }

            long revision;
            try
            {
                revision = request["rev"] == null ? -1 : (long)request["rev"];
            }
            catch (Exception)
            {
                return Error("bad_rev");
            }

            string actionId = (string)request["action_id"];
            string cached;
            ActionGateResult gate = _gate.Check(actionId, revision, _revision(), out cached);

            if (gate == ActionGateResult.Duplicate) return cached;
            if (gate == ActionGateResult.StaleRevision)
            {
                return "{\"ok\":false,\"error\":\"stale_rev\",\"rev\":" + _revision() + "}";
            }

            ActionResult result;
            try
            {
                result = _dispatch(op, request);
            }
            catch (Exception)
            {
                result = ActionResult.Failure(op, "exception");
            }

            string response = result.ToJson(_revision());
            _gate.Remember(actionId, response);
            _history.Add(op, request.ToString(Formatting.None), result.Ok, result.Error, _revision());
            return response;
        }

        private static string Error(string code)
        {
            return "{\"ok\":false,\"error\":\"" + code + "\"}";
        }
    }
}
