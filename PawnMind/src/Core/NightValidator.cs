namespace PawnMind.Core
{
    public enum NightCheck
    {
        Ok,
        Unknown,
        Undiscovered,
        Unavailable,
        AlreadyOutside
    }

    public sealed class NightFlags
    {
        public bool Known;
        public bool Discovered;
        public bool Available;
        public bool Outside;
    }

    public static class NightValidator
    {
        public static NightCheck Validate(NightFlags flags)
        {
            if (flags == null || !flags.Known) return NightCheck.Unknown;
            if (!flags.Discovered) return NightCheck.Undiscovered;
            if (!flags.Available) return NightCheck.Unavailable;
            if (flags.Outside) return NightCheck.AlreadyOutside;
            return NightCheck.Ok;
        }
    }
}
