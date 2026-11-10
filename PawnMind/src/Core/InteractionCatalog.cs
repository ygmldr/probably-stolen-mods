using System.Collections.Generic;

namespace PawnMind.Core
{
    public enum ItemInteraction
    {
        Activate,
        Toggle,
        Target
    }

    public sealed class InteractionFlags
    {
        public bool Owned;
        public bool CanActivate;
        public bool CanToggle;
        public bool CanTarget;
    }

    public static class InteractionCatalog
    {
        public static List<ItemInteraction> Available(InteractionFlags flags)
        {
            var actions = new List<ItemInteraction>();
            if (flags == null || !flags.Owned) return actions;

            if (flags.CanActivate) actions.Add(ItemInteraction.Activate);
            if (flags.CanToggle) actions.Add(ItemInteraction.Toggle);
            if (flags.CanTarget) actions.Add(ItemInteraction.Target);
            return actions;
        }
    }
}
