using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// S3B bridge orchestration for the new-game menu. It is deliberately free of
    /// Unity/game types so it can be unit tested with fake ports. Every write is
    /// additionally gated by the future S4 slot policy; until that policy exists
    /// the production wiring passes a provider that denies every write.
    /// </summary>
    public sealed class NewGameMenuService
    {
        private readonly NewGameMenuSession session;
        private readonly INewGameMenuPort port;
        private readonly INewGameMenuControlPort control;
        private readonly Func<string> writePolicy;

        public NewGameMenuService(
            INewGameMenuPort port,
            INewGameMenuControlPort control,
            Func<string> writePolicy)
        {
            this.port = port;
            this.session = new NewGameMenuSession(port);
            this.control = control;
            this.writePolicy = writePolicy;
        }

        public NewGameMenuView GetOptions()
        {
            return session.Observe();
        }

        public NewGameMenuSelectResult Select(NewGameOptionKind kind, string id)
        {
            string denied = CheckWrite();
            if (denied != null)
            {
                NewGameMenuSnapshot before = ReadSnapshot();
                return new NewGameMenuSelectResult
                {
                    Success = false,
                    Error = denied,
                    Kind = kind,
                    RequestedId = id,
                    View = NewGameMenuViewBuilder.Build(before)
                };
            }

            return session.Select(kind, id);
        }

        public NewGameMenuActionResult Open()
        {
            NewGameMenuActionResult result = NewResult();
            string denied = CheckWrite();
            if (denied != null) return DeniedResult(result, denied);

            NewGameMenuSnapshot before = ReadSnapshot();
            result.View = NewGameMenuViewBuilder.Build(before);
            if (before == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }
            if (!string.IsNullOrEmpty(before.PhaseError))
            {
                result.Error = before.PhaseError;
                return result;
            }
            if (before.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                result.PromptPending = true;
                return result;
            }
            if (before.Phase != NewGameMenuPhase.Closed)
            {
                result.Error = NewGameMenuErrors.MenuAlreadyOpen;
                return result;
            }
            if (control == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }

            NewGameControlCallResult call = control.Open();
            if (!CallSucceeded(result, call)) return result;

            NewGameMenuSnapshot after = ReadSnapshot();
            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }
            result.View = NewGameMenuViewBuilder.Build(after);
            result.PromptPending = after.Prompt != null;
            if (after.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                return result;
            }
            if (after.Phase != NewGameMenuPhase.Specialization)
            {
                return Mismatch(result, "specialization", after.Phase.ToString());
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        public NewGameMenuActionResult NextPage(NewGamePageRow row, int direction)
        {
            NewGameMenuActionResult result = NewResult();
            if (direction != 1 && direction != -1)
            {
                result.Error = NewGameMenuErrors.BadDirection;
                result.View = NewGameMenuViewBuilder.Build(ReadSnapshot());
                return result;
            }

            string denied = CheckWrite();
            if (denied != null) return DeniedResult(result, denied);

            NewGameMenuSnapshot before = ReadSnapshot();
            result.View = NewGameMenuViewBuilder.Build(before);
            string guard = GuardOpenMenu(before, result);
            if (guard != null) return result;

            int targetPage;
            if (row == NewGamePageRow.Selected)
            {
                if (before.Phase != NewGameMenuPhase.Perks)
                {
                    result.Error = NewGameMenuErrors.WrongRow;
                    return result;
                }
                int count = before.SelectedPerks == null ? 0 : before.SelectedPerks.Count;
                int selectedPageCount = (count + NewGameMenuViewBuilder.SelectedPerkPageSize - 1)
                    / NewGameMenuViewBuilder.SelectedPerkPageSize;
                targetPage = before.SelectedPage + direction;
                if (selectedPageCount <= 0 || targetPage < 0 || targetPage >= selectedPageCount)
                {
                    result.Error = NewGameMenuErrors.PageOutOfRange;
                    return result;
                }
            }
            else if (row == NewGamePageRow.Available)
            {
                targetPage = before.Page + direction;
                if (before.PageCount <= 0 || targetPage < 0 || targetPage >= before.PageCount)
                {
                    result.Error = NewGameMenuErrors.PageOutOfRange;
                    return result;
                }
            }
            else
            {
                result.Error = NewGameMenuErrors.WrongRow;
                return result;
            }

            if (control == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }

            NewGameControlCallResult call = control.TurnPage(row, direction);
            if (!CallSucceeded(result, call)) return result;

            NewGameMenuSnapshot after = ReadSnapshot();
            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }
            result.View = NewGameMenuViewBuilder.Build(after);
            result.PromptPending = after.Prompt != null;
            if (after.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                return result;
            }
            if (after.Phase != before.Phase)
            {
                return Mismatch(result, before.Phase.ToString(), after.Phase.ToString());
            }
            if (row == NewGamePageRow.Selected)
            {
                if (after.SelectedPage != targetPage)
                {
                    return Mismatch(result, "selected_page=" + targetPage,
                        "selected_page=" + after.SelectedPage);
                }
                if (after.Page != before.Page)
                {
                    return Mismatch(result, "page=" + before.Page, "page=" + after.Page);
                }
            }
            else
            {
                if (after.Page != targetPage)
                {
                    return Mismatch(result, "page=" + targetPage, "page=" + after.Page);
                }
                if (after.PageCount != before.PageCount)
                {
                    return Mismatch(result, "page_count=" + before.PageCount,
                        "page_count=" + after.PageCount);
                }
                if (before.Phase == NewGameMenuPhase.Perks
                    && after.SelectedPage != before.SelectedPage)
                {
                    return Mismatch(result, "selected_page=" + before.SelectedPage,
                        "selected_page=" + after.SelectedPage);
                }
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        public NewGameMenuActionResult FocusSelectedPerk(string id)
        {
            NewGameMenuActionResult result = NewResult();
            string denied = CheckWrite();
            if (denied != null) return DeniedResult(result, denied);

            NewGameMenuSnapshot before = ReadSnapshot();
            result.View = NewGameMenuViewBuilder.Build(before);
            string guard = GuardOpenMenu(before, result);
            if (guard != null) return result;
            if (before.Phase != NewGameMenuPhase.Perks)
            {
                result.Error = NewGameMenuErrors.WrongPhase;
                result.Reason = "phase=" + before.Phase;
                return result;
            }
            if (before.FirstRun)
            {
                result.Error = NewGameMenuErrors.FirstRunLocked;
                return result;
            }
            if (before.StartingGame)
            {
                result.Error = NewGameMenuErrors.MenuTransitioning;
                return result;
            }

            string lookup = id == null ? null : id.Trim();
            if (string.IsNullOrEmpty(lookup))
            {
                result.Error = NewGameMenuErrors.EmptyId;
                return result;
            }

            int copies = NewGameMenuPerks.SelectedCopies(before, lookup);
            if (copies == 0)
            {
                result.Error = NewGameMenuErrors.UnknownOption;
                result.Reason = "selected_row_missing";
                return result;
            }
            if (copies > 1)
            {
                result.Error = NewGameMenuErrors.DuplicateSelected;
                result.Reason = "selected_copies=" + copies;
                return result;
            }
            if (!NewGameMenuPerks.IsSelectedRowVisible(before, lookup))
            {
                result.Error = NewGameMenuErrors.SelectedRowNotVisible;
                result.Reason = "selected_row_is_on_another_page";
                return result;
            }

            int visibleAvailable = VisibleAvailableCount(before, lookup);
            if (visibleAvailable > 1)
            {
                result.Error = NewGameMenuErrors.AmbiguousOption;
                result.Reason = "duplicate_visible_id";
                return result;
            }
            if (NewGameMenuHover.IsAvailableHovered(before, lookup))
            {
                result.Error = NewGameMenuErrors.HoverConflict;
                result.Reason = "available_card_is_hovered";
                return result;
            }

            int hovered = NewGameMenuHover.HoveredPerkCount(before, lookup);
            if (hovered > 1)
            {
                result.Error = NewGameMenuErrors.HoverConflict;
                result.Reason = "multiple_hovered_views";
                return result;
            }
            if (hovered == 1)
            {
                // Already focused on the selected row; nothing to invoke.
                result.Success = true;
                result.Reason = "already_focused";
                result.PromptPending = before.Prompt != null;
                return result;
            }

            if (control == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }

            NewGameControlCallResult call = control.FocusSelectedPerk(lookup);
            if (!CallSucceeded(result, call)) return result;

            NewGameMenuSnapshot after = ReadSnapshot();
            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }
            result.View = NewGameMenuViewBuilder.Build(after);
            result.PromptPending = after.Prompt != null;
            if (after.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                return result;
            }
            if (after.Phase != NewGameMenuPhase.Perks)
            {
                return Mismatch(result, "perks", after.Phase.ToString());
            }
            if (NewGameMenuPerks.SelectedCopies(after, lookup) != 1
                || !NewGameMenuHover.IsSelectedRowHovered(after, lookup))
            {
                result.Error = NewGameMenuErrors.ReadbackMismatch;
                result.Reason = "selected_row_not_hovered";
                return result;
            }
            if (NewGameMenuHover.IsAvailableHovered(after, lookup))
            {
                result.Error = NewGameMenuErrors.HoverConflict;
                result.Reason = "available_card_is_hovered";
                return result;
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        public NewGameMenuActionResult BlurOption(string id)
        {
            NewGameMenuActionResult result = NewResult();
            string denied = CheckWrite();
            if (denied != null) return DeniedResult(result, denied);

            NewGameMenuSnapshot before = ReadSnapshot();
            result.View = NewGameMenuViewBuilder.Build(before);
            string guard = GuardOpenMenu(before, result);
            if (guard != null) return result;
            if (before.Phase != NewGameMenuPhase.Perks)
            {
                result.Error = NewGameMenuErrors.WrongPhase;
                result.Reason = "phase=" + before.Phase;
                return result;
            }

            string lookup = id == null ? null : id.Trim();
            if (string.IsNullOrEmpty(lookup))
            {
                result.Error = NewGameMenuErrors.EmptyId;
                return result;
            }

            int hovered = NewGameMenuHover.HoveredPerkCount(before, lookup);
            if (hovered == 0)
            {
                result.Error = NewGameMenuErrors.NotHovered;
                return result;
            }
            if (hovered > 1)
            {
                result.Error = NewGameMenuErrors.AmbiguousHover;
                result.Reason = "hovered_views=" + hovered;
                return result;
            }

            if (control == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }

            NewGameControlCallResult call = control.BlurPerk(lookup);
            if (!CallSucceeded(result, call)) return result;

            NewGameMenuSnapshot after = ReadSnapshot();
            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }
            result.View = NewGameMenuViewBuilder.Build(after);
            result.PromptPending = after.Prompt != null;
            if (after.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                return result;
            }
            if (after.Phase != before.Phase)
            {
                return Mismatch(result, before.Phase.ToString(), after.Phase.ToString());
            }
            if (NewGameMenuHover.HoveredPerkCount(after, lookup) != 0)
            {
                result.Error = NewGameMenuErrors.ReadbackMismatch;
                result.Reason = "hover_still_present";
                return result;
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        public NewGameMenuActionResult Continue()
        {
            NewGameMenuActionResult result = NewResult();
            string denied = CheckWrite();
            if (denied != null) return DeniedResult(result, denied);

            NewGameMenuSnapshot before = ReadSnapshot();
            result.View = NewGameMenuViewBuilder.Build(before);
            string guard = GuardOpenMenu(before, result);
            if (guard != null) return result;

            if (before.Phase == NewGameMenuPhase.Difficulty)
            {
                // The native difficulty Continue is Start: it begins the run and
                // writes the initial save. S3B must never invoke it.
                result.Error = NewGameMenuErrors.DifficultyStartForbidden;
                return result;
            }
            if (before.Phase != NewGameMenuPhase.Specialization
                && before.Phase != NewGameMenuPhase.Perks)
            {
                result.Error = NewGameMenuErrors.WrongPhase;
                result.Reason = "phase=" + before.Phase;
                return result;
            }
            if (before.StartingGame)
            {
                result.Error = NewGameMenuErrors.MenuTransitioning;
                return result;
            }
            if (before.Continue == null || !before.Continue.Visible || !before.Continue.Enabled)
            {
                result.Error = NewGameMenuErrors.ContinueUnavailable;
                result.Reason = "button_disabled";
                return result;
            }

            NewGameMenuPhase next;
            if (before.Phase == NewGameMenuPhase.Specialization)
            {
                if (!before.HasSelection || string.IsNullOrEmpty(before.SelectedId))
                {
                    result.Error = NewGameMenuErrors.ContinueUnavailable;
                    result.Reason = "no_scenario_selected";
                    return result;
                }
                NewGameCandidate selected = FindVisibleCandidate(before, before.SelectedId);
                if (selected == null || !selected.Enabled)
                {
                    result.Error = NewGameMenuErrors.ContinueUnavailable;
                    result.Reason = "scenario_unavailable";
                    return result;
                }
                next = NewGameMenuPhase.Perks;
            }
            else
            {
                if (!NewGameMenuPerks.CanStart(before))
                {
                    result.Error = NewGameMenuErrors.ContinueUnavailable;
                    result.Reason = "perks_cannot_start";
                    return result;
                }
                next = NewGameMenuPhase.Difficulty;
            }

            if (control == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return result;
            }

            NewGameControlCallResult call = control.Continue(before.Phase);
            if (!CallSucceeded(result, call)) return result;

            NewGameMenuSnapshot after = ReadSnapshot();
            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }
            result.View = NewGameMenuViewBuilder.Build(after);
            result.PromptPending = after.Prompt != null;
            if (after.Prompt != null)
            {
                // Scenario Continue can branch into the native first-run warning.
                // The prompt is a fork, not progress; confirming it is S4.
                result.Error = NewGameMenuErrors.PromptPending;
                return result;
            }
            if (after.Phase != next)
            {
                return Mismatch(result, next.ToString(), after.Phase.ToString());
            }
            if (after.StartingGame)
            {
                return Mismatch(result, "menu", "starting_game");
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        private NewGameMenuActionResult DeniedResult(NewGameMenuActionResult result, string error)
        {
            result.Error = error;
            result.View = NewGameMenuViewBuilder.Build(ReadSnapshot());
            result.PromptPending = result.View != null && result.View.PromptPending;
            return result;
        }

        private static NewGameMenuActionResult NewResult()
        {
            return new NewGameMenuActionResult();
        }

        private static string GuardOpenMenu(
            NewGameMenuSnapshot snapshot,
            NewGameMenuActionResult result)
        {
            if (snapshot == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                return NewGameMenuErrors.MenuUnavailable;
            }
            if (snapshot.Prompt != null)
            {
                result.Error = NewGameMenuErrors.PromptPending;
                result.PromptPending = true;
                return NewGameMenuErrors.PromptPending;
            }
            if (!string.IsNullOrEmpty(snapshot.PhaseError))
            {
                result.Error = snapshot.PhaseError;
                return snapshot.PhaseError;
            }
            if (snapshot.Phase == NewGameMenuPhase.Closed)
            {
                result.Error = NewGameMenuErrors.MenuClosed;
                return NewGameMenuErrors.MenuClosed;
            }
            if (snapshot.StartingGame)
            {
                result.Error = NewGameMenuErrors.MenuTransitioning;
                return NewGameMenuErrors.MenuTransitioning;
            }
            return null;
        }

        private static bool CallSucceeded(NewGameMenuActionResult result,
            NewGameControlCallResult call)
        {
            if (call == null)
            {
                result.Error = NewGameMenuErrors.NativeNotCalled;
                return false;
            }
            if (!call.Called)
            {
                result.Error = string.IsNullOrEmpty(call.Error)
                    ? NewGameMenuErrors.NativeNotCalled
                    : call.Error;
                return false;
            }
            return true;
        }

        private static NewGameMenuActionResult Mismatch(
            NewGameMenuActionResult result,
            string expected,
            string actual)
        {
            result.Error = NewGameMenuErrors.ReadbackMismatch;
            result.Readback = new NewGameReadback
            {
                Status = NewGameReadbackStatus.Mismatch,
                Error = NewGameMenuErrors.ReadbackMismatch,
                Expected = expected,
                Actual = actual
            };
            return result;
        }

        private static int VisibleAvailableCount(NewGameMenuSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.Candidates == null || string.IsNullOrEmpty(id))
            {
                return 0;
            }
            int count = 0;
            foreach (NewGameCandidate candidate in snapshot.Candidates)
            {
                if (candidate != null
                    && !candidate.Hidden
                    && candidate.Visible
                    && candidate.Page == snapshot.Page
                    && string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private static NewGameCandidate FindVisibleCandidate(
            NewGameMenuSnapshot snapshot,
            string id)
        {
            if (snapshot == null || snapshot.Candidates == null) return null;
            foreach (NewGameCandidate candidate in snapshot.Candidates)
            {
                if (candidate != null
                    && !candidate.Hidden
                    && candidate.Visible
                    && candidate.Page == snapshot.Page
                    && string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return null;
        }

        private string CheckWrite()
        {
            if (writePolicy == null) return RecordingSaveErrors.ServiceUnavailable;
            try
            {
                return writePolicy();
            }
            catch (Exception)
            {
                return RecordingSaveErrors.ServiceUnavailable;
            }
        }

        private NewGameMenuSnapshot ReadSnapshot()
        {
            if (port == null) return null;
            try
            {
                return port.Read();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Explicit, whitelisted JSON for the new-game ops. The view is rebuilt field
    /// by field; no native slot argument, candidate registry or save data is ever
    /// serialized implicitly.
    /// </summary>
    public static class NewGameMenuJson
    {
        public static string OptionsJson(NewGameMenuView view)
        {
            JObject payload = ViewJson(view);
            payload["ok"] = true;
            return payload.ToString(Formatting.None);
        }

        public static string SelectJson(NewGameMenuSelectResult result)
        {
            var payload = new JObject();
            payload["ok"] = result != null && result.Success;
            payload["op"] = "select_new_game_option";
            if (result == null)
            {
                payload["error"] = "no_result";
                return payload.ToString(Formatting.None);
            }

            if (!string.IsNullOrEmpty(result.Error)) payload["error"] = result.Error;
            if (!string.IsNullOrEmpty(result.Warning)) payload["warning"] = result.Warning;
            if (!string.IsNullOrEmpty(result.Reason)) payload["reason"] = result.Reason;
            payload["kind"] = KindText(result.Kind);
            payload["requested_id"] = result.RequestedId;
            payload["action"] = ActionText(result.Action);
            if (result.TargetId != null) payload["target_id"] = result.TargetId;
            if (result.Readback != null) payload["readback"] = ReadbackJson(result.Readback);
            if (result.View != null) payload["view"] = ViewJson(result.View);
            return payload.ToString(Formatting.None);
        }

        public static string ActionResultJson(string op, NewGameMenuActionResult result)
        {
            var payload = new JObject();
            payload["ok"] = result != null && result.Success;
            payload["op"] = op;
            if (result == null)
            {
                payload["error"] = "no_result";
                return payload.ToString(Formatting.None);
            }

            if (!string.IsNullOrEmpty(result.Error)) payload["error"] = result.Error;
            if (!string.IsNullOrEmpty(result.Warning)) payload["warning"] = result.Warning;
            if (!string.IsNullOrEmpty(result.Reason)) payload["reason"] = result.Reason;
            payload["prompt_pending"] = result.PromptPending;
            if (result.Readback != null) payload["readback"] = ReadbackJson(result.Readback);
            if (result.View != null) payload["view"] = ViewJson(result.View);
            return payload.ToString(Formatting.None);
        }

        private static JObject ViewJson(NewGameMenuView view)
        {
            var payload = new JObject();
            if (view == null)
            {
                payload["phase"] = "closed";
                payload["phase_error"] = NewGameMenuErrors.MenuUnavailable;
                return payload;
            }

            payload["phase"] = PhaseText(view.Phase);
            if (!string.IsNullOrEmpty(view.PhaseError)) payload["phase_error"] = view.PhaseError;
            if (view.Header != null) payload["header"] = view.Header;
            payload["page"] = view.Page;
            payload["page_count"] = view.PageCount;
            payload["has_previous_page"] = view.HasPreviousPage;
            payload["has_next_page"] = view.HasNextPage;
            payload["options"] = OptionsJson(view.Options);
            payload["selected_page"] = view.SelectedPage;
            payload["selected_page_count"] = view.SelectedPageCount;
            payload["selected_row_paged"] = view.SelectedRowPaged;
            payload["has_previous_selected_page"] = view.HasPreviousSelectedPage;
            payload["has_next_selected_page"] = view.HasNextSelectedPage;
            payload["selected_options"] = OptionsJson(view.SelectedOptions);
            if (view.SelectedId != null) payload["selected_id"] = view.SelectedId;
            payload["has_selection"] = view.HasSelection;
            if (view.Detail != null) payload["detail"] = DetailJson(view.Detail);
            if (view.Perks != null) payload["perks"] = PerksJson(view.Perks);
            payload["toggles"] = TogglesJson(view.Toggles);
            payload["continue"] = ButtonJson(view.Continue);
            payload["prompt_pending"] = view.PromptPending;
            if (view.Prompt != null) payload["prompt"] = PromptJson(view.Prompt);
            if (view.HoveredId != null) payload["hovered_id"] = view.HoveredId;
            payload["hovered_in_selected_row"] = view.HoveredInSelectedRow;
            return payload;
        }

        private static JArray OptionsJson(System.Collections.Generic.List<NewGameOptionView> options)
        {
            var rows = new JArray();
            if (options == null) return rows;
            foreach (NewGameOptionView option in options)
            {
                if (option == null) continue;
                var row = new JObject();
                row["id"] = option.Id;
                if (option.DisplayName != null) row["display_name"] = option.DisplayName;
                if (option.Description != null) row["description"] = option.Description;
                if (option.Tag != null) row["tag"] = option.Tag;
                if (option.Lines != null) row["lines"] = new JArray(option.Lines);
                row["cost"] = option.Cost;
                row["max_slot"] = option.MaxSlot;
                row["selected"] = option.Selected;
                row["enabled"] = option.Enabled;
                if (option.LockReason != null) row["lock_reason"] = option.LockReason;
                row["hovered"] = option.Hovered;
                rows.Add(row);
            }
            return rows;
        }

        private static JObject DetailJson(NewGameMenuDetail detail)
        {
            var row = new JObject();
            if (detail.Title != null) row["title"] = detail.Title;
            if (detail.Tag != null) row["tag"] = detail.Tag;
            if (detail.Description != null) row["description"] = detail.Description;
            if (detail.Warning != null) row["warning"] = detail.Warning;
            if (detail.Lines != null) row["lines"] = new JArray(detail.Lines);
            return row;
        }

        private static JObject PerksJson(NewGamePerkState perks)
        {
            var row = new JObject();
            row["first_run"] = perks.FirstRun;
            row["max_points"] = perks.MaxPoints;
            row["base_count"] = perks.BaseCount;
            row["used_points"] = perks.UsedPoints;
            row["remaining_points"] = perks.RemainingPoints;
            row["selected_count"] = perks.SelectedCount;
            row["max_count"] = perks.MaxCount;
            if (perks.PointsText != null) row["points_text"] = perks.PointsText;
            if (perks.CountText != null) row["count_text"] = perks.CountText;
            if (perks.Instruction != null) row["instruction"] = perks.Instruction;
            return row;
        }

        private static JArray TogglesJson(System.Collections.Generic.List<NewGameMenuToggleState> toggles)
        {
            var rows = new JArray();
            if (toggles == null) return rows;
            foreach (NewGameMenuToggleState toggle in toggles)
            {
                if (toggle == null) continue;
                var row = new JObject();
                row["id"] = toggle.Id;
                if (toggle.Label != null) row["label"] = toggle.Label;
                row["visible"] = toggle.Visible;
                row["selected"] = toggle.Selected;
                row["enabled"] = toggle.Enabled;
                if (toggle.LockReason != null) row["lock_reason"] = toggle.LockReason;
                rows.Add(row);
            }
            return rows;
        }

        private static JObject ButtonJson(NewGameMenuButtonState button)
        {
            var row = new JObject();
            if (button == null) return row;
            if (button.Label != null) row["label"] = button.Label;
            row["visible"] = button.Visible;
            row["enabled"] = button.Enabled;
            return row;
        }

        private static JObject PromptJson(NewGameMenuPrompt prompt)
        {
            var row = new JObject();
            row["id"] = prompt.Id;
            if (prompt.Text != null) row["text"] = prompt.Text;
            if (prompt.ConfirmText != null) row["confirm_text"] = prompt.ConfirmText;
            if (prompt.CancelText != null) row["cancel_text"] = prompt.CancelText;
            row["first_run"] = prompt.FirstRun;
            return row;
        }

        private static JObject ReadbackJson(NewGameReadback readback)
        {
            var row = new JObject();
            row["status"] = StatusText(readback.Status);
            if (readback.Error != null) row["error"] = readback.Error;
            if (readback.Expected != null) row["expected"] = readback.Expected;
            if (readback.Actual != null) row["actual"] = readback.Actual;
            return row;
        }

        private static string PhaseText(NewGameMenuPhase phase)
        {
            switch (phase)
            {
                case NewGameMenuPhase.Specialization: return "specialization";
                case NewGameMenuPhase.Perks: return "perks";
                case NewGameMenuPhase.Difficulty: return "difficulty";
                default: return "closed";
            }
        }

        private static string KindText(NewGameOptionKind kind)
        {
            switch (kind)
            {
                case NewGameOptionKind.Scenario: return "scenario";
                case NewGameOptionKind.Perk: return "perk";
                case NewGameOptionKind.Difficulty: return "difficulty";
                default: return "none";
            }
        }

        private static string ActionText(NewGameSelectionAction action)
        {
            switch (action)
            {
                case NewGameSelectionAction.Add: return "add";
                case NewGameSelectionAction.Remove: return "remove";
                case NewGameSelectionAction.Set: return "set";
                default: return "none";
            }
        }

        private static string StatusText(NewGameReadbackStatus status)
        {
            switch (status)
            {
                case NewGameReadbackStatus.Confirmed: return "confirmed";
                case NewGameReadbackStatus.PhaseChanged: return "phase_changed";
                default: return "mismatch";
            }
        }
    }
}
