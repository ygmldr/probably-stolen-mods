using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ServiceValidatorTests
    {
        [Test]
        public static void Service_Accepts_Unlocked_Optional_Ready()
        {
            Assert.Equal(ServiceCheck.Ok, ServiceValidator.Validate(true, true, false, false), "ok");
        }

        [Test]
        public static void Service_Rejects_Locked_Or_Cooldown()
        {
            Assert.Equal(ServiceCheck.Locked, ServiceValidator.Validate(true, false, false, false), "locked");
            Assert.Equal(ServiceCheck.Cooldown, ServiceValidator.Validate(true, true, false, true), "cooldown");
            Assert.Equal(ServiceCheck.Mandatory, ServiceValidator.Validate(true, true, true, false), "mandatory");
        }
    }
}
