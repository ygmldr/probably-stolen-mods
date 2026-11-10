namespace PawnMind.Core
{
    public enum ServiceCheck
    {
        Ok,
        NotFound,
        Locked,
        Mandatory,
        Cooldown
    }

    public static class ServiceValidator
    {
        public static ServiceCheck Validate(bool exists, bool unlocked, bool mandatory, bool cooldown)
        {
            if (!exists) return ServiceCheck.NotFound;
            if (!unlocked) return ServiceCheck.Locked;
            if (mandatory) return ServiceCheck.Mandatory;
            if (cooldown) return ServiceCheck.Cooldown;
            return ServiceCheck.Ok;
        }
    }
}
