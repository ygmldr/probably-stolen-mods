using System;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class FakeNewGameMenuControlPort : INewGameMenuControlPort
    {
        public bool Fail;
        public int OpenCalls;
        public int PageCalls;
        public int ContinueCalls;
        public int FocusCalls;
        public int BlurCalls;

        public NewGamePageRow LastRow;
        public int LastDirection;
        public NewGameMenuPhase LastContinuePhase;
        public string LastFocusId;
        public string LastBlurId;

        public Action OpenEffect;
        public Action PageEffect;
        public Action ContinueEffect;
        public Action FocusEffect;
        public Action BlurEffect;

        public NewGameControlCallResult Open()
        {
            OpenCalls++;
            return Result(OpenEffect);
        }

        public NewGameControlCallResult TurnPage(NewGamePageRow row, int direction)
        {
            PageCalls++;
            LastRow = row;
            LastDirection = direction;
            return Result(PageEffect);
        }

        public NewGameControlCallResult Continue(NewGameMenuPhase phase)
        {
            ContinueCalls++;
            LastContinuePhase = phase;
            return Result(ContinueEffect);
        }

        public NewGameControlCallResult FocusSelectedPerk(string id)
        {
            FocusCalls++;
            LastFocusId = id;
            return Result(FocusEffect);
        }

        public NewGameControlCallResult BlurPerk(string id)
        {
            BlurCalls++;
            LastBlurId = id;
            return Result(BlurEffect);
        }

        private NewGameControlCallResult Result(Action effect)
        {
            if (Fail)
            {
                return NewGameControlCallResult.NativeNotCalled("native_error");
            }

            if (effect != null) effect();
            return NewGameControlCallResult.NativeCalled();
        }
    }

    internal static class NewGameMenuServiceTests
    {
        private const string PolicyError = "test_policy_denied";

        private static string Allow()
        {
            return null;
        }

        private static string Deny()
        {
            return PolicyError;
        }

        private static NewGameMenuService Service(
            FakeNewGameMenuPort port,
            FakeNewGameMenuControlPort control,
            System.Func<string> policy)
        {
            return new NewGameMenuService(port, control, policy);
        }

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
                SlotArg = "secret-slot-arg-99",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Apprentice",
                Description = "Start here"
            });
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "fence",
                SlotArg = "secret-slot-arg-100",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Fence",
                Description = "Start there"
            });
            return snapshot;
        }

        private static NewGameMenuSnapshot PromptSnapshot()
        {
            return new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Closed,
                Prompt = new NewGameMenuPrompt
                {
                    Id = "first_run_confirm",
                    Text = "First run prompt text",
                    ConfirmText = "Yes",
                    CancelText = "No",
                    FirstRun = true
                }
            };
        }

        [Test]
        public static void Service_GetOptions_Returns_Sanitized_View_Only()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Deny);

            NewGameMenuView view = service.GetOptions();
            string json = NewGameMenuJson.OptionsJson(view);

            Assert.Equal(1, port.ReadCalls, "read op reads the UI once");
            Assert.Equal(2, view.Options.Count, "visible options are returned");
            Assert.Contains(json, "\"ok\":true", "read response is ok");
            Assert.Contains(json, "\"id\":\"fence\"", "semantic id is present");
            Assert.False(json.Contains("secret-slot-arg"), "native slot argument must not leak");
            Assert.False(json.Contains("slotArg"), "native slot argument field must not leak");
            Assert.False(json.Contains("slot_arg"), "native slot argument field must not leak");
            Assert.False(json.Contains("preview"), "save preview must not leak");
            Assert.False(json.Contains("saveSlot"), "save slot must not leak");
        }

        [Test]
        public static void Service_GetOptions_Reports_First_Run_Prompt()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(PromptSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Deny);

            NewGameMenuView view = service.GetOptions();
            string json = NewGameMenuJson.OptionsJson(view);

            Assert.True(view.PromptPending, "prompt must be reported as pending");
            Assert.NotNull(view.Prompt, "prompt identity is exposed");
            Assert.Equal("first_run_confirm", view.Prompt.Id, "first-run prompt identity");
            Assert.Equal("First run prompt text", view.Prompt.Text, "visible prompt text");
            Assert.Contains(json, "prompt_pending", "prompt pending flag is explicit");
            Assert.Contains(json, "First run prompt text", "prompt text is visible");
            Assert.False(json.Contains("started"), "a prompt must never be reported as a start");
        }

        [Test]
        public static void Service_Select_Is_Denied_When_Slot_Policy_Is_Unavailable()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Deny);

            NewGameMenuSelectResult result =
                service.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "write must fail closed without slot policy");
            Assert.Equal(PolicyError, result.Error, "stable slot-policy error");
            Assert.Equal(0, port.SelectCalls, "no native selection callback");
        }

        [Test]
        public static void Service_Select_Delegates_To_Session_Once_When_Allowed()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuSelectResult result =
                service.Select(NewGameOptionKind.Scenario, "fence");

            Assert.True(result.Success, "allowed selection succeeds");
            Assert.Equal(1, port.SelectCalls, "one native selection callback");
            Assert.Equal("fence", result.View.SelectedId, "readback selection");
        }

        [Test]
        public static void Service_Select_Does_Not_Call_Native_While_Prompt_Pending()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(PromptSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuSelectResult result =
                service.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "prompt blocks menu selection");
            Assert.Equal("prompt_pending", result.Error, "stable prompt code");
            Assert.Equal(0, port.SelectCalls, "no native callback during a prompt");
        }

        [Test]
        public static void Service_Write_Ops_Fail_Closed_Without_Slot_Policy()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Deny);

            Assert.Equal(PolicyError, service.Open().Error, "open is gated");
            Assert.Equal(PolicyError, service.NextPage(NewGamePageRow.Available, 1).Error,
                "paging is gated");
            Assert.Equal(PolicyError, service.FocusSelectedPerk("saving").Error,
                "focus is gated");
            Assert.Equal(PolicyError, service.Continue().Error, "continue is gated");
            Assert.Equal(0, control.OpenCalls, "no native open");
            Assert.Equal(0, control.PageCalls, "no native page turn");
            Assert.Equal(0, control.FocusCalls, "no native hover");
            Assert.Equal(0, control.ContinueCalls, "no native continue");
        }

        private static NewGameCandidate Perk(string id, int page = 0, bool visible = true)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = id,
                Page = page,
                Visible = visible,
                Cost = 1,
                MaxSlot = 0,
                DisplayName = "Perk " + id,
                Description = "Desc " + id
            };
        }

        private static NewGameSelectedPerk Selected(string id, bool hovered = false)
        {
            return new NewGameSelectedPerk
            {
                Id = id,
                Cost = 1,
                DisplayName = "Perk " + id,
                Description = "Desc " + id,
                Hovered = hovered
            };
        }

        private static NewGameMenuSnapshot PerkSnapshot()
        {
            NewGameMenuSnapshot snapshot = new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Perks,
                Page = 0,
                PageCount = 2,
                SelectedPage = 0,
                MaxPerkPoints = 30,
                BasePerkCount = 40,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Continue",
                    Visible = true,
                    Enabled = true
                }
            };
            return snapshot;
        }

        private static NewGameMenuSnapshot DifficultySnapshot()
        {
            NewGameMenuSnapshot snapshot = new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Difficulty,
                Page = 0,
                PageCount = 1,
                SelectedId = "normal",
                HasSelection = true,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Start Game",
                    Visible = true,
                    Enabled = true
                }
            };
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "normal",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true
            });
            return snapshot;
        }

        [Test]
        public static void Service_Open_Calls_Native_Once_And_Reads_Back_Specialization()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Closed
            });
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.OpenEffect = delegate
            {
                port.Snapshot.Phase = NewGameMenuPhase.Specialization;
                port.Snapshot.Page = 0;
                port.Snapshot.PageCount = 1;
            };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Open();

            Assert.True(result.Success, "native open confirmed by readback");
            Assert.Equal(1, control.OpenCalls, "exactly one native open callback");
            Assert.Equal(NewGameMenuPhase.Specialization, result.View.Phase,
                "post view shows the opened menu");
        }

        [Test]
        public static void Service_Open_Does_Not_Call_Native_While_Prompt_Pending()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(PromptSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Open();

            Assert.False(result.Success, "prompt blocks opening another menu");
            Assert.Equal("prompt_pending", result.Error, "stable prompt code");
            Assert.Equal(0, control.OpenCalls, "no native callback under a prompt");
        }

        [Test]
        public static void Service_Open_Native_Failure_Is_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Closed
            });
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort { Fail = true };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Open();

            Assert.False(result.Success, "native refusal is not success");
            Assert.Equal("native_error", result.Error, "native error surfaced");
        }

        [Test]
        public static void Service_NextPage_Rejects_Bad_Direction_And_Boundaries()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult bad = service.NextPage(NewGamePageRow.Available, 2);
            Assert.Equal("bad_direction", bad.Error, "only +/-1 is accepted");

            NewGameMenuActionResult back = service.NextPage(NewGamePageRow.Available, -1);
            Assert.Equal("page_out_of_range", back.Error, "cannot page before the first page");

            Assert.Equal(0, control.PageCalls, "no native call for invalid paging");
        }

        [Test]
        public static void Service_NextPage_Available_Calls_Native_Once_And_Reads_Page()
        {
            NewGameMenuSnapshot snapshot = ScenarioSnapshot();
            snapshot.Page = 0;
            snapshot.PageCount = 2;
            snapshot.Candidates[1].Page = 1;
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.PageEffect = delegate { port.Snapshot.Page = 1; };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.NextPage(NewGamePageRow.Available, 1);

            Assert.True(result.Success, "available page moved and read back");
            Assert.Equal(1, control.PageCalls, "exactly one native page callback");
            Assert.Equal(NewGamePageRow.Available, control.LastRow, "available row passed through");
            Assert.Equal(1, result.View.Page, "post view page");
        }

        [Test]
        public static void Service_NextPage_Selected_Is_Forbidden_Outside_Perks()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.NextPage(NewGamePageRow.Selected, 1);

            Assert.Equal("wrong_row", result.Error, "only perks have a selected-row pager");
            Assert.Equal(0, control.PageCalls, "no native page callback");
        }

        [Test]
        public static void Service_NextPage_Selected_Calls_Perks_Selected_Pager()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            for (int i = 0; i < 11; i++) snapshot.SelectedPerks.Add(Selected("p" + i));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.PageEffect = delegate { port.Snapshot.SelectedPage = 1; };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.NextPage(NewGamePageRow.Selected, 1);

            Assert.True(result.Success, "selected page moved and read back");
            Assert.Equal(1, control.PageCalls, "exactly one native page callback");
            Assert.Equal(NewGamePageRow.Selected, control.LastRow, "selected row passed through");
            Assert.Equal(1, result.View.SelectedPage, "post view selected page");
        }

        [Test]
        public static void Service_Continue_Difficulty_Start_Is_Forbidden()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(DifficultySnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Continue();

            Assert.False(result.Success, "difficulty Continue starts the game and is forbidden");
            Assert.Equal("difficulty_start_forbidden", result.Error, "stable refusal code");
            Assert.Equal(0, control.ContinueCalls, "native Start is never called");
        }

        [Test]
        public static void Service_Continue_Scenario_To_Perks_Succeeds()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.ContinueEffect = delegate
            {
                NewGameMenuSnapshot snap = port.Snapshot;
                snap.Phase = NewGameMenuPhase.Perks;
                snap.Page = 0;
                snap.PageCount = 1;
                snap.SelectedPage = 0;
                snap.MaxPerkPoints = 3;
                snap.BasePerkCount = 4;
            };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Continue();

            Assert.True(result.Success, "scenario Continue reached perks");
            Assert.Equal(1, control.ContinueCalls, "exactly one native Continue callback");
            Assert.Equal(NewGameMenuPhase.Specialization, control.LastContinuePhase,
                "native scenario callback");
            Assert.Equal(NewGameMenuPhase.Perks, result.View.Phase, "post view phase");
        }

        [Test]
        public static void Service_Continue_Scenario_First_Run_Prompt_Is_Pending_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.ContinueEffect = delegate
            {
                NewGameMenuSnapshot snap = port.Snapshot;
                snap.Phase = NewGameMenuPhase.Closed;
                snap.Prompt = new NewGameMenuPrompt
                {
                    Id = "first_run_confirm",
                    Text = "First run prompt text",
                    FirstRun = true
                };
            };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Continue();

            Assert.False(result.Success, "a first-run prompt is not progress");
            Assert.Equal("prompt_pending", result.Error, "stable prompt code");
            Assert.True(result.PromptPending, "prompt pending flag");
            Assert.Equal("first_run_confirm", result.View.Prompt.Id, "prompt identity");
            Assert.Equal(1, control.ContinueCalls, "the native scenario callback ran once");
        }

        [Test]
        public static void Service_Continue_Perks_To_Difficulty_Succeeds()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(Selected("saving"));
            snapshot.Candidates.Add(Perk("saving"));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.ContinueEffect = delegate
            {
                NewGameMenuSnapshot snap = port.Snapshot;
                snap.Phase = NewGameMenuPhase.Difficulty;
                snap.Page = 0;
                snap.PageCount = 1;
                snap.SelectedId = "normal";
                snap.HasSelection = true;
            };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Continue();

            Assert.True(result.Success, "perks Continue reached difficulty");
            Assert.Equal(1, control.ContinueCalls, "exactly one native Continue callback");
            Assert.Equal(NewGameMenuPhase.Perks, control.LastContinuePhase,
                "native perks callback");
            Assert.Equal(NewGameMenuPhase.Difficulty, result.View.Phase, "post view phase");
        }

        [Test]
        public static void Service_Continue_Disabled_Button_Does_Not_Call_Native()
        {
            NewGameMenuSnapshot snapshot = ScenarioSnapshot();
            snapshot.Continue.Enabled = false;
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.Continue();

            Assert.Equal("continue_unavailable", result.Error, "disabled Continue is refused");
            Assert.Equal(0, control.ContinueCalls, "no native callback");
        }

        [Test]
        public static void Service_Focus_Refuses_When_Available_Same_Id_Is_Hovered()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(Selected("saving"));
            NewGameCandidate available = Perk("saving");
            available.Hovered = true;
            snapshot.Candidates.Add(available);
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.FocusSelectedPerk("saving");

            Assert.Equal("hover_conflict", result.Error,
                "stale available hover must fail closed before the selected row");
            Assert.Equal(0, control.FocusCalls, "no native hover while the conflict exists");
        }

        [Test]
        public static void Service_Focus_Calls_Native_Once_And_Reads_Back_Hover()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(Selected("saving"));
            snapshot.Candidates.Add(Perk("saving"));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.FocusEffect = delegate { port.Snapshot.SelectedPerks[0].Hovered = true; };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.FocusSelectedPerk("saving");

            Assert.True(result.Success, "selected-row hover confirmed by readback");
            Assert.Equal(1, control.FocusCalls, "exactly one native hover callback");
            Assert.Equal("saving", control.LastFocusId, "native hover target");
            Assert.True(result.View.SelectedOptions[0].Hovered, "selected row is hovered");
            Assert.Equal("saving", result.View.HoveredId, "hovered id exposed");
            Assert.True(result.View.HoveredInSelectedRow, "hovered row exposed");
        }

        [Test]
        public static void Service_Focus_Requires_Visible_Selected_Row()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            for (int i = 0; i < 11; i++) snapshot.SelectedPerks.Add(Selected("p" + i));
            snapshot.Candidates.Add(Perk("p0"));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.FocusSelectedPerk("p10");

            Assert.Equal("selected_row_not_visible", result.Error,
                "focus must target the visible selected page");
            Assert.Equal(0, control.FocusCalls, "no native hover for an off-page card");
        }

        [Test]
        public static void Service_Blur_Calls_Native_Once_And_Reads_Back()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            NewGameCandidate available = Perk("saving");
            available.Hovered = true;
            snapshot.Candidates.Add(available);
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.BlurEffect = delegate { port.Snapshot.Candidates[0].Hovered = false; };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.BlurOption("saving");

            Assert.True(result.Success, "hover cleared and read back");
            Assert.Equal(1, control.BlurCalls, "exactly one native exit callback");
            Assert.Equal("saving", control.LastBlurId, "native blur target");
            Assert.False(result.View.Options[0].Hovered, "post view hover is cleared");
        }

        [Test]
        public static void Service_Blur_Not_Hovered_Does_Not_Call_Native()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.Candidates.Add(Perk("saving"));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult result = service.BlurOption("saving");

            Assert.Equal("not_hovered", result.Error, "nothing to blur");
            Assert.Equal(0, control.BlurCalls, "no native callback");
        }

        [Test]
        public static void Service_Two_Step_Focus_Then_Select_Deselects_Selected_Row()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(Selected("saving"));
            snapshot.Candidates.Add(Perk("saving"));
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            control.FocusEffect = delegate { port.Snapshot.SelectedPerks[0].Hovered = true; };
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuActionResult focus = service.FocusSelectedPerk("saving");
            NewGameMenuSelectResult select =
                service.Select(NewGameOptionKind.Perk, "saving");

            Assert.True(focus.Success, "step one focuses the selected row");
            Assert.True(select.Success, "step two uses the hovered selected row");
            Assert.Equal(NewGameSelectionAction.Remove, select.Action, "removal, not add");
            Assert.Equal(1, control.FocusCalls, "one native hover callback");
            Assert.Equal(1, port.SelectCalls, "one native click callback");
            Assert.Equal(0, select.View.Perks.SelectedCount, "perk was removed");
        }

        [Test]
        public static void Service_Select_Json_Does_Not_Leak_Slot_Arguments()
        {
            NewGameMenuSnapshot snapshot = ScenarioSnapshot();
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            FakeNewGameMenuControlPort control = new FakeNewGameMenuControlPort();
            NewGameMenuService service = Service(port, control, Allow);

            NewGameMenuSelectResult result =
                service.Select(NewGameOptionKind.Scenario, "fence");
            string json = NewGameMenuJson.SelectJson(result);

            Assert.True(result.Success, "selection succeeds");
            Assert.Contains(json, "\"action\":\"set\"", "selection action is visible");
            Assert.False(json.Contains("secret-slot-arg"), "slot argument must not leak");
            Assert.False(json.Contains("slot_arg"), "slot argument field must not leak");
            Assert.False(json.Contains("saveSlot"), "save slot must not leak");
        }
    }
}
