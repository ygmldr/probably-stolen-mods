using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class HealthRulesTests
    {
        [Test]
        public static void Serious_Wound_Threshold()
        {
            Assert.False(HealthRules.IsSeriouslyWounded(4), "4");
            Assert.True(HealthRules.IsSeriouslyWounded(5), "5");
            Assert.True(HealthRules.IsSeriouslyWounded(9), "9");
        }
    }
}
