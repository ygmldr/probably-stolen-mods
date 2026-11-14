namespace PawnMind.Core
{
    /// <summary>
    /// Which native row a paging request targets. Only the perk screen has a
    /// second (selected-row) pager; scenarios and difficulties only page the
    /// available list.
    /// </summary>
    public enum NewGamePageRow
    {
        Available,
        Selected
    }

    /// <summary>
    /// UI-visible confirmation prompt (for example the first-run warning). It is
    /// reported, never accepted or dismissed by S3B: confirming it belongs to a
    /// later, explicitly authorized step.
    /// </summary>
    public sealed class NewGameMenuPrompt
    {
        public string Id;
        public string Text;
        public string ConfirmText;
        public string CancelText;
        public bool FirstRun;
    }

    public sealed class NewGameControlCallResult
    {
        public bool Called;
        public string Error;

        public static NewGameControlCallResult NativeCalled()
        {
            return new NewGameControlCallResult { Called = true };
        }

        public static NewGameControlCallResult NativeCalledWithError(string error)
        {
            return new NewGameControlCallResult { Called = true, Error = error };
        }

        public static NewGameControlCallResult NativeNotCalled(string error)
        {
            return new NewGameControlCallResult { Called = false, Error = error };
        }
    }

    /// <summary>
    /// Game adapter port for the non-selection new-game UI callbacks. Each
    /// method must invoke exactly one native UI callback on the Unity main
    /// thread and nothing else; the Core/Bridge layer reads back and verifies.
    /// Implementations must not confirm prompts, start a game or save.
    /// </summary>
    public interface INewGameMenuControlPort
    {
        NewGameControlCallResult Open();
        NewGameControlCallResult TurnPage(NewGamePageRow row, int direction);
        NewGameControlCallResult Continue(NewGameMenuPhase phase);
        NewGameControlCallResult FocusSelectedPerk(string id);
        NewGameControlCallResult BlurPerk(string id);
    }

    /// <summary>
    /// Shared result for open/page/continue/hover. A failure always carries a
    /// stable error code and the sanitized post-read view when one is available.
    /// </summary>
    public sealed class NewGameMenuActionResult
    {
        public bool Success;
        public string Error;
        public string Warning;
        public string Reason;
        public NewGameMenuView View;
        public NewGameReadback Readback;
        public bool PromptPending;
    }
}
