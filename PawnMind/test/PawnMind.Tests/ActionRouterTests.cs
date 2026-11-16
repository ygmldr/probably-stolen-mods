using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ActionRouterTests
    {
        private static readonly string[] ReadOnlyOps =
        {
            "bargain_preview",
            "contraband_status",
            "events_status",
            "expedition_status",
            "get_day_report",
            "get_calendar",
            "get_clientele",
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

        private const string TestGameVersion = "VERSION 1-Rev1";
        private const string TestAssemblyMvid = "11111111-2222-3333-4444-555555555555";

        private static WriteGateDecision OpenGate()
        {
            return GameVersionGate.Evaluate(
                new GameIdentity(TestGameVersion, TestAssemblyMvid),
                TestGameVersion,
                TestAssemblyMvid);
        }

        private static Func<WriteGateDecision> OpenGateProvider()
        {
            return delegate { return OpenGate(); };
        }

        private static WriteGateDecision LockedGate(string reason)
        {
            return WriteGateDecision.Locked(
                reason,
                TestGameVersion,
                TestAssemblyMvid,
                TestGameVersion,
                TestAssemblyMvid,
                true);
        }

        private static ActionRouter Router(
            long revision,
            Func<string, JObject, ActionResult> dispatch,
            Func<long> refreshRevision = null,
            string secretToken = null,
            Func<WriteGateDecision> writeGate = null,
            Action<ActionOutcome> onOutcome = null)
        {
            return Router(delegate { return revision; }, dispatch,
                refreshRevision, secretToken, writeGate, onOutcome);
        }

        private static ActionRouter Router(
            Func<long> revision,
            Func<string, JObject, ActionResult> dispatch,
            Func<long> refreshRevision = null,
            string secretToken = null,
            Func<WriteGateDecision> writeGate = null,
            Action<ActionOutcome> onOutcome = null)
        {
            if (refreshRevision == null) refreshRevision = delegate { return revision(); };
            if (writeGate == null) writeGate = OpenGateProvider();

            return new ActionRouter(
                new ActionGate(),
                revision,
                dispatch,
                new ActionHistory(),
                refreshRevision,
                secretToken,
                writeGate,
                onOutcome);
        }

        [Test]
        public static void Router_Ping_Bypasses_Gate()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                throw new AssertionException("ping must not dispatch");
            });

            string response = router.Handle("{\"op\":\"ping\"}");
            Assert.Contains(response, "\"pong\":true", "ping response");
            Assert.Contains(response, "\"rev\":5", "current revision in ping");
        }

        [Test]
        public static void Router_Rejects_Stale_Revision()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":4,\"action_id\":\"stale1\"}");
            Assert.Contains(response, "stale_rev", "stale revision rejected");
            Assert.Contains(response, "\"rev\":5", "current revision returned");
            Assert.Equal(0, calls, "stale action must not dispatch");
        }

        [Test]
        public static void Router_Dispatches_With_Matching_Revision()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"a1\"}");
            Assert.Contains(response, "\"ok\":true", "action accepted");
            Assert.Contains(response, "\"op\":\"open_shop\"", "op echoed");
        }

        [Test]
        public static void Router_Does_Not_Run_Duplicate_Action_Twice()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"a1\"}";
            string first = router.Handle(body);
            string second = router.Handle(body);

            Assert.Equal(1, calls, "dispatch runs once");
            Assert.Equal(first, second, "second call returns the cached response");
        }

        [Test]
        public static void Router_Duplicate_With_Different_Revision_Returns_First_Result()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string first = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"retry1\"}");
            string second = router.Handle("{\"op\":\"open_shop\",\"rev\":6,\"action_id\":\"retry1\"}");

            Assert.Equal(1, calls, "retry must not dispatch again");
            Assert.Equal(first, second, "retry returns the first result");
        }

        [Test]
        public static void Router_Returns_Unknown_Op_From_Dispatcher()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "unknown_op");
            });

            string response = router.Handle("{\"op\":\"dance\",\"rev\":5,\"action_id\":\"u1\"}");
            Assert.Contains(response, "unknown_op", "unknown op surfaced");
        }

        [Test]
        public static void Router_Records_Action_History()
        {
            var history = new ActionHistory();
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); }, history,
                delegate { return 5; }, null, OpenGateProvider());

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"h1\"}");
            string response = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.Contains(response, "\"op\":\"open_shop\"", "history contains the action");
            Assert.Contains(response, "\"ok\":true", "history contains the result");
        }

        [Test]
        public static void Router_Rejects_Bad_Json()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            string response = router.Handle("{not json");
            Assert.Contains(response, "bad_json", "bad json rejected");
        }

        [Test]
        public static void Router_Reports_Exception_As_Failure()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                throw new InvalidOperationException("boom");
            });

            string response = router.Handle("{\"op\":\"end_day\",\"rev\":5,\"action_id\":\"e1\"}");
            Assert.Contains(response, "\"ok\":false", "exception is a failure");
            Assert.Contains(response, "exception", "exception code");
            Assert.False(response.Contains("boom"), "exception detail must not leak");
        }

        [Test]
        public static void Router_Rejects_Write_Without_Revision()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"action_id\":\"w1\"}");
            Assert.Contains(response, "missing_rev", "missing revision rejected");
            Assert.Contains(response, "\"rev\":5", "current revision returned");
            Assert.Equal(0, calls, "missing revision must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Write_Without_Action_Id()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5}");
            Assert.Contains(response, "missing_action_id", "missing action id rejected");
            Assert.Contains(response, "\"rev\":5", "current revision returned");
            Assert.Equal(0, calls, "missing action id must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Blank_Action_Id()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"   \"}");
            Assert.Contains(response, "missing_action_id", "blank action id rejected");
            Assert.Equal(0, calls, "blank action id must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Non_String_Action_Id()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":7}");
            Assert.Contains(response, "bad_action_id", "non-string action id rejected");
            Assert.Equal(0, calls, "bad action id must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Bad_Revision_Format()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string[] bodies =
            {
                "{\"op\":\"open_shop\",\"rev\":\"5\",\"action_id\":\"bad-rev-1\"}",
                "{\"op\":\"open_shop\",\"rev\":5.5,\"action_id\":\"bad-rev-2\"}",
                "{\"op\":\"open_shop\",\"rev\":true,\"action_id\":\"bad-rev-3\"}"
            };

            for (int i = 0; i < bodies.Length; i++)
            {
                string response = router.Handle(bodies[i]);
                Assert.Contains(response, "bad_rev", "bad revision format rejected");
            }

            Assert.Equal(0, calls, "bad revision format must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Negative_Revision()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":-1,\"action_id\":\"neg1\"}");
            Assert.Contains(response, "bad_rev", "negative revision rejected");
            Assert.Equal(0, calls, "negative revision must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Same_Action_Id_With_Different_Args()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"same\"}");
            string second = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"same\",\"force\":true}");
            Assert.Contains(second, "action_conflict", "same id with different args must conflict");
            Assert.Equal(1, calls, "conflicting action must not dispatch");
        }

        [Test]
        public static void Router_Rejects_Same_Action_Id_With_Different_Op()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"same-op\"}");
            string second = router.Handle("{\"op\":\"call_next_customer\",\"rev\":5,\"action_id\":\"same-op\"}");
            Assert.Contains(second, "action_conflict", "same id with different op must conflict");
            Assert.Equal(1, calls, "conflicting action must not dispatch");
        }

        [Test]
        public static void Router_Normalizes_Duplicate_Request()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string first = router.Handle(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"norm1\",\"args\":{\"b\":2,\"a\":1}}");
            string second = router.Handle(
                "{ \"action_id\" : \"norm1\", \"rev\" : 5, \"args\" : { \"a\" : 1, \"b\" : 2 }, \"op\" : \"open_shop\" }");

            Assert.Equal(1, calls, "normalized request is a duplicate");
            Assert.Equal(first, second, "normalized duplicate returns the first result");
        }

        [Test]
        public static void Router_Fingerprint_Includes_Sensitive_Args()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string first = router.Handle(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"secret-fp\","
                + "\"args\":{\"token\":\"first-secret-value\",\"password\":\"p-one\"}}");
            string second = router.Handle(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"secret-fp\","
                + "\"args\":{\"token\":\"second-secret-value\",\"password\":\"p-two\"}}");

            Assert.Contains(second, "action_conflict", "changing a sensitive arg must conflict");
            Assert.False(second == first, "conflict must not return the first response");
            Assert.Equal(1, calls, "conflicting action must not dispatch");

            string history = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(history.Contains("first-secret-value"), "audit must not store the first secret");
            Assert.False(history.Contains("second-secret-value"), "audit must not store the second secret");
            Assert.Contains(history, "[redacted]", "audit keeps redacted placeholders");
        }

        [Test]
        public static void Router_Does_Not_Bump_Revision_Without_State_Change()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            string first = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"bump1\"}");
            Assert.Contains(first, "\"rev\":5", "revision is not blindly incremented");

            string second = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"bump2\"}");
            Assert.Contains(second, "\"ok\":true", "same revision remains usable when state did not change");
        }

        [Test]
        public static void Router_Refreshes_State_Before_Checking_Write_Revision()
        {
            long snapshotRevision = 5;
            bool stateChanged = true;
            int calls = 0;

            ActionRouter router = Router(
                delegate { return snapshotRevision; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                delegate
                {
                    if (stateChanged)
                    {
                        snapshotRevision++;
                        stateChanged = false;
                    }

                    return snapshotRevision;
                });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"pre1\"}");
            Assert.Contains(response, "stale_rev", "revision changed before the action must be rejected");
            Assert.Contains(response, "\"rev\":6", "refreshed revision is returned");
            Assert.Equal(0, calls, "stale action must not dispatch");
        }

        [Test]
        public static void Router_Returns_Refreshed_Revision_After_Success()
        {
            long snapshotRevision = 5;
            bool stateChanged = false;

            ActionRouter router = Router(
                delegate { return snapshotRevision; },
                delegate(string op, JObject request)
                {
                    if ((string)request["action_id"] == "fresh1") stateChanged = true;
                    return ActionResult.Success(op);
                },
                delegate
                {
                    if (stateChanged)
                    {
                        snapshotRevision++;
                        stateChanged = false;
                    }

                    return snapshotRevision;
                });

            string first = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"fresh1\"}");
            Assert.Contains(first, "\"rev\":6", "write response carries the refreshed revision");

            string second = router.Handle("{\"op\":\"open_shop\",\"rev\":6,\"action_id\":\"fresh2\"}");
            Assert.Contains(second, "\"ok\":true", "next action can use the returned revision");
        }

        [Test]
        public static void Router_Returns_Refreshed_Revision_After_Failure()
        {
            long snapshotRevision = 5;
            bool stateChanged = false;

            ActionRouter router = Router(
                delegate { return snapshotRevision; },
                delegate(string op, JObject request)
                {
                    if ((string)request["action_id"] == "fail1")
                    {
                        stateChanged = true;
                        return ActionResult.Failure(op, "partial_failure");
                    }

                    return ActionResult.Success(op);
                },
                delegate
                {
                    if (stateChanged)
                    {
                        snapshotRevision++;
                        stateChanged = false;
                    }

                    return snapshotRevision;
                });

            string first = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"fail1\"}");
            Assert.Contains(first, "\"ok\":false", "failure response");
            Assert.Contains(first, "\"rev\":6", "failure response carries the refreshed revision");

            string second = router.Handle("{\"op\":\"open_shop\",\"rev\":6,\"action_id\":\"fail2\"}");
            Assert.Contains(second, "\"ok\":true", "next action can use the refreshed revision");
        }

        [Test]
        public static void Mode_Refusal_Happens_Before_Any_State_Refresh()
        {
            int refreshes = 0;
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); },
                new ActionHistory(),
                delegate { refreshes++; return 5; }, null, OpenGateProvider(), null,
                delegate(string op) { return op == "save_game" ? "mode_forbidden" : null; });

            string response = router.Handle(
                "{\"op\":\"save_game\",\"rev\":5,\"action_id\":\"m1\"}");

            Assert.Contains(response, "mode_forbidden", "refused by mode");
            Assert.Equal(0, refreshes, "no snapshot refresh before the refusal");
        }

        [Test]
        public static void Mode_Refusal_Also_Covers_Reads()
        {
            int refreshes = 0;
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); },
                new ActionHistory(),
                delegate { refreshes++; return 5; }, null, OpenGateProvider(), null,
                delegate(string op) { return op == "bargain_preview" ? "mode_forbidden" : null; });

            string response = router.Handle("{\"op\":\"bargain_preview\"}");

            Assert.Contains(response, "mode_forbidden", "reads are policed too");
            Assert.Equal(0, refreshes, "still no snapshot");
        }

        [Test]
        public static void Router_Rejects_Write_When_Pre_Refresh_Fails()
        {
            int calls = 0;
            bool available = false;
            var history = new ActionHistory();
            ActionRouter router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                history,
                delegate
                {
                    if (!available) throw new InvalidOperationException("snapshot failed");
                    return 5;
                },
                null,
                OpenGateProvider());

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"pre-fail\"}";
            string response = router.Handle(body);
            Assert.Contains(response, "state_unavailable", "unobservable state must reject before dispatch");
            Assert.Contains(response, "\"dispatched\":false", "pre-write refusal was not dispatched");
            Assert.False(response.Contains("\"ok\":true"), "unobservable state must not report success");
            Assert.Equal(0, calls, "pre-write refusal must dispatch zero actions");

            string historyJson = router.Handle("{\"op\":\"get_action_history\"}");
            Assert.Equal("[]", historyJson, "rejected write is not recorded as an executed action");

            available = true;
            string retry = router.Handle(body);
            Assert.Contains(retry, "\"ok\":true", "a pre-write refusal is not cached as an executed action");
            Assert.Equal(1, calls, "retry after state recovery executes once");
        }

        [Test]
        public static void Router_Write_Without_Refresh_Provider_Fails_Closed()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                null,
                null,
                null,
                OpenGateProvider());

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"no-refresh\"}");
            Assert.Contains(response, "state_unavailable", "write needs an observable state provider");
            Assert.Contains(response, "\"dispatched\":false", "pre-write refusal was not dispatched");
            Assert.Equal(0, calls, "no provider must dispatch zero actions");
        }

        [Test]
        public static void Router_Unknown_Outcome_When_Post_Refresh_Fails()
        {
            int dispatchCalls = 0;
            int refreshCalls = 0;
            long snapshotRevision = 5;

            ActionRouter router = Router(
                delegate { return snapshotRevision; },
                delegate(string op, JObject request)
                {
                    dispatchCalls++;
                    return ActionResult.Success(op);
                },
                delegate
                {
                    refreshCalls++;
                    if (refreshCalls >= 2)
                        throw new InvalidOperationException("post-dispatch snapshot failed");
                    return snapshotRevision;
                });

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"post-fail\"}";
            string first = router.Handle(body);

            Assert.Contains(first, "\"ok\":false", "unknown outcome is not a success");
            Assert.Contains(first, "outcome_unknown", "unknown outcome code");
            Assert.Contains(first, "\"unknown\":true", "unknown outcome is explicit");
            Assert.Contains(first, "\"dispatched\":true", "action was dispatched exactly once");
            Assert.False(first.Contains("\"ok\":true"), "unknown outcome must not report success");
            Assert.Equal(1, dispatchCalls, "action dispatched once");
            Assert.Equal(2, refreshCalls, "pre and post refresh attempted");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.Contains(historyJson, "outcome_unknown", "history records unknown outcome");
            Assert.False(historyJson.Contains("\"ok\":true"), "history must not record unknown as success");

            string second = router.Handle(body);
            Assert.Equal(first, second, "retry returns the same unknown result");
            Assert.Equal(1, dispatchCalls, "retry must not dispatch twice");
            Assert.Equal(2, refreshCalls, "retry uses the cached unknown result without another refresh");
        }

        [Test]
        public static void Router_Unknown_Outcome_Overrides_Reported_Failure()
        {
            int refreshCalls = 0;

            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.Failure(op, "not_in_game");
                },
                delegate
                {
                    refreshCalls++;
                    if (refreshCalls >= 2)
                        throw new InvalidOperationException("post-dispatch snapshot failed");
                    return 5;
                });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"post-fail-2\"}");
            Assert.Contains(response, "outcome_unknown", "unknown outcome is reported");
            Assert.False(response.Contains("not_in_game"), "unobservable state must not report a final failure");
        }

        [Test]
        public static void Router_Read_Actions_Do_Not_Require_Revision_Or_Action_Id()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            for (int i = 0; i < ReadOnlyOps.Length; i++)
            {
                string body = "{\"op\":\"" + ReadOnlyOps[i] + "\"}";
                string response = router.Handle(body);
                Assert.Contains(response, "\"ok\":true", ReadOnlyOps[i] + " must be dispatched as a read");
            }

            Assert.Equal(ReadOnlyOps.Length, calls, "all read actions dispatched");
        }

        [Test]
        public static void Router_Get_New_Game_Options_Is_A_Read_Op()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true,\"phase\":\"Specialization\"}");
            });

            string response = router.Handle("{\"op\":\"get_new_game_options\"}");

            Assert.Contains(response, "\"ok\":true", "new-game read is dispatched");
            Assert.Contains(response, "\"rev\":5", "read carries the current revision");
            Assert.Equal(1, calls, "read op did not need rev/action_id");
        }

        [Test]
        public static void Router_New_Game_Writes_Require_Action_Id_And_Revision()
        {
            string[] ops =
            {
                "open_new_game_menu",
                "select_new_game_option",
                "new_game_next_page",
                "focus_new_game_selected_perk",
                "new_game_continue"
            };

            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            for (int i = 0; i < ops.Length; i++)
            {
                string missingId = router.Handle("{\"op\":\"" + ops[i] + "\",\"rev\":5}");
                Assert.Contains(missingId, "missing_action_id", ops[i] + " needs action_id");

                string missingRev = router.Handle(
                    "{\"op\":\"" + ops[i] + "\",\"action_id\":\"id-" + i + "\"}");
                Assert.Contains(missingRev, "missing_rev", ops[i] + " needs rev");

                string stale = router.Handle(
                    "{\"op\":\"" + ops[i] + "\",\"action_id\":\"stale-" + i + "\",\"rev\":4}");
                Assert.Contains(stale, "stale_rev", ops[i] + " rejects stale rev");
            }

            Assert.Equal(0, calls, "no invalid new-game write may dispatch");
        }

        [Test]
        public static void Router_New_Game_Writes_Are_Blocked_By_Version_Gate()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { return LockedGate(GameVersionGate.ReasonMvidMismatch); });

            string response = router.Handle(
                "{\"op\":\"select_new_game_option\",\"kind\":\"scenario\",\"id\":\"fence\","
                + "\"action_id\":\"newgame-gate\",\"rev\":5}");

            Assert.Contains(response, "game_assembly_mvid_mismatch", "version gate refusal");
            Assert.Equal(0, calls, "locked write is not dispatched");
        }

        [Test]
        public static void Router_New_Game_Writes_Use_Action_Id_Idempotency()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true,\"selected\":\"fence\"}");
            });

            string body = "{\"op\":\"select_new_game_option\",\"kind\":\"scenario\","
                + "\"id\":\"fence\",\"action_id\":\"newgame-idem\",\"rev\":5}";
            string first = router.Handle(body);
            string second = router.Handle(body);

            Assert.Contains(first, "\"ok\":true", "first write runs");
            Assert.Equal(first, second, "retry returns the cached response");
            Assert.Equal(1, calls, "duplicate does not dispatch twice");

            string conflict = router.Handle(
                "{\"op\":\"select_new_game_option\",\"kind\":\"scenario\","
                + "\"id\":\"apprentice\",\"action_id\":\"newgame-idem\",\"rev\":5}");
            Assert.Contains(conflict, "action_conflict", "different args conflict");
            Assert.Equal(1, calls, "conflict does not dispatch");
        }

        [Test]
        public static void Router_Read_Actions_Ignore_Stale_Revision()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"list_goods\",\"rev\":1,\"action_id\":\"read1\"}");
            Assert.Contains(response, "\"ok\":true", "read action is not rejected as stale");
            Assert.Contains(response, "\"rev\":5", "read response carries the current revision");
            Assert.Equal(1, calls, "read action dispatched");
        }

        [Test]
        public static void Router_Read_Actions_Do_Not_Refresh_Revision()
        {
            int refreshes = 0;
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); },
                delegate
                {
                    refreshes++;
                    return 6;
                });

            string response = router.Handle("{\"op\":\"list_goods\"}");
            Assert.Contains(response, "\"ok\":true", "read action dispatched");
            Assert.Contains(response, "\"rev\":5", "read action uses the current revision");
            Assert.Equal(0, refreshes, "read action must not be treated as a write");
        }

        [Test]
        public static void Router_Rejects_Missing_Or_Bad_Op()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string missing = router.Handle("{\"rev\":5,\"action_id\":\"op1\"}");
            Assert.Contains(missing, "missing_op", "missing op rejected");

            string bad = router.Handle("{\"op\":7,\"rev\":5,\"action_id\":\"op2\"}");
            Assert.Contains(bad, "bad_op", "non-string op rejected");

            Assert.Equal(0, calls, "invalid op must not dispatch");
        }

        [Test]
        public static void Router_Does_Not_Record_Raw_Failure_As_Success()
        {
            var history = new ActionHistory();
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, "{\"ok\":false,\"error\":\"not_enough_cash\"}");
                }, history, delegate { return 5; }, null, OpenGateProvider());

            string response = router.Handle("{\"op\":\"buy_wholesale\",\"rev\":5,\"action_id\":\"raw1\"}");
            Assert.Contains(response, "\"ok\":false", "raw failure is returned");
            Assert.Contains(response, "\"rev\":5", "raw response carries a revision");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.Contains(historyJson, "\"ok\":false", "history records failure");
            Assert.Contains(historyJson, "not_enough_cash", "history records the original error");
            Assert.False(historyJson.Contains("\"ok\":true"), "raw failure must not be recorded as success");
        }

        [Test]
        public static void Router_Does_Not_Record_Raw_Missing_Ok_As_Success()
        {
            var history = new ActionHistory();
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, "{\"items\":[]}");
                }, history, delegate { return 5; }, null, OpenGateProvider());

            string response = router.Handle("{\"op\":\"list_goods\",\"rev\":5,\"action_id\":\"raw3\"}");
            Assert.Contains(response, "\"ok\":false", "raw result without ok is not a success");
            Assert.Contains(response, "bad_response", "malformed response code");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.Contains(historyJson, "\"ok\":false", "history records the malformed raw result as failure");
            Assert.Contains(historyJson, "bad_response", "history records the stable error");
            Assert.False(historyJson.Contains("\"ok\":true"), "missing ok must not be recorded as success");
        }

        [Test]
        public static void Router_Records_Raw_Success()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op, "{\"ok\":true,\"items\":[]}");
            });

            string response = router.Handle("{\"op\":\"list_goods\",\"rev\":5,\"action_id\":\"raw2\"}");
            Assert.Contains(response, "\"ok\":true", "raw success is returned");
            Assert.Contains(response, "\"rev\":5", "raw success carries a revision");
        }

        [Test]
        public static void Router_Redacts_Token_From_History()
        {
            const string secret = "0123456789abcdef0123456789abcdef";
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); },
                null,
                secret);

            string response = router.Handle(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"token1\",\"token\":\"" + secret
                + "\",\"nested\":{\"access_token\":\"" + secret + "\"}}");
            Assert.False(response.Contains(secret), "token must not be echoed");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(historyJson.Contains(secret), "token must not be audited");
            Assert.Contains(historyJson, "[redacted]", "token is redacted in history");
        }

        [Test]
        public static void Router_History_Remains_Json_For_Control_Characters_In_Error()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "bad\nline");
            });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"ctrl1\"}");
            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");

            JArray history = JArray.Parse(historyJson);
            Assert.Equal(1, history.Count, "history stays valid json");
            Assert.Contains(historyJson, "bad line", "control character is neutralized in audit");
        }

        [Test]
        public static void Router_Redacts_Token_From_Raw_Failure_Response_And_History()
        {
            const string secret = "0123456789abcdef0123456789abcdef";
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":false,\"error\":\"leaked_" + secret + "\"}");
                },
                null,
                secret);

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"rawtoken1\"}");
            Assert.Contains(response, "\"ok\":false", "raw failure returned");
            Assert.False(response.Contains(secret), "token must not appear in the raw failure response");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(historyJson.Contains(secret), "token must not appear in raw failure history");
            Assert.Contains(historyJson, "leaked_[redacted]", "original error prefix is preserved");
        }

        [Test]
        public static void Router_Redacts_Token_With_Json_Special_Characters()
        {
            const string secret = "0123456789abcdef\"\\0123456789abcdef";
            string escapedSecret = secret.Replace("\\", "\\\\").Replace("\"", "\\\"");

            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.Failure(op, "leaked_" + secret);
                },
                null,
                secret);

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"token3\"}");
            Assert.False(response.Contains(secret), "raw token must not appear");
            Assert.False(response.Contains(escapedSecret), "json-escaped token must not appear");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(historyJson.Contains(secret), "raw token must not appear in history");
            Assert.False(historyJson.Contains(escapedSecret), "json-escaped token must not appear in history");
        }

        [Test]
        public static void Router_Redacts_Token_From_Failure_Response_And_History()
        {
            const string secret = "0123456789abcdef0123456789abcdef";
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.Failure(op, "leaked_" + secret);
                },
                null,
                secret);

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"token2\"}");
            Assert.Contains(response, "\"ok\":false", "failure returned");
            Assert.False(response.Contains(secret), "token must not appear in the failure response");

            string historyJson = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(historyJson.Contains(secret), "token must not appear in history");
        }

        [Test]
        public static void Router_Rejects_Write_When_Version_Gate_Is_Locked()
        {
            int calls = 0;
            int refreshes = 0;
            var history = new ActionHistory();
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                history,
                delegate
                {
                    refreshes++;
                    return 5;
                },
                null,
                delegate { return LockedGate(GameVersionGate.ReasonMvidMismatch); });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate1\"}");
            Assert.Contains(response, "game_assembly_mvid_mismatch", "stable gate error code");
            Assert.Contains(response, "\"dispatched\":false", "denied before dispatch");
            Assert.Contains(response, "\"write_gate\":\"locked\"", "gate state is explicit");
            Assert.Contains(response, "\"rev\":5", "current revision is reported");
            Assert.False(response.Contains("\"ok\":true"), "denied write is not a success");
            Assert.Equal(0, calls, "denied write must dispatch zero actions");
            Assert.Equal(0, refreshes, "locked gate must be refused before touching game state");
            Assert.Equal("[]", router.Handle("{\"op\":\"get_action_history\"}"),
                "denied write is not recorded as an executed action");
        }

        [Test]
        public static void Router_Version_Gate_Denial_Is_Not_Cached()
        {
            int calls = 0;
            bool locked = true;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate
                {
                    return locked
                        ? LockedGate(GameVersionGate.ReasonUnconfigured)
                        : OpenGate();
                });

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate-retry\"}";
            string first = router.Handle(body);
            Assert.Contains(first, "version_gate_unconfigured", "first call is denied");
            Assert.Equal(0, calls, "denied call dispatches zero actions");

            locked = false;
            string second = router.Handle(body);
            Assert.Contains(second, "\"ok\":true", "retry can execute after the gate opens");
            Assert.Equal(1, calls, "a pre-dispatch gate refusal is not cached as executed");
        }

        [Test]
        public static void Router_All_Reads_Remain_Available_When_Version_Gate_Is_Locked()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { return LockedGate(GameVersionGate.ReasonVersionMismatch); });

            for (int i = 0; i < ReadOnlyOps.Length; i++)
            {
                string body = "{\"op\":\"" + ReadOnlyOps[i] + "\"}";
                string response = router.Handle(body);
                Assert.Contains(response, "\"ok\":true", ReadOnlyOps[i] + " must remain available");
            }

            Assert.Equal(ReadOnlyOps.Length, calls, "all reads dispatched despite the locked gate");

            string write = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate-read\"}");
            Assert.Contains(write, "game_version_mismatch", "writes remain locked");
            Assert.Equal(ReadOnlyOps.Length, calls, "locked write did not dispatch");
        }

        [Test]
        public static void Router_Duplicate_Cache_Wins_Over_Version_Gate()
        {
            int calls = 0;
            bool open = true;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate
                {
                    return open
                        ? OpenGate()
                        : LockedGate(GameVersionGate.ReasonMvidMismatch);
                });

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate-cache\"}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "first write is allowed");

            open = false;
            string second = router.Handle(body);
            Assert.Equal(first, second, "cached first result wins over the newly locked gate");
            Assert.Equal(1, calls, "duplicate must not dispatch again");
        }

        [Test]
        public static void Router_Unknown_Outcome_Cache_Wins_Over_Version_Gate()
        {
            int dispatchCalls = 0;
            int refreshCalls = 0;
            bool open = true;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    dispatchCalls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate
                {
                    refreshCalls++;
                    if (refreshCalls >= 2)
                        throw new InvalidOperationException("post-dispatch snapshot failed");
                    return 5;
                },
                null,
                delegate
                {
                    return open
                        ? OpenGate()
                        : LockedGate(GameVersionGate.ReasonMvidMismatch);
                });

            string body = "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate-unknown\"}";
            string first = router.Handle(body);
            Assert.Contains(first, "outcome_unknown", "first call records an unknown outcome");

            open = false;
            string second = router.Handle(body);
            Assert.Equal(first, second, "cached unknown outcome wins over the newly locked gate");
            Assert.Equal(1, dispatchCalls, "unknown outcome must not be dispatched again");
            Assert.Equal(2, refreshCalls, "retry uses the cached result without another refresh");
        }

        [Test]
        public static void Router_Write_Without_Version_Gate_Provider_Fails_Closed()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"no-gate\"}");
            Assert.Contains(response, "version_gate_unconfigured", "missing gate provider must lock writes");
            Assert.Contains(response, "\"dispatched\":false", "refusal happened before dispatch");
            Assert.Equal(0, calls, "no gate provider must dispatch zero actions");
        }

        [Test]
        public static void Router_Version_Gate_Provider_Exception_Fails_Closed()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { throw new InvalidOperationException("gate boom"); });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"gate-error\"}");
            Assert.Contains(response, "version_identity_unavailable", "gate failure locks writes");
            Assert.Contains(response, "\"dispatched\":false", "refusal happened before dispatch");
            Assert.False(response.Contains("gate boom"), "gate exception detail must not leak");
            Assert.Equal(0, calls, "failed gate must dispatch zero actions");
        }

        [Test]
        public static void Router_Locked_Version_Gate_Rejects_Recording_Writes_Before_Dispatch()
        {
            int calls = 0;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { return 5; },
                null,
                delegate { return LockedGate(GameVersionGate.ReasonUnconfigured); });

            string response = router.Handle(
                "{\"op\":\"confirm_new_game\",\"rev\":5,\"action_id\":\"rec-locked\"}");
            Assert.Contains(response, GameVersionGate.ReasonUnconfigured,
                "valid recording config cannot bypass the version gate");
            Assert.Contains(response, "\"dispatched\":false", "nothing was dispatched");
            Assert.Equal(0, calls, "locked version gate dispatches zero actions");
        }

        [Test]
        public static void Router_Recording_Writes_Still_Require_Revision_And_Action_Id()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                return ActionResult.Success(op);
            });

            string missingId = router.Handle("{\"op\":\"save_recording_game\",\"rev\":5}");
            Assert.Contains(missingId, "missing_action_id", "action id required");
            string missingRev = router.Handle(
                "{\"op\":\"load_recording_game\",\"action_id\":\"rec-load\"}");
            Assert.Contains(missingRev, "missing_rev", "revision required");
            Assert.Equal(0, calls, "no write dispatched without the protocol fields");
        }

        [Test]
        public static void Router_Marks_Undelivered_Write_As_Outcome_Unknown()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true,\"note\":\"ran\"}");
            });

            const string body = "{\"op\":\"store_blob\",\"action_id\":\"undelivered-1\",\"rev\":3}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "first attempt was dispatched and answered");

            // The transport could not deliver the first response. The cached
            // result must not keep claiming success to the retrying agent.
            router.MarkResponseUndelivered(body, first);

            string retry = router.Handle(body);
            Assert.Contains(retry, "\"ok\":false", "retry is not a success");
            Assert.Contains(retry, "outcome_unknown", "retry reports the unknown outcome");
            Assert.Contains(retry, "\"unknown\":true", "unknown is explicit");
            Assert.Contains(retry, "\"dispatched\":true", "the write did run");
            Assert.Equal(1, dispatches, "the retry must not dispatch the write again");
        }

        [Test]
        public static void Router_Does_Not_Mark_Rejected_Writes_As_Unknown()
        {
            int dispatches = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.Success(op);
            });

            const string body = "{\"op\":\"store_blob\",\"action_id\":\"stale-1\",\"rev\":4}";
            string first = router.Handle(body);
            Assert.Contains(first, "stale_rev", "stale write is refused before dispatch");

            router.MarkResponseUndelivered(body, first);

            string retry = router.Handle(body);
            Assert.Contains(retry, "stale_rev", "a refused write stays refused");
            Assert.False(retry.Contains("outcome_unknown"), "nothing ran, so nothing is unknown");
            Assert.Equal(0, dispatches, "no dispatch happened");
        }

        [Test]
        public static void Router_Does_Not_Mark_Undelivered_Reads()
        {
            int dispatches = 0;
            ActionRouter router = Router(2, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true}");
            });

            const string body = "{\"op\":\"list_goods\",\"scope\":\"all\"}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "read answered");

            // A lost read is harmless; the same action id must not poison a
            // later write cache entry.
            router.MarkResponseUndelivered(body, first);
            string history = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            Assert.False(history.Contains("outcome_unknown"), "reads are not write outcomes");
            Assert.Equal(1, dispatches, "read served once");
        }

        [Test]
        public static void Router_Notifies_Every_Successful_Write()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"shot1\"}");

            Assert.Contains(response, "\"ok\":true", "write succeeded");
            Assert.Equal(1, outcomes.Count, "one outcome callback");
            Assert.Equal("open_shop", outcomes[0].Op, "op recorded");
            Assert.True(outcomes[0].Ok, "success flagged");
            Assert.True(outcomes[0].Write, "write op flagged");
            Assert.Equal(5L, outcomes[0].Revision, "revision recorded");
            Assert.Null(outcomes[0].Error, "no error on success");
        }

        [Test]
        public static void Router_Notifies_Every_Failed_Write()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "cannot_afford");
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            string response = router.Handle("{\"op\":\"buy_wholesale\",\"rev\":5,\"action_id\":\"shot2\"}");

            Assert.Contains(response, "cannot_afford", "business rejection returned");
            Assert.Equal(1, outcomes.Count, "business failures also produce evidence");
            Assert.False(outcomes[0].Ok, "failure flagged");
            Assert.Equal("cannot_afford", outcomes[0].Error, "error code recorded");
            Assert.True(outcomes[0].Write, "write op flagged");
        }

        [Test]
        public static void Router_Does_Not_Notify_Read_Success()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"list_goods\"}");

            Assert.Equal(0, outcomes.Count, "reads without a hard failure are not captured");
        }

        [Test]
        public static void Router_Notifies_Read_Hard_Failure()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "native_error");
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"get_item\"}");

            Assert.Equal(1, outcomes.Count, "hard read failure is captured");
            Assert.False(outcomes[0].Ok, "failure flagged");
            Assert.False(outcomes[0].Write, "read op flagged");
            Assert.Equal("native_error", outcomes[0].Error, "error code recorded");
        }

        [Test]
        public static void Router_Does_Not_Notify_Read_Business_Rejection()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "no_client");
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"list_goods\"}");

            Assert.Equal(0, outcomes.Count, "soft read rejection is not captured");
        }

        [Test]
        public static void Router_Notifies_Stale_Write_Attempt()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                throw new AssertionException("stale action must not dispatch");
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"open_shop\",\"rev\":4,\"action_id\":\"shot3\"}");

            Assert.Equal(1, outcomes.Count, "a write attempt that returned stale_rev is captured");
            Assert.False(outcomes[0].Ok, "failure flagged");
            Assert.Equal("stale_rev", outcomes[0].Error, "stale_rev recorded as the outcome");
        }

        [Test]
        public static void Router_Notifies_Locked_Version_Gate_Write()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    throw new AssertionException("locked gate must not dispatch");
                },
                null,
                null,
                delegate { return LockedGate(GameVersionGate.ReasonVersionMismatch); },
                delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"shot4\"}");

            Assert.Equal(1, outcomes.Count, "locked write attempt is captured");
            Assert.Equal("game_version_mismatch", outcomes[0].Error, "gate reason recorded");
            Assert.True(outcomes[0].Write, "write op flagged");
        }

        [Test]
        public static void Router_Notifies_State_Unavailable_Once()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    throw new AssertionException("state unavailable must not dispatch");
                },
                delegate { return -1L; },
                null,
                null,
                delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"shot5\"}");

            Assert.Equal(1, outcomes.Count, "state unavailable captured once");
            Assert.Equal("state_unavailable", outcomes[0].Error, "stable error");
        }

        [Test]
        public static void Router_Notifies_Outcome_Unknown_Once()
        {
            int refreshes = 0;
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    return ActionResult.Success(op);
                },
                delegate
                {
                    refreshes++;
                    return refreshes >= 2 ? -1L : 5L;
                },
                null,
                null,
                delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"shot6\"}");

            Assert.Equal(1, outcomes.Count, "unknown outcome captured once");
            Assert.Equal("outcome_unknown", outcomes[0].Error, "unknown outcome code");
        }

        [Test]
        public static void Router_Does_Not_Notify_Ping_Or_Action_History()
        {
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            }, null, null, null, delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            router.Handle("{\"op\":\"ping\"}");
            router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");

            Assert.Equal(0, outcomes.Count, "protocol reads are not game actions");
        }

        [Test]
        public static void Router_Callback_Exception_Does_Not_Break_The_Response()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            }, null, null, null, delegate
            {
                throw new InvalidOperationException("evidence boom");
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"shot7\"}");

            Assert.Contains(response, "\"ok\":true", "response survives a broken evidence hook");
            Assert.False(response.Contains("evidence boom"), "callback detail must not leak");
        }

        [Test]
        public static void Router_Capture_Screenshot_Is_A_Read_Op_And_Bypasses_The_Version_Gate()
        {
            int calls = 0;
            var outcomes = new List<ActionOutcome>();
            ActionRouter router = Router(
                delegate { return 5; },
                delegate(string op, JObject request)
                {
                    calls++;
                    Assert.Equal("capture_screenshot", op, "capture op dispatched");
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":true,\"path\":\"screenshots/a.jpg\",\"reason\":\"manual\",\"rev\":5,\"day\":1,\"timestamp\":\"t\"}");
                },
                null,
                null,
                delegate { return LockedGate(GameVersionGate.ReasonVersionMismatch); },
                delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            string response = router.Handle("{\"op\":\"capture_screenshot\",\"reason\":\"manual\"}");

            Assert.Contains(response, "\"ok\":true", "manual capture works with a locked write gate");
            Assert.Contains(response, "screenshots/a.jpg", "capture path returned");
            Assert.Equal(1, calls, "capture dispatched without action_id or rev");
            Assert.Equal(0, outcomes.Count, "read op does not trigger automatic evidence");
        }

        [Test]
        public static void Router_List_Screenshots_Is_A_Read_Op()
        {
            int calls = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                calls++;
                Assert.Equal("list_screenshots", op, "list op dispatched");
                Assert.Equal(7, (int)request["limit"], "limit forwarded");
                return ActionResult.SuccessRaw(op,
                    "{\"ok\":true,\"total_count\":0,\"total_bytes\":0,\"budget_bytes\":1,\"dropped\":0,\"budget_exhausted\":false,\"screenshots\":[]}");
            });

            string response = router.Handle("{\"op\":\"list_screenshots\",\"limit\":7}");

            Assert.Contains(response, "total_count", "status returned");
            Assert.Equal(1, calls, "list dispatched without a revision");
        }

        [Test]
        public static void Router_Notifies_After_Undelivered_Write()
        {
            var outcomes = new List<ActionOutcome>();
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); },
                new ActionHistory(),
                delegate { return 5; },
                null,
                OpenGateProvider(),
                delegate(ActionOutcome outcome) { outcomes.Add(outcome); });

            string response = router.Handle(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"undelivered1\"}");
            outcomes.Clear();

            router.MarkResponseUndelivered(
                "{\"op\":\"open_shop\",\"rev\":5,\"action_id\":\"undelivered1\"}",
                response);

            Assert.Equal(1, outcomes.Count, "undelivered write is captured");
            Assert.Equal("outcome_unknown", outcomes[0].Error, "transport failure is unknown outcome");
        }
    }

    internal static class ActionResultTests
    {
        [Test]
        public static void Raw_Success_Is_Ok()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{\"ok\":true,\"items\":[]}");
            Assert.True(result.Ok, "ok:true is a success");
            Assert.Null(result.Error, "success has no error");
        }

        [Test]
        public static void Raw_Failure_Is_Not_Ok()
        {
            ActionResult result = ActionResult.SuccessRaw("buy_wholesale", "{\"ok\":false,\"error\":\"not_enough_cash\"}");
            Assert.False(result.Ok, "ok:false is a failure");
            Assert.Equal("not_enough_cash", result.Error, "original error code is preserved");
        }

        [Test]
        public static void Raw_Failure_Without_Error_Uses_Stable_Code()
        {
            ActionResult result = ActionResult.SuccessRaw("buy_wholesale", "{\"ok\":false}");
            Assert.False(result.Ok, "ok:false is a failure");
            Assert.Equal("raw_failure", result.Error, "stable fallback error code");
        }

        [Test]
        public static void Raw_Non_Boolean_Ok_Is_Not_Success()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{\"ok\":\"true\"}");
            Assert.False(result.Ok, "non-boolean ok is not a success");
            Assert.Equal("bad_response", result.Error, "malformed response code");

            string json = result.ToJson(6);
            Assert.Contains(json, "\"ok\":false", "malformed raw response must not be returned as success");
            Assert.Contains(json, "bad_response", "stable error code");
            Assert.Contains(json, "\"rev\":6", "revision included");
        }

        [Test]
        public static void Raw_Missing_Ok_Is_Not_Success()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{\"items\":[]}");
            Assert.False(result.Ok, "raw result without boolean ok is not a success");
            Assert.Equal("bad_response", result.Error, "missing ok uses the stable malformed code");

            string json = result.ToJson(6);
            Assert.Contains(json, "\"ok\":false", "raw result without ok must not be returned as success");
            Assert.Contains(json, "bad_response", "stable error code");
            Assert.Contains(json, "\"rev\":6", "revision included");
        }

        [Test]
        public static void Raw_Malformed_Is_Failure()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{not json");
            Assert.False(result.Ok, "malformed raw response is a failure");
            Assert.Equal("bad_response", result.Error, "malformed response code");
        }

        [Test]
        public static void Raw_ToJson_Injects_Revision_And_Preserves_Payload()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{\"ok\":true,\"items\":[],\"rev\":1}");
            string json = result.ToJson(9);
            Assert.Contains(json, "\"items\":[]", "payload preserved");
            Assert.Contains(json, "\"rev\":9", "authoritative revision injected");
            Assert.False(json.Contains("\"rev\":1"), "service revision overridden");
        }

        [Test]
        public static void Raw_Failure_ToJson_Keeps_Failure()
        {
            ActionResult result = ActionResult.SuccessRaw("buy_wholesale", "{\"ok\":false,\"error\":\"not_enough_cash\"}");
            string json = result.ToJson(3);
            Assert.Contains(json, "\"ok\":false", "failure preserved");
            Assert.Contains(json, "not_enough_cash", "error preserved");
            Assert.Contains(json, "\"rev\":3", "revision injected");
        }

        [Test]
        public static void Malformed_Raw_ToJson_Returns_Stable_Failure()
        {
            ActionResult result = ActionResult.SuccessRaw("list_goods", "{not json");
            string json = result.ToJson(4);
            Assert.Contains(json, "\"ok\":false", "malformed response becomes a failure");
            Assert.Contains(json, "bad_response", "stable error code");
            Assert.Contains(json, "\"rev\":4", "revision included");
        }

        [Test]
        public static void Typed_Failure_ToJson_Contains_Error_And_Rev()
        {
            string json = ActionResult.Failure("open_shop", "not_in_game").ToJson(8);
            Assert.Contains(json, "\"ok\":false", "failure flag");
            Assert.Contains(json, "not_in_game", "error code");
            Assert.Contains(json, "\"rev\":8", "revision");
        }

        [Test]
        public static void Typed_Failure_ToJson_Escapes_Control_Characters()
        {
            string json = ActionResult.Failure("open_shop", "bad\nline").ToJson(8);
            Assert.False(json.Contains("\n"), "raw control characters must not break the response");
            JObject parsed = JObject.Parse(json);
            Assert.Equal("bad\nline", (string)parsed["error"], "error round-trips through json");
        }
    }
}
