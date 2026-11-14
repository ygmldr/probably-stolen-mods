using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ActionGateTests
    {
        [Test]
        public static void Gate_Accepts_Matching_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.Accepted, gate.Check("a1", 4, 4, "op|args", out cached),
                "matching revision");
        }

        [Test]
        public static void Gate_Rejects_Stale_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.StaleRevision, gate.Check("a1", 3, 4, "op|args", out cached),
                "stale revision");
        }

        [Test]
        public static void Gate_Rejects_Request_Without_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.MissingRevision, gate.Check("a1", null, 4, "op|args", out cached),
                "missing revision must not be accepted");
        }

        [Test]
        public static void Gate_Rejects_Negative_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.InvalidRevision, gate.Check("a1", -1, 4, "op|args", out cached),
                "negative revision is not a valid current revision");
        }

        [Test]
        public static void Gate_Rejects_Missing_Action_Id()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.MissingActionId, gate.Check(null, 4, 4, "op|args", out cached),
                "null action id");
            Assert.Equal(ActionGateResult.MissingActionId, gate.Check(string.Empty, 4, 4, "op|args", out cached),
                "empty action id");
            Assert.Equal(ActionGateResult.MissingActionId, gate.Check("   ", 4, 4, "op|args", out cached),
                "blank action id");
        }

        [Test]
        public static void Gate_Rejects_Invalid_Action_Id()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.InvalidActionId,
                gate.Check("a\u0001b", 4, 4, "op|args", out cached), "control character");
            Assert.Equal(ActionGateResult.InvalidActionId,
                gate.Check(new string('a', 129), 4, 4, "op|args", out cached), "oversized id");
        }

        [Test]
        public static void Gate_Returns_Cached_Result_For_Duplicate_Action_Id()
        {
            var gate = new ActionGate();
            string cached;
            gate.Remember("a1", "op|args", "{\"ok\":true}");
            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a1", 3, 4, "op|args", out cached),
                "duplicate id wins over stale check");
            Assert.Equal("{\"ok\":true}", cached, "cached response");
        }

        [Test]
        public static void Gate_Rejects_Same_Action_Id_With_Different_Args()
        {
            var gate = new ActionGate();
            string cached;
            gate.Remember("a1", "op|args-one", "{\"ok\":true}");
            Assert.Equal(ActionGateResult.Conflict, gate.Check("a1", 4, 4, "op|args-two", out cached),
                "same id with different normalized op/args must conflict");
            Assert.Null(cached, "conflict must not return the first response");
        }

        [Test]
        public static void Gate_Keeps_More_Than_Sixty_Four_Actions()
        {
            var gate = new ActionGate();
            string cached;
            for (int i = 0; i < 100; i++)
            {
                gate.Remember("a" + i, "op|args-" + i, "result-" + i);
            }

            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a0", 0, 0, "op|args-0", out cached),
                "the default cache must not forget a retry after only 64 actions");
            Assert.Equal("result-0", cached, "cached response");
        }

        [Test]
        public static void Gate_Replaces_The_Cached_Response_Only_When_It_Matches()
        {
            var gate = new ActionGate();
            string cached;
            gate.Remember("a1", "op|args", "result-1");

            Assert.False(gate.Replace("a1", "some-other-result", "unknown"),
                "a mismatched expected response must not overwrite the cache");
            Assert.False(gate.Replace("missing", "result-1", "unknown"),
                "unknown action ids are not created by a replace");

            Assert.True(gate.Replace("a1", "result-1", "unknown"), "the matching cached result is replaced");
            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a1", 1, 1, "op|args", out cached),
                "the retry still hits the same id");
            Assert.Equal("unknown", cached, "the retry receives the replacement");
        }

        [Test]
        public static void Gate_Evicts_Old_Results()
        {
            var gate = new ActionGate(2);
            string cached;
            gate.Remember("a1", "op|args-1", "1");
            gate.Remember("a2", "op|args-2", "2");
            gate.Remember("a3", "op|args-3", "3");

            Assert.Equal(ActionGateResult.Accepted, gate.Check("a1", 1, 1, "op|args-1", out cached),
                "oldest entry evicted");
            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a3", 1, 1, "op|args-3", out cached),
                "newest entry kept");
        }
    }
}
