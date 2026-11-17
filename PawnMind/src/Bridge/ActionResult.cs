using System;
using Newtonsoft.Json;
using PawnMind.Core;
using Newtonsoft.Json.Linq;

namespace PawnMind.Bridge
{
    public sealed class ActionResult
    {
        public bool Ok { get; private set; }
        public string Op { get; private set; }
        public string Error { get; private set; }
        public string RawJson { get; private set; }

        /// <summary>
        /// The action asked the game to load a save or start a new run. This is a
        /// *request*, not a completed switch: the router only evicts the previous
        /// run's evidence once a native run identity readback confirms the switch
        /// (R4 / F1b). A pending load, a confirmed prompt and an unreadable run
        /// identity all leave the old evidence in place.
        /// </summary>
        public bool RunChangeRequested { get; private set; }

        /// <summary>
        /// R4/F4b: whether the native call ran, as claimed by the service that
        /// produced this result. A RawJson write response declares it explicitly;
        /// a response that says nothing is <see cref="DispatchStatus.Unknown"/> and
        /// is never turned into a safe-looking refusal.
        /// </summary>
        public string Dispatch { get; private set; }

        public ActionResult WithRunChangeRequested()
        {
            RunChangeRequested = true;
            return this;
        }

        /// <summary>The action ran and reported success: the native call happened.</summary>
        public static ActionResult Success(string op)
        {
            return new ActionResult
            {
                Ok = true, Op = op, Dispatch = DispatchStatus.Dispatched
            };
        }

        /// <summary>
        /// A guard refused before any native call. This is the *only* shape that is
        /// allowed to claim "nothing ran"; a failure raised after the native call
        /// must use <see cref="Failed"/>.
        /// </summary>
        public static ActionResult Failure(string op, string error)
        {
            return new ActionResult
            {
                Ok = false, Op = op, Error = error,
                Dispatch = DispatchStatus.NotDispatched
            };
        }

        /// <summary>The native call was issued and the action did not achieve it.</summary>
        public static ActionResult Failed(string op, string error)
        {
            return new ActionResult
            {
                Ok = false, Op = op, Error = error,
                Dispatch = DispatchStatus.Dispatched
            };
        }

        /// <summary>
        /// The native call may or may not have run (an exception, a lost readback).
        /// Never retried blindly and never reported as a safe rejection.
        /// </summary>
        public static ActionResult UnknownDispatch(string op, string error)
        {
            return new ActionResult
            {
                Ok = false, Op = op, Error = error,
                Dispatch = DispatchStatus.Unknown
            };
        }

        public static ActionResult SuccessRaw(string op, string json)
        {
            // Absent a trustworthy claim the raw producer is treated as unknown.
            var result = new ActionResult
            {
                Op = op, RawJson = json, Dispatch = DispatchStatus.Unknown
            };

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

                result.Dispatch = ReadDispatch(raw);
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

        /// <summary>
        /// Reads the service's dispatch claim. Only a real JSON boolean counts;
        /// anything else is "unknown", including a missing field, a string and a
        /// value the producer invented.
        /// </summary>
        private static string ReadDispatch(JObject raw)
        {
            JToken value = raw["dispatched"];
            if (value == null || value.Type != JTokenType.Boolean)
                return DispatchStatus.Unknown;

            return (bool)value ? DispatchStatus.Dispatched : DispatchStatus.NotDispatched;
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
