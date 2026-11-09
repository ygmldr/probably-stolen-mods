using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Price and reputation preview math from the game's bargaining table.
    /// Estimates only; exact settlement still goes through the game.
    /// </summary>
    public static class BargainMath
    {
        public static int AdjustPrice(int value, bool markup, int percent)
        {
            if (percent < 0) percent = 0;
            double factor = 1.0 + (markup ? 1.0 : -1.0) * percent / 100.0;
            return (int)Math.Round(value * factor);
        }

        public static double ReputationMultiplier(bool friendly, int percent)
        {
            if (friendly)
            {
                if (percent <= 10) return 1.65;
                if (percent <= 25) return 2.2;
                if (percent <= 50) return 3.0;
                return 3.9;
            }

            if (percent <= 10) return -0.5;
            if (percent <= 25) return -1.5;
            if (percent <= 50) return -3.0;
            return -5.0;
        }

        public static double ReputationPer1000(double multiplier, double factionFactor)
        {
            return multiplier * 2.5 * factionFactor;
        }

        public static int ReputationDelta(int preBargainValue, bool friendly, int percent, double factionFactor)
        {
            if (preBargainValue == 0) return 0;
            double value = preBargainValue * ReputationMultiplier(friendly, percent) * factionFactor / 400.0;
            return (int)Math.Round(value);
        }
    }
}
