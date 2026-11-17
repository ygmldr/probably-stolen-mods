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
                "get_calendar",
                "get_clientele",
                "get_day_report",
                "get_item",
                "get_layout",
                "get_new_game_options",
                "get_pending_prompt",
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
        // Runs before the cache probe and before any state refresh, so a refused
        // operation cannot have touched the snapshot (and therefore cannot have
        // triggered a native getter with side effects).
        private readonly Func<string, string> _authorize;

        // D2: optional readback verifier. It runs on GET /operation for a
        // non-terminal operation and may promote running/uncertain to completed or
        // failed. It must be a read-only game probe; null keeps the status frozen.
        private readonly Func<OperationRecord, OperationRecord> _operationVerifier;
        private bool _operationsCleared;

        public ActionRouter(
            ActionGate gate,
            Func<long> revision,
            Func<string, JObject, ActionResult> dispatch,
            ActionHistory history = null,
            Func<long> refreshRevision = null,
            string secretToken = null,
            Func<WriteGateDecision> writeGate = null,
            Action<ActionOutcome> onOutcome = null,
            Func<string, string> authorize = null,
            Func<OperationRecord, OperationRecord> operationVerifier = null)
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
            _authorize = authorize;
            _operationVerifier = operationVerifier;
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

            if (_authorize != null)
            {
                string refusal = _authorize(op);
                if (refusal != null) return ProtocolError(refusal);
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
            // the recorded first result (including a running/uncertain one) even
            // when the game snapshot is currently unavailable. The request revision
            // is passed as the comparison revision only to suppress the stale
            // check; format validation and duplicate/conflict detection still run.
            ActionGateResult cachedGate = _gate.Check(
                actionId,
                revision,
                revision ?? 0,
                fingerprint,
                out cached);

            if (cachedGate == ActionGateResult.Duplicate)
                return Scrub(DecorateCached(actionId, cached));

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

            // D4: while the same operation is still running (native animation or
            // async save), a second action id for that op would issue a duplicate
            // native call. Refuse it; unrelated writes (for example a dialogue
            // continuation) stay available so the workflow cannot deadlock.
            OperationRecord pending = _gate.FindPendingFor(op);
            if (pending != null) return Scrub(OperationPending(op, pending));

            OperationRecord operation = _gate.Begin(actionId, op, currentRevision, fingerprint);
            ActionResult result = Dispatch(op, request);

            long responseRevision;
            if (!TryRefreshRevision(out responseRevision))
            {
                // The action ran but the resulting state cannot be observed. Do
                // not report success/failure: cache an explicit uncertain outcome
                // so a retry with the same id returns the same answer without a
                // second dispatch.
                string unknown = OutcomeUnknown(op, currentRevision);
                bool unknownDispatched = DispatchedClaim(result);
                string envelope = DecorateResponse(actionId, unknown,
                    ExecutionStatus.Uncertain, DeliveryStatus.Delivered, false, unknownDispatched);
                _gate.Complete(actionId, envelope, ExecutionStatus.Uncertain,
                    DeliveryStatus.Delivered, currentRevision, "outcome_unknown", unknownDispatched);
                if (result.RunChanged) _gate.ClearOperationsExcept(actionId);
                _history.Add(
                    HistoryText(Scrub(op)),
                    Scrub(JsonForAudit(request)),
                    false,
                    "outcome_unknown",
                    currentRevision);
                return Scrub(envelope);
            }

            string response = result.ToJson(responseRevision);
            string execution = Classify(result, response, out string error, out bool dispatched);
            string decorated = DecorateResponse(actionId, response, execution,
                DeliveryStatus.Delivered, false, dispatched);
            _gate.Complete(actionId, decorated, execution, DeliveryStatus.Delivered,
                responseRevision, error, dispatched);
            if (result.RunChanged) _gate.ClearOperationsExcept(actionId);
            _history.Add(
                HistoryText(Scrub(op)),
                Scrub(JsonForAudit(request)),
                ExecutionStatus.IsSuccess(execution),
                HistoryText(Scrub(error)),
                responseRevision);
            return Scrub(decorated);
        }

        /// <summary>
        /// Called by the transport after a write action was dispatched but its
        /// response could not be delivered (client reset or write timeout).
        ///
        /// D1: only the delivery axis changes. The completed execution evidence and
        /// the cached response body stay intact, so a retry with the same id sees
        /// what really happened plus an explicit undelivered marker.
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

            if (!_gate.MarkUndelivered(actionId)) return;

            OperationRecord record = _gate.Find(actionId);
            _history.Add(
                HistoryText(Scrub(op)),
                Scrub(JsonForAudit(request)),
                record != null && record.Ok,
                "delivery_undelivered",
                CurrentRevision());
            Notify(new ActionOutcome(op, record != null && record.Ok, true,
                "delivery_undelivered", CurrentRevision()));
        }

        // ------------------------------------------------------------- D2 query

        /// <summary>
        /// Read-only two-axis query for one action id. Never dispatches and never
        /// builds the game snapshot. Unknown ids (including after a Mod restart or a
        /// run change) return a structured refusal; the caller must re-read state
        /// instead of replaying with a new action id.
        /// </summary>
        public JObject GetOperationJson(string operationId)
        {
            var payload = new JObject();

            if (string.IsNullOrEmpty(operationId) || operationId.Trim().Length == 0
                || operationId.Length > 128 || HasControlCharacter(operationId))
            {
                payload["ok"] = false;
                payload["known"] = false;
                payload["error"] = OperationErrors.BadOperationId;
                return payload;
            }

            OperationRecord record = _gate.Find(operationId);
            if (record == null)
            {
                payload["ok"] = false;
                payload["known"] = false;
                payload["operation_id"] = operationId;
                payload["error"] = OperationErrors.UnknownOperation;
                payload["next"] = "do not replay this id or start a new action id for"
                    + " the same op; re-read get_state and decide again";
                if (_operationsCleared || _gate.ClearCount > 0)
                {
                    payload["process_restarted"] = true;
                    payload["detail"] = "the operation cache was reset by a run change"
                        + " (new game / loaded save). Old evidence is not attributed to"
                        + " the current run.";
                }

                return payload;
            }

            record = Verify(record);
            payload["ok"] = record.Ok;
            payload["known"] = true;
            payload["operation_id"] = record.OperationId;
            payload["op"] = record.Op;
            payload["execution_status"] = record.Execution;
            payload["delivery_status"] = record.Delivery;
            payload["dispatched"] = record.Dispatched;
            payload["error"] = record.Error == null ? JValue.CreateNull() : new JValue(record.Error);
            payload["rev"] = record.Revision;
            payload["result_rev"] = record.ResultRevision;
            if (!ExecutionStatus.IsTerminal(record.Execution))
            {
                payload["next"] = record.Execution == ExecutionStatus.Uncertain
                    ? "the outcome is unknown: do not retry with a new action id; read state and decide"
                    : "still running: call wait_operation or read state again; do not re-dispatch";
            }

            return payload;
        }

        /// <summary>
        /// Run/timeline change (new game, loaded save, restored timeline): every
        /// old operation record is dropped so evidence from another run is never
        /// attributed to this one. Returns the number of dropped records.
        /// </summary>
        public int ResetOperations()
        {
            int cleared = _gate.ClearOperations();
            _operationsCleared = true;
            return cleared;
        }

        private OperationRecord Verify(OperationRecord record)
        {
            if (record == null) return null;
            if (ExecutionStatus.IsTerminal(record.Execution)) return record;
            if (_operationVerifier == null) return record;

            try
            {
                OperationRecord updated = _operationVerifier(record.Copy());
                if (updated == null) return record;
                _gate.Update(updated);
                return updated.Copy();
            }
            catch (Exception)
            {
                // A failing verifier must never break the read or fake a success.
                return record;
            }
        }

        private string DecorateCached(string actionId, string cached)
        {
            OperationRecord record = _gate.Find(actionId);
            if (record == null) return cached;

            bool redelivered = record.Delivery == DeliveryStatus.Undelivered;
            return DecorateResponse(actionId, cached, record.Execution, record.Delivery,
                redelivered, record.Dispatched);
        }

        private string DecorateResponse(string actionId, string response,
            string execution, string delivery, bool redelivered, bool dispatched)
        {
            JObject json;
            try
            {
                json = JObject.Parse(string.IsNullOrEmpty(response) ? "{}" : response);
            }
            catch (Exception)
            {
                // A malformed body is already a failure; do not invent fields on it.
                return response;
            }

            bool completed = ExecutionStatus.IsSuccess(execution);

            // F3: a promoted operation must not keep the facts of the state it was
            // promoted from (pending:true + ok:true is unreadable for a model). The
            // original body is preserved once, under initial_result, as evidence.
            JObject initial = null;
            if (completed
                && (JsonBool(json, "pending") || JsonBool(json, "uncertain") || JsonBool(json, "unknown")))
            {
                initial = (JObject)json.DeepClone();
                json.Remove("pending");
                json.Remove("uncertain");
                json.Remove("unknown");
                json.Remove("error");
                if (json["shop_closed_after"] != null) json["shop_closed_after"] = true;
            }

            json["ok"] = completed;
            json["operation_id"] = actionId;
            json["execution_status"] = execution;
            json["delivery_status"] = delivery;
            json["dispatched"] = dispatched;
            if (initial != null) json["initial_result"] = initial;

            if (redelivered) json["redelivered"] = true;
            return json.ToString(Formatting.None);
        }

        /// <summary>The service's own dispatched claim; a bare refusal dispatched nothing.</summary>
        private static bool DispatchedClaim(ActionResult result)
        {
            if (result == null) return false;
            if (result.RawJson == null) return result.Ok;
            try
            {
                JObject raw = JObject.Parse(result.RawJson);
                return raw["dispatched"] == null || JsonBool(raw, "dispatched");
            }
            catch (Exception)
            {
                return true;
            }
        }

        private string OperationPending(string op, OperationRecord pending)
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = OperationErrors.OperationPending;
            payload["op"] = op;
            payload["rev"] = CurrentRevision();
            payload["blocking_operation_id"] = pending.OperationId;
            payload["execution_status"] = pending.Execution;
            payload["delivery_status"] = pending.Delivery;
            payload["next"] = "call get_operation or wait_operation for the blocking id;"
                + " do not start a new action id for the same op";
            return payload.ToString(Formatting.None);
        }

        private static string Classify(ActionResult result, string response, out string error,
            out bool dispatched)
        {
            error = result == null ? null : result.Error;
            JObject json = null;
            try
            {
                json = JObject.Parse(string.IsNullOrEmpty(response) ? "{}" : response);
            }
            catch (Exception)
            {
            }

            bool ok = result != null && result.Ok;
            bool pending = JsonBool(json, "pending");
            bool unknown = JsonBool(json, "uncertain")
                || JsonBool(json, "unknown")
                || string.Equals(error, "outcome_unknown", StringComparison.Ordinal);

            if (json != null && json["dispatched"] != null)
            {
                dispatched = JsonBool(json, "dispatched");
            }
            else
            {
                // Raw services spell out dispatched. A bare Success ran the action;
                // a bare Failure is a pre-dispatch guard refusal, which is the
                // conservative reading.
                dispatched = result != null && (result.RawJson != null || result.Ok);
            }

            if (!ok && string.IsNullOrEmpty(error) && json != null)
            {
                JToken code = json["error"];
                if (code != null && code.Type == JTokenType.String) error = (string)code;
            }

            return ExecutionStatus.Classify(ok, pending, unknown, dispatched);
        }

        private static bool HasControlCharacter(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i])) return true;
            }

            return false;
        }

        private static bool JsonBool(JObject json, string name)
        {
            if (json == null) return false;
            JToken token = json[name];
            return token != null && token.Type == JTokenType.Boolean && (bool)token;
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
