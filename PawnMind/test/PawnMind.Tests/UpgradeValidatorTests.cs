using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class UpgradeValidatorTests
    {
        [Test]
        public static void Upgrade_Accepts_When_All_Checks_Pass()
        {
            Assert.Equal(UpgradeCheck.Ok, UpgradeValidator.Validate(true, false, true, true), "ok");
        }

        [Test]
        public static void Upgrade_Rejects_Already_Unlocked()
        {
            Assert.Equal(UpgradeCheck.AlreadyUnlocked, UpgradeValidator.Validate(true, true, true, true), "unlocked");
        }

        [Test]
        public static void Upgrade_Rejects_Missing_Prerequisite()
        {
            Assert.Equal(UpgradeCheck.Prerequisite, UpgradeValidator.Validate(true, false, false, true), "prereq");
        }

        [Test]
        public static void Upgrade_Rejects_No_Cash()
        {
            Assert.Equal(UpgradeCheck.NoCash, UpgradeValidator.Validate(true, false, true, false), "cash");
        }
    }
}
