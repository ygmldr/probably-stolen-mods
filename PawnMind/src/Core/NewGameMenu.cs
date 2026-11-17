using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// New-game menu phases, in native order: scenario -> perks -> difficulty ->
    /// native Continue/Start (not part of S3A).
    /// </summary>
    public enum NewGameMenuPhase
    {
        Closed,
        Specialization,
        Perks,
        Difficulty
    }

    /// <summary>
    /// Public semantic option kinds. Their ids are scenario.id / perk.id /
    /// difficulty.id. The native UI callback arguments are different for scenarios
    /// and difficulties (the visible-list index) and are kept in
    /// <see cref="NewGameCandidate.SlotArg"/> out of the public view.
    /// </summary>
    public enum NewGameOptionKind
    {
        Scenario,
        Perk,
        Difficulty
    }

    public enum NewGameSelectionAction
    {
        None,
        Add,
        Remove,
        Set
    }

    /// <summary>
    /// Raw menu data read by the Game adapter from the native UI's own lists.
    /// This object is internal to the Core/adapter handoff; only
    /// <see cref="NewGameMenuView"/> is meant to be exposed to the AI.
    /// </summary>
    public sealed class NewGameMenuSnapshot
    {
        public NewGameMenuPhase Phase;
        public string PhaseError;
        public string Header;
        public bool FirstRun;
        public bool StartingGame;
        public int Page;
        public int PageCount;
        public int SelectedPage;
        public int MaxPerkPoints = 3;
        public int BasePerkCount = 4;
        public string PointsText;
        public string CountText;
        public string PerkInstruction;
        public string SelectedId;
        public bool HasSelection;
        public NewGameMenuDetail Detail;
        public NewGameMenuPrompt Prompt;
        public string HoveredId;
        public bool HoveredInSelectedRow;
        public List<NewGameCandidate> Candidates = new List<NewGameCandidate>();
        public List<NewGameSelectedPerk> SelectedPerks = new List<NewGameSelectedPerk>();
        public List<NewGameMenuToggleState> Toggles = new List<NewGameMenuToggleState>();
        public NewGameMenuButtonState Continue = new NewGameMenuButtonState();
    }

    /// <summary>
    /// One candidate the native UI knows about. Visible means "built on the
    /// current page"; Hidden means "must never be offered".
    /// </summary>
    public sealed class NewGameCandidate
    {
        public string Id;
        public string SlotArg;
        public int Page;
        public bool Visible;
        public bool Hidden;
        public bool Enabled = true;
        public string LockReason;
        public string DisplayName;
        public string Description;
        public string Tag;
        public List<string> Lines = new List<string>();
        public int Cost;
        public int MaxSlot;
        public List<string> IncompatibleIds = new List<string>();
        public bool Hovered;
    }

    public sealed class NewGameSelectedPerk
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public int Cost;
        public int MaxSlot;
        public List<string> IncompatibleIds = new List<string>();
        public bool Hovered;
    }

    public sealed class NewGameMenuDetail
    {
        public string Title;
        public string Tag;
        public string Description;
        public string Warning;
        public List<string> Lines = new List<string>();
    }

    public sealed class NewGameMenuToggleState
    {
        public string Id;
        public string Label;
        public bool Visible;
        public bool Selected;
        public bool Enabled;
        public string LockReason;
    }

    public sealed class NewGameMenuButtonState
    {
        public string Label;
        public bool Visible;
        public bool Enabled;
    }

    /// <summary>
    /// Sanitized, AI-facing candidate view. It contains no slot-callback
    /// arguments and only the current page's visible options.
    /// </summary>
    public sealed class NewGameOptionView
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public string Tag;
        public List<string> Lines = new List<string>();
        public int Cost;
        public int MaxSlot;
        public bool Selected;
        public bool Enabled;
        public string LockReason;
        public bool Hovered;
    }

    public sealed class NewGamePerkState
    {
        public bool FirstRun;
        public int MaxPoints;
        public int BaseCount;
        public int UsedPoints;
        public int RemainingPoints;
        public int SelectedCount;
        public int MaxCount;
        public string PointsText;
        public string CountText;
        public string Instruction;
    }

    public sealed class NewGameMenuView
    {
        public NewGameMenuPhase Phase;
        public string PhaseError;
        public string Header;
        public int Page;
        public int PageCount;
        public bool HasPreviousPage;
        public bool HasNextPage;
        public List<NewGameOptionView> Options = new List<NewGameOptionView>();
        public int SelectedPage;
        public int SelectedPageCount;
        public bool SelectedRowPaged;
        public bool HasPreviousSelectedPage;
        public bool HasNextSelectedPage;
        public List<NewGameOptionView> SelectedOptions = new List<NewGameOptionView>();
        public string SelectedId;
        public bool HasSelection;
        public NewGameMenuDetail Detail;
        public NewGamePerkState Perks;
        public List<NewGameMenuToggleState> Toggles = new List<NewGameMenuToggleState>();
        public NewGameMenuButtonState Continue = new NewGameMenuButtonState();
        public NewGameMenuPrompt Prompt;
        public bool PromptPending;
        public string HoveredId;
        public bool HoveredInSelectedRow;
    }

    /// <summary>
    /// Game adapter port. Read must be a pure main-thread read; ApplySelection
    /// must invoke exactly one native UI selection callback and must not
    /// continue, confirm or start a new game.
    /// </summary>
    public interface INewGameMenuPort
    {
        NewGameMenuSnapshot Read();
        NewGameSelectCallResult ApplySelection(NewGameSelectionDecision decision);
    }

    public sealed class NewGameSelectCallResult
    {
        public bool Called;
        public string Error;

        public static NewGameSelectCallResult NativeCalled()
        {
            return new NewGameSelectCallResult { Called = true };
        }

        public static NewGameSelectCallResult NativeNotCalled(string error)
        {
            return new NewGameSelectCallResult { Called = false, Error = error };
        }
    }

    public sealed class NewGameSelectionDecision
    {
        public bool Allowed;
        public string Error;
        public string Reason;
        public NewGameOptionKind Kind;
        public NewGameSelectionAction Action;
        public NewGameCandidate Target;
    }

    public enum NewGameReadbackStatus
    {
        Confirmed,
        Mismatch,
        PhaseChanged
    }

    public sealed class NewGameReadback
    {
        public NewGameReadbackStatus Status;
        public string Error;
        public string Expected;
        public string Actual;
    }

    public sealed class NewGameMenuSelectResult
    {
        public bool Success;
        public string Error;
        public string Warning;
        public string Reason;
        public NewGameOptionKind Kind;
        public string RequestedId;
        public NewGameSelectionAction Action;
        public string TargetId;
        public NewGameReadback Readback;
        public NewGameMenuView View;

        /// <summary>R4/F4b: whether the native selection callback was issued.</summary>
        public bool Dispatched;
    }

    /// <summary>
    /// Builds the AI-facing view. Hidden candidates, off-page candidates and
    /// perk cards that are not on the selected-row page are filtered out here,
    /// so callers never receive undisplayed options.
    /// </summary>
    public static class NewGameMenuViewBuilder
    {
        public const int SelectedPerkPageSize = 10;

        public static NewGameMenuView Build(NewGameMenuSnapshot snapshot)
        {
            NewGameMenuView view = new NewGameMenuView();
            if (snapshot == null)
            {
                view.Phase = NewGameMenuPhase.Closed;
                view.PhaseError = NewGameMenuErrors.MenuUnavailable;
                return view;
            }

            view.Phase = snapshot.Phase;
            view.PhaseError = snapshot.PhaseError;
            view.Header = snapshot.Header;
            view.Page = snapshot.Page;
            view.PageCount = snapshot.PageCount;
            view.HasPreviousPage = snapshot.Page > 0;
            view.HasNextPage = snapshot.PageCount > 0 && snapshot.Page + 1 < snapshot.PageCount;
            view.SelectedId = snapshot.SelectedId;
            view.HasSelection = snapshot.HasSelection;
            view.Continue = CloneButton(snapshot.Continue);
            view.Prompt = ClonePrompt(snapshot.Prompt);
            view.PromptPending = view.Prompt != null;
            view.HoveredId = snapshot.HoveredId;
            view.HoveredInSelectedRow = snapshot.HoveredInSelectedRow;

            if (snapshot.Phase == NewGameMenuPhase.Closed)
            {
                return view;
            }

            if (!string.IsNullOrEmpty(snapshot.PhaseError))
            {
                return view;
            }

            view.Detail = CloneDetail(snapshot.Detail);

            if (snapshot.Candidates != null)
            {
                foreach (NewGameCandidate candidate in snapshot.Candidates)
                {
                    if (!NewGameMenuCandidates.IsOnCurrentPage(snapshot, candidate))
                    {
                        continue;
                    }

                    if (snapshot.Phase == NewGameMenuPhase.Perks)
                    {
                        view.Options.Add(BuildPerkOption(snapshot, candidate));
                    }
                    else
                    {
                        view.Options.Add(BuildSetOption(snapshot, candidate));
                    }
                }
            }

            if (snapshot.Phase == NewGameMenuPhase.Perks)
            {
                BuildPerkState(snapshot, view);
            }

            if (snapshot.Toggles != null)
            {
                foreach (NewGameMenuToggleState toggle in snapshot.Toggles)
                {
                    if (toggle == null || !toggle.Visible)
                    {
                        continue;
                    }
                    view.Toggles.Add(CloneToggle(toggle));
                }
            }

            ApplyHover(view);
            return view;
        }

        private static void ApplyHover(NewGameMenuView view)
        {
            if (view == null) return;

            string hoveredId = view.HoveredId;
            bool hoveredSelected = view.HoveredInSelectedRow;
            int distinct = 0;
            string found = null;

            if (view.Options != null)
            {
                foreach (NewGameOptionView option in view.Options)
                {
                    if (option == null || !option.Hovered) continue;
                    if (found == null) found = option.Id;
                    else if (!string.Equals(found, option.Id, System.StringComparison.Ordinal))
                    {
                        distinct++;
                    }
                }
            }
            if (view.SelectedOptions != null)
            {
                foreach (NewGameOptionView option in view.SelectedOptions)
                {
                    if (option == null || !option.Hovered) continue;
                    hoveredSelected = true;
                    if (found == null) found = option.Id;
                    else if (!string.Equals(found, option.Id, System.StringComparison.Ordinal))
                    {
                        distinct++;
                    }
                }
            }

            if (string.IsNullOrEmpty(hoveredId) && found != null && distinct == 0)
            {
                hoveredId = found;
            }
            view.HoveredId = hoveredId;
            view.HoveredInSelectedRow = hoveredSelected;
        }

        private static NewGameOptionView BuildSetOption(
            NewGameMenuSnapshot snapshot,
            NewGameCandidate candidate)
        {
            bool selected = snapshot.HasSelection
                && string.Equals(snapshot.SelectedId, candidate.Id, System.StringComparison.Ordinal);

            NewGameOptionView option = BuildOption(candidate);
            option.Selected = selected;
            if (selected)
            {
                option.Enabled = false;
                option.LockReason = "already_selected";
            }
            else
            {
                option.Enabled = candidate.Enabled;
                option.LockReason = candidate.LockReason;
            }
            return option;
        }

        private static NewGameOptionView BuildPerkOption(
            NewGameMenuSnapshot snapshot,
            NewGameCandidate candidate)
        {
            NewGameOptionView option = BuildOption(candidate);
            option.Selected = NewGameMenuPerks.IsSelected(snapshot, candidate.Id);
            option.LockReason = NewGameMenuPerks.LockReason(snapshot, candidate);
            option.Enabled = option.LockReason == null;
            return option;
        }

        private static void BuildPerkState(NewGameMenuSnapshot snapshot, NewGameMenuView view)
        {
            int used = NewGameMenuPerks.UsedPoints(snapshot);
            int maxCount = NewGameMenuPerks.MaxCount(snapshot);
            int count = snapshot.SelectedPerks == null ? 0 : snapshot.SelectedPerks.Count;

            view.Perks = new NewGamePerkState
            {
                FirstRun = snapshot.FirstRun,
                MaxPoints = snapshot.MaxPerkPoints,
                BaseCount = snapshot.BasePerkCount,
                UsedPoints = used,
                RemainingPoints = snapshot.MaxPerkPoints - used,
                SelectedCount = count,
                MaxCount = maxCount,
                PointsText = snapshot.PointsText,
                CountText = snapshot.CountText,
                Instruction = snapshot.PerkInstruction
            };

            int selectedPage = snapshot.SelectedPage < 0 ? 0 : snapshot.SelectedPage;
            view.SelectedPage = selectedPage;
            view.SelectedPageCount = (count + SelectedPerkPageSize - 1) / SelectedPerkPageSize;
            view.SelectedRowPaged = count > SelectedPerkPageSize;
            view.HasPreviousSelectedPage = view.SelectedRowPaged && selectedPage > 0;
            view.HasNextSelectedPage = view.SelectedRowPaged
                && selectedPage + 1 < view.SelectedPageCount;

            if (snapshot.SelectedPerks == null)
            {
                return;
            }

            int start = selectedPage * SelectedPerkPageSize;
            int end = start + SelectedPerkPageSize;
            if (end > count) end = count;
            for (int i = start; i < end; i++)
            {
                NewGameSelectedPerk perk = snapshot.SelectedPerks[i];
                if (perk == null) continue;

                NewGameOptionView option = new NewGameOptionView
                {
                    Id = perk.Id,
                    DisplayName = perk.DisplayName,
                    Description = perk.Description,
                    Cost = perk.Cost,
                    MaxSlot = perk.MaxSlot,
                    Selected = true,
                    Enabled = !snapshot.FirstRun,
                    LockReason = snapshot.FirstRun ? "first_run" : null,
                    Hovered = perk.Hovered
                };
                view.SelectedOptions.Add(option);
            }
        }

        private static NewGameOptionView BuildOption(NewGameCandidate candidate)
        {
            NewGameOptionView option = new NewGameOptionView
            {
                Id = candidate.Id,
                DisplayName = candidate.DisplayName,
                Description = candidate.Description,
                Tag = candidate.Tag,
                Cost = candidate.Cost,
                MaxSlot = candidate.MaxSlot,
                Hovered = candidate.Hovered
            };
            if (candidate.Lines != null)
            {
                option.Lines.AddRange(candidate.Lines);
            }
            return option;
        }

        private static NewGameMenuDetail CloneDetail(NewGameMenuDetail detail)
        {
            if (detail == null) return null;
            NewGameMenuDetail copy = new NewGameMenuDetail
            {
                Title = detail.Title,
                Tag = detail.Tag,
                Description = detail.Description,
                Warning = detail.Warning
            };
            if (detail.Lines != null) copy.Lines.AddRange(detail.Lines);
            return copy;
        }

        private static NewGameMenuToggleState CloneToggle(NewGameMenuToggleState toggle)
        {
            return new NewGameMenuToggleState
            {
                Id = toggle.Id,
                Label = toggle.Label,
                Visible = toggle.Visible,
                Selected = toggle.Selected,
                Enabled = toggle.Enabled,
                LockReason = toggle.LockReason
            };
        }

        private static NewGameMenuButtonState CloneButton(NewGameMenuButtonState button)
        {
            if (button == null) return new NewGameMenuButtonState();
            return new NewGameMenuButtonState
            {
                Label = button.Label,
                Visible = button.Visible,
                Enabled = button.Enabled
            };
        }

        private static NewGameMenuPrompt ClonePrompt(NewGameMenuPrompt prompt)
        {
            if (prompt == null) return null;
            return new NewGameMenuPrompt
            {
                Id = prompt.Id,
                Text = prompt.Text,
                ConfirmText = prompt.ConfirmText,
                CancelText = prompt.CancelText,
                FirstRun = prompt.FirstRun
            };
        }
    }

    /// <summary>
    /// A candidate is user-visible only when the UI has built it on the current
    /// page. Visible=true from a stale/forged snapshot is not enough.
    /// </summary>
    internal static class NewGameMenuCandidates
    {
        public static bool IsOnCurrentPage(NewGameMenuSnapshot snapshot, NewGameCandidate candidate)
        {
            return snapshot != null
                && candidate != null
                && !candidate.Hidden
                && candidate.Visible
                && candidate.Page == snapshot.Page;
        }
    }

    /// <summary>
    /// Native perk math shared by the view and the selection rules. All values
    /// are derived from the UI's selected list, not from a guessed default.
    /// </summary>
    internal static class NewGameMenuPerks
    {
        public static int UsedPoints(NewGameMenuSnapshot snapshot)
        {
            int used = 0;
            if (snapshot == null || snapshot.SelectedPerks == null) return used;
            foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
            {
                if (perk != null) used += perk.Cost;
            }
            return used;
        }

        public static int MaxCount(NewGameMenuSnapshot snapshot)
        {
            int count = snapshot == null ? 0 : snapshot.BasePerkCount;
            if (snapshot == null || snapshot.SelectedPerks == null) return count;
            foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
            {
                if (perk != null) count += perk.MaxSlot;
            }
            return count;
        }

        public static bool IsSelected(NewGameMenuSnapshot snapshot, string id)
        {
            return SelectedCopies(snapshot, id) > 0;
        }

        public static int SelectedCopies(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.SelectedPerks == null || string.IsNullOrEmpty(id))
            {
                return 0;
            }
            int copies = 0;
            foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
            {
                if (perk != null && string.Equals(perk.Id, id, System.StringComparison.Ordinal))
                {
                    copies++;
                }
            }
            return copies;
        }

        public static string LockedBy(NewGameMenuSnapshot snapshot, NewGameCandidate candidate)
        {
            if (snapshot == null || snapshot.SelectedPerks == null || candidate == null)
            {
                return null;
            }

            foreach (NewGameSelectedPerk selected in snapshot.SelectedPerks)
            {
                if (selected == null || string.Equals(selected.Id, candidate.Id, System.StringComparison.Ordinal))
                {
                    continue;
                }
                if (Contains(selected.IncompatibleIds, candidate.Id)) return selected.Id;
                if (Contains(candidate.IncompatibleIds, selected.Id)) return selected.Id;
            }
            return null;
        }

        /// <summary>
        /// Null when the native click would select it; otherwise a stable reason.
        /// </summary>
        public static string LockReason(NewGameMenuSnapshot snapshot, NewGameCandidate candidate)
        {
            if (snapshot == null || candidate == null) return "unavailable";
            if (snapshot.FirstRun) return "first_run";
            if (IsSelected(snapshot, candidate.Id)) return "already_selected";
            string lockedBy = LockedBy(snapshot, candidate);
            if (lockedBy != null) return "incompatible:" + lockedBy;
            return null;
        }

        public static bool IsSelectedRowVisible(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.SelectedPerks == null || string.IsNullOrEmpty(id))
            {
                return false;
            }
            int index = -1;
            for (int i = 0; i < snapshot.SelectedPerks.Count; i++)
            {
                NewGameSelectedPerk perk = snapshot.SelectedPerks[i];
                if (perk != null && string.Equals(perk.Id, id, System.StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return false;

            int page = snapshot.SelectedPage < 0 ? 0 : snapshot.SelectedPage;
            int start = page * NewGameMenuViewBuilder.SelectedPerkPageSize;
            return index >= start && index < start + NewGameMenuViewBuilder.SelectedPerkPageSize;
        }

        public static bool Contains(List<string> values, string id)
        {
            if (values == null || string.IsNullOrEmpty(id)) return false;
            foreach (string value in values)
            {
                if (string.Equals(value, id, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Mirrors the native CanStart: enough points and no perk-count overflow.
        /// </summary>
        public static bool CanStart(NewGameMenuSnapshot snapshot)
        {
            if (snapshot == null) return false;
            int count = snapshot.SelectedPerks == null ? 0 : snapshot.SelectedPerks.Count;
            return UsedPoints(snapshot) <= snapshot.MaxPerkPoints
                && count <= MaxCount(snapshot);
        }
    }

    /// <summary>
    /// Native hover disambiguation helpers. A hovered selected-row card is the
    /// only way the real OnSlotClicked can target the selected row when an
    /// available card with the same id is also built on this page.
    /// </summary>
    internal static class NewGameMenuHover
    {
        public static bool IsSelectedRowHovered(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.SelectedPerks == null || string.IsNullOrEmpty(id))
            {
                return false;
            }

            foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
            {
                if (perk != null
                    && perk.Hovered
                    && string.Equals(perk.Id, id, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsAvailableHovered(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.Candidates == null || string.IsNullOrEmpty(id))
            {
                return false;
            }

            foreach (NewGameCandidate candidate in snapshot.Candidates)
            {
                if (candidate != null
                    && candidate.Hovered
                    && string.Equals(candidate.Id, id, System.StringComparison.Ordinal)
                    && NewGameMenuCandidates.IsOnCurrentPage(snapshot, candidate))
                {
                    return true;
                }
            }
            return false;
        }

        public static int HoveredPerkCount(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || string.IsNullOrEmpty(id)) return 0;
            int count = 0;
            if (snapshot.Candidates != null)
            {
                foreach (NewGameCandidate candidate in snapshot.Candidates)
                {
                    if (candidate != null
                        && candidate.Hovered
                        && string.Equals(candidate.Id, id, System.StringComparison.Ordinal)
                        && NewGameMenuCandidates.IsOnCurrentPage(snapshot, candidate))
                    {
                        count++;
                    }
                }
            }
            if (snapshot.SelectedPerks != null)
            {
                foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
                {
                    if (perk != null
                        && perk.Hovered
                        && string.Equals(perk.Id, id, System.StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }
            return count;
        }
    }
}
