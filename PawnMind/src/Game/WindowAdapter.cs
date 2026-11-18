using System;
using System.Collections.Generic;
using PawnMind.Core;
using UnityEngine;
using UnityEngine.UI;

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

        // R5/B2: the two adapter-less overlays. The paths are the ones the live
        // session observed in the UI snapshot; the buttons are the player's own
        // dismiss/apply controls, so only the onClick callback is invoked.
        private const string TutorialOverlayButton =
            "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton";
        private const string DialogueSettingsCloseButton =
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/CloseButton";
        private const string DialogueSettingsApplyButton =
            "CanvasScreenLayer2/_DialogueOptionUI/Panel/ApplyButton";

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

                // R5/B2: typed overlay dismissal instead of the test-only press.
                snapshot.TutorialOverlayOpen = FindControl(TutorialOverlayButton) != null;
                snapshot.DialogueSettingsOpen =
                    FindControl(DialogueSettingsCloseButton) != null
                    || FindControl(DialogueSettingsApplyButton) != null;
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
        /// R5/B2: dismisses the tutorial control-hint overlay and the dialogue
        /// settings dialog by invoking their own close/apply buttons. If neither
        /// control can be found the call throws; WindowService turns that into a
        /// readback mismatch, never a success.
        /// </summary>
        public void CloseOverlays()
        {
            bool dismissedTutorial = InvokeControl(TutorialOverlayButton);

            // Apply is the dialog's confirm path; Close is a fallback when the
            // apply button is not present in this build/layout.
            bool dismissedDialogue = InvokeControl(DialogueSettingsApplyButton);
            if (!dismissedDialogue) dismissedDialogue = InvokeControl(DialogueSettingsCloseButton);

            if (!dismissedTutorial && !dismissedDialogue)
            {
                throw new InvalidOperationException("no overlay control");
            }
        }

        private static bool InvokeControl(string path)
        {
            Button button = FindControl(path);
            if (button == null) return false;
            button.onClick.Invoke();
            return true;
        }

        private static Button FindControl(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Button[] buttons = UnityEngine.Object.FindObjectsOfType<Button>();
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.isActiveAndEnabled) continue;
                if (string.Equals(PathOf(button.transform), path, StringComparison.Ordinal))
                    return button;
            }

            return null;
        }

        private static string PathOf(Transform transform)
        {
            string path = transform.name;
            Transform parent = transform.parent;
            for (int i = 0; i < 3 && parent != null; i++)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
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
