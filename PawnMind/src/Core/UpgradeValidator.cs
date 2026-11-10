namespace PawnMind.Core
{
    public enum UpgradeCheck
    {
        Ok,
        NotFound,
        AlreadyUnlocked,
        Prerequisite,
        NoCash
    }

    public static class UpgradeValidator
    {
        public static UpgradeCheck Validate(bool exists, bool unlocked, bool prerequisite, bool enoughCash)
        {
            if (!exists) return UpgradeCheck.NotFound;
            if (unlocked) return UpgradeCheck.AlreadyUnlocked;
            if (!prerequisite) return UpgradeCheck.Prerequisite;
            if (!enoughCash) return UpgradeCheck.NoCash;
            return UpgradeCheck.Ok;
        }
    }
}
