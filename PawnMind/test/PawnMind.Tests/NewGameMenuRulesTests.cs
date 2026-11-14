using System.Reflection;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Pure rules for the new-game menu: candidate view filtering, one-step
    /// selection decisions, and readback verification. No Unity or game types.
    /// </summary>
    internal static class NewGameMenuRulesTests
    {
        private static NewGameMenuSnapshot Base(NewGameMenuPhase phase)
        {
            return new NewGameMenuSnapshot
            {
                Phase = phase,
                Page = 0,
                PageCount = 1,
                MaxPerkPoints = 3,
                BasePerkCount = 4,
                Continue = new NewGameMenuButtonState
                {
                    Label = "Continue",
                    Visible = true,
                    Enabled = true
                }
            };
        }

        private static NewGameCandidate ScenarioCandidate(
            string id,
            int index,
            bool visible,
            bool enabled = true,
            string lockReason = null,
            bool hidden = false)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = index.ToString(),
                Page = index / 10,
                Visible = visible,
                Hidden = hidden,
                Enabled = enabled,
                LockReason = lockReason,
                DisplayName = "Title " + id,
                Description = "Desc " + id
            };
        }

        private static NewGameCandidate DifficultyCandidate(string id, int index, bool visible)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = index.ToString(),
                Page = index / 3,
                Visible = visible,
                Enabled = true,
                DisplayName = "Title " + id,
                Description = "Subtitle " + id
            };
        }

        private static NewGameCandidate Perk(
            string id,
            int cost,
            int maxSlot = 0,
            int page = 0,
            bool visible = true)
        {
            return new NewGameCandidate
            {
                Id = id,
                SlotArg = id,
                Page = page,
                Visible = visible,
                Cost = cost,
                MaxSlot = maxSlot,
                DisplayName = "Perk " + id,
                Description = "Perk desc " + id
            };
        }

        private static NewGameCandidate IncompatiblePerk(
            string id,
            int cost,
            int maxSlot,
            params string[] incompatible)
        {
            NewGameCandidate candidate = Perk(id, cost, maxSlot);
            candidate.IncompatibleIds.AddRange(incompatible);
            return candidate;
        }

        private static NewGameSelectedPerk SelectedPerk(
            string id,
            int cost,
            int maxSlot = 0,
            params string[] incompatible)
        {
            NewGameSelectedPerk perk = new NewGameSelectedPerk
            {
                Id = id,
                Cost = cost,
                MaxSlot = maxSlot,
                DisplayName = "Perk " + id,
                Description = "Perk desc " + id
            };
            perk.IncompatibleIds.AddRange(incompatible);
            return perk;
        }

        // -----------------------------------------------------------------
        // Phase / visibility gates
        // -----------------------------------------------------------------

        [Test]
        public static void Rules_Reject_Closed_Menu()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Closed);

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "apprentice");

            Assert.False(decision.Allowed, "closed menu must reject");
            Assert.Equal("menu_closed", decision.Error, "stable closed code");
        }

        [Test]
        public static void Rules_Reject_Wrong_Phase()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, true));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "perk on scenario page must reject");
            Assert.Equal("wrong_phase", decision.Error, "stable wrong-phase code");
        }

        [Test]
        public static void Rules_Reject_Hidden_Option()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            snapshot.Candidates.Add(ScenarioCandidate("secret", 0, false, true, null, true));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "secret");

            Assert.False(decision.Allowed, "hidden option must reject");
            Assert.Equal("option_hidden", decision.Error, "stable hidden code");
        }

        [Test]
        public static void Rules_Reject_Option_Off_Current_Page()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            snapshot.Candidates.Add(ScenarioCandidate("fence", 10, false));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "fence");

            Assert.False(decision.Allowed, "off-page option must reject");
            Assert.Equal("option_not_on_page", decision.Error, "stable off-page code");
        }

        [Test]
        public static void Rules_Reject_Visible_Flag_From_Another_Page()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Page = 1;
            snapshot.PageCount = 2;
            snapshot.SelectedId = "apprentice";
            snapshot.HasSelection = true;
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "stale",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Stale"
            });

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "stale");

            Assert.False(decision.Allowed, "Visible=true on another page must fail closed");
            Assert.Equal("option_not_on_page", decision.Error, "page is part of visibility");
        }

        [Test]
        public static void Rules_Reject_Disabled_Scenario()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("junker", 0, true, false, "coming_soon"));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "junker");

            Assert.False(decision.Allowed, "coming-soon scenario must reject");
            Assert.Equal("option_locked", decision.Error, "stable locked code");
            Assert.Equal("coming_soon", decision.Reason, "lock reason is surfaced");
        }

        [Test]
        public static void Rules_Reject_Already_Selected_Scenario()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("fence", 1, true));
            snapshot.SelectedId = "fence";
            snapshot.HasSelection = true;

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "fence");

            Assert.False(decision.Allowed, "re-clicking the selected scenario must not claim a change");
            Assert.Equal("already_selected", decision.Error, "stable already-selected code");
        }

        [Test]
        public static void Rules_Reject_Unknown_Option()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, true));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "not_a_scenario");

            Assert.False(decision.Allowed, "unknown id must reject");
            Assert.Equal("unknown_option", decision.Error, "stable unknown code");
        }

        [Test]
        public static void Rules_Reject_Empty_Id()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "   ");

            Assert.False(decision.Allowed, "blank id must reject");
            Assert.Equal("empty_id", decision.Error, "stable empty-id code");
        }

        [Test]
        public static void Rules_Reject_When_Menu_Is_Transitioning()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.StartingGame = true;
            snapshot.Candidates.Add(Perk("saving", 1));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "menu already starting the game must reject");
            Assert.Equal("menu_transitioning", decision.Error, "stable transitioning code");
        }

        [Test]
        public static void Rules_Propagate_Phase_Error()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.PhaseError = "ambiguous_phase";

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "apprentice");

            Assert.False(decision.Allowed, "ambiguous phase must reject");
            Assert.Equal("ambiguous_phase", decision.Error, "adapter error is propagated");
        }

        // -----------------------------------------------------------------
        // Scenario / difficulty selection
        // -----------------------------------------------------------------

        [Test]
        public static void Rules_Scenario_Targets_Native_Slot_Index()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            snapshot.Candidates.Add(ScenarioCandidate("fence", 5, true));
            snapshot.SelectedId = "apprentice";
            snapshot.HasSelection = true;

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Scenario, "fence");

            Assert.True(decision.Allowed, "visible scenario must be selectable");
            Assert.Equal(NewGameSelectionAction.Set, decision.Action, "scenario is a set, not a toggle");
            Assert.Equal("fence", decision.Target.Id, "semantic id stays public");
            Assert.Equal("5", decision.Target.SlotArg, "native callback takes the visible-list index");
        }

        [Test]
        public static void Rules_Difficulty_Targets_Native_Slot_Index()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Difficulty);
            snapshot.Candidates.Add(DifficultyCandidate("normal", 1, true));
            snapshot.Candidates.Add(DifficultyCandidate("hard", 2, true));
            snapshot.SelectedId = "normal";
            snapshot.HasSelection = true;

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Difficulty, "hard");

            Assert.True(decision.Allowed, "visible difficulty must be selectable");
            Assert.Equal(NewGameSelectionAction.Set, decision.Action, "difficulty is a set");
            Assert.Equal("2", decision.Target.SlotArg, "native callback takes the visible-list index");

            snapshot.SelectedId = "hard";
            NewGameSelectionDecision repeated =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Difficulty, "hard");
            Assert.False(repeated.Allowed, "re-clicking selected difficulty is a no-op");
            Assert.Equal("already_selected", repeated.Error, "stable already-selected code");
        }

        // -----------------------------------------------------------------
        // Perk rules: first run, incompatibility, points, count, ambiguity
        // -----------------------------------------------------------------

        [Test]
        public static void Rules_Reject_Perk_First_Run_Lock()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.FirstRun = true;
            snapshot.Candidates.Add(Perk("saving", 1));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "totalRunCount==0 perk lock must not be bypassed");
            Assert.Equal("first_run_locked", decision.Error, "stable first-run code");
        }

        [Test]
        public static void Rules_Reject_Incompatible_Perks_Both_Directions()
        {
            NewGameMenuSnapshot selectedSide = Base(NewGameMenuPhase.Perks);
            selectedSide.SelectedPerks.Add(SelectedPerk("convict", -1, 0, "model_citizen"));
            selectedSide.Candidates.Add(Perk("model_citizen", 1));

            NewGameSelectionDecision first =
                NewGameMenuRules.Decide(selectedSide, NewGameOptionKind.Perk, "model_citizen");
            Assert.False(first.Allowed, "selected perk's incompatible list must lock the candidate");
            Assert.Equal("incompatible", first.Error, "stable incompatible code");
            Assert.Contains(first.Reason, "convict", "locking perk is reported");

            NewGameMenuSnapshot candidateSide = Base(NewGameMenuPhase.Perks);
            candidateSide.SelectedPerks.Add(SelectedPerk("model_citizen", 1));
            candidateSide.Candidates.Add(IncompatiblePerk("convict", -1, 0, "model_citizen"));

            NewGameSelectionDecision second =
                NewGameMenuRules.Decide(candidateSide, NewGameOptionKind.Perk, "convict");
            Assert.False(second.Allowed, "candidate's own incompatible list must also lock it");
            Assert.Equal("incompatible", second.Error, "bidirectional incompatibility code");
        }

        [Test]
        public static void Rules_Reject_Perk_Over_Points()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.MaxPerkPoints = 3;
            snapshot.SelectedPerks.Add(SelectedPerk("well_connected", 2));
            snapshot.Candidates.Add(Perk("alert", 2));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "alert");

            Assert.False(decision.Allowed, "4 points must not pass the 3 point budget");
            Assert.Equal("insufficient_points", decision.Error, "stable points code");

            snapshot.Candidates.Clear();
            snapshot.Candidates.Add(Perk("bad_lease", -1));
            NewGameSelectionDecision refund =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "bad_lease");
            Assert.True(refund.Allowed, "negative cost must refund points");
            Assert.Equal(NewGameSelectionAction.Add, refund.Action, "negative perk is added");
        }

        [Test]
        public static void Rules_Reject_Perk_Over_Count_Unless_MaxSlot_Grows()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.BasePerkCount = 4;
            snapshot.SelectedPerks.Add(SelectedPerk("a", 0));
            snapshot.SelectedPerks.Add(SelectedPerk("b", 0));
            snapshot.SelectedPerks.Add(SelectedPerk("c", 0));
            snapshot.SelectedPerks.Add(SelectedPerk("d", 0));
            snapshot.Candidates.Add(Perk("extra", 0));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "extra");
            Assert.False(decision.Allowed, "fifth perk must exceed the base four slots");
            Assert.Equal("perk_limit_reached", decision.Error, "stable count code");

            snapshot.Candidates.Clear();
            snapshot.Candidates.Add(Perk("colorful_character", 0, 3));
            NewGameSelectionDecision growing =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "colorful_character");
            Assert.True(growing.Allowed, "a maxSlot perk raises the count ceiling");
        }

        [Test]
        public static void Rules_Reject_Ambiguous_Duplicate_Available_Perk()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.Candidates.Add(Perk("saving", 1));
            NewGameCandidate duplicate = Perk("saving", 1);
            duplicate.SlotArg = "saving#2";
            snapshot.Candidates.Add(duplicate);

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "two visible definitions with one id must not be guessed");
            Assert.Equal("ambiguous_option", decision.Error, "stable ambiguous option code");
        }

        [Test]
        public static void Rules_Reject_Deselect_When_Available_Card_Shadows_Selected_Row()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.Candidates.Add(Perk("saving", 1));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "one native id click hits the available card first");
            Assert.Equal("ambiguous_target", decision.Error, "stable ambiguous target code");
        }

        [Test]
        public static void Rules_Allow_Deselect_When_Selected_Row_Is_The_Only_Target()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.True(decision.Allowed, "selected-row-only target must deselect");
            Assert.Equal(NewGameSelectionAction.Remove, decision.Action, "native click toggles off");
            Assert.Equal("saving", decision.Target.SlotArg, "perk callback takes perk.id");
        }

        [Test]
        public static void Rules_Reject_Deselect_When_Selected_List_Contains_Duplicates()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "saving");

            Assert.False(decision.Allowed, "native RemoveAll(id) would remove more than one");
            Assert.Equal("duplicate_selected", decision.Error, "duplicate selected state is refused");
        }

        [Test]
        public static void Rules_Reject_Deselect_When_Selected_Row_Page_Is_Not_Visible()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPage = 0;
            for (int i = 0; i < 11; i++)
            {
                snapshot.SelectedPerks.Add(SelectedPerk("p" + i, 0));
            }
            snapshot.Candidates.Add(Perk("p10", 0));

            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(snapshot, NewGameOptionKind.Perk, "p10");

            Assert.False(decision.Allowed, "selected row page two is not on screen");
            Assert.Equal("selected_row_not_visible", decision.Error, "stable invisible-row code");
        }

        // -----------------------------------------------------------------
        // Readback verification
        // -----------------------------------------------------------------

        [Test]
        public static void Readback_Confirms_Scenario_Set()
        {
            NewGameMenuSnapshot before = Base(NewGameMenuPhase.Specialization);
            before.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            before.Candidates.Add(ScenarioCandidate("fence", 1, true));
            before.SelectedId = "apprentice";
            before.HasSelection = true;
            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(before, NewGameOptionKind.Scenario, "fence");

            NewGameMenuSnapshot after = Base(NewGameMenuPhase.Specialization);
            after.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            after.Candidates.Add(ScenarioCandidate("fence", 1, true));
            after.SelectedId = "fence";
            after.HasSelection = true;

            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, decision, after);

            Assert.Equal(NewGameReadbackStatus.Confirmed, readback.Status, "UI selection matches");
        }

        [Test]
        public static void Readback_Rejects_Scenario_Data_Change_Without_Ui_Selection()
        {
            NewGameMenuSnapshot before = Base(NewGameMenuPhase.Specialization);
            before.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            before.Candidates.Add(ScenarioCandidate("fence", 1, true));
            before.SelectedId = "apprentice";
            before.HasSelection = true;
            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(before, NewGameOptionKind.Scenario, "fence");

            // The fake wrote NewGameData but the UI's pending selection did not move.
            NewGameMenuSnapshot after = Base(NewGameMenuPhase.Specialization);
            after.Candidates.Add(ScenarioCandidate("apprentice", 0, true));
            after.Candidates.Add(ScenarioCandidate("fence", 1, true));
            after.SelectedId = "apprentice";
            after.HasSelection = true;

            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, decision, after);

            Assert.Equal(NewGameReadbackStatus.Mismatch, readback.Status, "no fake success");
            Assert.Equal("readback_mismatch", readback.Error, "stable mismatch code");
        }

        [Test]
        public static void Readback_Rejects_Phase_Change()
        {
            NewGameMenuSnapshot before = Base(NewGameMenuPhase.Perks);
            before.SelectedPerks.Add(SelectedPerk("saving", 1));
            NewGameSelectionDecision decision =
                NewGameMenuRules.Decide(before, NewGameOptionKind.Perk, "saving");

            NewGameMenuSnapshot after = Base(NewGameMenuPhase.Closed);

            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, decision, after);

            Assert.Equal(NewGameReadbackStatus.PhaseChanged, readback.Status, "phase change is not success");
            Assert.Equal("readback_phase_changed", readback.Error, "stable phase code");
        }

        [Test]
        public static void Readback_Confirms_Perk_Add_And_Remove()
        {
            NewGameMenuSnapshot beforeAdd = Base(NewGameMenuPhase.Perks);
            beforeAdd.Candidates.Add(Perk("saving", 1));
            NewGameSelectionDecision add =
                NewGameMenuRules.Decide(beforeAdd, NewGameOptionKind.Perk, "saving");

            NewGameMenuSnapshot afterAdd = Base(NewGameMenuPhase.Perks);
            afterAdd.Candidates.Add(Perk("saving", 1));
            afterAdd.SelectedPerks.Add(SelectedPerk("saving", 1));
            NewGameReadback addReadback = NewGameMenuReadbackRules.Verify(beforeAdd, add, afterAdd);
            Assert.Equal(NewGameReadbackStatus.Confirmed, addReadback.Status, "add confirmed");

            NewGameMenuSnapshot beforeRemove = Base(NewGameMenuPhase.Perks);
            beforeRemove.SelectedPerks.Add(SelectedPerk("saving", 1));
            NewGameSelectionDecision remove =
                NewGameMenuRules.Decide(beforeRemove, NewGameOptionKind.Perk, "saving");
            NewGameMenuSnapshot afterRemove = Base(NewGameMenuPhase.Perks);
            NewGameReadback removeReadback =
                NewGameMenuReadbackRules.Verify(beforeRemove, remove, afterRemove);
            Assert.Equal(NewGameReadbackStatus.Confirmed, removeReadback.Status, "remove confirmed");
        }

        [Test]
        public static void Readback_Rejects_Perk_Add_Without_State_Change()
        {
            NewGameMenuSnapshot before = Base(NewGameMenuPhase.Perks);
            before.Candidates.Add(Perk("saving", 1));
            NewGameSelectionDecision add =
                NewGameMenuRules.Decide(before, NewGameOptionKind.Perk, "saving");

            NewGameMenuSnapshot after = Base(NewGameMenuPhase.Perks);
            after.Candidates.Add(Perk("saving", 1));

            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, add, after);

            Assert.Equal(NewGameReadbackStatus.Mismatch, readback.Status, "unchanged state is not success");
            Assert.Equal("readback_mismatch", readback.Error, "stable mismatch code");
        }

        // -----------------------------------------------------------------
        // View filtering and state
        // -----------------------------------------------------------------

        [Test]
        public static void Readback_Rejects_Multi_Removal_Of_Duplicate_Selection()
        {
            NewGameMenuSnapshot before = Base(NewGameMenuPhase.Perks);
            before.SelectedPerks.Add(SelectedPerk("saving", 1));
            before.SelectedPerks.Add(SelectedPerk("saving", 1));
            NewGameSelectionDecision decision = new NewGameSelectionDecision
            {
                Allowed = true,
                Kind = NewGameOptionKind.Perk,
                Action = NewGameSelectionAction.Remove,
                Target = new NewGameCandidate { Id = "saving", SlotArg = "saving" }
            };

            NewGameMenuSnapshot after = Base(NewGameMenuPhase.Perks);
            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, decision, after);

            Assert.Equal(NewGameReadbackStatus.Mismatch, readback.Status,
                "one callback removing two selected copies is not a one-perk removal");
            Assert.Equal("readback_mismatch", readback.Error, "stable mismatch code");
        }

        [Test]
        public static void View_Lists_Only_Visible_Current_Page_Options()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Page = 1;
            snapshot.PageCount = 2;
            snapshot.Candidates.Add(ScenarioCandidate("apprentice", 0, false));
            snapshot.Candidates.Add(ScenarioCandidate("fence", 10, true));
            snapshot.Candidates.Add(ScenarioCandidate("secret", 11, true, true, null, true));

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.Equal(1, view.Options.Count, "only the built current-page card is listed");
            Assert.Equal("fence", view.Options[0].Id, "listed public id is the scenario id");
            Assert.Equal(1, view.Page, "page index reported");
            Assert.Equal(2, view.PageCount, "page count reported");
            Assert.True(view.HasPreviousPage, "page one has a previous page");
            Assert.False(view.HasNextPage, "page one is the last page");
            Assert.False(view.HasSelection, "no pending scenario selection");
        }

        [Test]
        public static void View_Excludes_Visible_Candidate_From_Another_Page()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Page = 1;
            snapshot.PageCount = 2;
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "stale",
                SlotArg = "0",
                Page = 0,
                Visible = true,
                Enabled = true,
                DisplayName = "Stale"
            });
            snapshot.Candidates.Add(new NewGameCandidate
            {
                Id = "current",
                SlotArg = "10",
                Page = 1,
                Visible = true,
                Enabled = true,
                DisplayName = "Current"
            });

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.Equal(1, view.Options.Count, "only the current page card is listed");
            Assert.Equal("current", view.Options[0].Id, "stale Visible=true card is not exposed");
        }

        [Test]
        public static void View_Computes_Perk_Totals_And_Lock_Reasons()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPerks.Add(SelectedPerk("convict", -1, 0, "model_citizen"));
            snapshot.Candidates.Add(Perk("saving", 1));
            snapshot.Candidates.Add(Perk("model_citizen", 1));
            snapshot.PointsText = "<color=#fff>-1</color>";
            snapshot.CountText = "1/4";

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.NotNull(view.Perks, "perk state is present");
            Assert.Equal(-1, view.Perks.UsedPoints, "used points sum selected costs");
            Assert.Equal(4, view.Perks.RemainingPoints, "negative cost refunds points");
            Assert.Equal(1, view.Perks.SelectedCount, "selected count");
            Assert.Equal(4, view.Perks.MaxCount, "base count without maxSlot perks");
            Assert.Equal("<color=#fff>-1</color>", view.Perks.PointsText, "display text is passed through");
            Assert.Equal("1/4", view.Perks.CountText, "counter text is passed through");
            Assert.Equal(1, view.SelectedOptions.Count, "selected row shows the selected perk");

            NewGameOptionView saving = Find(view.Options, "saving");
            Assert.NotNull(saving, "saving is listed");
            Assert.True(saving.Enabled, "normal perk is enabled");
            Assert.False(saving.Selected, "normal perk is not selected");

            NewGameOptionView incompatible = Find(view.Options, "model_citizen");
            Assert.NotNull(incompatible, "model_citizen is listed");
            Assert.False(incompatible.Enabled, "incompatible perk is disabled");
            Assert.Contains(incompatible.LockReason, "convict", "locking perk is named");
        }

        [Test]
        public static void View_First_Run_Disables_Perk_Cards()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.FirstRun = true;
            snapshot.SelectedPerks.Add(SelectedPerk("saving", 1));
            snapshot.Candidates.Add(Perk("saving", 1));

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.Equal("first_run", view.Options[0].LockReason, "available card is first-run locked");
            Assert.False(view.Options[0].Enabled, "available card is disabled");
            Assert.Equal("first_run", view.SelectedOptions[0].LockReason, "selected row is locked too");
            Assert.True(view.Perks.FirstRun, "first-run flag is reported");
        }

        [Test]
        public static void View_Pages_Selected_Perk_Row()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Perks);
            snapshot.SelectedPage = 0;
            for (int i = 0; i < 11; i++)
            {
                snapshot.SelectedPerks.Add(SelectedPerk("p" + i, 0));
            }

            NewGameMenuView firstPage = NewGameMenuViewBuilder.Build(snapshot);
            Assert.Equal(10, firstPage.SelectedOptions.Count, "ten selected perks per page");
            Assert.Equal(2, firstPage.SelectedPageCount, "two selected-row pages");
            Assert.True(firstPage.SelectedRowPaged, "row pagination is exposed");
            Assert.False(firstPage.HasPreviousSelectedPage, "first selected page has no previous");
            Assert.True(firstPage.HasNextSelectedPage, "first selected page has a next");

            snapshot.SelectedPage = 1;
            NewGameMenuView secondPage = NewGameMenuViewBuilder.Build(snapshot);
            Assert.Equal(1, secondPage.SelectedOptions.Count, "second page holds the eleventh perk");
            Assert.Equal("p10", secondPage.SelectedOptions[0].Id, "second page starts at index ten");
            Assert.True(secondPage.HasPreviousSelectedPage, "second selected page has a previous");
            Assert.False(secondPage.HasNextSelectedPage, "second selected page is last");
        }

        [Test]
        public static void View_Does_Not_Expose_Native_Slot_Arguments()
        {
            Assert.Null(typeof(NewGameOptionView).GetField("SlotArg"), "option view has no slot arg field");
            Assert.Null(typeof(NewGameOptionView).GetProperty("SlotArg"), "option view has no slot arg property");
            Assert.Null(typeof(NewGameMenuView).GetField("SlotArg"), "menu view has no slot arg field");
            Assert.Null(typeof(NewGameMenuView).GetProperty("SlotArg"), "menu view has no slot arg property");
        }

        [Test]
        public static void View_Passes_Through_Actual_Scenario_Panel_Text()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Specialization);
            snapshot.Candidates.Add(ScenarioCandidate("fence", 1, true));
            snapshot.SelectedId = "fence";
            snapshot.HasSelection = true;
            snapshot.Header = "New Game";
            snapshot.Detail = new NewGameMenuDetail
            {
                Title = "Fence Title",
                Tag = "Advanced",
                Description = "Fence Description",
                Warning = "Intro unavailable"
            };
            snapshot.Detail.Lines.Add("Start with fence gear");

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.Equal("New Game", view.Header, "header text");
            Assert.NotNull(view.Detail, "detail panel is present");
            Assert.Equal("Fence Title", view.Detail.Title, "title text");
            Assert.Equal("Advanced", view.Detail.Tag, "tag text");
            Assert.Equal("Fence Description", view.Detail.Description, "description text");
            Assert.Equal("Intro unavailable", view.Detail.Warning, "warning text");
            Assert.Equal(1, view.Detail.Lines.Count, "start-with bullet lines");
            Assert.Equal("Start with fence gear", view.Detail.Lines[0], "bullet text");
        }

        [Test]
        public static void View_Reports_Continue_Button_State()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Difficulty);
            snapshot.Candidates.Add(DifficultyCandidate("normal", 1, true));
            snapshot.SelectedId = "normal";
            snapshot.HasSelection = true;
            snapshot.Continue = new NewGameMenuButtonState
            {
                Label = "Start Game",
                Visible = true,
                Enabled = true
            };

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.NotNull(view.Continue, "continue state is always present");
            Assert.Equal("Start Game", view.Continue.Label, "actual continue/start label");
            Assert.True(view.Continue.Visible, "continue button is visible");
            Assert.True(view.Continue.Enabled, "continue button is enabled on the difficulty page");
        }

        [Test]
        public static void View_Reports_Difficulty_Toggles_Read_Only()
        {
            NewGameMenuSnapshot snapshot = Base(NewGameMenuPhase.Difficulty);
            snapshot.Candidates.Add(DifficultyCandidate("normal", 1, true));
            snapshot.SelectedId = "normal";
            snapshot.HasSelection = true;
            snapshot.Toggles.Add(new NewGameMenuToggleState
            {
                Id = "skip_intro",
                Label = "Skip Intro",
                Visible = true,
                Selected = false,
                Enabled = false,
                LockReason = "scenario_forces_skip"
            });

            NewGameMenuView view = NewGameMenuViewBuilder.Build(snapshot);

            Assert.Equal(1, view.Toggles.Count, "checkbox is observed");
            Assert.Equal("skip_intro", view.Toggles[0].Id, "toggle id");
            Assert.False(view.Toggles[0].Enabled, "forced skip checkbox is disabled");
            Assert.Equal("scenario_forces_skip", view.Toggles[0].LockReason, "lock reason is reported");
            Assert.Equal("normal", view.SelectedId, "selected difficulty is reported");
            Assert.True(view.Options[0].Selected, "selected plate is marked selected");
        }

        private static NewGameOptionView Find(System.Collections.Generic.List<NewGameOptionView> options, string id)
        {
            foreach (NewGameOptionView option in options)
            {
                if (option.Id == id) return option;
            }
            return null;
        }
    }
}
