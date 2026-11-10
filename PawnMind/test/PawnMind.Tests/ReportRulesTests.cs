using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ReportRulesTests
    {
        [Test]
        public static void Report_Requires_Client_And_Availability()
        {
            Assert.Equal(ReportCheck.Ok, ReportRules.Validate(true, true, false), "ok");
            Assert.Equal(ReportCheck.NoClient, ReportRules.Validate(false, true, false), "no client");
            Assert.Equal(ReportCheck.Unavailable, ReportRules.Validate(true, false, false), "unavailable");
            Assert.Equal(ReportCheck.AlreadyReported, ReportRules.Validate(true, true, true), "already");
        }
    }
}
