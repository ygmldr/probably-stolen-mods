namespace PawnMind.Core
{
    public static class WineMath
    {
        public const int MinimumWaterMl = 749;

        public static bool CanFerment(int waterMl, bool hasIngredient)
        {
            return waterMl >= MinimumWaterMl && hasIngredient;
        }
    }
}
