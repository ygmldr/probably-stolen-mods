namespace PawnMind.Core
{
    /// <summary>The facts a move_item decision depends on.</summary>
    public sealed class MoveItemInput
    {
        public bool HasX;
        public bool HasY;
        public int Turns;

        public bool ItemFound;
        public bool ContainerFound;
        public string ContainerId = string.Empty;
        public bool ContainerReachable;

        public bool Pinned;
        public bool ContainerLocked;
        public bool SameContainer;
    }

    /// <summary>
    /// R5.3/A1 — the player-equivalence gate for move_item.
    ///
    /// The old guard was `specific = x &gt;= 0 &amp;&amp; y &gt;= 0`, so a half-specified
    /// request (x=-1, y=999) silently fell through to automatic placement. The
    /// container was never checked for player reachability either: any inventory the
    /// bridge could find (sold, machine interiors, the night satchel) was a legal
    /// target.
    ///
    /// A refusal here is decided *before* anything is dispatched, so a refused move
    /// has no side effects.
    /// </summary>
    public static class MoveItemPolicy
    {
        public const string NotInGame = "not_in_game";
        public const string ItemNotFound = "item_not_found";
        public const string UnknownContainer = "unknown_container";
        public const string UnreachableContainer = "container_not_reachable";
        public const string ItemLocked = "item_locked";
        public const string ContainerLocked = "container_locked";
        public const string BadRotation = "bad_rotation";
        public const string PartialCoordinates = "partial_coordinates";
        public const string SameContainerNeedsCoordinates = "same_container_needs_coordinates";

        /// <summary>Null when the request may proceed; otherwise a stable error code.</summary>
        public static string Validate(MoveItemInput input)
        {
            if (input == null) return NotInGame;
            if (input.Turns < 0 || input.Turns > 3) return BadRotation;
            if (!input.ItemFound) return ItemNotFound;
            if (!input.ContainerFound) return UnknownContainer;

            // Everything the player cannot touch is refused, whatever its name.
            if (!input.ContainerReachable) return UnreachableContainer;

            if (input.Pinned) return ItemLocked;
            if (input.ContainerLocked) return ContainerLocked;

            // One coordinate is not a placement: accepting it would turn a stray
            // argument into an automatic move the caller never asked for.
            if (input.HasX != input.HasY) return PartialCoordinates;

            if (!input.HasX && input.SameContainer) return SameContainerNeedsCoordinates;

            return null;
        }
    }
}
