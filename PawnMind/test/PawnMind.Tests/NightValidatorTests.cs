using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class NightValidatorTests
    {
        [Test]
        public static void Night_Accepts_Discovered_Available_Location()
        {
            NightCheck check = NightValidator.Validate(new NightFlags
            {
                Known = true, Discovered = true, Available = true, Outside = false
            });
            Assert.Equal(NightCheck.Ok, check, "valid");
        }

        [Test]
        public static void Night_Rejects_Unknown()
        {
            Assert.Equal(NightCheck.Unknown, NightValidator.Validate(new NightFlags()), "unknown");
        }

        [Test]
        public static void Night_Rejects_Undiscovered()
        {
            Assert.Equal(NightCheck.Undiscovered, NightValidator.Validate(new NightFlags
            {
                Known = true, Discovered = false, Available = true
            }), "undiscovered");
        }

        [Test]
        public static void Night_Rejects_Unavailable()
        {
            Assert.Equal(NightCheck.Unavailable, NightValidator.Validate(new NightFlags
            {
                Known = true, Discovered = true, Available = false
            }), "unavailable");
        }

        [Test]
        public static void Night_Rejects_Already_Outside()
        {
            Assert.Equal(NightCheck.AlreadyOutside, NightValidator.Validate(new NightFlags
            {
                Known = true, Discovered = true, Available = true, Outside = true
            }), "already outside");
        }
    }
}
