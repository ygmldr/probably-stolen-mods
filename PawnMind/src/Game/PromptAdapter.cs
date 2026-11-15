using System;
using PawnMind.Core;
using TMPro;
using UnityEngine;

namespace PawnMind.Game
{
    /// <summary>
    /// Game adapter for the native generic confirmation modal
    /// (<c>GenericConfirmationUIManager</c>). Every member runs on the Unity main
    /// thread. It only projects text the player can read and invokes exactly one
    /// of the two native button callbacks; it never touches GameObject state or
    /// the captured confirm/cancel closures directly.
    /// </summary>
    internal sealed class PromptAdapter : IPromptPort, IPromptStatePort, INightReturnPort
    {
        public static readonly PromptAdapter Instance = new PromptAdapter();

        // ----------------------------------------------------------------- read

        public PromptSnapshot Read()
        {
            try
            {
                GenericConfirmationUIManager ui = GenericConfirmationUIManager.Instance;
                if (ui == null || ui.panel == null)
                {
                    // No modal manager in this scene: report a closed prompt
                    // only when the game itself is readable.
                    return new PromptSnapshot { Readable = true, Open = false };
                }

                if (!ui.panel.activeInHierarchy)
                {
                    return new PromptSnapshot { Readable = true, Open = false };
                }

                var prompt = new PendingPrompt
                {
                    Kind = PromptKinds.Confirmation,
                    Body = ReadText(ui.bodyTMP),
                    ConfirmText = ReadText(ui.confirmButtonTMP),
                    CancelText = ReadText(ui.cancelButtonTMP),
                    SingleButton = IsHidden(ui.cancelButton)
                };

                if (string.IsNullOrEmpty(prompt.Body))
                {
                    // A visible panel without readable body text is not a safe
                    // prompt: the caller must not guess what it is approving.
                    return new PromptSnapshot { Readable = false, Error = PromptErrors.ReadFailed };
                }

                return new PromptSnapshot { Readable = true, Open = true, Prompt = prompt };
            }
            catch (Exception)
            {
                return new PromptSnapshot { Readable = false, Error = PromptErrors.ReadFailed };
            }
        }

        // ------------------------------------------------------------- answer

        public void Answer(string choice)
        {
            GenericConfirmationUIManager ui = GenericConfirmationUIManager.Instance;
            if (ui == null)
            {
                throw new InvalidOperationException("no confirmation manager");
            }

            if (choice == PromptChoices.Confirm)
            {
                ui.ConfirmButtonPressed();
                return;
            }

            if (choice == PromptChoices.Cancel)
            {
                ui.CancelButtonPressed();
                return;
            }

            throw new ArgumentException("unexpected choice", "choice");
        }

        // -------------------------------------------------------- state summary

        public PromptStateSummary ReadPromptState()
        {
            try
            {
                PlayerStore store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                return new PromptStateSummary
                {
                    Readable = store != null,
                    Day = store == null ? 0 : GameState.GetDay(),
                    Cash = store == null ? 0 : store.GetCash(),
                    Outside = GameState.IsOutside,
                    Location = GameState.CurrentLocationId
                };
            }
            catch (Exception)
            {
                return new PromptStateSummary
                {
                    Readable = false,
                    Error = PromptErrors.ReadFailed
                };
            }
        }

        // ------------------------------------------------------- night return

        public bool IsOutside
        {
            get { return GameState.IsOutside; }
        }

        public int ItemsOnGround
        {
            get
            {
                try
                {
                    EmporiumEntry entry = EmporiumEntry.Instance;
                    if (entry == null || entry.afterhourInventory == null) return 0;
                    return entry.afterhourInventory.childItems.Count;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public void Return()
        {
            MapUIManager ui = MapUIManager.Instance;
            if (ui == null)
            {
                throw new InvalidOperationException("no map manager");
            }

            ui.Return();
        }

        // ------------------------------------------------------------ helpers

        private static string ReadText(TextMeshProUGUI label)
        {
            try
            {
                return label == null ? string.Empty : (label.text ?? string.Empty);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsHidden(GameObject button)
        {
            try
            {
                return button == null || !button.activeInHierarchy;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
