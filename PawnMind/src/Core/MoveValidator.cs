namespace PawnMind.Core
{
    public enum MoveCheck
    {
        Ok,
        ItemNotFound,
        ItemLocked,
        TargetNotFound,
        TargetLocked,
        SameContainer
    }

    public sealed class MoveFlags
    {
        public bool ItemExists;
        public bool ItemLocked;
        public bool TargetExists;
        public bool TargetLocked;
        public bool SameContainer;
    }

    public static class MoveValidator
    {
        public static MoveCheck Validate(MoveFlags flags)
        {
            if (flags == null) flags = new MoveFlags();
            if (!flags.ItemExists) return MoveCheck.ItemNotFound;
            if (!flags.TargetExists) return MoveCheck.TargetNotFound;
            if (flags.ItemLocked) return MoveCheck.ItemLocked;
            if (flags.TargetLocked) return MoveCheck.TargetLocked;
            if (flags.SameContainer) return MoveCheck.SameContainer;
            return MoveCheck.Ok;
        }
    }
}
