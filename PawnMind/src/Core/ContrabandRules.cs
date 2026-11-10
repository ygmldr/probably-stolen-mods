namespace PawnMind.Core
{
    public static class ContrabandRules
    {
        public static bool IsHighRisk(int contrabandLevel, bool stolen)
        {
            return stolen || contrabandLevel >= 3;
        }
    }
}
