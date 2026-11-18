using System;
using System.Collections.Generic;
using PawnMind.Core;
using UnityEngine;

namespace PawnMind.Game
{
    /// <summary>
    /// Game adapter for the windows that cover the shop: the paper panel
    /// (notes, books, contracts, deeds) and any floating movable window.
    /// Every method runs on the Unity main thread and only calls the game's own
    /// close methods; it never deactivates GameObjects directly.
    /// </summary>
    internal sealed class WindowAdapter : IWindowPort
    {
        public static readonly WindowAdapter Instance = new WindowAdapter();

        public WindowSnapshot ReadWindows()
        {
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    return new WindowSnapshot
                    {
                        Readable = false,
                        Error = WindowErrors.NotInGame
                    };
                }

                var snapshot = new WindowSnapshot { Readable = true };

                PaperUIManager paper = PaperUIManager.Instance;
                if (paper != null)
                {
                    snapshot.PaperOpen = IsActive(paper.panel);
                    snapshot.ContractOpen = IsActive(paper.contractPanel);
                    snapshot.DeedOpen = IsActive(paper.deedPanel);
                    snapshot.BookOpen = IsActive(paper.bookPanel);
                }

                snapshot.TabletOpen = AnyTabletOpen();
                snapshot.MovableVisible = CountVisibleMovable();
                snapshot.CustomWindowOpen = HasCustomWindow();
                return snapshot;
            }
            catch (Exception)
            {
                return new WindowSnapshot
                {
                    Readable = false,
                    Error = WindowErrors.Unavailable
                };
            }
        }

        public void CloseWindow(string kind)
        {
            PaperUIManager paper = PaperUIManager.Instance;
            if (paper == null)
            {
                throw new InvalidOperationException("no paper ui");
            }

            switch (kind)
            {
                case WindowKinds.Book:
                    paper.CloseBook();
                    return;
                case WindowKinds.Paper:
                    paper.CloseUI();
                    return;
                case WindowKinds.Contract:
                    paper.CloseContract();
                    return;
                case WindowKinds.Deed:
                    paper.CloseDeed();
                    return;
                case WindowKinds.Tablet:
                    CloseTablets();
                    return;
                default:
                    throw new ArgumentException("unexpected window kind", "kind");
            }
        }

        public void HideMovableWindows()
        {
            WindowsHandler.HideAllMovable();
        }

        /// <summary>
        /// Counts the CustomUIWindows that are actually visible.
        ///
        /// Deliberately NOT CustomUIManager.HasEscapeTarget(): that only reports
        /// windows with closeOnEscape, and the wild-network window — the one that
        /// blocked day 7 of the first week — does not set it. Ask the objects
        /// themselves instead of trusting a convenient accessor.
        /// </summary>
        public bool HasCustomWindow()
        {
            CustomUIManager manager = CustomUIManager.Instance;
            if (manager == null) return false;

            string[] layers =
            {
                CustomUIManager.LAYER_FRONT,
                CustomUIManager.LAYER_PAPER,
                CustomUIManager.LAYER_REAR,
                CustomUIManager.LAYER_META,
                CustomUIManager.LAYER_MENU,
                CustomUIManager.LAYER_OVERLAY
            };

            for (int l = 0; l < layers.Length; l++)
            {
                Transform root = manager.GetLayerRoot(layers[l]);
                if (root == null) continue;

                for (int i = 0; i < root.childCount; i++)
                {
                    Transform child = root.GetChild(i);
                    if (child == null || !child.gameObject.activeSelf) continue;
                    if (child.name.StartsWith("custom_window_", StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        public void CloseCustomWindows()
        {
            CustomUIManager manager = CustomUIManager.Instance;
            if (manager != null) manager.CloseAllWindows();
        }

        /// <summary>
        /// The tablet panels (calendar, clientele, notebook, reputation) each own
        /// their own manager, so "close the tablet" means asking whichever ones
        /// are open to close themselves.
        /// </summary>
        private static bool AnyTabletOpen()
        {
            AdvCalendarUIManager calendar = AdvCalendarUIManager.Instance;
            if (calendar != null && IsActive(calendar.UIPanel)) return true;

            ClienteleUIManager clientele = ClienteleUIManager.Instance;
            if (clientele != null && IsActive(clientele.panel)) return true;

            NotebookUIManager notebook = NotebookUIManager.Instance;
            if (notebook != null && IsActive(notebook.panelObject)) return true;

            ReputationUIManager reputation = ReputationUIManager.Instance;
            if (reputation != null && IsActive(reputation.uiPanel)) return true;

            return false;
        }

        private static void CloseTablets()
        {
            AdvCalendarUIManager calendar = AdvCalendarUIManager.Instance;
            if (calendar != null && IsActive(calendar.UIPanel)) calendar.CloseUI();

            ClienteleUIManager clientele = ClienteleUIManager.Instance;
            if (clientele != null && IsActive(clientele.panel)) clientele.CloseUI();

            NotebookUIManager notebook = NotebookUIManager.Instance;
            if (notebook != null && IsActive(notebook.panelObject)) notebook.CloseUI();

            ReputationUIManager reputation = ReputationUIManager.Instance;
            if (reputation != null && IsActive(reputation.uiPanel)) reputation.CloseUI();
        }

        private static int CountVisibleMovable()
        {
            WindowsHandler handler = WindowsHandler.current;
            if (handler == null || handler.visibleWindows == null) return 0;

            int count = 0;
            for (int i = 0; i < handler.visibleWindows.Count; i++)
            {
                PixelWindow window = handler.visibleWindows[i];
                if (window == null) continue;
                // HideAllMovable only hides canMove windows, so a non-movable
                // window must not be counted here: otherwise the readback claims
                // the screen is still dirty and the op reports a false mismatch.
                if (!window.canMove) continue;
                bool visible;
                try
                {
                    visible = window.IsVisible();
                }
                catch (Exception)
                {
                    visible = false;
                }
                if (visible) count++;
            }
            return count;
        }

        private static bool IsActive(GameObject panel)
        {
            try
            {
                return panel != null && panel.activeInHierarchy;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
