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

        // R4/F1b: an opaque native run identity. Only an observed *change* may
        // evict the previous run's evidence; null means the identity is not
        // readable, which is "unknown", never "unchanged" and never "changed".
        private readonly Func<string> _readRunId;

        // Runs after a run change is confirmed (rent receipts belong to the old
        // run). Never called for a request, a pending switch or an unknown one.
        private readonly Action _onRunChangeConfirmed;
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
            Func<OperationRecord, OperationRecord> operationVerifier = null,
            Func<string> readRunId = null,
            Action onRunChangeConfirmed = null)
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
            _readRunId = readRunId;
            _onRunChangeConfirmed = onRunChangeConfirmed;
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
            bool callScoped = PendingScope.IsCallScoped(op);
            OperationRecord pending = _gate.FindPendingFor(
                op, callScoped ? fingerprint : null);
            if (pending != null) return Scrub(OperationPending(op, pending));

            // R4/F1b: a run-changing write whose switch is not yet confirmed must
            // not be re-issued under a new action id. That would be a blind retry
            // of a native load that may already be in flight.
            string effectiveOp = RunChangeOps.Resolve(op, (string)request["action"]);
            bool runChange = RunChangeOps.IsRunChangeOp(effectiveOp);
            if (runChange)
            {
                OperationRecord unconfirmed = _gate.FindUnconfirmedRunChange();
                if (unconfirmed != null) return Scrub(RunChangePending(op, unconfirmed));
            }

            // The identity must be sampled before the native call: comparing two
            // post-dispatch reads would prove nothing.
            string runIdBefore = runChange ? SafeReadRunId() : null;

            OperationRecord operation = _gate.Begin(actionId, op, currentRevision, fingerprint);
            operation.RunChangeRequested = runChange;
            operation.RunIdBefore = runIdBefore;
            operation.RunChange = runChange ? RunChangeStatus.Requested : RunChangeStatus.NotRequested;

            ActionResult result = Dispatch(op, request);
            operation.Dispatch = result.Dispatch;

            string runIdAfter = runChange ? SafeReadRunId() : null;
            if (runChange) operation.RunChange = RunChangePolicy.Classify(runIdBefore, runIdAfter);

            long responseRevision;
            if (!TryRefreshRevision(out responseRevision))
            {
                // The action ran but the resulting state cannot be observed. Do
                // not report success/failure: cache an explicit uncertain outcome
                // so a retry with the same id returns the same answer without a
                // second dispatch.
                string unknown = OutcomeUnknown(op, currentRevision);
                _gate.Complete(actionId, unknown, ExecutionStatus.Uncertain,
                    DeliveryStatus.Delivered, currentRevision, "outcome_unknown",
                    operation.Dispatch);
                string envelope = DecorateResponse(actionId, unknown, _gate.Find(actionId));
                _gate.Replace(actionId, unknown, envelope);
                ApplyRunChange(actionId);
                _history.Add(
                    HistoryText(Scrub(op)),
                    Scrub(JsonForAudit(request)),
                    false,
                    "outcome_unknown",
                    currentRevision);
                return Scrub(envelope);
            }

            string response = result.ToJson(responseRevision);
            string execution = Classify(result, response, out string error);
            _gate.Complete(actionId, response, execution, DeliveryStatus.Delivered,
                responseRevision, error, operation.Dispatch);

            // R5/A1: the service is the only side that knows whether a native
            // run switch was actually requested. A first-run prompt confirmation
            // claims "not issued" and must not leave an unconfirmed run-change
            // request behind: that request used to refuse the next, real
            // confirm_new_game forever (NoRun -> NoRun). Results without a claim
            // keep the conservative op-name behavior.
            if (runChange
                && string.Equals(result.RunChangeClaim, ActionResult.ClaimNotIssued,
                    StringComparison.Ordinal))
            {
                operation.RunChangeRequested = false;
                operation.RunChange = RunChangeStatus.NotRequested;
                operation.RunIdBefore = null;
                _gate.Update(operation);
            }

            string decorated = DecorateResponse(actionId, response, _gate.Find(actionId));
            _gate.Replace(actionId, response, decorated);
            ApplyRunChange(actionId);
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
            // "*" lists the non-terminal operations so a stuck one stays reachable
            // even after it leaves the bounded action history (R4-D).
            if (string.Equals(operationId, "*", StringComparison.Ordinal))
                return ListOperationsJson();

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
            ApplyRunChange(record.OperationId);
            record = _gate.Find(record.OperationId) ?? record;
            payload["ok"] = record.Ok;
            payload["known"] = true;
            payload["operation_id"] = record.OperationId;
            payload["op"] = record.Op;
            payload["execution_status"] = record.Execution;
            payload["delivery_status"] = record.Delivery;
            payload["dispatch_status"] = DispatchStatus.Normalize(record.Dispatch);
            payload["dispatched"] = DispatchedJson(record.Dispatch);
            if (record.RunChange != RunChangeStatus.NotRequested)
            {
                payload["run_change_status"] = record.RunChange;
                payload["run_change_confirmed"] = record.RunChangeConfirmed;
            }
            if (ExecutionStatus.IsSuccess(record.Execution)
                && !string.IsNullOrEmpty(record.CompletionEvidence))
            {
                try
                {
                    payload["completion_evidence"] = JObject.Parse(record.CompletionEvidence);
                }
                catch (Exception)
                {
                }
            }
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

        private JObject ListOperationsJson()
        {
            var payload = new JObject();
            payload["ok"] = true;
            payload["operations"] = new JArray();

            JArray rows = (JArray)payload["operations"];
            foreach (OperationRecord operation in _gate.ListNonTerminal(20))
            {
                var row = new JObject();
                row["operation_id"] = operation.OperationId;
                row["op"] = operation.Op;
                row["execution_status"] = operation.Execution;
                row["delivery_status"] = operation.Delivery;
                row["dispatch_status"] = DispatchStatus.Normalize(operation.Dispatch);
                row["dispatched"] = DispatchedJson(operation.Dispatch);
                row["rev"] = operation.Revision;
                row["result_rev"] = operation.ResultRevision;
                row["error"] = operation.Error == null
                    ? JValue.CreateNull()
                    : new JValue(operation.Error);
                rows.Add(row);
            }

            // R5/A2: a bounded list must never hide the fact that it is bounded.
            // The caller has to know a stuck operation may be outside this window.
            int total = _gate.CountNonTerminal();
            bool truncated = total > rows.Count;
            payload["count"] = rows.Count;
            payload["total_non_terminal"] = total;
            payload["truncated"] = truncated;
            if (truncated)
            {
                payload["truncation_note"] = "only the newest " + rows.Count
                    + " non-terminal operations are listed; older ones can still be"
                    + " resolved by get_operation with their known id";
            }
            payload["next"] = "call get_operation with one of these ids; a sleep or"
                + " save that never finished must be resolved before retrying it";
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

            // A run switch is verified on every read even when the execution axis
            // is already terminal: "the load request completed" and "the run
            // identity changed" are different facts (R4/F1b).
            OperationRecord checkedRecord = VerifyRunChange(record.Copy());
            if (checkedRecord.RunChangeConfirmed && !record.RunChangeConfirmed)
            {
                _gate.Update(checkedRecord);
                record = checkedRecord.Copy();
            }

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

        /// <summary>
        /// R4/F1b: re-checks an unconfirmed run switch. A load completes
        /// asynchronously, so the identity may only become readable on a later
        /// get_operation / wait_operation read. Promotion to "confirmed" evicts the
        /// previous run's evidence exactly once.
        /// </summary>
        private OperationRecord VerifyRunChange(OperationRecord record)
        {
            if (record == null || !record.RunChangeRequested) return record;
            if (record.RunChangeConfirmed) return record;
            if (!DispatchStatus.MayHaveRun(record.Dispatch)) return record;

            string runIdAfter = SafeReadRunId();
            if (string.IsNullOrEmpty(runIdAfter)) return record;

            string status = RunChangePolicy.Classify(record.RunIdBefore, runIdAfter);
            if (status != RunChangeStatus.Confirmed) return record;

            record.RunChange = status;
            return record;
        }

        /// <summary>
        /// Evicts the previous run's evidence once, and only after a confirmed
        /// switch. The switching operation's own id survives, so its response and
        /// idempotency are intact.
        /// </summary>
        private void ApplyRunChange(string actionId)
        {
            OperationRecord record = _gate.Find(actionId);
            if (record == null || !record.RunChangeConfirmed || record.RunChangeApplied)
                return;

            record.RunChangeApplied = true;
            _gate.Update(record);
            _gate.ClearOperationsExcept(actionId);

            Action callback = _onRunChangeConfirmed;
            if (callback == null) return;
            try
            {
                callback();
            }
            catch (Exception)
            {
                // A failing bookkeeping callback must not undo the confirmed fact.
            }
        }

        private string SafeReadRunId()
        {
            if (_readRunId == null) return null;
            try
            {
                return _readRunId();
            }
            catch (Exception)
            {
                // An unreadable identity is unknown, never "unchanged".
                return null;
            }
        }

        private string DecorateCached(string actionId, string cached)
        {
            OperationRecord record = _gate.Find(actionId);
            if (record == null) return cached;

            OperationRecord checkedRecord = VerifyRunChange(record);
            if (checkedRecord.RunChangeConfirmed && !record.RunChangeConfirmed)
                _gate.Update(checkedRecord);

            ApplyRunChange(actionId);
            record = _gate.Find(actionId) ?? checkedRecord;
            return DecorateResponse(actionId, cached, record);
        }

        /// <summary>
        /// Adds the envelope facts to a response body. The record is the single
        /// source of truth: execution, delivery, dispatch and run-change facts are
        /// never re-derived from the body being decorated.
        ///
        /// R4/F3b: a promoted operation keeps the dispatch-time body verbatim under
        /// <c>initial_result</c> and exposes only the current, provable facts at the
        /// top level. Rebuilding from the immutable cached body makes the retry
        /// deterministic and stops the historical snapshot from being re-read as a
        /// completion-time snapshot.
        /// </summary>
        private string DecorateResponse(string actionId, string response, OperationRecord record)
        {
            if (record == null) return response;

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

            bool completed = ExecutionStatus.IsSuccess(record.Execution);

            // The body only says "the dispatch is still running" when it carries one
            // of the non-terminal markers. Equal facts never re-promote.
            JObject initial = null;
            if (completed
                && (JsonBool(json, "pending") || JsonBool(json, "uncertain") || JsonBool(json, "unknown")))
            {
                initial = (JObject)json.DeepClone();
                // Defensive: never nest a previous promotion inside the new one.
                initial.Remove("initial_result");

                json = new JObject();
                JToken op = initial["op"];
                if (op != null && op.Type == JTokenType.String) json["op"] = op;
                json["initial_result"] = initial;
                json["initial_result_note"] =
                    "the dispatch-time response; its readback describes the moment of"
                    + " the call, not the moment it completed";
            }

            json["ok"] = completed;
            json["operation_id"] = actionId;
            json["execution_status"] = record.Execution;
            json["delivery_status"] = record.Delivery;
            json["dispatch_status"] = DispatchStatus.Normalize(record.Dispatch);
            json["dispatched"] = DispatchedJson(record.Dispatch);

            if (completed && !string.IsNullOrEmpty(record.CompletionEvidence))
            {
                try
                {
                    json["completion_evidence"] = JObject.Parse(record.CompletionEvidence);
                }
                catch (Exception)
                {
                    // Never surface a malformed evidence blob as a fact.
                }
            }

            if (record.RunChange != RunChangeStatus.NotRequested)
            {
                json["run_change_status"] = record.RunChange;
                json["run_change_confirmed"] = record.RunChangeConfirmed;
                if (!record.RunChangeConfirmed)
                {
                    json["run_change_next"] = "the run switch was requested but not"
                        + " confirmed by a run identity readback: call get_operation"
                        + " (or wait_operation) for this action id; do not start a new"
                        + " action id for a load/new game";
                }
            }

            // An unknown dispatch fact is exactly the case where a retry under a new
            // action id is forbidden; say so in the response itself.
            if (!completed && json["next"] == null
                && string.Equals(record.Execution, ExecutionStatus.Uncertain, StringComparison.Ordinal))
            {
                json["next"] = "the outcome is unknown: do not retry with a new action"
                    + " id; read the state and decide";
            }

            if (string.Equals(record.Delivery, DeliveryStatus.Undelivered, StringComparison.Ordinal))
                json["redelivered"] = true;

            return json.ToString(Formatting.None);
        }

        /// <summary>
        /// boolean when known, null when the bridge cannot say. An explicit null is
        /// the wire form of "unknown"; it must never be serialised as false.
        /// </summary>
        private static JToken DispatchedJson(string dispatch)
        {
            string claim = DispatchStatus.Normalize(dispatch);
            if (claim == DispatchStatus.Dispatched) return new JValue(true);
            if (claim == DispatchStatus.NotDispatched) return new JValue(false);
            return JValue.CreateNull();
        }

        private string RunChangePending(string op, OperationRecord blocking)
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = OperationErrors.RunChangePending;
            payload["op"] = op;
            payload["rev"] = CurrentRevision();
            payload["run_change_status"] = blocking.RunChange;
            payload["run_change_confirmed"] = false;
            payload["blocking_operation_id"] = blocking.OperationId;
            payload["next"] = "a load/new game is already in flight; call get_operation"
                + " for the blocking id and wait for run_change_status=confirmed"
                + " before issuing another run-changing action";
            return payload.ToString(Formatting.None);
        }

        private string OperationPending(string op, OperationRecord pending)
        {
            var payload = new JObject();
            payload["ok"] = false;
            payload["error"] = OperationErrors.OperationPending;
            payload["op"] = op;
            payload["rev"] = CurrentRevision();
            payload["blocking_operation_id"] = pending.OperationId;
            payload["blocking_same_call"] = true;
            payload["execution_status"] = pending.Execution;
            payload["delivery_status"] = pending.Delivery;
            payload["dispatch_status"] = DispatchStatus.Normalize(pending.Dispatch);
            payload["dispatched"] = DispatchedJson(pending.Dispatch);
            payload["next"] = "call get_operation or wait_operation for the blocking id;"
                + " do not start a new action id for the same op";
            return payload.ToString(Formatting.None);
        }

        private static string Classify(ActionResult result, string response, out string error)
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

            // R4/F4b: exactly one source. The service's own claim is parsed once by
            // ActionResult; ok, the presence of a body and thrown exceptions are not
            // evidence about whether the native call ran.
            string dispatch = result == null ? DispatchStatus.Unknown : result.Dispatch;

            if (!ok && string.IsNullOrEmpty(error) && json != null)
            {
                JToken code = json["error"];
                if (code != null && code.Type == JTokenType.String) error = (string)code;
            }

            return ExecutionStatus.Classify(ok, pending, unknown, dispatch);
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
                // The native call may already have run: an exception is not a
                // pre-dispatch refusal (R4/F4b).
                return ActionResult.UnknownDispatch(op, "exception");
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
