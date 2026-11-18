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

        // ------------------------------------- R5/B2: typed overlay dismissal

        /// <summary>
        /// R5-B2: the two adapter-less overlays (control-hint tutorial overlay
        /// and the dialogue/typewriter settings dialog) used to need the
        /// test-only <c>press</c>. close_windows now dismisses them through the
        /// same typed op, before the other panels.
        /// </summary>
        [Test]
        public static void R5_WIN_01_Plan_Dismisses_Typed_Overlays_First()
        {
            var snapshot = new WindowSnapshot
            {
                TutorialOverlayOpen = true,
                DialogueSettingsOpen = true,
                PaperOpen = true
            };

            var plan = WindowRules.PlanClosures(snapshot);
            Equal(2, plan.Count, "one overlay dismissal plus the paper close");
            Equal(WindowKinds.Overlays, plan[0], "overlays are dismissed first");
            Equal(WindowKinds.Paper, plan[1], "then the paper panel");
        }

        /// <summary>
        /// R5-B2: a screen with one of those overlays is not clean, otherwise the
        /// typed dismissal could claim success while the player still cannot click.
        /// </summary>
        [Test]
        public static void R5_WIN_02_Clean_Requires_Overlays_Closed()
        {
            False(new WindowSnapshot { TutorialOverlayOpen = true }.Clean,
                "the tutorial overlay keeps the screen dirty");
            False(new WindowSnapshot { DialogueSettingsOpen = true }.Clean,
                "the dialogue settings dialog keeps the screen dirty");
            True(new WindowSnapshot().Clean, "a plain screen is clean");
        }

        [Test]
        public static void IsClean_Requires_A_Readable_Empty_Screen()
        {
            True(WindowRules.IsClean(new WindowSnapshot()), "empty readable");
            False(WindowRules.IsClean(null), "null is not clean");
        }
    }
}
