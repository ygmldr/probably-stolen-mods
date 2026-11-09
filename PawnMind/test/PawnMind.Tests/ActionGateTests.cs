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
            Assert.Equal(ActionGateResult.Accepted, gate.Check("a1", 4, 4, out cached), "matching revision");
        }

        [Test]
        public static void Gate_Rejects_Stale_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.StaleRevision, gate.Check("a1", 3, 4, out cached), "stale revision");
        }

        [Test]
        public static void Gate_Accepts_Request_Without_Revision()
        {
            var gate = new ActionGate();
            string cached;
            Assert.Equal(ActionGateResult.Accepted, gate.Check("a1", -1, 4, out cached), "no revision means no check");
        }

        [Test]
        public static void Gate_Returns_Cached_Result_For_Duplicate_Action_Id()
        {
            var gate = new ActionGate();
            string cached;
            gate.Remember("a1", "{\"ok\":true}");
            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a1", 3, 4, out cached), "duplicate id wins over stale check");
            Assert.Equal("{\"ok\":true}", cached, "cached response");
        }

        [Test]
        public static void Gate_Evicts_Old_Results()
        {
            var gate = new ActionGate(2);
            string cached;
            gate.Remember("a1", "1");
            gate.Remember("a2", "2");
            gate.Remember("a3", "3");

            Assert.Equal(ActionGateResult.Accepted, gate.Check("a1", 1, 1, out cached), "oldest entry evicted");
            Assert.Equal(ActionGateResult.Duplicate, gate.Check("a3", 1, 1, out cached), "newest entry kept");
        }
    }
}
