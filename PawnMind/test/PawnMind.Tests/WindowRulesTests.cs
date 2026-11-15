using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// close_windows planning: the bridge must close exactly what the player can
    /// see, in a stable order, and report an unreadable screen as "not clean".
    /// </summary>
    internal static class WindowRulesTests
    {
        [Test]
        public static void Plan_Is_Empty_When_Nothing_Is_Open()
        {
            var snapshot = new WindowSnapshot();

            True(snapshot.Clean, "an untouched screen is clean");
            Equal(0, WindowRules.PlanClosures(snapshot).Count, "nothing to close");
        }

        [Test]
        public static void Plan_Closes_The_Open_Panels_In_Order()
        {
            var snapshot = new WindowSnapshot
            {
                BookOpen = true,
                PaperOpen = true,
                ContractOpen = true,
                DeedOpen = true,
                TabletOpen = true,
                MovableVisible = 2
            };

            var plan = WindowRules.PlanClosures(snapshot);
            Equal(6, plan.Count, "all six closes planned");
            Equal(WindowKinds.Book, plan[0], "book first");
            Equal(WindowKinds.Paper, plan[1], "paper second");
            Equal(WindowKinds.Contract, plan[2], "contract third");
            Equal(WindowKinds.Deed, plan[3], "deed fourth");
            Equal(WindowKinds.Tablet, plan[4], "tablet fifth");
            Equal(WindowKinds.Movable, plan[5], "movable last");
        }

        [Test]
        public static void Plan_Skips_What_Is_Closed()
        {
            var plan = WindowRules.PlanClosures(
                new WindowSnapshot { PaperOpen = true, MovableVisible = 0 });

            Equal(1, plan.Count, "one close planned");
            Equal(WindowKinds.Paper, plan[0], "only paper");
        }

        [Test]
        public static void Plan_Closes_The_Tablet_Panel()
        {
            var plan = WindowRules.PlanClosures(new WindowSnapshot { TabletOpen = true });

            Equal(1, plan.Count, "tablet planned");
            Equal(WindowKinds.Tablet, plan[0], "tablet kind");
        }

        [Test]
        public static void Plan_Includes_Movable_Only_When_One_Is_Visible()
        {
            var plan = WindowRules.PlanClosures(new WindowSnapshot { MovableVisible = 1 });

            Equal(1, plan.Count, "one close planned");
            Equal(WindowKinds.Movable, plan[0], "movable only");
        }

        [Test]
        public static void Unreadable_Snapshot_Plans_Nothing()
        {
            var snapshot = new WindowSnapshot
            {
                Readable = false,
                Error = WindowErrors.Unavailable,
                PaperOpen = true
            };

            Equal(0, WindowRules.PlanClosures(snapshot).Count, "nothing to close");
            False(WindowRules.IsClean(snapshot), "unreadable is not clean");
        }

        [Test]
        public static void IsClean_Requires_A_Readable_Empty_Screen()
        {
            True(WindowRules.IsClean(new WindowSnapshot()), "empty readable");
            False(WindowRules.IsClean(null), "null is not clean");
        }
    }
}
