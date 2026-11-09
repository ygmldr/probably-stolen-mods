using System;

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
            return new ActionResult { Ok = true, Op = op, RawJson = json };
        }

        public string ToJson(long revision)
        {
            if (RawJson != null) return RawJson;

            string json = "{\"ok\":" + (Ok ? "true" : "false")
                + ",\"op\":\"" + Escape(Op) + "\"";

            if (!Ok) json += ",\"error\":\"" + Escape(Error) + "\"";

            json += ",\"rev\":" + revision + "}";
            return json;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
