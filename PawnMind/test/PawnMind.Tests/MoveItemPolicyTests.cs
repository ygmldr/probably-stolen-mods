using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5.3/A1 (player-legality audit 2026-11-19): move_item must refuse anything a
    /// player could not do, and the refusal has to be a decision, not a side effect
    /// discovered afterwards.
    /// </summary>
    internal static class MoveItemPolicyTests
    {
        private static MoveItemInput Valid()
        {
            return new MoveItemInput
            {
                HasX = true,
                HasY = true,
                Turns = 0,
                ItemFound = true,
                ContainerFound = true,
                ContainerId = "back",
                ContainerReachable = true,
                SourceContainerId = "main",
                SourceReachable = true,
                SameContainer = false
            };
        }

        [Test]
        public static void Complete_Request_Has_No_Refusal()
        {
            Assert.Null(MoveItemPolicy.Validate(Valid()), "a fully specified reachable move is fine");
        }

        /// <summary>
        /// The old guard was `specific = x >= 0 && y >= 0`, so x=-1,y=999 silently
        /// became an automatic placement instead of an error.
        /// </summary>
        [Test]
        public static void Half_Given_Coordinates_Are_Refused()
        {
            MoveItemInput input = Valid();
            input.HasX = true;
            input.HasY = false;

            Assert.Equal(MoveItemPolicy.PartialCoordinates, MoveItemPolicy.Validate(input),
                "one coordinate is not a placement");

            input = Valid();
            input.HasX = false;
            input.HasY = true;
            Assert.Equal(MoveItemPolicy.PartialCoordinates, MoveItemPolicy.Validate(input),
                "the other half must be refused too");
        }

        [Test]
        public static void Both_Coordinates_Omitted_Is_Auto_Placement()
        {
            MoveItemInput input = Valid();
            input.HasX = false;
            input.HasY = false;

            Assert.Null(MoveItemPolicy.Validate(input), "auto placement is still allowed");
        }

        [Test]
        public static void Unreachable_Container_Is_Refused()
        {
            foreach (string container in new[] { "sold", "afterhour", "vending", "fountain", "hireling" })
            {
                MoveItemInput input = Valid();
                input.ContainerId = container;
                input.ContainerReachable = StagingContainers.IsPlayerReachable(container);

                Assert.Equal(MoveItemPolicy.UnreachableContainer,
                    MoveItemPolicy.Validate(input),
                    "the player cannot touch " + container + " from the counter");
            }
        }

        [Test]
        public static void Player_Visible_Containers_Are_Reachable()
        {
            foreach (string container in new[] { "main", "front", "back", "showcase", "hidden", "trash" })
            {
                MoveItemInput input = Valid();
                input.ContainerId = container;
                input.ContainerReachable = StagingContainers.IsPlayerReachable(container);

                Assert.Null(MoveItemPolicy.Validate(input), container + " is reachable");
            }
        }

        /// <summary>
        /// R5.4: the source container was never checked. FindItemAnywhere can return
        /// an item that lives in `sold` or a machine interior — places the player
        /// cannot drag from right now — and Expel would happily move it out.
        /// </summary>
        [Test]
        public static void Unreachable_Source_Container_Is_Refused()
        {
            foreach (string container in new[] { "sold", "afterhour", "vending", "fountain", "hireling" })
            {
                MoveItemInput input = Valid();
                input.SourceContainerId = container;
                input.SourceReachable = StagingContainers.IsPlayerReachable(container);

                Assert.Equal(MoveItemPolicy.SourceNotReachable,
                    MoveItemPolicy.Validate(input),
                    "cannot drag out of " + container);
            }
        }

        [Test]
        public static void Unknown_Source_Container_Is_Refused()
        {
            MoveItemInput input = Valid();
            input.SourceContainerId = string.Empty;
            input.SourceReachable = false;

            Assert.Equal(MoveItemPolicy.SourceNotReachable, MoveItemPolicy.Validate(input),
                "an unnameable source is not a player-reachable source");
        }

        [Test]
        public static void Reachable_Source_Is_Fine()
        {
            MoveItemInput input = Valid();
            input.SourceContainerId = "front";
            input.SourceReachable = StagingContainers.IsPlayerReachable("front");

            Assert.Null(MoveItemPolicy.Validate(input), "front is reachable");
        }

        [Test]
        public static void Pinned_And_Locked_Are_Refused()
        {
            MoveItemInput pinned = Valid();
            pinned.Pinned = true;
            Assert.Equal(MoveItemPolicy.ItemLocked, MoveItemPolicy.Validate(pinned), "pinned item");

            MoveItemInput locked = Valid();
            locked.ContainerLocked = true;
            Assert.Equal(MoveItemPolicy.ContainerLocked, MoveItemPolicy.Validate(locked), "locked container");
        }

        [Test]
        public static void Bad_Rotation_Is_Refused()
        {
            MoveItemInput input = Valid();
            input.Turns = 4;

            Assert.Equal(MoveItemPolicy.BadRotation, MoveItemPolicy.Validate(input), "0..3 only");
        }

        [Test]
        public static void Same_Container_Without_Coordinates_Is_Refused()
        {
            MoveItemInput input = Valid();
            input.SameContainer = true;
            input.HasX = false;
            input.HasY = false;

            Assert.Equal(MoveItemPolicy.SameContainerNeedsCoordinates,
                MoveItemPolicy.Validate(input),
                "an in-place move needs a destination");
        }
    }
}
