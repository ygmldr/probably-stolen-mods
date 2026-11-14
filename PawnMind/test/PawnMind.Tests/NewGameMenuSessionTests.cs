using System.Collections.Generic;
using System.Reflection;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Fake native menu port. It mirrors what the real adapter is allowed to do:
    /// read the current UI state, and apply exactly one native selection callback.
    /// It never continues, confirms or starts a game.
    /// </summary>
    internal sealed class FakeNewGameMenuPort : INewGameMenuPort
    {
        private NewGameMenuSnapshot master;

        public FakeNewGameMenuPort(NewGameMenuSnapshot snapshot)
        {
            master = snapshot;
        }

        public NewGameMenuSnapshot Snapshot
        {
            get { return master; }
        }

        public bool ApplyNative = true;
        public bool WriteConfirmedOnly;
        public bool CloseOnApply;
        public bool FailApply;
        public string ConfirmedScenarioId = "apprentice";
        public int ReadCalls;
        public int SelectCalls;

        public NewGameMenuSnapshot Read()
        {
            ReadCalls++;
            return Copy(master);
        }

        public NewGameSelectCallResult ApplySelection(NewGameSelectionDecision decision)
        {
            SelectCalls++;
            if (FailApply)
            {
                return NewGameSelectCallResult.NativeNotCalled("native_not_called");
            }

            if (CloseOnApply)
            {
                master = new NewGameMenuSnapshot { Phase = NewGameMenuPhase.Closed };
                return NewGameSelectCallResult.NativeCalled();
            }

            if (!ApplyNative)
            {
                return NewGameSelectCallResult.NativeCalled();
            }

            if (decision.Action == NewGameSelectionAction.Set)
            {
                if (master.Phase == NewGameMenuPhase.Specialization)
                {
                    // The real scenario callback only moves the UI's pending selection;
                    // NewGameData is written later by the native Continue path.
                    if (WriteConfirmedOnly)
                    {
                        ConfirmedScenarioId = decision.Target.Id;
                    }
                    else
                    {
                        master.SelectedId = decision.Target.Id;
                        master.HasSelection = true;
                    }
                }
                else
                {
                    master.SelectedId = decision.Target.Id;
                    master.HasSelection = true;
                }
            }
            else if (decision.Action == NewGameSelectionAction.Add)
            {
                NewGameSelectedPerk selected = new NewGameSelectedPerk
                {
                    Id = decision.Target.Id,
                    DisplayName = decision.Target.DisplayName,
                    Description = decision.Target.Description,
                    Cost = decision.Target.Cost,
                    MaxSlot = decision.Target.MaxSlot
                };
                if (decision.Target.IncompatibleIds != null)
                {
                    selected.IncompatibleIds.AddRange(decision.Target.IncompatibleIds);
                }
                master.SelectedPerks.Add(selected);
            }
            else if (decision.Action == NewGameSelectionAction.Remove)
            {
                master.SelectedPerks.RemoveAll(p => p.Id == decision.Target.Id);
            }

            return NewGameSelectCallResult.NativeCalled();
        }

        private static NewGameMenuSnapshot Copy(NewGameMenuSnapshot source)
        {
            NewGameMenuSnapshot copy = new NewGameMenuSnapshot
            {
                Phase = source.Phase,
                PhaseError = source.PhaseError,
                Header = source.Header,
                FirstRun = source.FirstRun,
                StartingGame = source.StartingGame,
                Page = source.Page,
                PageCount = source.PageCount,
                SelectedPage = source.SelectedPage,
                MaxPerkPoints = source.MaxPerkPoints,
                BasePerkCount = source.BasePerkCount,
                PointsText = source.PointsText,
                CountText = source.CountText,
                PerkInstruction = source.PerkInstruction,
                SelectedId = source.SelectedId,
                HasSelection = source.HasSelection,
                Detail = CopyDetail(source.Detail),
                Prompt = CopyPrompt(source.Prompt),
                Continue = CopyButton(source.Continue)
            };

            if (source.Candidates != null)
            {
                foreach (NewGameCandidate candidate in source.Candidates)
                {
                    copy.Candidates.Add(CopyCandidate(candidate));
                }
            }

            if (source.SelectedPerks != null)
            {
                foreach (NewGameSelectedPerk perk in source.SelectedPerks)
                {
                    copy.SelectedPerks.Add(CopySelectedPerk(perk));
                }
            }

            if (source.Toggles != null)
            {
                foreach (NewGameMenuToggleState toggle in source.Toggles)
                {
                    copy.Toggles.Add(CopyToggle(toggle));
                }
            }

            return copy;
        }

        private static NewGameCandidate CopyCandidate(NewGameCandidate source)
        {
            NewGameCandidate copy = new NewGameCandidate
            {
                Id = source.Id,
                SlotArg = source.SlotArg,
                Page = source.Page,
                Visible = source.Visible,
                Hidden = source.Hidden,
                Enabled = source.Enabled,
                LockReason = source.LockReason,
                DisplayName = source.DisplayName,
                Description = source.Description,
                Tag = source.Tag,
                Cost = source.Cost,
                MaxSlot = source.MaxSlot,
                Hovered = source.Hovered
            };
            if (source.Lines != null) copy.Lines.AddRange(source.Lines);
            if (source.IncompatibleIds != null) copy.IncompatibleIds.AddRange(source.IncompatibleIds);
            return copy;
        }

        private static NewGameSelectedPerk CopySelectedPerk(NewGameSelectedPerk source)
        {
            NewGameSelectedPerk copy = new NewGameSelectedPerk
            {
                Id = source.Id,
                DisplayName = source.DisplayName,
                Description = source.Description,
                Cost = source.Cost,
                MaxSlot = source.MaxSlot,
                Hovered = source.Hovered
            };
            if (source.IncompatibleIds != null) copy.IncompatibleIds.AddRange(source.IncompatibleIds);
            return copy;
        }

        private static NewGameMenuPrompt CopyPrompt(NewGameMenuPrompt source)
        {
            if (source == null) return null;
            return new NewGameMenuPrompt
            {
                Id = source.Id,
                Text = source.Text,
                ConfirmText = source.ConfirmText,
                CancelText = source.CancelText,
                FirstRun = source.FirstRun
            };
        }

        private static NewGameMenuDetail CopyDetail(NewGameMenuDetail source)
        {
            if (source == null) return null;
            NewGameMenuDetail copy = new NewGameMenuDetail
            {
                Title = source.Title,
                Tag = source.Tag,
                Description = source.Description,
                Warning = source.Warning
            };
            if (source.Lines != null) copy.Lines.AddRange(source.Lines);
            return copy;
        }

        private static NewGameMenuToggleState CopyToggle(NewGameMenuToggleState source)
        {
            return new NewGameMenuToggleState
            {
                Id = source.Id,
                Label = source.Label,
                Visible = source.Visible,
                Selected = source.Selected,
                Enabled = source.Enabled,
                LockReason = source.LockReason
            };
        }

        private static NewGameMenuButtonState CopyButton(NewGameMenuButtonState source)
        {
            if (source == null) return new NewGameMenuButtonState();
            return new NewGameMenuButtonState
            {
                Label = source.Label,
                Visible = source.Visible,
                Enabled = source.Enabled
            };
        }
    }

    internal static class NewGameMenuSessionTests
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
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0));
            snapshot.Candidates.Add(ScenarioCandidate("fence", 1));
            return snapshot;
        }

        private static NewGameCandidate ScenarioCandidate(string id, int index, bool visible = true)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = index.ToString(),
                Page = index / 10,
                Visible = visible,
                Enabled = true,
                DisplayName = "Title " + id,
                Description = "Desc " + id
            };
        }

        private static NewGameMenuSnapshot PerkSnapshot()
        {
            NewGameMenuSnapshot snapshot = new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Perks,
                Page = 0,
                PageCount = 1,
                SelectedPage = 0,
                MaxPerkPoints = 3,
                BasePerkCount = 4,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Continue",
                    Visible = true,
                    Enabled = false
                }
            };
            return snapshot;
        }

        private static NewGameCandidate Perk(string id, int cost, int maxSlot = 0)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = id,
                Page = 0,
                Visible = true,
                Cost = cost,
                MaxSlot = maxSlot,
                DisplayName = "Perk " + id,
                Description = "Perk desc " + id
            };
        }

        private static NewGameSelectedPerk SelectedPerk(string id, int cost, int maxSlot = 0)
        {
            return new NewGameSelectedPerk
            {
                Id = id,
                DisplayName = "Perk " + id,
                Description = "Perk desc " + id,
                Cost = cost,
                MaxSlot = maxSlot
            };
        }

        private static bool Contains(List<NewGameOptionView> options, string id)
        {
            foreach (NewGameOptionView option in options)
            {
                if (option.Id == id) return true;
            }
            return false;
        }

        [Test]
        public static void Session_Observe_Lists_Only_Visible_Unhidden_Options()
        {
            NewGameMenuSnapshot snapshot = ScenarioSnapshot();
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "secret",
                SlotArg = "9",
                Page = 0,
                Visible = false,
                Hidden = true,
                Enabled = true,
                DisplayName = "Secret"
            });
            snapshot.Candidates.Add(ScenarioCandidate("off_page", 10, false));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuView view = session.Observe();

            Assert.Equal(1, port.ReadCalls, "observe reads the UI exactly once");
            Assert.Equal(2, view.Options.Count, "only the two visible scenarios are listed");
            Assert.False(Contains(view.Options, "secret"), "hidden scenario must not leak");
            Assert.False(Contains(view.Options, "off_page"), "off-page scenario must not leak");
        }

        [Test]
        public static void Session_Other_Page_Visible_Flag_Does_Not_Call_Native()
        {
            NewGameMenuSnapshot snapshot = ScenarioSnapshot();
            snapshot.Page = 1;
            snapshot.PageCount = 2;
            snapshot.Candidates.Clear();
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "stale",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Stale"
            });

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "stale");

            Assert.False(result.Success, "off-page Visible=true snapshot must fail closed");
            Assert.Equal("option_not_on_page", result.Error, "page is part of visibility");
            Assert.Equal(0, port.SelectCalls, "no native callback for a stale page flag");
        }

        [Test]
        public static void Session_Select_Scenario_Calls_Native_Once_And_Reads_Back()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "fence");

            Assert.True(result.Success, "native selection was confirmed");
            Assert.Equal(1, port.SelectCalls, "exactly one native callback");
            Assert.Equal(2, port.ReadCalls, "one read before and one after");
            Assert.Equal(NewGameReadbackStatus.Confirmed, result.Readback.Status, "readback confirmed");
            Assert.Equal("fence", result.View.SelectedId, "post view shows the pending selection");
            Assert.Equal("fence", port.Snapshot.SelectedId, "native pending selection moved");
            Assert.Equal("apprentice", port.ConfirmedScenarioId,
                "scenario pending selection must not be reported as a NewGameData write");
        }

        [Test]
        public static void Session_Scenario_Noop_Native_Call_Is_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            port.ApplyNative = false;
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "unchanged UI must not be reported as success");
            Assert.Equal("readback_mismatch", result.Error, "stable mismatch code");
            Assert.Equal("apprentice", result.View.SelectedId, "post view shows the real state");
            Assert.Equal(1, port.SelectCalls, "the callback was still invoked once");
        }

        [Test]
        public static void Session_Scenario_Data_Only_Write_Is_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            port.WriteConfirmedOnly = true;
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "writing NewGameData is not proof the UI selected it");
            Assert.Equal("readback_mismatch", result.Error, "stable mismatch code");
            Assert.Equal("fence", port.ConfirmedScenarioId, "the fake did write the data");
            Assert.Equal("apprentice", result.View.SelectedId, "UI pending state stayed put");
        }

        [Test]
        public static void Session_Select_Difficulty_Reads_Back_Selected_Plate()
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
                SlotArg = "1",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Normal"
            });
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "hard",
                SlotArg = "2",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Hard"
            });

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Difficulty, "hard");

            Assert.True(result.Success, "difficulty selection confirmed");
            Assert.Equal(NewGameSelectionAction.Set, result.Action, "difficulty is a set");
            Assert.Equal("hard", result.View.SelectedId, "post view shows the selected plate");
            Assert.Equal(1, port.SelectCalls, "exactly one native callback");
        }

        [Test]
        public static void Session_Select_Perk_Adds_And_Reads_Back_Totals()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.Candidates.Add(Perk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.True(result.Success, "perk add confirmed");
            Assert.Equal(NewGameSelectionAction.Add, result.Action, "unselected perk is added");
            Assert.Equal(1, port.SelectCalls, "exactly one native callback");
            Assert.NotNull(result.View.Perks, "post view has perk state");
            Assert.Equal(1, result.View.Perks.UsedPoints, "readback points");
            Assert.Equal(1, result.View.Perks.SelectedCount, "readback count");
            Assert.Equal(1, result.View.SelectedOptions.Count, "selected row shows the perk");
            Assert.Equal("saving", result.View.SelectedOptions[0].Id, "selected row id");
        }

        [Test]
        public static void Session_Deselect_Perk_When_Available_Card_Is_Off_Page()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.True(result.Success, "selected-row-only target deselects");
            Assert.Equal(NewGameSelectionAction.Remove, result.Action, "action is remove");
            Assert.Equal(0, port.Snapshot.SelectedPerks.Count, "native selection cleared");
            Assert.Equal(0, result.View.Perks.SelectedCount, "readback count");
        }

        [Test]
        public static void Session_Deselect_Perk_When_Selected_Row_Is_Hovered_Resolves_Same_Id_Card()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.SelectedPerks[0].Hovered = true;
            snapshot.Candidates.Add(Perk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.True(result.Success, "hovered selected row is the native click target");
            Assert.Equal(NewGameSelectionAction.Remove, result.Action, "action is remove");
            Assert.Equal(1, port.SelectCalls, "exactly one native callback");
            Assert.Equal(0, result.View.Perks.SelectedCount, "readback count");
        }

        [Test]
        public static void Session_Deselect_Perk_When_Available_Same_Id_Is_Hovered_Is_Refused()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            NewGameCandidate available = Perk("saving", 1);
            available.Hovered = true;
            snapshot.Candidates.Add(available);

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.False(result.Success, "available hover would swallow the selected click");
            Assert.Equal("ambiguous_target", result.Error, "stable ambiguous target code");
            Assert.Equal(0, port.SelectCalls, "no native callback for an ambiguous hover");
        }

        [Test]
        public static void Session_Deselect_Perk_When_Both_Rows_Are_Hovered_Is_Refused()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.SelectedPerks[0].Hovered = true;
            NewGameCandidate available = Perk("saving", 1);
            available.Hovered = true;
            snapshot.Candidates.Add(available);

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.False(result.Success, "two hovered same-id rows are ambiguous");
            Assert.Equal("ambiguous_target", result.Error, "stable ambiguous target code");
            Assert.Equal(0, port.SelectCalls, "no native callback");
        }

        [Test]
        public static void Session_Duplicate_Selection_Deselect_Does_Not_Call_Native()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.False(result.Success, "native RemoveAll would drop both copies");
            Assert.Equal("duplicate_selected", result.Error, "duplicate selected state refused");
            Assert.Equal(0, port.SelectCalls, "no native callback for ambiguous multi-removal");
        }

        [Test]
        public static void Session_Refuses_Ambiguous_Perk_Deselect_Without_Calling_Native()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.Candidates.Add(Perk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.False(result.Success, "available card shadows the selected row");
            Assert.Equal("ambiguous_target", result.Error, "stable ambiguous target code");
            Assert.Equal(0, port.SelectCalls, "no native callback for an ambiguous target");
            Assert.Equal(1, port.ReadCalls, "only the pre-read happened");
        }

        [Test]
        public static void Session_First_Run_Refuses_Perk_Without_Calling_Native()
        {
            NewGameMenuSnapshot snapshot = PerkSnapshot();
            snapshot.FirstRun = true;
            snapshot.Candidates.Add(Perk("saving", 1));

            FakeNewGameMenuPort port = new FakeNewGameMenuPort(snapshot);
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result = session.Select(NewGameOptionKind.Perk, "saving");

            Assert.False(result.Success, "first-run perk lock is not bypassed");
            Assert.Equal("first_run_locked", result.Error, "stable first-run code");
            Assert.Equal(0, port.SelectCalls, "no native callback while first-run locked");
        }

        [Test]
        public static void Session_Unknown_Id_Does_Not_Call_Native()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "not_a_scenario");

            Assert.False(result.Success, "unknown id rejected");
            Assert.Equal("unknown_option", result.Error, "stable unknown code");
            Assert.Equal(0, port.SelectCalls, "no native callback for unknown id");
        }

        [Test]
        public static void Session_Native_Failure_Is_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            port.FailApply = true;
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "native refusal is not success");
            Assert.Equal("native_not_called", result.Error, "native error surfaced");
            Assert.Equal(1, port.SelectCalls, "the callback was attempted once");
            Assert.Equal(1, port.ReadCalls, "no success readback after a failed call");
        }

        [Test]
        public static void Session_Phase_Change_During_Readback_Is_Not_Success()
        {
            FakeNewGameMenuPort port = new FakeNewGameMenuPort(ScenarioSnapshot());
            port.CloseOnApply = true;
            NewGameMenuSession session = new NewGameMenuSession(port);

            NewGameMenuSelectResult result =
                session.Select(NewGameOptionKind.Scenario, "fence");

            Assert.False(result.Success, "phase change is not success");
            Assert.Equal("readback_phase_changed", result.Error, "stable phase code");
            Assert.Equal(NewGameMenuPhase.Closed, result.View.Phase, "post view reports closed");
            Assert.Equal(1, port.SelectCalls, "exactly one callback");
        }

        [Test]
        public static void Session_Exposes_No_Continue_Confirm_Or_Start_Action()
        {
            MethodInfo[] methods = typeof(NewGameMenuSession).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.Equal(2, methods.Length, "only Observe and Select are exposed");
            List<string> names = new List<string>();
            foreach (MethodInfo method in methods) names.Add(method.Name);
            Assert.True(names.Contains("Observe"), "Observe stays available");
            Assert.True(names.Contains("Select"), "one-step Select stays available");
            Assert.False(names.Contains("Continue"), "S3A must not expose Continue");
            Assert.False(names.Contains("Confirm"), "S3A must not expose Confirm");
            Assert.False(names.Contains("Start"), "S3A must not expose Start");

            MethodInfo[] portMethods = typeof(INewGameMenuPort).GetMethods();
            Assert.Equal(2, portMethods.Length, "port only reads and applies one selection");
        }
    }
}
