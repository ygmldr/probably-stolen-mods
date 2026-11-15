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

                snapshot.MovableVisible = CountVisibleMovable();
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
                default:
                    throw new ArgumentException("unexpected window kind", "kind");
            }
        }

        public void HideMovableWindows()
        {
            WindowsHandler.HideAllMovable();
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
