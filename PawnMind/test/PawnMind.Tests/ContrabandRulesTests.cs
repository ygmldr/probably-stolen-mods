using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ContrabandRulesTests
    {
        [Test]
        public static void High_Risk_Flags_Serious_Or_Stolen()
        {
            Assert.False(ContrabandRules.IsHighRisk(0, false), "clean");
            Assert.True(ContrabandRules.IsHighRisk(3, false), "serious");
            Assert.True(ContrabandRules.IsHighRisk(0, true), "stolen");
        }
    }
}
