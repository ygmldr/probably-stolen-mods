using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class WholesaleMathTests
    {
        [Test]
        public static void Total_Sums_Values()
        {
            Assert.Equal(30, WholesaleMath.Total(new List<int> { 10, 20 }), "sum");
        }

        [Test]
        public static void Discounted_Applies_Negative_Discount()
        {
            Assert.Equal(26, WholesaleMath.Discounted(30, -15), "15% off");
            Assert.Equal(30, WholesaleMath.Discounted(30, 0), "no discount");
        }
    }
}
