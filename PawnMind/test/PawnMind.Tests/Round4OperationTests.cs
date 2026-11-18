using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Round 4 (brief 2026-11-18): operation-fact correctness.
    ///
    /// F1b — "a load/new-game request was accepted" is not "the run identity
    /// changed". Old operation and rent evidence may only be evicted after a
    /// trusted readback proves the switch.
    ///
    /// F4b — the dispatch fact is an explicit tri-state claim produced by the
    /// service; it is never inferred from ok, from the existence of a JSON body
    /// or from an exception.
    ///
    /// Evidence level: L1-fake (no game process). The real native load/new-game
    /// completion events stay NOT_RUN.
    /// </summary>
    internal static class Round4OperationTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";
        private const string GameVersion = "VERSION 1-Rev1";
        private const string GameMvid = "11111111-2222-3333-4444-555555555555";

        private static WriteGateDecision OpenGate()
        {
            return GameVersionGate.Evaluate(
                new GameIdentity(GameVersion, GameMvid), GameVersion, GameMvid);
        }

        internal static ActionRouter Router(
            long revision,
            Func<string, JObject, ActionResult> dispatch,
            Func<OperationRecord, OperationRecord> verifier = null,
            Func<string> readRunId = null,
            Action onRunChangeConfirmed = null)
        {
            return new ActionRouter(
                new ActionGate(),
                delegate { return revision; },
                dispatch,
                new ActionHistory(),
                delegate { return revision; },
                Token,
                delegate { return OpenGate(); },
                null,
                null,
                verifier,
                readRunId,
                onRunChangeConfirmed);
        }

        private static string Write(string op, string actionId, long rev)
        {
            return "{\"op\":\"" + op + "\",\"action_id\":\"" + actionId
                + "\",\"rev\":" + rev + "}";
        }

        // ------------------------------------------------------- F1b: requests

        /// <summary>
        /// R4-F1-01: confirming the first-run prompt does not enter the new run.
        /// The old operations and the rent receipts belong to the run we are still
        /// in, so nothing may be evicted yet.
        /// </summary>
        [Test]
        public static void R4_F1_01_Confirm_Prompt_Does_Not_Evict_Old_Evidence()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                if (op == "confirm_new_game")
                {
                    // R5/A1: the service claims that a prompt confirmation is not
                    // a run-changing native start.
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"prompt_confirmed\":true,"
                            + "\"run_change\":\"not_issued\"}");
                }

                return ActionResult.Success(op);
            },
            null,
            delegate { return "run-a"; });

            router.Handle(Write("open_shop", "old-prompt", 3));
            JObject json = JObject.Parse(router.Handle(Write("confirm_new_game", "f1b-prompt", 3)));

            Assert.Null(json["run_change_status"],
                "R5/A1: a confirmed prompt is not a run-changing request at all");
            Assert.Null(json["run_change_confirmed"], "nothing to confirm");
            Assert.True((bool)router.GetOperationJson("old-prompt")["known"],
                "the run has not changed, so the old operation evidence must survive");
        }

        /// <summary>
        /// R4-F1-02: load_requested + load_pending means the native load was
        /// accepted, not that the run identity already changed.
        /// </summary>
        [Test]
        public static void R4_F1_02_Load_Pending_Does_Not_Evict_Old_Evidence()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                if (op == "load_game")
                {
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true,"
                            + "\"load_pending\":true}")
                        .WithRunChangeRequested();
                }

                return ActionResult.Success(op);
            },
            null,
            delegate { return "run-a"; });

            router.Handle(Write("open_shop", "old-load", 3));
            JObject json = JObject.Parse(router.Handle(Write("load_game", "f1b-load", 3)));

            Assert.Equal("requested", (string)json["run_change_status"],
                "a pending load is still a request");
            Assert.True((bool)router.GetOperationJson("old-load")["known"],
                "old evidence stays until the switch is confirmed");
        }

        /// <summary>
        /// R4-F1-03: start_requested + start_pending is the same shape as a load.
        /// </summary>
        [Test]
        public static void R4_F1_03_Start_Pending_Does_Not_Evict_Old_Evidence()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                if (op == "new_game")
                {
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                            + "\"start_pending\":true}")
                        .WithRunChangeRequested();
                }

                return ActionResult.Success(op);
            },
            null,
            delegate { return "run-a"; });

            router.Handle(Write("open_shop", "old-start", 3));
            JObject json = JObject.Parse(router.Handle(Write("new_game", "f1b-start", 3)));

            Assert.Equal("requested", (string)json["run_change_status"], "a pending start is a request");
            Assert.True((bool)router.GetOperationJson("old-start")["known"],
                "the started run is not observable yet, so nothing may be evicted");
        }

        /// <summary>
        /// R4-F1-04: a trusted run identity change is the only thing that may evict
        /// the previous run's evidence, and the switching operation itself must
        /// keep its idempotent response.
        /// </summary>
        [Test]
        public static void R4_F1_04_Confirmed_Run_Change_Evicts_Old_Evidence()
        {
            int dispatches = 0;
            int confirmations = 0;
            string runId = "run-a";

            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    if (op == "load_game") dispatches++;
                    if (op == "load_game")
                    {
                        // The native load really switched the run.
                        runId = "run-b";
                        return ActionResult.SuccessRaw(op,
                                "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}")
                            .WithRunChangeRequested();
                    }

                    return ActionResult.Success(op);
                },
                null,
                delegate { return runId; },
                delegate { confirmations++; });

            router.Handle(Write("open_shop", "old-run", 3));
            string body = Write("load_game", "f1b-confirmed", 3);
            JObject json = JObject.Parse(router.Handle(body));

            Assert.Equal("confirmed", (string)json["run_change_status"],
                "a readable, different run identity confirms the switch");
            Assert.True((bool)json["run_change_confirmed"], "confirmed is explicit");
            Assert.False((bool)router.GetOperationJson("old-run")["known"],
                "the old run's evidence is evicted after confirmation");
            Assert.True((bool)router.GetOperationJson("f1b-confirmed")["known"],
                "the switching operation keeps its own record");
            Assert.Equal(1, confirmations, "the rent receipts are reset exactly once");

            string retry = router.Handle(body);
            Assert.Contains(retry, "\"run_change_status\":\"confirmed\"",
                "the retry repeats the confirmed switch");
            Assert.Equal(1, dispatches, "the retry never re-dispatches");
            Assert.Equal(1, confirmations, "the retry never resets the receipts again");
        }

        /// <summary>
        /// R4-F1-05: retrying the same switching action id must not dispatch twice.
        /// </summary>
        [Test]
        public static void R4_F1_05_Same_Run_Change_Action_Id_Replays_Once()
        {
            int dispatches = 0;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    dispatches++;
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return "run-a"; });

            string body = Write("load_game", "f1b-same", 3);
            JObject first = JObject.Parse(router.Handle(body));
            JObject second = JObject.Parse(router.Handle(body));

            Assert.Equal("requested", (string)first["run_change_status"], "first response");
            Assert.Equal((string)first["run_change_status"], (string)second["run_change_status"],
                "the retry repeats the recorded run-change status");
            Assert.Equal(1, dispatches, "one native load request");
        }

        /// <summary>
        /// R4-F1-06: an exception during the switch is not evidence that the run
        /// changed. Nothing is evicted and the outcome is uncertain.
        /// </summary>
        [Test]
        public static void R4_F1_06_Throwing_Run_Change_Keeps_Old_Evidence()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                if (op == "load_game") throw new InvalidOperationException("native blew up");
                return ActionResult.Success(op);
            });

            router.Handle(Write("open_shop", "old-throw", 3));
            JObject json = JObject.Parse(router.Handle(Write("load_game", "f1b-throw", 3)));

            Assert.Equal(ExecutionStatus.Uncertain, (string)json["execution_status"],
                "a throw after a possible native load is uncertain, never completed");
            Assert.True((bool)router.GetOperationJson("old-throw")["known"],
                "an unknown switch must not evict the old run's evidence");
        }

        /// <summary>
        /// R4-F1-07: a change of revision, day or scene is not a run identity. When
        /// the identity cannot be read the status stays unknown and nothing is
        /// evicted, no matter how much else moved.
        /// </summary>
        [Test]
        public static void R4_F1_07_Rev_Change_Is_Not_A_Run_Identity()
        {
            long revision = 3;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    // Everything except the identity moves.
                    revision = 9;
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return null; });

            router.Handle(Write("open_shop", "old-rev", 3));
            JObject json = JObject.Parse(router.Handle(Write("load_game", "f1b-rev", 3)));

            Assert.Equal(RunChangeStatus.Unknown, (string)json["run_change_status"],
                "an unreadable run identity is unknown, not confirmed");
            Assert.False((bool)json["run_change_confirmed"], "never assumed");
            Assert.True((bool)router.GetOperationJson("old-rev")["known"],
                "a revision bump must not evict the old run's evidence");
            Assert.Equal(9L, revision, "the fixture really moved the revision");
        }

        /// <summary>
        /// R4-F1-08: while a switch is unconfirmed a second run-changing action id
        /// would be a blind retry. It is refused and names the blocking id.
        /// </summary>
        [Test]
        public static void R4_F1_08_Second_Run_Change_Is_Refused_While_Unconfirmed()
        {
            int loads = 0;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    if (op != "load_game") return ActionResult.Success(op);
                    loads++;
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true,"
                            + "\"load_pending\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return "run-a"; });

            router.Handle(Write("load_game", "f1b-first", 3));
            string second = router.Handle(Write("load_game", "f1b-second", 3));

            Assert.Contains(second, OperationErrors.RunChangePending,
                "a second switch is refused while the first is unconfirmed");
            Assert.Contains(second, "f1b-first", "the refusal names the blocking id");
            Assert.Equal(1, loads, "no second native load");
        }

        /// <summary>
        /// R4-F1-09: the switch is confirmed later, on a get_operation read, because
        /// the native load is asynchronous. Confirmation evicts once and keeps the
        /// switching operation.
        /// </summary>
        [Test]
        public static void R4_F1_09_Async_Confirmation_On_Operation_Read()
        {
            string runId = "run-a";
            int confirmations = 0;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    if (op != "load_game") return ActionResult.Success(op);
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true,"
                            + "\"load_pending\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return runId; },
                delegate { confirmations++; });

            router.Handle(Write("open_shop", "old-async", 3));
            router.Handle(Write("load_game", "f1b-async", 3));
            Assert.True((bool)router.GetOperationJson("old-async")["known"],
                "not evicted while the load is still pending");

            // The native load finishes.
            runId = "run-b";
            JObject operation = router.GetOperationJson("f1b-async");
            Assert.Equal(RunChangeStatus.Confirmed, (string)operation["run_change_status"],
                "the later read confirms the switch");
            Assert.False((bool)router.GetOperationJson("old-async")["known"],
                "the old run's evidence is evicted on confirmation");
            Assert.Equal(1, confirmations, "the receipts are reset once");

            router.GetOperationJson("f1b-async");
            Assert.Equal(1, confirmations, "a repeated read does not reset again");
        }

        // --------------------------------------------------- F4b: dispatch facts

        /// <summary>
        /// R4-F4-01: an explicit refusal is the only shape that may claim "nothing
        /// ran", and it stays a safe rejection.
        /// </summary>
        [Test]
        public static void R4_F4_01_Explicit_Refusal_Is_Rejected()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":false,\"error\":\"trade_locked\",\"dispatched\":false}");
            });

            JObject json = JObject.Parse(router.Handle(Write("confirm_trade", "f4b-01", 3)));
            Assert.Equal(ExecutionStatus.Rejected, (string)json["execution_status"], "rejected");
            Assert.Equal(DispatchStatus.NotDispatched, (string)json["dispatch_status"],
                "the service says nothing ran");
            Assert.False((bool)json["dispatched"], "the boolean agrees");
        }

        /// <summary>
        /// R4-F4-02: an explicit dispatched failure is `failed`, never a rejection.
        /// </summary>
        [Test]
        public static void R4_F4_02_Explicit_Dispatch_Is_Failed()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":false,\"error\":\"open_shop_failed\",\"dispatched\":true}");
            });

            JObject json = JObject.Parse(router.Handle(Write("open_shop", "f4b-02", 3)));
            Assert.Equal(ExecutionStatus.Failed, (string)json["execution_status"], "failed");
            Assert.Equal(DispatchStatus.Dispatched, (string)json["dispatch_status"], "dispatched");
            Assert.True((bool)json["dispatched"], "the boolean agrees");
        }

        /// <summary>
        /// R4-F4-03: a failure body that says nothing about dispatch must not be
        /// reported as "nothing ran" (which reads as a safe refusal) nor as
        /// "the native call failed". It is uncertain and never auto-retried.
        /// </summary>
        [Test]
        public static void R4_F4_03_Silent_Failure_Is_Unknown_Not_Rejected()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":false,\"error\":\"recording_locked\"}");
            });

            string body = Write("confirm_new_game", "f4b-03", 3);
            JObject json = JObject.Parse(router.Handle(body));

            Assert.Equal(ExecutionStatus.Uncertain, (string)json["execution_status"],
                "an absent dispatch fact is uncertain, not a safe rejection");
            Assert.Equal(DispatchStatus.Unknown, (string)json["dispatch_status"], "unknown");
            Assert.Equal(JTokenType.Null, json["dispatched"].Type,
                "unknown is null, never false");
            Assert.Contains((string)json["next"] ?? "", "do not", "the caller is told not to retry");

            JObject operation = router.GetOperationJson("f4b-03");
            Assert.Equal((string)json["dispatch_status"], (string)operation["dispatch_status"],
                "get_operation agrees with the first response");
            Assert.Equal(1, dispatches, "the query never dispatches");
        }

        /// <summary>
        /// R4-F4-04: a body that is not valid JSON (or carries a non-boolean
        /// dispatched) cannot prove anything, so it is unknown and not replayed.
        /// </summary>
        [Test]
        public static void R4_F4_04_Malformed_Dispatch_Claim_Is_Unknown()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":false,\"dispatched\":\"yes\"}");
            });

            string body = Write("close_windows", "f4b-04", 3);
            JObject json = JObject.Parse(router.Handle(body));

            Assert.Equal(DispatchStatus.Unknown, (string)json["dispatch_status"],
                "a string is not a dispatch claim");
            Assert.Equal(ExecutionStatus.Uncertain, (string)json["execution_status"], "uncertain");
            Assert.Equal(DispatchStatus.Unknown,
                (string)router.GetOperationJson("f4b-04")["dispatch_status"], "record agrees");

            router.Handle(body);
            Assert.Equal(1, dispatches, "a malformed outcome is never re-dispatched");
        }

        /// <summary>
        /// R4-F4-05: the new-game menu producers must state the dispatch fact too.
        /// A policy refusal and a readback-only result both dispatched nothing.
        /// </summary>
        [Test]
        public static void R4_F4_05_New_Game_Menu_Declares_No_Dispatch()
        {
            string select = NewGameMenuJson.SelectJson(new NewGameMenuSelectResult
            {
                Success = false,
                Error = "menu_unavailable",
                Action = NewGameSelectionAction.None
            });
            Assert.Contains(select, "\"dispatched\":false",
                "a new-game menu refusal must not leave dispatched unknown");

            string action = NewGameMenuJson.ActionResultJson("open_new_game_menu",
                new NewGameMenuActionResult { Success = false, Error = "menu_unavailable" });
            Assert.Contains(action, "\"dispatched\":false",
                "a menu action refusal must not leave dispatched unknown");

            string success = NewGameMenuJson.ActionResultJson("open_new_game_menu",
                new NewGameMenuActionResult { Success = true, Dispatched = true });
            Assert.Contains(success, "\"dispatched\":true", "a menu action that ran says so");
        }

        /// <summary>
        /// R4-F4-06: an exception thrown out of the dispatcher may mean the native
        /// call already ran. It is uncertain, not a rejection.
        /// </summary>
        [Test]
        public static void R4_F4_06_Dispatcher_Exception_Is_Unknown()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                throw new InvalidOperationException("native state corrupted");
            });

            JObject json = JObject.Parse(router.Handle(Write("open_shop", "f4b-06", 3)));
            Assert.Equal(DispatchStatus.Unknown, (string)json["dispatch_status"],
                "a thrown exception proves neither dispatch nor refusal");
            Assert.Equal(ExecutionStatus.Uncertain, (string)json["execution_status"], "uncertain");
            Assert.Equal(JTokenType.Null, json["dispatched"].Type, "unknown is null");
        }

        /// <summary>
        /// R4-F4-07: the first response, get_operation and a re-delivered retry must
        /// all report the same dispatch fact, and the native call stays at one.
        /// </summary>
        [Test]
        public static void R4_F4_07_All_Views_Agree_On_The_Dispatch_Fact()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":false,\"error\":\"opaque\"}");
            });

            string body = Write("redeem_ticket", "f4b-07", 3);
            string first = router.Handle(body);
            router.MarkResponseUndelivered(body, first);

            JObject operation = router.GetOperationJson("f4b-07");
            JObject retry = JObject.Parse(router.Handle(body));

            Assert.Equal(DispatchStatus.Unknown, (string)operation["dispatch_status"], "operation");
            Assert.Equal(DispatchStatus.Unknown, (string)retry["dispatch_status"], "retry");
            Assert.Equal(JTokenType.Null, retry["dispatched"].Type, "retry keeps null");
            Assert.Equal(1, dispatches, "one native call across all views");
        }

        // -------------------------------------- F3b: promoted retry evidence

        private sealed class PromotingVerifier
        {
            public int Calls;
            public string ShopState = "CLOSED";

            public OperationRecord Promote(OperationRecord record)
            {
                Calls++;
                if (Calls < 2) return record;
                record.Execution = ExecutionStatus.Completed;
                record.ResultRevision = 4;
                record.Error = null;
                record.CompletionEvidence = "{\"source\":\"shop_day_readback\","
                    + "\"shop_state\":\"" + ShopState + "\",\"observed_rev\":4}";
                return record;
            }
        }

        private static string EndDayPending()
        {
            return "{\"ok\":false,\"op\":\"end_day\",\"dispatched\":true,"
                + "\"pending\":true,\"error\":\"shop_still_open\","
                + "\"shop_closed_after\":false,"
                + "\"readback\":{\"status\":\"open\",\"shop_state\":\"OPEN\"},"
                + "\"can_end_day_after\":false}";
        }

        /// <summary>
        /// R4-F3-01: once promoted, every retry with the same id returns the same
        /// body, keeps the dispatch-time evidence under initial_result and never
        /// re-dispatches.
        /// </summary>
        [Test]
        public static void R4_F3_01_Promoted_Retry_Is_Deterministic()
        {
            int dispatches = 0;
            var verifier = new PromotingVerifier();
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    dispatches++;
                    return ActionResult.SuccessRaw(op, EndDayPending());
                },
                verifier.Promote);

            string body = Write("end_day", "f3b-01", 3);
            router.Handle(body);
            router.GetOperationJson("f3b-01");
            router.GetOperationJson("f3b-01");

            string first = router.Handle(body);
            string second = router.Handle(body);
            JObject json = JObject.Parse(second);

            Assert.True((bool)json["ok"], "the promoted operation is ok");
            Assert.Equal(ExecutionStatus.Completed, (string)json["execution_status"], "completed");
            Assert.Null(json["pending"], "no stale pending at the top level");
            Assert.Null(json["error"], "no stale error at the top level");
            Assert.NotNull(json["initial_result"], "the dispatch-time evidence is kept");
            Assert.Equal("open", (string)json["initial_result"]["readback"]["status"],
                "the historical readback is preserved verbatim");
            Assert.Equal(first, second, "two retries return byte-identical bodies");
            Assert.Equal(1, dispatches, "no second native end_day");
        }

        /// <summary>
        /// R4-F3-02: the top level may not claim the shop is closed while showing a
        /// dispatch-time OPEN readback. Historical facts live under initial_result,
        /// and the completion is backed by explicit evidence.
        /// </summary>
        [Test]
        public static void R4_F3_02_No_Contradictory_Completion_Snapshot()
        {
            var verifier = new PromotingVerifier();
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, EndDayPending());
                },
                verifier.Promote);

            string body = Write("end_day", "f3b-02", 3);
            router.Handle(body);
            router.GetOperationJson("f3b-02");
            router.GetOperationJson("f3b-02");
            JObject json = JObject.Parse(router.Handle(body));

            Assert.Null(json["readback"],
                "a dispatch-time readback must not sit at the top level of a completion");
            Assert.Null(json["shop_closed_after"],
                "the bridge must not invent shop_closed_after=true");
            Assert.Null(json["can_end_day_after"], "same for can_end_day_after");
            Assert.NotNull(json["completion_evidence"], "the completion states its evidence");
            Assert.Equal("CLOSED", (string)json["completion_evidence"]["shop_state"],
                "the evidence is the verifier's observation");
        }

        /// <summary>
        /// R4-F3-03: without a verifier readback a running operation never becomes
        /// completed, no matter how often it is read.
        /// </summary>
        [Test]
        public static void R4_F3_03_No_Evidence_Stays_Running()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op, EndDayPending());
            });

            string body = Write("end_day", "f3b-03", 3);
            router.Handle(body);
            for (int i = 0; i < 5; i++) router.GetOperationJson("f3b-03");

            JObject json = JObject.Parse(router.Handle(body));
            Assert.Equal(ExecutionStatus.Running, (string)json["execution_status"],
                "still running without trusted evidence");
            Assert.False((bool)json["ok"], "running is not ok");
            Assert.Null(json["completion_evidence"], "no invented evidence");
        }

        /// <summary>
        /// R4-F3-04: a lost response changes only the delivery axis; the promoted
        /// completion evidence survives the redelivery.
        /// </summary>
        [Test]
        public static void R4_F3_04_Redelivery_Keeps_Completion_Evidence()
        {
            var verifier = new PromotingVerifier();
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, EndDayPending());
                },
                verifier.Promote);

            string body = Write("end_day", "f3b-04", 3);
            string first = router.Handle(body);
            router.MarkResponseUndelivered(body, first);
            router.GetOperationJson("f3b-04");
            router.GetOperationJson("f3b-04");

            JObject json = JObject.Parse(router.Handle(body));
            Assert.Equal(DeliveryStatus.Undelivered, (string)json["delivery_status"], "undelivered");
            Assert.Equal(ExecutionStatus.Completed, (string)json["execution_status"], "completed");
            Assert.NotNull(json["initial_result"], "the dispatch evidence is still there");
            Assert.NotNull(json["completion_evidence"], "the completion evidence is still there");
        }

        /// <summary>
        /// R4-F3-05: ten retries of a body that already nests JSON must not grow or
        /// nest initial_result.
        /// </summary>
        [Test]
        public static void R4_F3_05_Initial_Result_Does_Not_Grow()
        {
            var verifier = new PromotingVerifier();
            string nested = "{\"ok\":false,\"op\":\"end_day\",\"dispatched\":true,"
                + "\"pending\":true,\"error\":\"shop_still_open\","
                + "\"readback\":{\"status\":\"open\",\"extra\":{\"a\":1,\"b\":[1,2,3]}}}";
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, nested);
                },
                verifier.Promote);

            string body = Write("end_day", "f3b-05", 3);
            router.Handle(body);
            router.GetOperationJson("f3b-05");
            router.GetOperationJson("f3b-05");

            string reference = router.Handle(body);
            for (int i = 0; i < 9; i++)
            {
                string retry = router.Handle(body);
                Assert.Equal(reference, retry, "retry " + i + " must be identical");
            }

            JObject json = JObject.Parse(reference);
            Assert.Null(json["initial_result"]["initial_result"],
                "initial_result never nests itself");
            Assert.Equal("[1,2,3]",
                ((JArray)json["initial_result"]["readback"]["extra"]["b"]).ToString(Formatting.None),
                "nested JSON is preserved, not flattened");
        }

        /// <summary>
        /// R4-D: a stuck sleep can outlive the bounded action history. It must stay
        /// discoverable, otherwise its operation id is unreachable and the block on
        /// later sleeps can never be resolved.
        /// </summary>
        [Test]
        public static void R4_F1_14_Non_Terminal_Operations_Are_Listable()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":false,\"op\":\"sleep\",\"dispatched\":true,"
                    + "\"pending\":true,\"error\":\"saving\"}");
            });

            router.Handle(Write("sleep", "sleep-1", 3));

            JObject listed = router.GetOperationJson("*");
            Assert.True((bool)listed["ok"], "the listing is a read");
            Assert.Equal(1, (int)listed["count"], "one non-terminal operation");
            JArray rows = (JArray)listed["operations"];
            Assert.Equal("sleep-1", (string)rows[0]["operation_id"], "the stuck id is visible");
            Assert.Equal(ExecutionStatus.Running, (string)rows[0]["execution_status"], "still running");

            JObject one = router.GetOperationJson("sleep-1");
            Assert.Equal("sleep-1", (string)one["operation_id"], "the id still resolves normally");
        }

        /// <summary>
        /// R4-F1-10: starting a new game from the main menu has no previous run id,
        /// so "requested" must still become "confirmed" once a run appears. Without
        /// this the switch is unconfirmable forever and blocks every later load.
        /// </summary>
        [Test]
        public static void R4_F1_10_Start_From_No_Run_Is_Confirmed()
        {
            string runId = RunChangePolicy.NoRun;
            int confirmations = 0;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    runId = "run-new";
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"start_requested\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return runId; },
                delegate { confirmations++; });

            JObject json = JObject.Parse(router.Handle(Write("confirm_new_game", "f1b-new", 3)));

            Assert.Equal(RunChangeStatus.Confirmed, (string)json["run_change_status"],
                "a run appearing where there was none is a confirmed switch");
            Assert.Equal(1, confirmations, "the old-run bookkeeping runs once");
        }

        /// <summary>
        /// R4-F1-11: a run that becomes unreadable may be loading or gone. That is
        /// never proof of a completed switch.
        /// </summary>
        [Test]
        public static void R4_F1_11_A_Vanishing_Run_Is_Not_A_Confirmed_Switch()
        {
            string runId = "run-a";
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    if (op != "load_game") return ActionResult.Success(op);
                    runId = RunChangePolicy.NoRun;
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return runId; });

            router.Handle(Write("open_shop", "old-vanish", 3));
            JObject json = JObject.Parse(router.Handle(Write("load_game", "f1b-vanish", 3)));

            Assert.Equal(RunChangeStatus.Unknown, (string)json["run_change_status"],
                "mid-load is unknown, not confirmed");
            Assert.True((bool)router.GetOperationJson("old-vanish")["known"],
                "nothing is evicted while the run is mid-load");
        }

        /// <summary>
        /// R4-F1-12: still no run after the request is only "requested".
        /// </summary>
        [Test]
        public static void R4_F1_12_No_Run_To_No_Run_Stays_Requested()
        {
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    // R5/A1: this is a real start request (not a prompt), so the
                    // service claims "issued"; the identity has not changed yet.
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"start_requested\":true,"
                            + "\"run_change\":\"issued\"}");
                },
                null,
                delegate { return RunChangePolicy.NoRun; });

            JObject json = JObject.Parse(router.Handle(Write("confirm_new_game", "f1b-none", 3)));
            Assert.Equal(RunChangeStatus.Requested, (string)json["run_change_status"],
                "a dispatched start with no run before and after is still only a request");
        }

        /// <summary>
        /// R4-F1-13: after the switch is confirmed, a second run-changing action is
        /// allowed again. This is the deadlock the live session hit.
        /// </summary>
        [Test]
        public static void R4_F1_13_Confirmed_Switch_Releases_The_Run_Change_Gate()
        {
            string runId = RunChangePolicy.NoRun;
            int loads = 0;
            ActionRouter router = Router(
                3,
                delegate(string op, JObject request)
                {
                    if (op == "load_game") loads++;
                    runId = "run-" + (loads + 1);
                    return ActionResult.SuccessRaw(op,
                            "{\"ok\":true,\"dispatched\":true,\"load_requested\":true}")
                        .WithRunChangeRequested();
                },
                null,
                delegate { return runId; });

            router.Handle(Write("confirm_new_game", "f1b-gate-1", 3));
            string second = router.Handle(Write("load_game", "f1b-gate-2", 3));

            Assert.False(second.Contains(OperationErrors.RunChangePending),
                "a confirmed switch must not keep blocking the next one");
            Assert.Equal(1, loads, "the second run-changing action was dispatched");
        }
    }
}
