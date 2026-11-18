using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Stable error codes for the close_windows op. Callers branch on these
    /// strings, so changing one is a protocol change.
    /// </summary>
    public static class WindowErrors
    {
        public const string Unavailable = "windows_unavailable";
        public const string ReadbackMismatch = "window_readback_mismatch";
        public const string NotInGame = "not_in_game";
    }

    /// <summary>Native panels the bridge may close, in the order they are closed.</summary>
    public static class WindowKinds
    {
        public const string Book = "book";
        public const string Paper = "paper";
        public const string Contract = "contract";
        public const string Deed = "deed";
        public const string Tablet = "tablet";
        public const string Movable = "movable";
        public const string Custom = "custom";
    }

    /// <summary>
    /// One reading of the native windows. Movable windows are counted rather
    /// than listed: the bridge never names individual windows, it only reports
    /// how many the player could still see.
    /// </summary>
    public sealed class WindowSnapshot
    {
        public bool Readable = true;
        public string Error;
        public bool BookOpen;
        public bool PaperOpen;
        public bool ContractOpen;
        public bool DeedOpen;
        public int MovableVisible;

        /// <summary>Any tablet panel is open (calendar, clientele, notebook, reputation).</summary>
        public bool TabletOpen;

        /// <summary>
        /// A CustomUIManager window is open (the wild network, appraisal, ...). These
        /// are the windows the player closes with ESC; without this the shop UI
        /// closes but a custom window keeps blocking customers (live session,
        /// 2026-11-19: day 7 of the first week).
        /// </summary>
        public bool CustomWindowOpen;

        /// <summary>True when nothing this op can close is on screen.</summary>
        public bool Clean
        {
            get
            {
                return !BookOpen && !PaperOpen && !ContractOpen && !DeedOpen
                    && !TabletOpen && MovableVisible <= 0;
            }
        }
    }

    public interface IWindowPort
    {
        /// <summary>Read-only projection of the visible native windows.</summary>
        WindowSnapshot ReadWindows();

        /// <summary>Close one panel through its own native close method.</summary>
        void CloseWindow(string kind);

        /// <summary>Hide every movable window (WindowsHandler.HideAllMovable).</summary>
        void HideMovableWindows();

        /// <summary>True when a CustomUIManager window would close on ESC.</summary>
        bool HasCustomWindow();

        /// <summary>CustomUIManager.CloseAllWindows: what the player's ESC does.</summary>
        void CloseCustomWindows();
    }

    public static class WindowRules
    {
        /// <summary>
        /// Which native closes to run, in order. The paper windows are closed
        /// first so a note that covers the counter disappears before the
        /// floating windows move, matching what the player would do.
        /// </summary>
        public static List<string> PlanClosures(WindowSnapshot snapshot)
        {
            var plan = new List<string>();
            if (snapshot == null || !snapshot.Readable) return plan;

            if (snapshot.BookOpen) plan.Add(WindowKinds.Book);
            if (snapshot.PaperOpen) plan.Add(WindowKinds.Paper);
            if (snapshot.ContractOpen) plan.Add(WindowKinds.Contract);
            if (snapshot.DeedOpen) plan.Add(WindowKinds.Deed);
            if (snapshot.TabletOpen) plan.Add(WindowKinds.Tablet);
            if (snapshot.MovableVisible > 0) plan.Add(WindowKinds.Movable);
            if (snapshot.CustomWindowOpen) plan.Add(WindowKinds.Custom);
            return plan;
        }

        /// <summary>Read-and-clean, verified: nothing this op closes may remain.</summary>
        public static bool IsClean(WindowSnapshot snapshot)
        {
            return snapshot != null && snapshot.Readable && snapshot.Clean;
        }
    }
}
