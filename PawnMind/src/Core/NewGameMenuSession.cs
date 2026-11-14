using System;

namespace PawnMind.Core
{
    /// <summary>
    /// S3A new-game menu session: observe the native candidate view, evaluate one
    /// selection, delegate exactly one native UI callback, then re-read and verify.
    /// It deliberately has no Observe-next-page, Continue, Confirm or Start action.
    /// Called on the Unity main thread by a future S3B bridge; no HTTP here.
    /// </summary>
    public sealed class NewGameMenuSession
    {
        private readonly INewGameMenuPort port;

        public NewGameMenuSession(INewGameMenuPort port)
        {
            this.port = port;
        }

        public NewGameMenuView Observe()
        {
            if (port == null) return ClosedView(NewGameMenuErrors.MenuUnavailable);
            try
            {
                return NewGameMenuViewBuilder.Build(port.Read());
            }
            catch (Exception)
            {
                return ClosedView(NewGameMenuErrors.MenuUnavailable);
            }
        }

        public NewGameMenuSelectResult Select(NewGameOptionKind kind, string id)
        {
            NewGameMenuSelectResult result = new NewGameMenuSelectResult
            {
                Kind = kind,
                RequestedId = id,
                Action = NewGameSelectionAction.None
            };

            if (port == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                result.View = ClosedView(NewGameMenuErrors.MenuUnavailable);
                return result;
            }

            NewGameMenuSnapshot before;
            try
            {
                before = port.Read();
            }
            catch (Exception)
            {
                before = null;
            }

            if (before == null)
            {
                result.Error = NewGameMenuErrors.MenuUnavailable;
                result.View = ClosedView(NewGameMenuErrors.MenuUnavailable);
                return result;
            }

            result.View = NewGameMenuViewBuilder.Build(before);
            NewGameSelectionDecision decision = NewGameMenuRules.Decide(before, kind, id);
            result.Action = decision.Action;
            result.TargetId = decision.Target == null ? null : decision.Target.Id;

            if (!decision.Allowed)
            {
                result.Error = decision.Error;
                result.Reason = decision.Reason;
                return result;
            }

            NewGameSelectCallResult call;
            try
            {
                call = port.ApplySelection(decision);
            }
            catch (Exception)
            {
                call = null;
            }

            if (call == null)
            {
                result.Error = NewGameMenuErrors.NativeNotCalled;
                return result;
            }
            if (!call.Called)
            {
                result.Error = string.IsNullOrEmpty(call.Error)
                    ? NewGameMenuErrors.NativeNotCalled
                    : call.Error;
                return result;
            }

            NewGameMenuSnapshot after;
            try
            {
                after = port.Read();
            }
            catch (Exception)
            {
                after = null;
            }

            if (after == null)
            {
                result.Error = NewGameMenuErrors.ReadbackUnavailable;
                return result;
            }

            result.View = NewGameMenuViewBuilder.Build(after);
            NewGameReadback readback = NewGameMenuReadbackRules.Verify(before, decision, after);
            result.Readback = readback;
            if (readback.Status != NewGameReadbackStatus.Confirmed)
            {
                result.Error = readback.Error;
                result.Reason = readback.Expected;
                return result;
            }

            result.Success = true;
            result.Warning = call.Error;
            return result;
        }

        private static NewGameMenuView ClosedView(string error)
        {
            return new NewGameMenuView
            {
                Phase = NewGameMenuPhase.Closed,
                PhaseError = error
            };
        }
    }
}
