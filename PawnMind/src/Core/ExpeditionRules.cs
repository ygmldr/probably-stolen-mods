namespace PawnMind.Core
{
    public static class ExpeditionRules
    {
        public static bool CanDeploy(bool unlocked, bool hasHireling, bool injured)
        {
            return unlocked && hasHireling && !injured;
        }
    }
}
