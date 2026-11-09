using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class BargainMathTests
    {
        [Test]
        public static void Price_Markup_Raises_Value()
        {
            Assert.Equal(110, BargainMath.AdjustPrice(100, true, 10), "10% markup");
            Assert.Equal(125, BargainMath.AdjustPrice(100, true, 25), "25% markup");
        }

        [Test]
        public static void Price_Discount_Lowers_Value()
        {
            Assert.Equal(90, BargainMath.AdjustPrice(100, false, 10), "10% discount");
            Assert.Equal(75, BargainMath.AdjustPrice(100, false, 25), "25% discount");
        }

        [Test]
        public static void Reputation_Multipliers_Match_Game_Table()
        {
            Assert.Equal(1.65, BargainMath.ReputationMultiplier(true, 10), "friendly 10");
            Assert.Equal(2.2, BargainMath.ReputationMultiplier(true, 25), "friendly 25");
            Assert.Equal(-3.0, BargainMath.ReputationMultiplier(false, 50), "demand 50");
            Assert.Equal(-5.0, BargainMath.ReputationMultiplier(false, 75), "demand 75");
        }

        [Test]
        public static void Reputation_Per_1000_Uses_Multiplier_And_Factor()
        {
            double value = BargainMath.ReputationPer1000(2.2, 0.66);
            Assert.Equal(3.63, System.Math.Round(value, 2), "per 1000");
        }

        [Test]
        public static void Reputation_Delta_Uses_Faction_Factor()
        {
            // 1000 * 2.2 * 0.66 / 400 = 3.63 -> 4
            Assert.Equal(4, BargainMath.ReputationDelta(1000, true, 25, 0.66), "lower level");
        }
    }
}
