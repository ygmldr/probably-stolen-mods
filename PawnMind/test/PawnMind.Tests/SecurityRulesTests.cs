using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class SecurityRulesTests
    {
        [Test]
        public static void Evidence_Level_Flags_Investigation()
        {
            Assert.False(SecurityRules.IsUnderInvestigation(0), "none");
            Assert.True(SecurityRules.IsUnderInvestigation(1), "evidence");
        }
    }
}
