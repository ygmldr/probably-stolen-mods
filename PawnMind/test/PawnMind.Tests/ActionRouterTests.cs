using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ActionRouterTests
    {
        private static ActionRouter Router(long revision, Func<string, JObject, ActionResult> dispatch)
        {
            return new ActionRouter(new ActionGate(), delegate { return revision; }, dispatch);
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
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Success(op);
            });

            string response = router.Handle("{\"op\":\"open_shop\",\"rev\":4}");
            Assert.Contains(response, "stale_rev", "stale revision rejected");
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
        public static void Router_Returns_Unknown_Op_From_Dispatcher()
        {
            ActionRouter router = Router(5, delegate(string op, JObject request)
            {
                return ActionResult.Failure(op, "unknown_op");
            });

            string response = router.Handle("{\"op\":\"dance\",\"rev\":5}");
            Assert.Contains(response, "unknown_op", "unknown op surfaced");
        }

        [Test]
        public static void Router_Records_Action_History()
        {
            var history = new ActionHistory();
            var router = new ActionRouter(new ActionGate(), delegate { return 5; },
                delegate(string op, JObject request) { return ActionResult.Success(op); }, history);

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

            string response = router.Handle("{\"op\":\"end_day\",\"rev\":5}");
            Assert.Contains(response, "\"ok\":false", "exception is a failure");
            Assert.Contains(response, "exception", "exception code");
        }
    }
}
