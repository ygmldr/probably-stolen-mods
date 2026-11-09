using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class PlacementMathTests
    {
        [Test]
        public static void BottomRight_Uses_Unrotated_Size()
        {
            int x, y;
            PlacementMath.BottomRight(24, 10, 3, 2, false, out x, out y);
            Assert.Equal(21, x, "bottom-right x");
            Assert.Equal(8, y, "bottom-right y");
        }

        [Test]
        public static void BottomRight_Swaps_Size_When_Rotated()
        {
            int x, y;
            PlacementMath.BottomRight(24, 10, 3, 2, true, out x, out y);
            Assert.Equal(22, x, "rotated x");
            Assert.Equal(7, y, "rotated y");
        }
    }
}
