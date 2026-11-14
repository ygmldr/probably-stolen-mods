using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PawnMind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnMind.Game
{
    /// <summary>
    /// Game adapter for the new-game menu. All access must happen on the Unity
    /// main thread. Public semantic ids are translated to the native slot callback
    /// argument here: scenario/difficulty callbacks take the visible-list index,
    /// perks take perk.id. Private UI members are read through reflection because
    /// only src/Game is allowed to touch game internals.
    ///
    /// Selection only invokes one of NewGameSpecializationUI.OnSlotClicked,
    /// NewGamePerksUI.OnSlotClicked or NewGameDifficultyUI.OnPlateClicked.
    /// Control ops invoke exactly one real native callback each: OnNewGameClick,
    /// TurnPage/TurnSelectedPage, the scenario/perks Continue handler, or the
    /// perk slot's own WildNetworkSlot.OnPointerEnter/OnPointerExit. The adapter
    /// never confirms a prompt, never starts a game and never saves.
    /// </summary>
    internal sealed class NewGameMenuAdapter : INewGameMenuPort, INewGameMenuControlPort
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const int ScenarioPageSize = 10;
        private const int PerkPageSize = 36;
        private const int DifficultyPageSize = 3;
        private const int SelectedPerkPageSize = 10;

        public static readonly NewGameMenuAdapter Instance = new NewGameMenuAdapter();

        public NewGameMenuSnapshot Read()
        {
            try
            {
                return ReadCore();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game menu read failed: " + e.GetType().Name);
                return ErrorSnapshot("adapter_read_failed");
            }
        }

        private static NewGameMenuSnapshot ReadCore()
        {
            bool promptFailed;
            NewGameMenuPrompt prompt = ReadPrompt(out promptFailed);
            if (promptFailed)
            {
                // A visible but unreadable prompt is still a fork: fail closed and
                // never let it look like a normal closed/started menu.
                return new NewGameMenuSnapshot
                {
                    Phase = NewGameMenuPhase.Closed,
                    PhaseError = "prompt_read_failed",
                    Prompt = prompt ?? UnknownPrompt()
                };
            }

            bool phaseReadFailed = false;
            bool specializationOpen = TryReadOpen(
                () => NewGameSpecializationUI.IsOpen, ref phaseReadFailed);
            bool perksOpen = TryReadOpen(
                () => NewGamePerksUI.IsOpen, ref phaseReadFailed);
            bool difficultyOpen = TryReadOpen(
                () => NewGameDifficultyUI.IsOpen, ref phaseReadFailed);

            if (phaseReadFailed) return ErrorSnapshot("phase_read_failed");

            int openCount = (specializationOpen ? 1 : 0) + (perksOpen ? 1 : 0) + (difficultyOpen ? 1 : 0);
            if (openCount > 1) return ErrorSnapshot("ambiguous_phase");
            if (openCount == 0)
            {
                return new NewGameMenuSnapshot
                {
                    Phase = NewGameMenuPhase.Closed,
                    Prompt = prompt
                };
            }

            NewGameMenuSnapshot snapshot;
            if (specializationOpen)
            {
                snapshot = new NewGameMenuSnapshot { Phase = NewGameMenuPhase.Specialization };
                BuildSpecialization(snapshot);
            }
            else if (perksOpen)
            {
                snapshot = new NewGameMenuSnapshot { Phase = NewGameMenuPhase.Perks };
                BuildPerks(snapshot);
            }
            else
            {
                snapshot = new NewGameMenuSnapshot { Phase = NewGameMenuPhase.Difficulty };
                BuildDifficulty(snapshot);
            }

            snapshot.Prompt = prompt;
            return snapshot;
        }

        private static NewGameMenuPrompt UnknownPrompt()
        {
            return new NewGameMenuPrompt { Id = "unknown_prompt" };
        }

        private static NewGameMenuPrompt ReadPrompt(out bool failed)
        {
            failed = false;
            try
            {
                MainMenuUIController menu = MainMenuUIController.Instance;
                if (menu == null) return null;
                ConfirmPanelUIManager confirm = menu.confirmPanelUIManager;
                if (confirm == null || confirm.panel == null) return null;
                if (!confirm.panel.activeInHierarchy) return null;

                string text = ReadText(confirm.bodyTMP);
                NewGameMenuPrompt prompt = new NewGameMenuPrompt
                {
                    Id = "confirm_panel",
                    Text = text,
                    ConfirmText = ReadText(confirm.yesTMP),
                    CancelText = ReadText(confirm.noTMP)
                };

                try
                {
                    string firstRun = LocHelper.GetLocalizedMainMenu("first_run_confirm");
                    string skipIntroFirstRun =
                        LocHelper.GetLocalizedMainMenu("skip_intro_first_run_confirm");
                    if (!string.IsNullOrEmpty(text)
                        && string.Equals(text, firstRun, StringComparison.Ordinal))
                    {
                        prompt.Id = "first_run_confirm";
                        prompt.FirstRun = true;
                    }
                    else if (!string.IsNullOrEmpty(text)
                        && string.Equals(text, skipIntroFirstRun, StringComparison.Ordinal))
                    {
                        prompt.Id = "skip_intro_first_run_confirm";
                        prompt.FirstRun = true;
                    }
                }
                catch (Exception)
                {
                    // The prompt is still reported by its visible text/id; only the
                    // more specific first-run identity is unavailable.
                }

                return prompt;
            }
            catch (Exception)
            {
                failed = true;
                return UnknownPrompt();
            }
        }

        // -----------------------------------------------------------------
        // Read: scenario page
        // -----------------------------------------------------------------

        private static void BuildSpecialization(NewGameMenuSnapshot snapshot)
        {
            NewGameSpecializationFrame frame =
                StaticField<NewGameSpecializationFrame>(typeof(NewGameSpecializationUI), "frame");
            List<StartingScenario> scenarios =
                StaticField<List<StartingScenario>>(typeof(NewGameSpecializationUI), "scenarios");
            int page = StaticField<int>(typeof(NewGameSpecializationUI), "page");
            int pageCount = InvokeStaticInt(typeof(NewGameSpecializationUI), "PageCount");

            if (frame == null || scenarios == null)
            {
                snapshot.PhaseError = "ui_not_ready";
                return;
            }
            int expectedPageCount = (scenarios.Count + ScenarioPageSize - 1) / ScenarioPageSize;
            if (pageCount != expectedPageCount)
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }
            if (page < 0 || (pageCount > 0 && page >= pageCount))
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }

            snapshot.Page = page;
            snapshot.PageCount = pageCount;
            snapshot.Header = ReadText(frame.headerTitle);
            snapshot.Continue = ReadButton(frame.continueButton, frame.continueLabel);

            StartingScenario selected = NewGameSpecializationUI.Selected;
            snapshot.HasSelection = selected != null;
            snapshot.SelectedId = selected == null ? null : selected.id;

            for (int i = 0; i < scenarios.Count; i++)
            {
                StartingScenario scenario = scenarios[i];
                if (scenario == null) continue;

                bool visible = i / ScenarioPageSize == page;
                NewGameCandidate candidate = new NewGameCandidate
                {
                    Id = scenario.id,
                    // Native WildNetworkSlot.id for a scenario is the visible-list index.
                    SlotArg = i.ToString(),
                    Page = i / ScenarioPageSize,
                    Visible = visible,
                    Hidden = scenario.hidden,
                    Enabled = scenario.CanStart(),
                    // Text is only available for cards the UI has actually built.
                    DisplayName = visible ? scenario.GetTitle() : null,
                    Description = visible ? scenario.GetDescription() : null,
                    Tag = visible ? scenario.GetTag() : null
                };
                candidate.LockReason = candidate.Enabled
                    ? null
                    : (scenario.comingSoon ? "coming_soon" : "unavailable");
                if (visible) AddLines(candidate.Lines, scenario.GetStartWith());
                snapshot.Candidates.Add(candidate);
            }

            if (selected != null)
            {
                snapshot.Detail = new NewGameMenuDetail
                {
                    Title = ReadText(frame.title),
                    Tag = ReadText(frame.tag),
                    Description = ReadText(frame.description),
                    Warning = ReadText(frame.warning)
                };
                AddLines(snapshot.Detail.Lines, selected.GetStartWith());
            }
        }

        // -----------------------------------------------------------------
        // Read: perks page
        // -----------------------------------------------------------------

        private static void BuildPerks(NewGameMenuSnapshot snapshot)
        {
            NewGamePerksFrame frame =
                StaticField<NewGamePerksFrame>(typeof(NewGamePerksUI), "frame");
            List<StartingPerk> sorted =
                StaticField<List<StartingPerk>>(typeof(NewGamePerksUI), "sorted");
            List<StartingPerk> selected =
                StaticProperty<List<StartingPerk>>(typeof(NewGamePerksUI), "SelectedPerks");
            int page = StaticField<int>(typeof(NewGamePerksUI), "page");
            int selectedPage = StaticField<int>(typeof(NewGamePerksUI), "selectedPage");
            bool firstRun = StaticField<bool>(typeof(NewGamePerksUI), "firstRun");
            bool startingGame = StaticField<bool>(typeof(NewGamePerksUI), "startingGame");
            int pageCount = InvokeStaticInt(typeof(NewGamePerksUI), "PageCount");
            int maxPoints = ConstInt(typeof(NewGamePerksUI), "MAX_PERK_POINT");
            int baseCount = ConstInt(typeof(NewGamePerksUI), "BASE_PERK_COUNT");

            if (frame == null || sorted == null || selected == null)
            {
                snapshot.PhaseError = "ui_not_ready";
                return;
            }
            if (maxPoints <= 0 || baseCount <= 0)
            {
                snapshot.PhaseError = "ui_not_ready";
                return;
            }
            int expectedPageCount = (sorted.Count + PerkPageSize - 1) / PerkPageSize;
            if (pageCount != expectedPageCount)
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }
            if (page < 0 || (pageCount > 0 && page >= pageCount))
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }

            int expectedSelectedPageCount =
                (selected.Count + SelectedPerkPageSize - 1) / SelectedPerkPageSize;
            int selectedPageCount = InvokeStaticInt(typeof(NewGamePerksUI), "SelectedPageCount");
            if (selectedPageCount != expectedSelectedPageCount)
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }
            if (selectedPage < 0
                || (selected.Count == 0 && selectedPage != 0)
                || (selected.Count > 0 && selectedPage >= selectedPageCount))
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }
            if (firstRun && selected.Count > 0)
            {
                // Native Open clears the list and every click returns early.
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }

            snapshot.FirstRun = firstRun;
            snapshot.StartingGame = startingGame;
            snapshot.Page = page;
            snapshot.PageCount = pageCount;
            snapshot.SelectedPage = selectedPage;
            snapshot.MaxPerkPoints = maxPoints;
            snapshot.BasePerkCount = baseCount;
            snapshot.Header = ReadText(frame.headerTitle);
            snapshot.Continue = ReadButton(frame.continueButton, frame.continueLabel);
            snapshot.PointsText = ReadText(frame.pointsCounter);
            snapshot.CountText = ReadText(frame.perksCounter);
            snapshot.PerkInstruction = ReadText(frame.instruction);

            for (int i = 0; i < sorted.Count; i++)
            {
                StartingPerk perk = sorted[i];
                if (perk == null) continue;

                bool visible = i / PerkPageSize == page;
                NewGameCandidate candidate = new NewGameCandidate
                {
                    Id = perk.id,
                    // Native WildNetworkSlot.id for a perk is perk.id itself.
                    SlotArg = perk.id,
                    Page = i / PerkPageSize,
                    Visible = visible,
                    Cost = perk.cost,
                    MaxSlot = perk.maxSlot,
                    DisplayName = visible ? perk.GetLocalizedDisplayName() : null,
                    Description = visible ? perk.GetLocalizedDescription() : null
                };
                if (perk.incompatiblePerks != null)
                {
                    candidate.IncompatibleIds.AddRange(perk.incompatiblePerks);
                }
                snapshot.Candidates.Add(candidate);
            }

            int selectedStart = selectedPage * SelectedPerkPageSize;
            int selectedEnd = selectedStart + SelectedPerkPageSize;
            for (int i = 0; i < selected.Count; i++)
            {
                StartingPerk perk = selected[i];
                if (perk == null) continue;

                bool visible = i >= selectedStart && i < selectedEnd;
                NewGameSelectedPerk selectedPerk = new NewGameSelectedPerk
                {
                    Id = perk.id,
                    Cost = perk.cost,
                    MaxSlot = perk.maxSlot,
                    DisplayName = visible ? perk.GetLocalizedDisplayName() : null,
                    Description = visible ? perk.GetLocalizedDescription() : null
                };
                if (perk.incompatiblePerks != null)
                {
                    selectedPerk.IncompatibleIds.AddRange(perk.incompatiblePerks);
                }
                snapshot.SelectedPerks.Add(selectedPerk);
            }

            ReadPerkHover(snapshot);
        }

        private sealed class PerkSlotInfo
        {
            public string Id;
            public bool InSelectedRow;
            public bool Hovered;
            public Image FrameImage;
        }

        /// <summary>
        /// Reads the UI's own SlotView list. This is the only reliable way to
        /// know which exact card (available or selected row) a hover belongs to:
        /// both rows contain WildNetworkSlot components with the same perk.id.
        /// </summary>
        private static List<PerkSlotInfo> ReadPerkSlots()
        {
            var slots = new List<PerkSlotInfo>();
            try
            {
                FieldInfo field = typeof(NewGamePerksUI).GetField("views", StaticMembers);
                if (field == null) return slots;
                IList views = field.GetValue(null) as IList;
                if (views == null) return slots;

                foreach (object view in views)
                {
                    if (view == null) continue;
                    Type type = view.GetType();
                    FieldInfo perkField = type.GetField("perk", BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo selectedField = type.GetField("inSelectedRow", BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo hoveredField = type.GetField("hovered", BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo imageField = type.GetField("frameImage", BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic);
                    if (perkField == null || selectedField == null || hoveredField == null)
                    {
                        continue;
                    }

                    object perkObject = perkField.GetValue(view);
                    StartingPerk perk = perkObject as StartingPerk;
                    if (perk == null || string.IsNullOrEmpty(perk.id)) continue;

                    slots.Add(new PerkSlotInfo
                    {
                        Id = perk.id,
                        InSelectedRow = selectedField.GetValue(view) is bool
                            && (bool)selectedField.GetValue(view),
                        Hovered = hoveredField.GetValue(view) is bool
                            && (bool)hoveredField.GetValue(view),
                        FrameImage = imageField == null
                            ? null
                            : imageField.GetValue(view) as Image
                    });
                }
            }
            catch (Exception)
            {
                return new List<PerkSlotInfo>();
            }

            return slots;
        }

        private static void ReadPerkHover(NewGameMenuSnapshot snapshot)
        {
            List<PerkSlotInfo> slots = ReadPerkSlots();
            if (slots.Count == 0) return;

            foreach (NewGameCandidate candidate in snapshot.Candidates)
            {
                if (candidate == null) continue;
                foreach (PerkSlotInfo slot in slots)
                {
                    if (!slot.InSelectedRow
                        && slot.Hovered
                        && string.Equals(slot.Id, candidate.Id, StringComparison.Ordinal))
                    {
                        candidate.Hovered = true;
                    }
                }
            }

            foreach (NewGameSelectedPerk selected in snapshot.SelectedPerks)
            {
                if (selected == null) continue;
                foreach (PerkSlotInfo slot in slots)
                {
                    if (slot.InSelectedRow
                        && slot.Hovered
                        && string.Equals(slot.Id, selected.Id, StringComparison.Ordinal))
                    {
                        selected.Hovered = true;
                    }
                }
            }

            string hoveredId = null;
            bool hoveredSelected = false;
            bool multiple = false;
            foreach (PerkSlotInfo slot in slots)
            {
                if (!slot.Hovered) continue;
                if (hoveredId == null)
                {
                    hoveredId = slot.Id;
                    hoveredSelected = slot.InSelectedRow;
                }
                else if (!string.Equals(hoveredId, slot.Id, StringComparison.Ordinal))
                {
                    multiple = true;
                }
                else if (slot.InSelectedRow)
                {
                    hoveredSelected = true;
                }
            }

            if (!multiple)
            {
                snapshot.HoveredId = hoveredId;
                snapshot.HoveredInSelectedRow = hoveredSelected;
            }
        }
        // -----------------------------------------------------------------
        // Read: difficulty page
        // -----------------------------------------------------------------

        private static void BuildDifficulty(NewGameMenuSnapshot snapshot)
        {
            NewGameDifficultyFrame frame =
                StaticField<NewGameDifficultyFrame>(typeof(NewGameDifficultyUI), "frame");
            List<Difficulty> difficulties =
                StaticField<List<Difficulty>>(typeof(NewGameDifficultyUI), "difficulties");
            int page = StaticField<int>(typeof(NewGameDifficultyUI), "page");
            bool startingGame = StaticField<bool>(typeof(NewGameDifficultyUI), "startingGame");
            bool skipIntroForced = StaticField<bool>(typeof(NewGameDifficultyUI), "skipIntroForced");
            int pageCount = InvokeStaticInt(typeof(NewGameDifficultyUI), "PageCount");

            if (frame == null || difficulties == null)
            {
                snapshot.PhaseError = "ui_not_ready";
                return;
            }
            int expectedPageCount = (difficulties.Count + DifficultyPageSize - 1) / DifficultyPageSize;
            if (pageCount != expectedPageCount)
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }
            if (page < 0 || (pageCount > 0 && page >= pageCount))
            {
                snapshot.PhaseError = "ui_state_mismatch";
                return;
            }

            snapshot.Page = page;
            snapshot.PageCount = pageCount;
            snapshot.StartingGame = startingGame;
            snapshot.Header = ReadText(frame.headerTitle);
            snapshot.Continue = ReadButton(frame.continueButton, frame.continueLabel);

            Difficulty selected = NewGameDifficultyUI.Selected;
            snapshot.HasSelection = selected != null;
            snapshot.SelectedId = selected == null ? null : selected.id;

            for (int i = 0; i < difficulties.Count; i++)
            {
                Difficulty difficulty = difficulties[i];
                if (difficulty == null) continue;

                bool visible = i / DifficultyPageSize == page;
                NewGameCandidate candidate = new NewGameCandidate
                {
                    Id = difficulty.id,
                    // Native WildNetworkSlot.id for a difficulty is the visible-list index.
                    SlotArg = i.ToString(),
                    Page = i / DifficultyPageSize,
                    Visible = visible,
                    Enabled = true,
                    DisplayName = visible ? difficulty.GetTitle() : null,
                    Description = visible ? difficulty.GetSubtitle() : null
                };
                if (visible)
                {
                    try
                    {
                        AddLines(candidate.Lines, Difficulty.GetEffectLines(difficulty));
                    }
                    catch (Exception)
                    {
                        // A mod difficulty with broken text must not break the whole view.
                    }
                }
                snapshot.Candidates.Add(candidate);
            }

            AddToggle(snapshot, "skip_intro", frame.skipIntro,
                skipIntroForced ? "scenario_forces_skip" : "unavailable");
            AddToggle(snapshot, "endless", frame.endless, "unavailable");
        }

        private static void AddToggle(
            NewGameMenuSnapshot snapshot,
            string id,
            NewGameCheckbox checkbox,
            string disabledReason)
        {
            if (checkbox == null || checkbox.root == null || checkbox.button == null
                || checkbox.mark == null || checkbox.label == null)
            {
                return;
            }
            if (!checkbox.root.gameObject.activeInHierarchy) return;

            bool interactable = checkbox.button.interactable;
            snapshot.Toggles.Add(new NewGameMenuToggleState
            {
                Id = id,
                Label = ReadText(checkbox.label),
                Visible = true,
                Selected = checkbox.mark.gameObject.activeSelf,
                Enabled = interactable,
                LockReason = interactable ? null : disabledReason
            });
        }

        // -----------------------------------------------------------------
        // One native selection callback
        // -----------------------------------------------------------------

        public NewGameSelectCallResult ApplySelection(NewGameSelectionDecision decision)
        {
            bool invoked = false;
            try
            {
                if (decision == null || !decision.Allowed || decision.Target == null)
                {
                    return NewGameSelectCallResult.NativeNotCalled("invalid_decision");
                }

                NewGameSelectCallResult guard;
                switch (decision.Kind)
                {
                    case NewGameOptionKind.Scenario:
                        guard = GuardScenario(decision);
                        if (guard != null) return guard;
                        invoked = true;
                        InvokeVoid(typeof(NewGameSpecializationUI), "OnSlotClicked",
                            decision.Target.SlotArg);
                        return NewGameSelectCallResult.NativeCalled();

                    case NewGameOptionKind.Perk:
                        guard = GuardPerk(decision);
                        if (guard != null) return guard;
                        invoked = true;
                        InvokeVoid(typeof(NewGamePerksUI), "OnSlotClicked", decision.Target.Id);
                        return NewGameSelectCallResult.NativeCalled();

                    case NewGameOptionKind.Difficulty:
                        guard = GuardDifficulty(decision);
                        if (guard != null) return guard;
                        invoked = true;
                        InvokeVoid(typeof(NewGameDifficultyUI), "OnPlateClicked",
                            decision.Target.SlotArg);
                        return NewGameSelectCallResult.NativeCalled();

                    default:
                        return NewGameSelectCallResult.NativeNotCalled("wrong_phase");
                }
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                Debug.LogWarning("[PawnMind] new-game selection callback threw: " + inner.GetType().Name);
                return invoked
                    ? NativeCalledWithError("native_error")
                    : NewGameSelectCallResult.NativeNotCalled("native_error");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game selection failed: " + e.GetType().Name);
                return invoked
                    ? NativeCalledWithError("native_error")
                    : NewGameSelectCallResult.NativeNotCalled("native_error");
            }
        }

        private static NewGameSelectCallResult NativeCalledWithError(string error)
        {
            // The callback was attempted, so the caller must still read back and
            // verify. Do not turn an exception into a fake success or a retry.
            return new NewGameSelectCallResult { Called = true, Error = error };
        }

        private static NewGameSelectCallResult GuardScenario(NewGameSelectionDecision decision)
        {
            if (!NewGameSpecializationUI.IsOpen) return Stale();

            List<StartingScenario> scenarios =
                StaticField<List<StartingScenario>>(typeof(NewGameSpecializationUI), "scenarios");
            int page = StaticField<int>(typeof(NewGameSpecializationUI), "page");

            int index;
            if (scenarios == null
                || !int.TryParse(decision.Target.SlotArg, out index)
                || index < 0
                || index >= scenarios.Count)
            {
                return Stale();
            }

            StartingScenario scenario = scenarios[index];
            if (scenario == null
                || scenario.hidden
                || !string.Equals(scenario.id, decision.Target.Id, StringComparison.Ordinal)
                || index / ScenarioPageSize != page
                || !scenario.CanStart())
            {
                return Stale();
            }

            StartingScenario selected = NewGameSpecializationUI.Selected;
            if (selected != null
                && string.Equals(selected.id, decision.Target.Id, StringComparison.Ordinal))
            {
                return Stale();
            }

            return null;
        }

        private static NewGameSelectCallResult GuardPerk(NewGameSelectionDecision decision)
        {
            if (!NewGamePerksUI.IsOpen) return Stale();

            bool firstRun = StaticField<bool>(typeof(NewGamePerksUI), "firstRun");
            bool startingGame = StaticField<bool>(typeof(NewGamePerksUI), "startingGame");
            if (firstRun) return NewGameSelectCallResult.NativeNotCalled("first_run_locked");
            if (startingGame) return NewGameSelectCallResult.NativeNotCalled("menu_transitioning");

            List<StartingPerk> sorted =
                StaticField<List<StartingPerk>>(typeof(NewGamePerksUI), "sorted");
            List<StartingPerk> selected =
                StaticProperty<List<StartingPerk>>(typeof(NewGamePerksUI), "SelectedPerks");
            int page = StaticField<int>(typeof(NewGamePerksUI), "page");
            int selectedPage = StaticField<int>(typeof(NewGamePerksUI), "selectedPage");
            if (sorted == null || selected == null) return Stale();

            string id = decision.Target.Id;
            if (decision.Action == NewGameSelectionAction.Add)
            {
                int availableIndex = IndexOf(sorted, id);
                if (availableIndex < 0 || availableIndex / PerkPageSize != page) return Stale();
                if (IndexOf(selected, id) >= 0) return Stale();
                if (LockedBy(selected, sorted[availableIndex]) != null) return Stale();
            }
            else if (decision.Action == NewGameSelectionAction.Remove)
            {
                int selectedIndex = IndexOf(selected, id);
                if (selectedIndex < 0 || selectedIndex / SelectedPerkPageSize != selectedPage)
                {
                    return Stale();
                }
                if (CountSelected(selected, id) != 1)
                {
                    // OnSlotClicked removes all entries with this id; never allow
                    // one selection call to silently drop more than one perk.
                    return NewGameSelectCallResult.NativeNotCalled(
                        NewGameMenuErrors.DuplicateSelected);
                }

                // An available card with the same id is built first and would
                // swallow the native click. The Core rules only allow this when
                // the selected-row card itself is hovered and the available card
                // is not; verify that exact native slot state again here.
                int availableIndex = IndexOf(sorted, id);
                if (availableIndex >= 0 && availableIndex / PerkPageSize == page)
                {
                    int hoveredSelected = 0;
                    bool hoveredAvailable = false;
                    foreach (PerkSlotInfo slot in ReadPerkSlots())
                    {
                        if (!string.Equals(slot.Id, id, StringComparison.Ordinal)) continue;
                        if (slot.InSelectedRow)
                        {
                            if (slot.Hovered) hoveredSelected++;
                        }
                        else if (slot.Hovered)
                        {
                            hoveredAvailable = true;
                        }
                    }

                    if (hoveredSelected != 1)
                    {
                        return NewGameSelectCallResult.NativeNotCalled(
                            NewGameMenuErrors.AmbiguousTarget);
                    }
                    if (hoveredAvailable)
                    {
                        return NewGameSelectCallResult.NativeNotCalled(
                            NewGameMenuErrors.HoverConflict);
                    }
                }
            }
            else
            {
                return Stale();
            }

            return null;
        }

        private static NewGameSelectCallResult GuardDifficulty(NewGameSelectionDecision decision)
        {
            if (!NewGameDifficultyUI.IsOpen) return Stale();

            bool startingGame = StaticField<bool>(typeof(NewGameDifficultyUI), "startingGame");
            if (startingGame) return NewGameSelectCallResult.NativeNotCalled("menu_transitioning");

            List<Difficulty> difficulties =
                StaticField<List<Difficulty>>(typeof(NewGameDifficultyUI), "difficulties");
            int page = StaticField<int>(typeof(NewGameDifficultyUI), "page");
            int selected = StaticField<int>(typeof(NewGameDifficultyUI), "selected");

            int index;
            if (difficulties == null
                || !int.TryParse(decision.Target.SlotArg, out index)
                || index < 0
                || index >= difficulties.Count)
            {
                return Stale();
            }

            Difficulty difficulty = difficulties[index];
            if (difficulty == null
                || !string.Equals(difficulty.id, decision.Target.Id, StringComparison.Ordinal)
                || index / DifficultyPageSize != page
                || selected == index)
            {
                return Stale();
            }

            return null;
        }

        private static NewGameSelectCallResult Stale()
        {
            return NewGameSelectCallResult.NativeNotCalled("stale_menu");
        }

        private static int IndexOf(List<StartingPerk> perks, string id)
        {
            if (perks == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < perks.Count; i++)
            {
                if (perks[i] != null && string.Equals(perks[i].id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int CountSelected(List<StartingPerk> selected, string id)
        {
            if (selected == null || string.IsNullOrEmpty(id)) return 0;
            int copies = 0;
            foreach (StartingPerk perk in selected)
            {
                if (perk != null && string.Equals(perk.id, id, StringComparison.Ordinal))
                {
                    copies++;
                }
            }
            return copies;
        }

        private static string LockedBy(List<StartingPerk> selected, StartingPerk candidate)
        {
            if (selected == null || candidate == null) return null;
            foreach (StartingPerk perk in selected)
            {
                if (perk == null || string.Equals(perk.id, candidate.id, StringComparison.Ordinal))
                {
                    continue;
                }
                if (Contains(perk.incompatiblePerks, candidate.id)) return perk.id;
                if (Contains(candidate.incompatiblePerks, perk.id)) return perk.id;
            }
            return null;
        }

        private static bool Contains(List<string> values, string id)
        {
            if (values == null || string.IsNullOrEmpty(id)) return false;
            foreach (string value in values)
            {
                if (string.Equals(value, id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // S3B: one native control callback per call
        // -----------------------------------------------------------------

        public NewGameControlCallResult Open()
        {
            bool invoked = false;
            try
            {
                if (IsInGame()) return NewGameControlCallResult.NativeNotCalled("in_game");

                bool promptFailed;
                NewGameMenuPrompt prompt = ReadPrompt(out promptFailed);
                if (promptFailed)
                {
                    return NewGameControlCallResult.NativeNotCalled("prompt_read_failed");
                }
                if (prompt != null)
                {
                    return NewGameControlCallResult.NativeNotCalled("prompt_pending");
                }
                if (AnyNewGameMenuOpen())
                {
                    return NewGameControlCallResult.NativeNotCalled("menu_already_open");
                }

                MainMenuUIController menu = MainMenuUIController.Instance;
                if (menu == null)
                {
                    return NewGameControlCallResult.NativeNotCalled("no_main_menu");
                }

                invoked = true;
                menu.OnNewGameClick();
                return NewGameControlCallResult.NativeCalled();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game open failed: " + e.GetType().Name);
                return invoked
                    ? NewGameControlCallResult.NativeCalledWithError("native_error")
                    : NewGameControlCallResult.NativeNotCalled("native_error");
            }
        }

        public NewGameControlCallResult TurnPage(NewGamePageRow row, int direction)
        {
            if (direction != 1 && direction != -1)
            {
                return NewGameControlCallResult.NativeNotCalled("bad_direction");
            }

            try
            {
                if (row == NewGamePageRow.Selected)
                {
                    if (!NewGamePerksUI.IsOpen)
                    {
                        return NewGameControlCallResult.NativeNotCalled("wrong_row");
                    }

                    int page = StaticField<int>(typeof(NewGamePerksUI), "selectedPage");
                    int pageCount = InvokeStaticInt(typeof(NewGamePerksUI), "SelectedPageCount");
                    int target = page + direction;
                    if (pageCount <= 0 || target < 0 || target >= pageCount)
                    {
                        return NewGameControlCallResult.NativeNotCalled("page_out_of_range");
                    }

                    InvokeStaticVoidInt(typeof(NewGamePerksUI), "TurnSelectedPage", direction);
                    return NewGameControlCallResult.NativeCalled();
                }

                if (NewGameSpecializationUI.IsOpen)
                {
                    int page = StaticField<int>(typeof(NewGameSpecializationUI), "page");
                    int pageCount = InvokeStaticInt(typeof(NewGameSpecializationUI), "PageCount");
                    if (!PageTargetInRange(page, pageCount, direction))
                    {
                        return NewGameControlCallResult.NativeNotCalled("page_out_of_range");
                    }
                    InvokeStaticVoidInt(typeof(NewGameSpecializationUI), "TurnPage", direction);
                    return NewGameControlCallResult.NativeCalled();
                }

                if (NewGamePerksUI.IsOpen)
                {
                    int page = StaticField<int>(typeof(NewGamePerksUI), "page");
                    int pageCount = InvokeStaticInt(typeof(NewGamePerksUI), "PageCount");
                    if (!PageTargetInRange(page, pageCount, direction))
                    {
                        return NewGameControlCallResult.NativeNotCalled("page_out_of_range");
                    }
                    InvokeStaticVoidInt(typeof(NewGamePerksUI), "TurnPage", direction);
                    return NewGameControlCallResult.NativeCalled();
                }

                if (NewGameDifficultyUI.IsOpen)
                {
                    int page = StaticField<int>(typeof(NewGameDifficultyUI), "page");
                    int pageCount = InvokeStaticInt(typeof(NewGameDifficultyUI), "PageCount");
                    if (!PageTargetInRange(page, pageCount, direction))
                    {
                        return NewGameControlCallResult.NativeNotCalled("page_out_of_range");
                    }
                    InvokeStaticVoidInt(typeof(NewGameDifficultyUI), "TurnPage", direction);
                    return NewGameControlCallResult.NativeCalled();
                }

                return NewGameControlCallResult.NativeNotCalled("stale_menu");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game paging failed: " + e.GetType().Name);
                return NewGameControlCallResult.NativeNotCalled("native_error");
            }
        }

        public NewGameControlCallResult Continue(NewGameMenuPhase phase)
        {
            try
            {
                bool promptFailed;
                NewGameMenuPrompt prompt = ReadPrompt(out promptFailed);
                if (promptFailed)
                {
                    return NewGameControlCallResult.NativeNotCalled("prompt_read_failed");
                }
                if (prompt != null)
                {
                    return NewGameControlCallResult.NativeNotCalled("prompt_pending");
                }

                if (phase == NewGameMenuPhase.Difficulty)
                {
                    // Native difficulty Continue is Start; S3B must never invoke it.
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.DifficultyStartForbidden);
                }

                if (phase == NewGameMenuPhase.Specialization)
                {
                    if (!NewGameSpecializationUI.IsOpen)
                    {
                        return NewGameControlCallResult.NativeNotCalled("stale_menu");
                    }
                    NewGameSpecializationFrame frame =
                        StaticField<NewGameSpecializationFrame>(
                            typeof(NewGameSpecializationUI), "frame");
                    StartingScenario selected = NewGameSpecializationUI.Selected;
                    if (frame == null
                        || selected == null
                        || !selected.CanStart()
                        || !ButtonUsable(frame.continueButton))
                    {
                        return NewGameControlCallResult.NativeNotCalled(
                            NewGameMenuErrors.ContinueUnavailable);
                    }

                    InvokeStaticVoid(typeof(NewGameSpecializationUI), "OnContinueClicked");
                    return NewGameControlCallResult.NativeCalled();
                }

                if (phase == NewGameMenuPhase.Perks)
                {
                    if (!NewGamePerksUI.IsOpen)
                    {
                        return NewGameControlCallResult.NativeNotCalled("stale_menu");
                    }
                    if (StaticField<bool>(typeof(NewGamePerksUI), "startingGame"))
                    {
                        return NewGameControlCallResult.NativeNotCalled("menu_transitioning");
                    }

                    NewGamePerksFrame frame = StaticField<NewGamePerksFrame>(
                        typeof(NewGamePerksUI), "frame");
                    if (frame == null
                        || !ButtonUsable(frame.continueButton)
                        || !InvokeStaticBool(typeof(NewGamePerksUI), "CanStart"))
                    {
                        return NewGameControlCallResult.NativeNotCalled(
                            NewGameMenuErrors.ContinueUnavailable);
                    }

                    InvokeStaticVoid(typeof(NewGamePerksUI), "OnContinueClicked");
                    return NewGameControlCallResult.NativeCalled();
                }

                return NewGameControlCallResult.NativeNotCalled("wrong_phase");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game continue failed: " + e.GetType().Name);
                return NewGameControlCallResult.NativeNotCalled("native_error");
            }
        }

        public NewGameControlCallResult FocusSelectedPerk(string id)
        {
            try
            {
                if (!NewGamePerksUI.IsOpen)
                {
                    return NewGameControlCallResult.NativeNotCalled("stale_menu");
                }
                if (StaticField<bool>(typeof(NewGamePerksUI), "firstRun"))
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.FirstRunLocked);
                }
                if (StaticField<bool>(typeof(NewGamePerksUI), "startingGame"))
                {
                    return NewGameControlCallResult.NativeNotCalled("menu_transitioning");
                }

                List<StartingPerk> selected =
                    StaticProperty<List<StartingPerk>>(typeof(NewGamePerksUI), "SelectedPerks");
                int selectedPage = StaticField<int>(typeof(NewGamePerksUI), "selectedPage");
                if (selected == null || string.IsNullOrEmpty(id))
                {
                    return NewGameControlCallResult.NativeNotCalled("unknown_option");
                }

                int index = IndexOf(selected, id);
                if (index < 0 || index / SelectedPerkPageSize != selectedPage)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.SelectedRowNotVisible);
                }
                if (CountSelected(selected, id) != 1)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.DuplicateSelected);
                }

                PerkSlotInfo target = null;
                int selectedViews = 0;
                bool availableHovered = false;
                foreach (PerkSlotInfo slot in ReadPerkSlots())
                {
                    if (!string.Equals(slot.Id, id, StringComparison.Ordinal)) continue;
                    if (slot.InSelectedRow)
                    {
                        selectedViews++;
                        target = slot;
                    }
                    else if (slot.Hovered)
                    {
                        availableHovered = true;
                    }
                }

                if (availableHovered)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.HoverConflict);
                }
                if (selectedViews != 1 || target == null)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.AmbiguousTarget);
                }

                WildNetworkSlot slotComponent = SlotComponent(target);
                if (slotComponent == null)
                {
                    return NewGameControlCallResult.NativeNotCalled("no_hover_target");
                }

                // Invoke the real UI hover path only; do not assign hovered/views.
                slotComponent.OnPointerEnter(null);
                return NewGameControlCallResult.NativeCalled();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game focus failed: " + e.GetType().Name);
                return NewGameControlCallResult.NativeNotCalled("native_error");
            }
        }

        public NewGameControlCallResult BlurPerk(string id)
        {
            try
            {
                if (!NewGamePerksUI.IsOpen)
                {
                    return NewGameControlCallResult.NativeNotCalled("stale_menu");
                }
                if (string.IsNullOrEmpty(id))
                {
                    return NewGameControlCallResult.NativeNotCalled("empty_id");
                }

                PerkSlotInfo target = null;
                int count = 0;
                foreach (PerkSlotInfo slot in ReadPerkSlots())
                {
                    if (!slot.Hovered || !string.Equals(slot.Id, id, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    count++;
                    target = slot;
                }

                if (count == 0)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.NotHovered);
                }
                if (count > 1)
                {
                    return NewGameControlCallResult.NativeNotCalled(
                        NewGameMenuErrors.AmbiguousHover);
                }

                WildNetworkSlot slotComponent = SlotComponent(target);
                if (slotComponent == null)
                {
                    return NewGameControlCallResult.NativeNotCalled("no_hover_target");
                }

                // Invoke the real UI exit path only.
                slotComponent.OnPointerExit(null);
                return NewGameControlCallResult.NativeCalled();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] new-game blur failed: " + e.GetType().Name);
                return NewGameControlCallResult.NativeNotCalled("native_error");
            }
        }

        private static WildNetworkSlot SlotComponent(PerkSlotInfo slot)
        {
            if (slot == null || slot.FrameImage == null) return null;
            return slot.FrameImage.GetComponent<WildNetworkSlot>();
        }

        private static bool PageTargetInRange(int page, int pageCount, int direction)
        {
            int target = page + direction;
            return pageCount > 0 && target >= 0 && target < pageCount;
        }

        private static bool ButtonUsable(Button button)
        {
            return button != null
                && button.gameObject != null
                && button.gameObject.activeInHierarchy
                && button.interactable;
        }

        private static bool IsInGame()
        {
            try
            {
                // HasInstance must be used here: the Instance getter creates an
                // empty PlayerStore, which would make the main menu look in-game.
                return PlayerStore.HasInstance;
            }
            catch (Exception)
            {
                // If presence cannot be determined, do not open a new-game menu.
                return true;
            }
        }

        private static bool AnyNewGameMenuOpen()
        {
            try
            {
                return NewGameSpecializationUI.IsOpen
                    || NewGamePerksUI.IsOpen
                    || NewGameDifficultyUI.IsOpen;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // -----------------------------------------------------------------
        // Reflection helpers (main thread only)
        // -----------------------------------------------------------------

        private static bool TryReadOpen(Func<bool> getter, ref bool failed)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                failed = true;
                return false;
            }
        }

        private static T StaticField<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(name, StaticMembers);
            if (field == null) return default(T);
            object value = field.GetValue(null);
            return value is T typed ? typed : default(T);
        }

        private static T StaticProperty<T>(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, StaticMembers);
            if (property == null) return default(T);
            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null) return default(T);
            object value = getter.Invoke(null, null);
            return value is T typed ? typed : default(T);
        }

        private static int InvokeStaticInt(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, StaticMembers);
            if (method == null || method.GetParameters().Length != 0) return 0;
            object value = method.Invoke(null, null);
            return value is int typed ? typed : 0;
        }

        private static void InvokeVoid(Type type, string name, string argument)
        {
            MethodInfo method = type.GetMethod(
                name,
                StaticMembers,
                null,
                new[] { typeof(string) },
                null);
            if (method == null)
            {
                throw new MissingMethodException(type.Name, name);
            }
            method.Invoke(null, new object[] { argument });
        }

        private static void InvokeStaticVoid(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, StaticMembers, null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new MissingMethodException(type.Name, name);
            }
            method.Invoke(null, null);
        }

        private static void InvokeStaticVoidInt(Type type, string name, int argument)
        {
            MethodInfo method = type.GetMethod(
                name,
                StaticMembers,
                null,
                new[] { typeof(int) },
                null);
            if (method == null)
            {
                throw new MissingMethodException(type.Name, name);
            }
            method.Invoke(null, new object[] { argument });
        }

        private static bool InvokeStaticBool(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, StaticMembers, null, Type.EmptyTypes, null);
            if (method == null) return false;
            object value = method.Invoke(null, null);
            return value is bool typed && typed;
        }

        private static int ConstInt(Type type, string name)
        {
            FieldInfo field = type.GetField(name, StaticMembers);
            if (field == null || !field.IsLiteral) return 0;
            object value = field.GetRawConstantValue();
            return value is int typed ? typed : 0;
        }

        private static NewGameMenuButtonState ReadButton(Button button, TextMeshProUGUI label)
        {
            bool visible = button != null
                && button.gameObject != null
                && button.gameObject.activeInHierarchy;
            return new NewGameMenuButtonState
            {
                Label = ReadText(label),
                Visible = visible,
                Enabled = visible && button.interactable
            };
        }

        private static string ReadText(TextMeshProUGUI label)
        {
            try
            {
                return label == null ? null : label.text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddLines(List<string> target, List<string> lines)
        {
            if (target == null || lines == null) return;
            foreach (string line in lines)
            {
                if (!string.IsNullOrEmpty(line)) target.Add(line);
            }
        }

        private static NewGameMenuSnapshot ErrorSnapshot(string error)
        {
            return new NewGameMenuSnapshot
            {
                Phase = NewGameMenuPhase.Closed,
                PhaseError = error
            };
        }
    }
}
