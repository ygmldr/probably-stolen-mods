using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class MoveValidatorTests
    {
        [Test]
        public static void Move_Accepts_Valid_Target()
        {
            MoveCheck check = MoveValidator.Validate(new MoveFlags
            {
                ItemExists = true,
                TargetExists = true
            });
            Assert.Equal(MoveCheck.Ok, check, "valid move");
        }

        [Test]
        public static void Move_Rejects_Missing_Item()
        {
            Assert.Equal(MoveCheck.ItemNotFound,
                MoveValidator.Validate(new MoveFlags { TargetExists = true }), "missing item");
        }

        [Test]
        public static void Move_Rejects_Missing_Target()
        {
            Assert.Equal(MoveCheck.TargetNotFound,
                MoveValidator.Validate(new MoveFlags { ItemExists = true }), "missing target");
        }

        [Test]
        public static void Move_Rejects_Locked_Item()
        {
            Assert.Equal(MoveCheck.ItemLocked,
                MoveValidator.Validate(new MoveFlags { ItemExists = true, ItemLocked = true, TargetExists = true }),
                "locked item");
        }

        [Test]
        public static void Move_Rejects_Locked_Target()
        {
            Assert.Equal(MoveCheck.TargetLocked,
                MoveValidator.Validate(new MoveFlags { ItemExists = true, TargetExists = true, TargetLocked = true }),
                "locked target");
        }

        [Test]
        public static void Move_Rejects_Same_Container()
        {
            Assert.Equal(MoveCheck.SameContainer,
                MoveValidator.Validate(new MoveFlags { ItemExists = true, TargetExists = true, SameContainer = true }),
                "same container");
        }
    }
}
