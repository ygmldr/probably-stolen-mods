namespace PawnMind.Core
{
    public static class HealthRules
    {
        public const int SeriousWoundThreshold = 5;

        public static bool IsSeriouslyWounded(int woundState)
        {
            return woundState >= SeriousWoundThreshold;
        }
    }
}
