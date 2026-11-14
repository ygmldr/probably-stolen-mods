using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PawnMind.Bridge
{
    public sealed class ActionResult
    {
        public bool Ok { get; private set; }
        public string Op { get; private set; }
        public string Error { get; private set; }
        public string RawJson { get; private set; }

        public static ActionResult Success(string op)
        {
            return new ActionResult { Ok = true, Op = op };
        }

        public static ActionResult Failure(string op, string error)
        {
            return new ActionResult { Ok = false, Op = op, Error = error };
        }

        public static ActionResult SuccessRaw(string op, string json)
        {
            var result = new ActionResult { Op = op, RawJson = json };

            if (string.IsNullOrEmpty(json))
            {
                result.Error = "empty_response";
                return result;
            }

            try
            {
                JObject raw = JObject.Parse(json);
                JToken ok = raw["ok"];

                // Every current raw producer emits a top-level boolean ok. A
                // missing/non-boolean flag is a malformed response and must be
                // fail-closed; a legitimate new producer must wrap its payload
                // with ok rather than relaxing this check.
                if (ok == null || ok.Type != JTokenType.Boolean)
                {
                    result.Error = "bad_response";
                    return result;
                }

                result.Ok = (bool)ok;
                if (!result.Ok)
                {
                    JToken error = raw["error"];
                    result.Error = error != null && error.Type == JTokenType.String
                        ? (string)error
                        : "raw_failure";
                }
            }
            catch (Exception)
            {
                result.Error = "bad_response";
            }

            return result;
        }

        public string ToJson(long revision)
        {
            if (RawJson != null)
            {
                try
                {
                    JObject raw = JObject.Parse(RawJson);
                    JToken ok = raw["ok"];
                    if (ok != null && ok.Type == JTokenType.Boolean)
                    {
                        raw["rev"] = revision;
                        return raw.ToString(Formatting.None);
                    }
                }
                catch (Exception)
                {
                    // A malformed raw result must not be returned as a success.
                }
            }

            string json = "{\"ok\":" + (Ok ? "true" : "false")
                + ",\"op\":\"" + Escape(Op) + "\"";

            if (!Ok) json += ",\"error\":\"" + Escape(Error) + "\"";

            json += ",\"rev\":" + revision + "}";
            return json;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string json = JsonConvert.ToString(value);
            return json.Substring(1, json.Length - 2);
        }
    }
}
