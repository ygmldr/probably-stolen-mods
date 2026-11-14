using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
        private static readonly HashSet<string> ReadOnlyOps =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "bargain_preview",
                "capture_screenshot",
                "contraband_status",
                "events_status",
                "expedition_status",
                "get_item",
                "get_layout",
                "get_new_game_options",
                "get_recording_save_status",
                "get_ui_snapshot",
                "health_status",
                "husbandry_status",
                "inspect_ticket",
                "list_analyzers",
                "list_books",
                "list_contacts",
                "list_containers",
                "list_factions",
                "list_goods",
                "list_interactions",
                "list_inventory",
                "list_labels",
                "list_locations",
                "list_machines",
                "list_marks",
                "list_requests",
                "list_services",
                "list_signs",
                "list_screenshots",
                "list_tickets",
                "list_upgrades",
                "list_wine",
                "lottery_status",
                "machine_grow_status",
                "machine_status",
                "phone_state",
                "read_book",
                "read_item",
                "security_status",
                "wanted_status",
                "barter_status",
                "wine_status"
            };

        private static readonly HashSet<string> SensitiveKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "token",
                "authorization",
                "password",
                "secret",
                "api_key",
                "apikey",
                "access_token",
                "refresh_token"
            };

        private readonly ActionGate _gate;
        private readonly Func<long> _revision;
        private readonly Func<string, JObject, ActionResult> _dispatch;
        private readonly ActionHistory _history;
        // Writes fail closed when this is null; reads, ping and history do not need it.
        private readonly Func<long> _refreshRevision;
        private readonly Func<WriteGateDecision> _writeGate;
        private readonly string _secretToken;
        private readonly Action<ActionOutcome> _onOutcome;

        public ActionRouter(
            ActionGate gate,
            Func<long> revision,
            Func<string, JObject, ActionResult> dispatch,
            ActionHistory history = null,
            Func<long> refreshRevision = null,
            string secretToken = null,
            Func<WriteGateDecision> writeGate = null,
            Action<ActionOutcome> onOutcome = null)
        {
            if (gate == null) throw new ArgumentNullException("gate");
            if (revision == null) throw new ArgumentNullException("revision");
            if (dispatch == null) throw new ArgumentNullException("dispatch");

            _gate = gate;
            _revision = revision;
            _dispatch = dispatch;
            _history = history ?? new ActionHistory();
            _refreshRevision = refreshRevision;
            _writeGate = writeGate;
            _secretToken = secretToken;
            _onOutcome = onOutcome;
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
                return ProtocolError("bad_json");
            }

            JToken opToken = request["op"];
            if (opToken == null || opToken.Type == JTokenType.Null || opToken.Type == JTokenType.Undefined)
                return ProtocolError("missing_op");
            if (opToken.Type != JTokenType.String) return ProtocolError("bad_op");

            string op = ((string)opToken ?? string.Empty).Trim();
            if (op.Length == 0) return ProtocolError("missing_op");

            if (op == "ping")
            {
                return Scrub("{\"ok\":true,\"pong\":true,\"rev\":" + CurrentRevision() + "}");
            }

            if (op == "get_action_history")
            {
                return Scrub(_history.ToJson(ReadHistoryLimit(request)));
            }

            if (ReadOnlyOps.Contains(op))
            {
                string readResponse = HandleRead(op, request);
                NotifyReadResponse(op, readResponse);
                return readResponse;
            }

            // Every write result (success, business refusal, stale revision or a
            // gate refusal) is evidence: the caller sees one outcome per attempt.
            string writeResponse = HandleWrite(op, request);
            NotifyWriteResponse(op, writeResponse);
            return writeResponse;
        }

        private string HandleWrite(string op, JObject request)
        {
            string actionId;
            string actionIdError;
            if (!TryReadActionId(request, out actionId, out actionIdError))
                return ProtocolError(actionIdError);

            long? revision;
            string revisionError;
            if (!TryReadRevision(request, out revision, out revisionError))
                return ProtocolError(revisionError);

            JObject fingerprintArgs = ExtractFingerprintArgs(request);
            string fingerprint = Fingerprint(op, fingerprintArgs);
            string cached;

            // Probe the cache before refreshing state: a safe retry must return
            // the recorded first result (including an unknown outcome) even when
            // the game snapshot is currently unavailable. The request revision is
            // passed as the comparison revision only to suppress the stale check;
            // format validation and duplicate/conflict detection still run.
            ActionGateResult cachedGate = _gate.Check(
                actionId,
                revision,
                revision ?? 0,
                fingerprint,
                out cached);

            string cachedFailure = GateResponse(cachedGate, cached);
            if (cachedFailure != null) return Scrub(cachedFailure);

            // A locked version gate is a preflight refusal: do not touch game
            // state or dispatch. Cached duplicates/unknown outcomes above still
            // win, as required by the S1 protocol behavior.
            WriteGateDecision writeGate = CurrentWriteGate();
            if (writeGate == null || !writeGate.WriteAllowed) return Scrub(VersionGateResponse(writeGate));

            long currentRevision;
            if (!TryRefreshRevision(out currentRevision)) return StateUnavailable();

            ActionGateResult gate = _gate.Check(
                actionId,
                revision,
                currentRevision,
                fingerprint,
                out cached);

            string gateFailure = GateResponse(gate, cached);
            if (gateFailure != null) return Scrub(gateFailure);

            ActionResult result = Dispatch(op, request);

            long responseRevision;
            if (!TryRefreshRevision(out responseRevision))
            {
                // The action ran but the resulting state cannot be observed. Do
                // not report success/failure: cache an explicit unknown outcome so
                // a retry with the same id returns the same answer without a
                // second dispatch.
                string unknown = OutcomeUnknown(op, currentRevision);
                _gate.Remember(actionId, fingerprint, unknown);
                _history.Add(
                    HistoryText(Scrub(op)),
                    Scrub(JsonForAudit(request)),
                    false,
                    "outcome_unknown",
                    currentRevision);
                return Scrub(unknown);
            }

            string response = result.ToJson(responseRevision);

            _gate.Remember(actionId, fingerprint, response);
            _history.Add(
                HistoryText(Scrub(op)),
                Scrub(JsonForAudit(request)),
                result.Ok,
                HistoryText(Scrub(result.Error)),
                responseRevision);
            return Scrub(response);
        }

        /// <summary>
        /// Called by the transport after a write action was dispatched but its
        /// response could not be delivered (client reset or write timeout). The
        /// cached success/failure must not be shown to the retrying caller as if
        /// nothing ran: replace it with an explicit unknown outcome. A request
        /// that never reached the action cache (ping, reads, stale/refused writes)
        /// is ignored.
        /// </summary>
        public void MarkResponseUndelivered(string requestBody, string responseBody)
        {
            if (string.IsNullOrEmpty(requestBody)) return;

            JObject request;
            try
            {
                request = JObject.Parse(requestBody);
            }
            catch (Exception)
            {
                return;
            }

            JToken opToken = request["op"];
            if (opToken == null || opToken.Type != JTokenType.String) return;

            string op = ((string)opToken ?? string.Empty).Trim();
            if (op.Length == 0
                || op == "ping"
                || op == "get_action_history"
                || ReadOnlyOps.Contains(op))
            {
                return;
            }

            string actionId;
            string actionIdError;
            if (!TryReadActionId(request, out actionId, out actionIdError)) return;
            if (string.IsNullOrEmpty(actionId)) return;

            string unknown = OutcomeUnknown(op, CurrentRevision(), "transport_undelivered");
            if (!_gate.Replace(actionId, responseBody ?? string.Empty, unknown)) return;

            _history.Add(
                HistoryText(Scrub(op)),
                Scrub(JsonForAudit(request)),
                false,
                "outcome_unknown",
                CurrentRevision());
            Notify(new ActionOutcome(op, false, true, "outcome_unknown", CurrentRevision()));
        }

        private string HandleRead(string op, JObject request)
        {
            ActionResult result = Dispatch(op, request);
            long revision = CurrentRevision();

            _history.Add(
                HistoryText(Scrub(op)),
                Scrub(JsonForAudit(request)),
                result.Ok,
                HistoryText(Scrub(result.Error)),
                revision);
            return Scrub(result.ToJson(revision));
        }

        private ActionResult Dispatch(string op, JObject request)
        {
            try
            {
                ActionResult result = _dispatch(op, request);
                return result ?? ActionResult.Failure(op, "no_result");
            }
            catch (Exception)
            {
                return ActionResult.Failure(op, "exception");
            }
        }

        private void NotifyReadResponse(string op, string response)
        {
            bool ok;
            string error;
            ParseResponse(response, out ok, out error);
            if (!ok && FailureEvidence.IsHardFailure(error))
            {
                Notify(new ActionOutcome(op, false, false, error, CurrentRevision()));
            }
        }

        private void NotifyWriteResponse(string op, string response)
        {
            bool ok;
            string error;
            ParseResponse(response, out ok, out error);
            Notify(new ActionOutcome(op, ok, true, ok ? null : error, CurrentRevision()));
        }

        private void Notify(ActionOutcome outcome)
        {
            Action<ActionOutcome> handler = _onOutcome;
            if (handler == null || outcome == null) return;

            try
            {
                handler(outcome);
            }
            catch (Exception)
            {
                // Evidence capture must never break the action response.
            }
        }

        private static void ParseResponse(string response, out bool ok, out string error)
        {
            ok = false;
            error = null;

            try
            {
                JObject json = JObject.Parse(response ?? "{}");
                JToken flag = json["ok"];
                ok = flag != null && flag.Type == JTokenType.Boolean && (bool)flag;

                JToken code = json["error"];
                if (code != null && code.Type == JTokenType.String)
                {
                    error = (string)code;
                }
                else if (!ok)
                {
                    error = "bad_response";
                }
            }
            catch (Exception)
            {
                error = "bad_response";
            }
        }

        private string GateResponse(ActionGateResult result, string cached)
        {
            switch (result)
            {
                case ActionGateResult.Duplicate:
                    return string.IsNullOrEmpty(cached)
                        ? ProtocolError("missing_cached_response")
                        : cached;
                case ActionGateResult.Conflict:
                    return ProtocolError("action_conflict");
                case ActionGateResult.MissingActionId:
                    return ProtocolError("missing_action_id");
                case ActionGateResult.InvalidActionId:
                    return ProtocolError("bad_action_id");
                case ActionGateResult.MissingRevision:
                    return ProtocolError("missing_rev");
                case ActionGateResult.InvalidRevision:
                    return ProtocolError("bad_rev");
                case ActionGateResult.StaleRevision:
                    return ProtocolError("stale_rev");
                default:
                    return null;
            }
        }

        private string StateUnavailable()
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = "state_unavailable";
            payload["dispatched"] = false;
            payload["rev"] = CurrentRevision();
            return Scrub(payload.ToString(Formatting.None));
        }

        private WriteGateDecision CurrentWriteGate()
        {
            if (_writeGate == null) return WriteGateDecision.Locked(GameVersionGate.ReasonUnconfigured);

            try
            {
                return _writeGate() ?? WriteGateDecision.Locked(GameVersionGate.ReasonUnconfigured);
            }
            catch (Exception)
            {
                return WriteGateDecision.Locked(GameVersionGate.ReasonIdentityUnavailable);
            }
        }

        private string VersionGateResponse(WriteGateDecision decision)
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = decision == null || string.IsNullOrEmpty(decision.Reason)
                ? GameVersionGate.ReasonUnconfigured
                : decision.Reason;
            payload["write_gate"] = GameVersionGate.StateLocked;
            payload["dispatched"] = false;
            payload["rev"] = CurrentRevision();
            return Scrub(payload.ToString(Formatting.None));
        }

        private string OutcomeUnknown(string op, long revision, string detail = "state_unavailable")
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = "outcome_unknown";
            payload["detail"] = string.IsNullOrEmpty(detail) ? "state_unavailable" : detail;
            payload["unknown"] = true;
            payload["dispatched"] = true;
            payload["op"] = op;
            payload["rev"] = revision;
            return Scrub(payload.ToString(Formatting.None));
        }

        private long CurrentRevision()
        {
            try
            {
                return _revision();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private bool TryRefreshRevision(out long revision)
        {
            revision = 0;
            if (_refreshRevision == null) return false;

            try
            {
                revision = _refreshRevision();
                return revision >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private int ReadHistoryLimit(JObject request)
        {
            int limit = 20;
            JToken token = request["limit"];
            if (token == null) return limit;

            try
            {
                if (token.Type == JTokenType.Integer) limit = (int)token;
            }
            catch (Exception)
            {
            }

            return limit;
        }

        private static bool TryReadActionId(JObject request, out string actionId, out string error)
        {
            actionId = null;
            error = null;

            JToken token = request["action_id"];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return true;

            if (token.Type != JTokenType.String)
            {
                error = "bad_action_id";
                return false;
            }

            actionId = (string)token;
            return true;
        }

        private static bool TryReadRevision(JObject request, out long? revision, out string error)
        {
            revision = null;
            error = null;

            JToken token = request["rev"];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                error = "bad_rev";
                return false;
            }

            try
            {
                revision = (long)token;
            }
            catch (Exception)
            {
                error = "bad_rev";
                return false;
            }

            return true;
        }

        private static JObject ExtractFingerprintArgs(JObject request)
        {
            var args = new JObject();

            foreach (JProperty property in request.Properties())
            {
                if (property.Name == "op" || property.Name == "rev" || property.Name == "action_id")
                    continue;

                // The digest must cover the complete original op/args. Audit
                // redaction happens separately in JsonForAudit.
                args[property.Name] = property.Value.DeepClone();
            }

            return args;
        }

        private static JToken SanitizeToken(JToken token)
        {
            if (token == null) return JValue.CreateNull();

            if (token.Type == JTokenType.Object)
            {
                var sanitized = new JObject();
                foreach (JProperty property in ((JObject)token).Properties())
                {
                    sanitized[property.Name] = SensitiveKeys.Contains(property.Name)
                        ? new JValue("[redacted]")
                        : SanitizeToken(property.Value);
                }

                return sanitized;
            }

            if (token.Type == JTokenType.Array)
            {
                var sanitized = new JArray();
                foreach (JToken item in (JArray)token) sanitized.Add(SanitizeToken(item));
                return sanitized;
            }

            return token.DeepClone();
        }

        private static string JsonForAudit(JObject request)
        {
            return ((JObject)SanitizeToken(request)).ToString(Formatting.None);
        }

        private static string Fingerprint(string op, JObject args)
        {
            string canonical = op + "\n" + Canonicalize(args);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string Canonicalize(JToken token)
        {
            if (token == null) return "null";

            if (token.Type == JTokenType.Object)
            {
                JObject obj = (JObject)token;
                var names = new List<string>();
                foreach (JProperty property in obj.Properties()) names.Add(property.Name);
                names.Sort(StringComparer.Ordinal);

                var builder = new StringBuilder();
                builder.Append('{');
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(JsonConvert.ToString(names[i]));
                    builder.Append(':');
                    builder.Append(Canonicalize(obj[names[i]]));
                }

                builder.Append('}');
                return builder.ToString();
            }

            if (token.Type == JTokenType.Array)
            {
                var builder = new StringBuilder();
                builder.Append('[');
                bool first = true;
                foreach (JToken item in (JArray)token)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(Canonicalize(item));
                }

                builder.Append(']');
                return builder.ToString();
            }

            return token.ToString(Formatting.None);
        }

        private string ProtocolError(string code)
        {
            return Scrub(
                "{\"ok\":false,\"error\":\"" + code + "\",\"rev\":" + CurrentRevision() + "}");
        }

        private static string HistoryText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                builder.Append(char.IsControl(value[i]) ? ' ' : value[i]);
            }

            return builder.ToString();
        }

        private string Scrub(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (string.IsNullOrEmpty(_secretToken) || _secretToken.Length < 8) return value;

            string scrubbed = value.Replace(_secretToken, "[redacted]");

            // JSON-escaped forms of the token must not leak either. This is only
            // relevant for unusually configured tokens; generated tokens are hex.
            string escaped = JsonConvert.ToString(_secretToken);
            if (escaped.Length >= 2)
            {
                escaped = escaped.Substring(1, escaped.Length - 2);
                if (!string.Equals(escaped, _secretToken, StringComparison.Ordinal))
                {
                    scrubbed = scrubbed.Replace(escaped, "[redacted]");
                }
            }

            return scrubbed;
        }
    }
}
