using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RecordingMenuPolicyTests
    {
        private static NewGameMenuSnapshot ScenarioSnapshot()
        {
            NewGameMenuSnapshot snapshot = new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Specialization,
                Page = 0,
                PageCount = 1,
                SelectedId = "apprentice",
                HasSelection = true,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Continue",
                    Visible = true,
                    Enabled = true
                }
            };
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "apprentice",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Apprentice"
            });
            return snapshot;
        }

        private static NewGameMenuService MenuService(
            RecordingHarness h,
            FakeNewGameMenuPort port = null,
            FakeNewGameMenuControlPort control = null)
        {
            port = port ?? new FakeNewGameMenuPort(ScenarioSnapshot());
            control = control ?? new FakeNewGameMenuControlPort();
            return new NewGameMenuService(port, control, delegate
            {
                return h.Service.CheckMenuWrite();
            });
        }

        [Test]
        public static void Menu_Default_Config_Replaces_The_Fixed_Placeholder_With_Recording_Error()
        {
            RecordingHarness h = RecordingHarness.Create(RecordingTestData.Off());
            NewGameMenuService service = MenuService(h);

            NewGameMenuActionResult result = service.Open();
            Assert.False(result.Success, "menu write denied by default");
            Assert.Equal(RecordingSaveErrors.Unconfigured, result.Error,
                "the fixed placeholder is replaced by the real policy evaluation");
        }

        [Test]
        public static void Menu_Armed_Config_Allows_Preparation_Writes_Only()
        {
            RecordingHarness h = RecordingHarness.Create();
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(
                new NewGameMenuSnapshot { Phase = NewGameMenuPhase.Closed });
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.OpenEffect = delegate
            {
                port.Snapshot.Phase = NewGameMenuPhase.Specialization;
                port.Snapshot.Page = 0;
                port.Snapshot.PageCount = 1;
            };
            NewGameMenuService service = MenuService(h, port, control);

            NewGameMenuActionResult result = service.Open();
            Assert.True(result.Success, "armed menu preparation is allowed: " + result.Error);
            Assert.Equal(1, control.OpenCalls, "one native open");
        }

        [Test]
        public static void Menu_Bound_State_Denies_Menu_Writes()
        {
            RecordingHarness h = RecordingHarness.Create();
            h.Bind();
            NewGameMenuService service = MenuService(h);

            NewGameMenuActionResult result = service.Open();
            Assert.False(result.Success, "bound state denies new-game menu writes");
            Assert.Equal(RecordingSaveErrors.AlreadyBound, result.Error, "stable bound code");
        }
    }
}
