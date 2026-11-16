using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// D module: execution/delivery are two independent axes, an operation can be
    /// queried while it is still running, and a lost HTTP response must never
    /// rewrite a completed business fact.
    ///
    /// Evidence level: L1-fake (no game process). The real Unity async paths
    /// (shutter animation, sleep blackscreen, save file) stay NOT_RUN.
    /// </summary>
    internal static class OperationTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";
        private const string GameVersion = "VERSION 1-Rev1";
        private const string GameMvid = "11111111-2222-3333-4444-555555555555";

        private static WriteGateDecision OpenGate()
        {
            return GameVersionGate.Evaluate(
                new GameIdentity(GameVersion, GameMvid), GameVersion, GameMvid);
        }

        private static ActionRouter Router(
            long revision,
            Func<string, JObject, ActionResult> dispatch,
            Func<string, string> authorize = null,
            Func<OperationRecord, OperationRecord> verifier = null)
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
                authorize,
                verifier);
        }

        private static string PendingResponse(string op)
        {
            return "{\"ok\":false,\"op\":\"" + op + "\",\"dispatched\":true,"
                + "\"pending\":true,\"error\":\"shop_still_open\","
                + "\"execution_status\":\"running\"}";
        }

        // ------------------------------------------------------ gate two axes

        [Test]
        public static void PP_ASYNC_02_Gate_Tracks_Execution_And_Delivery_Axes()
        {
            var gate = new ActionGate();
            gate.Begin("op-1", "end_day", 4);
            gate.Complete("op-1", "{\"ok\":true}", ExecutionStatus.Completed,
                DeliveryStatus.Delivered, 5, null);

            OperationRecord record = gate.Find("op-1");
            Assert.NotNull(record, "operation recorded");
            Assert.Equal(ExecutionStatus.Completed, record.Execution, "execution completed");
            Assert.Equal(DeliveryStatus.Delivered, record.Delivery, "delivery delivered");
            Assert.Equal(5L, record.ResultRevision, "result revision");
            Assert.True(record.Dispatched, "dispatched");
        }

        [Test]
        public static void PP_ASYNC_02_Gate_MarkUndelivered_Keeps_Completed_Execution()
        {
            var gate = new ActionGate();
            gate.Begin("op-2", "confirm_trade", 7);
            gate.Complete("op-2", "{\"ok\":true}", ExecutionStatus.Completed,
                DeliveryStatus.Delivered, 8, null);

            Assert.True(gate.MarkUndelivered("op-2"), "delivery axis updated");
            OperationRecord record = gate.Find("op-2");
            Assert.Equal(ExecutionStatus.Completed, record.Execution,
                "the completed business fact survives a lost response");
            Assert.Equal(DeliveryStatus.Undelivered, record.Delivery, "delivery is undelivered");
            Assert.Equal("{\"ok\":true}", gate.CachedResponse("op-2"),
                "the original evidence body is preserved");
        }

        [Test]
        public static void PP_ASYNC_03_Gate_Clear_Forgets_Operations()
        {
            var gate = new ActionGate();
            gate.Begin("old", "confirm_trade", 3);
            gate.Complete("old", "{\"ok\":true}", ExecutionStatus.Completed,
                DeliveryStatus.Delivered, 4, null);

            Assert.Equal(1, gate.ClearOperations(), "one operation cleared");
            Assert.Null(gate.Find("old"), "a cleared operation cannot be replayed");
            string cached;
            Assert.Equal(ActionGateResult.Accepted, gate.Check("old", 1, 1, "p", out cached),
                "the id is unknown to the cache again (but the router still refuses blind replay)");
        }

        [Test]
        public static void Gate_Legacy_Remember_Still_Records_An_Operation()
        {
            var gate = new ActionGate();
            gate.Remember("legacy", "op|args", "{\"ok\":false,\"error\":\"cannot_end_day\"}");
            OperationRecord record = gate.Find("legacy");
            Assert.NotNull(record, "legacy cache entries expose an operation record");
            Assert.Equal(ExecutionStatus.Rejected, record.Execution,
                "a non-ok legacy response is not a success");
            Assert.Equal(DeliveryStatus.Delivered, record.Delivery, "delivered");
        }

        // ------------------------------------------------- write response axes

        [Test]
        public static void PP_ASYNC_01_Write_Response_Carries_Both_Axes()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op, "{\"ok\":true}");
            });

            string response = router.Handle(
                "{\"op\":\"open_shop\",\"action_id\":\"axis-1\",\"rev\":3}");
            JObject json = JObject.Parse(response);
            Assert.True((bool)json["ok"], "success stays true");
            Assert.Equal(ExecutionStatus.Completed, (string)json["execution_status"],
                "ok:true means completed");
            Assert.Equal(DeliveryStatus.Delivered, (string)json["delivery_status"],
                "the caller received the response");
            Assert.Equal("axis-1", (string)json["operation_id"], "operation id echoed");
        }

        [Test]
        public static void PP_ASYNC_01_Pending_Write_Is_Running_Not_Completed()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.SuccessRaw(op, PendingResponse(op));
            });

            string response = router.Handle(
                "{\"op\":\"end_day\",\"action_id\":\"pending-1\",\"rev\":3}");
            JObject json = JObject.Parse(response);
            Assert.False((bool)json["ok"], "a running operation is not a success");
            Assert.Equal(ExecutionStatus.Running, (string)json["execution_status"], "running");
            Assert.Equal(DeliveryStatus.Delivered, (string)json["delivery_status"], "delivered");

            JObject operation = router.GetOperationJson("pending-1");
            Assert.True((bool)operation["known"], "the operation is queryable");
            Assert.Equal(ExecutionStatus.Running, (string)operation["execution_status"], "running");
            Assert.Equal("end_day", (string)operation["op"], "op recorded");
        }

        [Test]
        public static void PP_ASYNC_01_Verifier_Advances_Running_To_Completed()
        {
            int verifications = 0;
            ActionRouter router = Router(3,
                delegate(string op, JObject request)
                {
                    return ActionResult.SuccessRaw(op, PendingResponse(op));
                },
                null,
                delegate(OperationRecord record)
                {
                    verifications++;
                    if (verifications < 2) return record;
                    record.Execution = ExecutionStatus.Completed;
                    record.ResultRevision = 4;
                    record.Error = null;
                    return record;
                });

            router.Handle("{\"op\":\"end_day\",\"action_id\":\"verify-1\",\"rev\":3}");

            JObject first = router.GetOperationJson("verify-1");
            Assert.Equal(ExecutionStatus.Running, (string)first["execution_status"],
                "still running before the readback proves completion");
            Assert.False((bool)first["ok"], "running is not ok");

            JObject second = router.GetOperationJson("verify-1");
            Assert.Equal(ExecutionStatus.Completed, (string)second["execution_status"],
                "the verifier promoted the operation");
            Assert.True((bool)second["ok"], "completed is ok");
        }

        [Test]
        public static void PP_ASYNC_02_Undelivered_Response_Keeps_Completed_Evidence()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true,\"note\":\"ran\"}");
            });

            const string body = "{\"op\":\"store_blob\",\"action_id\":\"undelivered-2\",\"rev\":3}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "first attempt ran");

            router.MarkResponseUndelivered(body, first);

            string retry = router.Handle(body);
            JObject json = JObject.Parse(retry);
            Assert.True((bool)json["ok"], "the completed business fact is not rewritten");
            Assert.Equal(ExecutionStatus.Completed, (string)json["execution_status"], "still completed");
            Assert.Equal(DeliveryStatus.Undelivered, (string)json["delivery_status"],
                "the lost response is recorded on the delivery axis");
            Assert.True((bool)json["redelivered"], "the retry flags that it is a re-delivery");
            Assert.Equal(1, dispatches, "the retry must not dispatch the write again");
        }

        [Test]
        public static void PP_ASYNC_02_Rejected_Writes_Are_Not_Marked_Undelivered()
        {
            int dispatches = 0;
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.Success(op);
            });

            const string body = "{\"op\":\"store_blob\",\"action_id\":\"stale-9\",\"rev\":4}";
            string first = router.Handle(body);
            Assert.Contains(first, "stale_rev", "stale write refused before dispatch");

            router.MarkResponseUndelivered(body, first);
            string retry = router.Handle(body);
            Assert.Contains(retry, "stale_rev", "a refused write stays refused");
            Assert.Equal(0, dispatches, "no dispatch happened");
        }

        // ------------------------------------------------------ conflict / wait

        [Test]
        public static void PP_ASYNC_04_Pending_Operation_Blocks_The_Same_Op_Not_Other_Writes()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                if (op == "end_day") return ActionResult.SuccessRaw(op, PendingResponse(op));
                return ActionResult.SuccessRaw(op, "{\"ok\":true}");
            });

            router.Handle("{\"op\":\"end_day\",\"action_id\":\"block-1\",\"rev\":3}");
            Assert.Equal(1, dispatches, "first end_day dispatched once");

            string conflict = router.Handle(
                "{\"op\":\"end_day\",\"action_id\":\"block-2\",\"rev\":3,\"dismiss_waiting\":true}");
            Assert.Contains(conflict, "operation_pending", "a second end_day is refused");
            Assert.Contains(conflict, "block-1", "the blocking operation is named");
            Assert.Equal(1, dispatches, "the conflicting write is not dispatched");

            // The dialogue continuation is a different write and must not be
            // deadlocked by the pending shop operation.
            string dialogue = router.Handle(
                "{\"op\":\"advance_dialogue\",\"action_id\":\"block-3\",\"rev\":3}");
            Assert.Contains(dialogue, "\"ok\":true", "an unrelated write still runs");
            Assert.Equal(2, dispatches, "advance_dialogue dispatched");
        }

        [Test]
        public static void PP_ASYNC_04_Same_Action_Id_Retry_Returns_The_Pending_Operation()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, PendingResponse(op));
            });

            const string body = "{\"op\":\"end_day\",\"action_id\":\"pending-retry\",\"rev\":3}";
            string first = router.Handle(body);
            string retry = router.Handle(body);

            Assert.Equal(1, dispatches, "a retried action id never re-dispatches");
            JObject json = JObject.Parse(retry);
            Assert.Equal(ExecutionStatus.Running, (string)json["execution_status"], "still running");
            Assert.Equal("pending-retry", (string)json["operation_id"], "same operation id");
            Assert.Contains(first, "shop_still_open", "first response preserved");
        }

        // -------------------------------------------------------- query paths

        [Test]
        public static void PP_ASYNC_05_Unknown_Operation_Is_A_Structured_Read()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            JObject unknown = router.GetOperationJson("never-issued");
            Assert.False((bool)unknown["ok"], "unknown is not a success");
            Assert.False((bool)unknown["known"], "known=false");
            Assert.Equal("unknown_operation", (string)unknown["error"], "stable code");
            Assert.NotNull(unknown["next"], "the caller is told what to do instead of retrying blindly");
        }

        [Test]
        public static void PP_ASYNC_05_Bad_Operation_Id_Is_Refused_Without_Touching_State()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.Success(op);
            });

            JObject bad = router.GetOperationJson(null);
            Assert.Equal("bad_operation_id", (string)bad["error"], "missing id refused");
            Assert.Equal(0, dispatches, "a read query never dispatches");
        }

        [Test]
        public static void PP_RECOVERY_01_Cleared_Operations_Report_A_Process_Restart()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            router.Handle("{\"op\":\"open_shop\",\"action_id\":\"run-1\",\"rev\":3}");
            router.ResetOperations();

            JObject after = router.GetOperationJson("run-1");
            Assert.False((bool)after["known"], "the old run's evidence is gone");
            Assert.Equal("unknown_operation", (string)after["error"], "stable code");
            Assert.True((bool)after["process_restarted"],
                "the caller learns the operation cache was reset (no blind replay)");
        }

        [Test]
        public static void PP_MODE_02b_Mode_Change_Never_Redispatches_A_Cached_Action()
        {
            int dispatches = 0;
            string refusal = null;
            ActionRouter router = Router(3,
                delegate(string op, JObject request)
                {
                    dispatches++;
                    return ActionResult.SuccessRaw(op, "{\"ok\":true}");
                },
                delegate(string op) { return refusal; });

            const string body = "{\"op\":\"open_shop\",\"action_id\":\"mode-1\",\"rev\":3}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "first attempt ran");

            // The operator switches to a stricter mode; the old id must not be
            // re-dispatched and the recorded evidence must not be rewritten.
            refusal = "mode_forbidden";
            string retry = router.Handle(body);
            Assert.Contains(retry, "mode_forbidden", "the new mode refuses the call");
            Assert.Equal(1, dispatches, "no second dispatch");

            JObject operation = router.GetOperationJson("mode-1");
            Assert.Equal(ExecutionStatus.Completed, (string)operation["execution_status"],
                "completed evidence is intact after the mode change");
        }

        // ------------------------------------------------------------- history

        [Test]
        public static void Undelivered_Write_Is_Recorded_As_Delivery_Not_Outcome_Unknown()
        {
            int dispatches = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                dispatches++;
                return ActionResult.SuccessRaw(op, "{\"ok\":true}");
            });

            const string body = "{\"op\":\"open_shop\",\"action_id\":\"hist-1\",\"rev\":3}";
            string first = router.Handle(body);
            router.MarkResponseUndelivered(body, first);

            string history = router.Handle("{\"op\":\"get_action_history\",\"limit\":5}");
            JArray actions = JArray.Parse(history);
            Assert.NotNull(actions, "history rows");
            bool sawDelivery = false;
            for (int i = 0; i < actions.Count; i++)
            {
                string error = (string)actions[i]["error"];
                if (error != null && error.IndexOf("delivery", StringComparison.OrdinalIgnoreCase) >= 0)
                    sawDelivery = true;
                Assert.False(string.Equals(error, "outcome_unknown", StringComparison.Ordinal),
                    "a delivered action is not an unknown outcome");
            }

            Assert.True(sawDelivery, "history records the delivery failure separately");
            Assert.Equal(1, dispatches, "no re-dispatch");
        }
    }
}
