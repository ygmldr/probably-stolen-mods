using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class MachineProgressTests
    {
        [Test]
        public static void Percent_Computes_And_Clamps()
        {
            Assert.Equal(0, MachineProgress.Percent(0, 10), "zero");
            Assert.Equal(50, MachineProgress.Percent(5, 10), "half");
            Assert.Equal(100, MachineProgress.Percent(12, 10), "clamped");
            Assert.Equal(0, MachineProgress.Percent(1, 0), "zero target");
        }
    }
}
