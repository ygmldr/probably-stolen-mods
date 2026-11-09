using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public static class WholesaleMath
    {
        public static int Total(IEnumerable<int> values)
        {
            int total = 0;
            if (values == null) return total;
            foreach (int value in values) total += value;
            return total;
        }

        public static int Discounted(int total, int discountPercent)
        {
            return (int)Math.Round(total * (100.0 + discountPercent) / 100.0);
        }
    }
}
