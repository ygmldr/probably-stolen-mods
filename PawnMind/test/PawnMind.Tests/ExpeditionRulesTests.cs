using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ExpeditionRulesTests
    {
        [Test]
        public static void Deploy_Requires_Unlock_Hireling_Health()
        {
            Assert.True(ExpeditionRules.CanDeploy(true, true, false), "ok");
            Assert.False(ExpeditionRules.CanDeploy(false, true, false), "locked");
            Assert.False(ExpeditionRules.CanDeploy(true, false, false), "no hireling");
            Assert.False(ExpeditionRules.CanDeploy(true, true, true), "injured");
        }
    }
}
