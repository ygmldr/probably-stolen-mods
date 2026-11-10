using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class WineMathTests
    {
        [Test]
        public static void Ferment_Requires_Water_And_Ingredient()
        {
            Assert.False(WineMath.CanFerment(748, true), "water low");
            Assert.False(WineMath.CanFerment(750, false), "no ingredient");
            Assert.True(WineMath.CanFerment(750, true), "ready");
        }
    }
}
