using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class MarkingPenModeTests
    {
        [Test]
        public static void Modes_Map_To_Colors()
        {
            Assert.Equal("red", MarkingPenMode.ColorId(0), "red");
            Assert.Equal("purple", MarkingPenMode.ColorId(1), "purple");
            Assert.Equal("green", MarkingPenMode.ColorId(2), "green");
            Assert.Equal("blue", MarkingPenMode.ColorId(3), "blue");
            Assert.Equal("orange", MarkingPenMode.ColorId(4), "orange");
            Assert.Equal("eraser", MarkingPenMode.ColorId(5), "eraser");
        }

        [Test]
        public static void Invalid_Modes_Are_Rejected()
        {
            Assert.False(MarkingPenMode.IsValid(-1), "negative");
            Assert.False(MarkingPenMode.IsValid(6), "too high");
            Assert.True(MarkingPenMode.IsValid(5), "eraser valid");
        }
    }
}
