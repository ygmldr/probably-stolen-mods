namespace PawnMind.Core
{
    /// <summary>
    /// Stable selection/readback error codes. Callers (and later the MCP layer)
    /// can branch on these without parsing prose.
    /// </summary>
    public static class NewGameMenuErrors
    {
        public const string MenuUnavailable = "menu_unavailable";
        public const string MenuClosed = "menu_closed";
        public const string MenuTransitioning = "menu_transitioning";
        public const string PromptPending = "prompt_pending";
        public const string MenuAlreadyOpen = "menu_already_open";
        public const string BadDirection = "bad_direction";
        public const string PageOutOfRange = "page_out_of_range";
        public const string WrongRow = "wrong_row";
        public const string ContinueUnavailable = "continue_unavailable";
        public const string DifficultyStartForbidden = "difficulty_start_forbidden";
        public const string HoverConflict = "hover_conflict";
        public const string NotHovered = "not_hovered";
        public const string AmbiguousHover = "ambiguous_hover";
        public const string WrongPhase = "wrong_phase";
        public const string EmptyId = "empty_id";
        public const string UnknownOption = "unknown_option";
        public const string OptionHidden = "option_hidden";
        public const string OptionNotOnPage = "option_not_on_page";
        public const string OptionLocked = "option_locked";
        public const string AlreadySelected = "already_selected";
        public const string AmbiguousOption = "ambiguous_option";
        public const string AmbiguousTarget = "ambiguous_target";
        public const string DuplicateSelected = "duplicate_selected";
        public const string SelectedRowNotVisible = "selected_row_not_visible";
        public const string FirstRunLocked = "first_run_locked";
        public const string Incompatible = "incompatible";
        public const string InsufficientPoints = "insufficient_points";
        public const string PerkLimitReached = "perk_limit_reached";
        public const string NativeNotCalled = "native_not_called";
        public const string ReadbackUnavailable = "readback_unavailable";
        public const string ReadbackMismatch = "readback_mismatch";
        public const string ReadbackPhaseChanged = "readback_phase_changed";
    }

    /// <summary>
    /// One-step selection rules. Decide never touches game state or Unity; it
    /// only answers whether the native UI's click handler would perform the
    /// requested change and, if so, which exact native callback argument to use.
    /// </summary>
    public static class NewGameMenuRules
    {
        public static NewGameSelectionDecision Decide(
            NewGameMenuSnapshot snapshot,
            NewGameOptionKind kind,
            string id)
        {
            if (snapshot == null) return Reject(kind, NewGameMenuErrors.MenuUnavailable, null);
            if (snapshot.Prompt != null)
            {
                // A visible first-run/confirmation prompt is an unknown fork.
                // Never select under it and never treat it as the next phase.
                return Reject(kind, NewGameMenuErrors.PromptPending, snapshot.Prompt.Id);
            }
            if (!string.IsNullOrEmpty(snapshot.PhaseError))
            {
                return Reject(kind, snapshot.PhaseError, snapshot.PhaseError);
            }
            if (snapshot.StartingGame)
            {
                return Reject(kind, NewGameMenuErrors.MenuTransitioning, "start_in_progress");
            }
            if (snapshot.Phase == NewGameMenuPhase.Closed)
            {
                return Reject(kind, NewGameMenuErrors.MenuClosed, null);
            }

            NewGameMenuPhase expected = PhaseFor(kind);
            if (expected != snapshot.Phase)
            {
                return Reject(kind, NewGameMenuErrors.WrongPhase, "phase=" + snapshot.Phase);
            }

            string lookup = id == null ? null : id.Trim();
            if (string.IsNullOrEmpty(lookup))
            {
                return Reject(kind, NewGameMenuErrors.EmptyId, null);
            }

            int matchCount = 0;
            int hiddenCount = 0;
            int visibleCount = 0;
            NewGameCandidate visible = null;
            NewGameCandidate hidden = null;
            if (snapshot.Candidates != null)
            {
                foreach (NewGameCandidate candidate in snapshot.Candidates)
                {
                    if (candidate == null || !string.Equals(candidate.Id, lookup, System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchCount++;
                    if (candidate.Hidden)
                    {
                        hiddenCount++;
                        hidden = candidate;
                        continue;
                    }

                    if (NewGameMenuCandidates.IsOnCurrentPage(snapshot, candidate))
                    {
                        visibleCount++;
                        visible = candidate;
                    }
                }
            }

            bool isSelected = kind == NewGameOptionKind.Perk
                && NewGameMenuPerks.IsSelected(snapshot, lookup);

            if (matchCount == 0 && !isSelected)
            {
                return Reject(kind, NewGameMenuErrors.UnknownOption, "id=" + lookup);
            }
            if (visibleCount == 0 && !isSelected)
            {
                if (hiddenCount > 0)
                {
                    return Reject(kind, NewGameMenuErrors.OptionHidden, hidden.LockReason);
                }
                return Reject(kind, NewGameMenuErrors.OptionNotOnPage, "id=" + lookup);
            }
            if (hiddenCount > 0)
            {
                // A hidden definition sharing an id with a visible one is not a
                // state the adapter should ever produce; refuse instead of guessing.
                return Reject(kind, NewGameMenuErrors.AmbiguousOption, "hidden_and_visible");
            }

            if (kind == NewGameOptionKind.Perk)
            {
                return DecidePerk(snapshot, lookup, visibleCount, visible, isSelected);
            }

            if (visibleCount > 1)
            {
                return Reject(kind, NewGameMenuErrors.AmbiguousOption, "duplicate_visible_id");
            }

            bool selected = snapshot.HasSelection
                && string.Equals(snapshot.SelectedId, lookup, System.StringComparison.Ordinal);
            if (selected)
            {
                return Reject(kind, NewGameMenuErrors.AlreadySelected, "id=" + lookup);
            }
            if (!visible.Enabled)
            {
                return Reject(kind, NewGameMenuErrors.OptionLocked, visible.LockReason);
            }

            return Allow(kind, NewGameSelectionAction.Set, visible, null);
        }

        private static NewGameSelectionDecision DecidePerk(
            NewGameMenuSnapshot snapshot,
            string lookup,
            int visibleCount,
            NewGameCandidate visible,
            bool isSelected)
        {
            if (snapshot.FirstRun)
            {
                return Reject(NewGameOptionKind.Perk,
                    NewGameMenuErrors.FirstRunLocked, "totalRunCount==0");
            }

            int selectedCopies = NewGameMenuPerks.SelectedCopies(snapshot, lookup);
            if (isSelected && selectedCopies > 1)
            {
                // Native OnSlotClicked uses SelectedPerks.RemoveAll(id); one click
                // would remove multiple entries, so a one-step removal is ambiguous.
                return Reject(NewGameOptionKind.Perk,
                    NewGameMenuErrors.DuplicateSelected,
                    "selected_copies=" + selectedCopies);
            }

            bool rowVisible = NewGameMenuPerks.IsSelectedRowVisible(snapshot, lookup);

            if (!isSelected)
            {
                if (visibleCount == 0)
                {
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.OptionNotOnPage, "id=" + lookup);
                }
                if (visibleCount > 1)
                {
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.AmbiguousOption, "duplicate_visible_id");
                }
                if (rowVisible)
                {
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.AmbiguousTarget, "selected_row_without_selected_state");
                }

                string lockReason = NewGameMenuPerks.LockReason(snapshot, visible);
                if (lockReason != null)
                {
                    if (lockReason.StartsWith("incompatible:", System.StringComparison.Ordinal))
                    {
                        return Reject(NewGameOptionKind.Perk,
                            NewGameMenuErrors.Incompatible, lockReason);
                    }
                    if (lockReason == "first_run")
                    {
                        return Reject(NewGameOptionKind.Perk,
                            NewGameMenuErrors.FirstRunLocked, lockReason);
                    }
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.OptionLocked, lockReason);
                }

                int used = NewGameMenuPerks.UsedPoints(snapshot);
                if (used + visible.Cost > snapshot.MaxPerkPoints)
                {
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.InsufficientPoints,
                        "used=" + used + ",cost=" + visible.Cost + ",max=" + snapshot.MaxPerkPoints);
                }

                int count = snapshot.SelectedPerks == null ? 0 : snapshot.SelectedPerks.Count;
                int maxCount = NewGameMenuPerks.MaxCount(snapshot) + visible.MaxSlot;
                if (count + 1 > maxCount)
                {
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.PerkLimitReached,
                        "count=" + (count + 1) + ",max=" + maxCount);
                }

                return Allow(NewGameOptionKind.Perk, NewGameSelectionAction.Add, visible, null);
            }

            // The perk is selected. One native id click can only deselect it when
            // the selected-row card is the single matching view: an available
            // card with the same id is built first and would swallow the click.
            // The only exception is a previously verified native hover on the
            // selected-row card (and no same-id available hover).
            if (visibleCount > 0)
            {
                if (rowVisible)
                {
                    if (NewGameMenuHover.IsSelectedRowHovered(snapshot, lookup)
                        && !NewGameMenuHover.IsAvailableHovered(snapshot, lookup))
                    {
                        return Allow(NewGameOptionKind.Perk, NewGameSelectionAction.Remove,
                            SelectedTarget(snapshot, lookup), "hovered_selected_row");
                    }
                    return Reject(NewGameOptionKind.Perk,
                        NewGameMenuErrors.AmbiguousTarget, "available_card_shadows_selected_row");
                }
                return Reject(NewGameOptionKind.Perk,
                    NewGameMenuErrors.SelectedRowNotVisible, "selected_row_is_on_another_page");
            }
            if (!rowVisible)
            {
                return Reject(NewGameOptionKind.Perk,
                    NewGameMenuErrors.SelectedRowNotVisible, "selected_row_is_on_another_page");
            }

            return Allow(NewGameOptionKind.Perk, NewGameSelectionAction.Remove,
                SelectedTarget(snapshot, lookup), null);
        }

        private static NewGameMenuPhase PhaseFor(NewGameOptionKind kind)
        {
            switch (kind)
            {
                case NewGameOptionKind.Scenario: return NewGameMenuPhase.Specialization;
                case NewGameOptionKind.Perk: return NewGameMenuPhase.Perks;
                case NewGameOptionKind.Difficulty: return NewGameMenuPhase.Difficulty;
                default: return NewGameMenuPhase.Closed;
            }
        }

        private static NewGameCandidate SelectedTarget(NewGameMenuSnapshot snapshot, string id)
        {
            NewGameSelectedPerk selected = null;
            if (snapshot.SelectedPerks != null)
            {
                foreach (NewGameSelectedPerk perk in snapshot.SelectedPerks)
                {
                    if (perk != null && string.Equals(perk.Id, id, System.StringComparison.Ordinal))
                    {
                        selected = perk;
                        break;
                    }
                }
            }

            return new NewGameCandidate
            {
                Id = id,
                SlotArg = id,
                Page = snapshot.SelectedPage,
                Visible = true,
                DisplayName = selected == null ? null : selected.DisplayName,
                Description = selected == null ? null : selected.Description,
                Cost = selected == null ? 0 : selected.Cost,
                MaxSlot = selected == null ? 0 : selected.MaxSlot
            };
        }

        private static NewGameSelectionDecision Allow(
            NewGameOptionKind kind,
            NewGameSelectionAction action,
            NewGameCandidate target,
            string reason)
        {
            return new NewGameSelectionDecision
            {
                Allowed = true,
                Error = null,
                Reason = reason,
                Kind = kind,
                Action = action,
                Target = target
            };
        }

        private static NewGameSelectionDecision Reject(
            NewGameOptionKind kind,
            string error,
            string reason)
        {
            return new NewGameSelectionDecision
            {
                Allowed = false,
                Error = error,
                Reason = reason,
                Kind = kind,
                Action = NewGameSelectionAction.None,
                Target = null
            };
        }
    }

    /// <summary>
    /// Verifies that the one native callback actually produced the expected
    /// state. A mismatch is always a failure, never a success.
    /// </summary>
    public static class NewGameMenuReadbackRules
    {
        public static NewGameReadback Verify(
            NewGameMenuSnapshot before,
            NewGameSelectionDecision decision,
            NewGameMenuSnapshot after)
        {
            if (before == null || decision == null || !decision.Allowed || decision.Target == null)
            {
                return Mismatch(NewGameMenuErrors.ReadbackMismatch, "invalid_readback_request", null);
            }
            if (after == null)
            {
                return Mismatch(NewGameMenuErrors.ReadbackUnavailable, "after=null", null);
            }
            if (!string.IsNullOrEmpty(after.PhaseError))
            {
                return Mismatch(after.PhaseError, "after_error", after.PhaseError);
            }
            if (after.Phase != before.Phase)
            {
                return new NewGameReadback
                {
                    Status = NewGameReadbackStatus.PhaseChanged,
                    Error = NewGameMenuErrors.ReadbackPhaseChanged,
                    Expected = before.Phase.ToString(),
                    Actual = after.Phase.ToString()
                };
            }

            switch (decision.Action)
            {
                case NewGameSelectionAction.Set:
                    if (!after.HasSelection
                        || !string.Equals(after.SelectedId, decision.Target.Id, System.StringComparison.Ordinal))
                    {
                        return Mismatch(
                            NewGameMenuErrors.ReadbackMismatch,
                            decision.Target.Id,
                            after.HasSelection ? after.SelectedId : "<none>");
                    }
                    return Confirmed();

                case NewGameSelectionAction.Add:
                {
                    int beforeCount = Count(before.SelectedPerks);
                    int afterCount = Count(after.SelectedPerks);
                    if (afterCount != beforeCount + 1)
                    {
                        return Mismatch(
                            NewGameMenuErrors.ReadbackMismatch,
                            "selected_count=" + (beforeCount + 1),
                            "selected_count=" + afterCount);
                    }
                    if (!NewGameMenuPerks.IsSelected(after, decision.Target.Id))
                    {
                        return Mismatch(NewGameMenuErrors.ReadbackMismatch,
                            "selected=" + decision.Target.Id, "not_selected");
                    }
                    int beforeUsed = NewGameMenuPerks.UsedPoints(before);
                    int afterUsed = NewGameMenuPerks.UsedPoints(after);
                    if (afterUsed != beforeUsed + decision.Target.Cost)
                    {
                        return Mismatch(
                            NewGameMenuErrors.ReadbackMismatch,
                            "used_points=" + (beforeUsed + decision.Target.Cost),
                            "used_points=" + afterUsed);
                    }
                    return Confirmed();
                }

                case NewGameSelectionAction.Remove:
                {
                    int beforeCopies = NewGameMenuPerks.SelectedCopies(before, decision.Target.Id);
                    if (beforeCopies != 1)
                    {
                        return Mismatch(
                            NewGameMenuErrors.ReadbackMismatch,
                            "selected_copies=1",
                            "selected_copies=" + beforeCopies);
                    }

                    int beforeCount = Count(before.SelectedPerks);
                    int afterCount = Count(after.SelectedPerks);
                    if (NewGameMenuPerks.IsSelected(after, decision.Target.Id)
                        || afterCount != beforeCount - 1)
                    {
                        return Mismatch(
                            NewGameMenuErrors.ReadbackMismatch,
                            "selected_count=" + (beforeCount - 1),
                            "selected_count=" + afterCount);
                    }
                    return Confirmed();
                }

                default:
                    return Mismatch(NewGameMenuErrors.ReadbackMismatch, "action", "none");
            }
        }

        private static int Count(System.Collections.Generic.List<NewGameSelectedPerk> perks)
        {
            return perks == null ? 0 : perks.Count;
        }

        private static NewGameReadback Confirmed()
        {
            return new NewGameReadback { Status = NewGameReadbackStatus.Confirmed };
        }

        private static NewGameReadback Mismatch(string error, string expected, string actual)
        {
            return new NewGameReadback
            {
                Status = NewGameReadbackStatus.Mismatch,
                Error = error,
                Expected = expected,
                Actual = actual
            };
        }
    }
}
