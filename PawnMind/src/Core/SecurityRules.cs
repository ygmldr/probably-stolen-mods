namespace PawnMind.Core
{
    public static class SecurityRules
    {
        public static bool IsUnderInvestigation(int evidenceLevel)
        {
            return evidenceLevel > 0;
        }
    }
}
