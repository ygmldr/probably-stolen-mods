using System;
using System.Collections.Generic;
using PawnMind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnMind.Game
{
    /// <summary>
    /// Game adapter for the native confirmation modals. Every member runs on the
    /// Unity main thread. It only projects text the player can read, gives every
    /// modal a stable identity, and invokes exactly one native answer callback
    /// that belongs to that identity; it never guesses a button by index and
    /// never touches panel state directly.
    ///
    /// R5/B2 recognises two native families:
    ///  * GenericConfirmationUIManager.panel (generic confirm/cancel callbacks),
    ///  * NegotiationTablet/Confirm: the yes/no panel built by
    ///    NegociacionUIManager.OpenConfirmPanel. Decomp shows it is raised by
    ///    PlayerStore.SellItemFromTable / OnSellAllClick for a sale over the
    ///    client's budget (not by end_day; see R5_B_GAMEPLAY_REVIEW W-B2-3). Its
    ///    two options are answered through the public OnConfirmPanelYes /
    ///    OnConfirmPanelNo callbacks.
    /// If both are open at once the adapter reports an ambiguity instead of
    /// picking one.
    /// </summary>
    internal sealed class PromptAdapter : IPromptPort, IPromptStatePort, INightReturnPort
    {
        public static readonly PromptAdapter Instance = new PromptAdapter();

        // ----------------------------------------------------------------- read

        public PromptSnapshot Read()
        {
            try
            {
                var opened = new List<PendingPrompt>();

                PromptSnapshot generic = ReadGenericConfirmation();
                if (generic != null)
                {
                    if (!generic.Readable) return generic;
                    if (generic.Open) opened.Add(generic.Prompt);
                }

                PromptSnapshot negotiation = ReadNegotiationConfirm();
                if (negotiation != null)
                {
                    if (!negotiation.Readable) return negotiation;
                    if (negotiation.Open) opened.Add(negotiation.Prompt);
                }

                if (opened.Count > 1)
                {
                    // Two native modals own the input: answering either one would
                    // be a guess. Fail closed and let the caller decide.
                    return new PromptSnapshot
                    {
                        Readable = false,
                        Open = true,
                        Error = PromptErrors.PromptAmbiguous
                    };
                }

                if (opened.Count == 1)
                {
                    return new PromptSnapshot
                    {
                        Readable = true,
                        Open = true,
                        Prompt = opened[0]
                    };
                }

                return new PromptSnapshot { Readable = true, Open = false };
            }
            catch (Exception)
            {
                return Unreadable(PromptErrors.ReadFailed);
            }
        }

        private static PromptSnapshot ReadGenericConfirmation()
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
                    Target = PromptTargets.GenericConfirmation,
                    Body = ReadText(ui.bodyTMP),
                    ConfirmText = ReadText(ui.confirmButtonTMP),
                    CancelText = ReadText(ui.cancelButtonTMP),
                    SingleButton = IsHidden(ui.cancelButton),
                    ConfirmEnabled = IsClickable(ui.confirmButton),
                    CancelEnabled = IsClickable(ui.cancelButton)
                };

                if (string.IsNullOrEmpty(prompt.Body))
                {
                    // A visible panel without readable body text is not a safe
                    // prompt: the caller must not guess what it is approving.
                    return Unreadable(PromptErrors.ReadFailed);
                }

                return new PromptSnapshot { Readable = true, Open = true, Prompt = prompt };
            }
            catch (Exception)
            {
                return Unreadable(PromptErrors.ReadFailed);
            }
        }

        /// <summary>
        /// Reads the negotiation tablet's confirm panel. The identity comes from
        /// the native panel object itself (NegotiationTabletFrame.confirmRoot),
        /// the text from confirmText, and the two options from the TabletButtons
        /// that OpenConfirmPanel creates (confirm first, cancel second — ordered
        /// here by their native x position, never by a hard-coded index).
        /// </summary>
        private static PromptSnapshot ReadNegotiationConfirm()
        {
            try
            {
                NegociationUIManager ui = NegociationUIManager.Instance;
                if (ui == null || ui.uiObject == null)
                {
                    return new PromptSnapshot { Readable = true, Open = false };
                }

                NegotiationTabletFrame frame = ui.uiObject.GetComponent<NegotiationTabletFrame>();
                if (frame == null)
                {
                    // The manager exists but its frame cannot be read: never claim
                    // "no prompt" for a panel we simply cannot see.
                    return Unreadable(PromptErrors.ReadFailed);
                }

                if (frame.confirmRoot == null || !frame.confirmRoot.activeInHierarchy)
                {
                    return new PromptSnapshot { Readable = true, Open = false };
                }

                string body = ReadText(frame.confirmText);
                if (string.IsNullOrEmpty(body) || frame.confirmButtonHost == null)
                {
                    return Unreadable(PromptErrors.ReadFailed);
                }

                TabletButton confirm;
                TabletButton cancel;
                if (!TryReadButtons(frame.confirmButtonHost, out confirm, out cancel))
                {
                    return Unreadable(PromptErrors.ReadFailed);
                }

                var prompt = new PendingPrompt
                {
                    Kind = PromptKinds.NegotiationConfirm,
                    Target = PromptTargets.NegotiationTabletConfirm,
                    Body = body,
                    ConfirmText = ReadText(confirm.label),
                    CancelText = ReadText(cancel.label),
                    SingleButton = false,
                    ConfirmEnabled = confirm.Interactable,
                    CancelEnabled = cancel.Interactable
                };

                if (string.IsNullOrEmpty(prompt.ConfirmText)
                    || string.IsNullOrEmpty(prompt.CancelText))
                {
                    return Unreadable(PromptErrors.ReadFailed);
                }

                return new PromptSnapshot { Readable = true, Open = true, Prompt = prompt };
            }
            catch (Exception)
            {
                return Unreadable(PromptErrors.ReadFailed);
            }
        }

        private static bool TryReadButtons(
            RectTransform host, out TabletButton confirm, out TabletButton cancel)
        {
            confirm = null;
            cancel = null;
            TabletButton[] buttons = host.GetComponentsInChildren<TabletButton>(true);
            var usable = new List<TabletButton>();
            for (int i = 0; i < buttons.Length; i++)
            {
                TabletButton button = buttons[i];
                if (button == null || button.label == null) continue;
                if (!button.gameObject.activeInHierarchy) continue;
                usable.Add(button);
            }

            // The native panel has exactly yes + no; anything else is a modal we
            // do not understand and must not click.
            if (usable.Count != 2) return false;

            float first = X(usable[0]);
            float second = X(usable[1]);
            if (first < second)
            {
                confirm = usable[0];
                cancel = usable[1];
            }
            else if (second < first)
            {
                confirm = usable[1];
                cancel = usable[0];
            }
            else
            {
                // Same position: the layout is not the native one; refuse.
                return false;
            }

            return true;
        }

        private static float X(TabletButton button)
        {
            RectTransform rect = button.transform as RectTransform;
            return rect == null ? 0f : rect.anchoredPosition.x;
        }

        // ------------------------------------------------------------- answer

        public PromptAnswerOutcome Answer(PendingPrompt prompt, string choice)
        {
            if (prompt == null || string.IsNullOrEmpty(prompt.Kind))
            {
                return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
            }

            string normalized;
            if (!PromptChoices.TryNormalize(choice, out normalized))
            {
                return PromptAnswerOutcome.NotDispatched(PromptErrors.BadChoice);
            }

            if (prompt.Kind == PromptKinds.NegotiationConfirm)
            {
                return AnswerNegotiationConfirm(prompt, normalized);
            }

            if (prompt.Kind == PromptKinds.Confirmation)
            {
                return AnswerGenericConfirmation(prompt, normalized);
            }

            // Unknown native modal: no safe callback exists for it.
            return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
        }

        private static PromptAnswerOutcome AnswerGenericConfirmation(
            PendingPrompt prompt, string normalized)
        {
            if (!string.Equals(prompt.Target, PromptTargets.GenericConfirmation,
                    StringComparison.Ordinal))
            {
                return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
            }

            PromptSnapshot current = ReadGenericConfirmation();
            if (current == null || !current.Readable || !current.Open)
            {
                return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
            }

            GenericConfirmationUIManager ui = GenericConfirmationUIManager.Instance;
            if (ui == null) return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);

            if (normalized == PromptChoices.Confirm)
            {
                if (!IsClickable(ui.confirmButton))
                {
                    return PromptAnswerOutcome.NotDispatched(PromptErrors.ChoiceUnavailable);
                }
                ui.ConfirmButtonPressed();
                return PromptAnswerOutcome.Called();
            }

            if (normalized == PromptChoices.Cancel)
            {
                if (!IsClickable(ui.cancelButton))
                {
                    return PromptAnswerOutcome.NotDispatched(PromptErrors.ChoiceUnavailable);
                }
                ui.CancelButtonPressed();
                return PromptAnswerOutcome.Called();
            }

            return PromptAnswerOutcome.NotDispatched(PromptErrors.BadChoice);
        }

        private static PromptAnswerOutcome AnswerNegotiationConfirm(
            PendingPrompt prompt, string normalized)
        {
            if (!string.Equals(prompt.Target, PromptTargets.NegotiationTabletConfirm,
                    StringComparison.Ordinal))
            {
                return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
            }

            PromptSnapshot current = ReadNegotiationConfirm();
            if (current == null || !current.Readable || !current.Open)
            {
                // The panel changed between the read and the answer: nothing may
                // be clicked, and the caller sees "not dispatched".
                return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);
            }

            NegociationUIManager ui = NegociationUIManager.Instance;
            if (ui == null) return PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);

            // The enabled state is re-checked against the live read so a greyed
            // out option is never invoked.
            if (normalized == PromptChoices.Confirm)
            {
                if (!current.Prompt.ConfirmEnabled)
                {
                    return PromptAnswerOutcome.NotDispatched(PromptErrors.ChoiceUnavailable);
                }
                ui.OnConfirmPanelYes();
                return PromptAnswerOutcome.Called();
            }

            if (normalized == PromptChoices.Cancel)
            {
                if (!current.Prompt.CancelEnabled)
                {
                    return PromptAnswerOutcome.NotDispatched(PromptErrors.ChoiceUnavailable);
                }
                ui.OnConfirmPanelNo();
                return PromptAnswerOutcome.Called();
            }

            return PromptAnswerOutcome.NotDispatched(PromptErrors.BadChoice);
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
                    Location = GameState.CurrentLocationId,
                    ShopState = ReadShopState()
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

        private static string ReadShopState()
        {
            try
            {
                ShopDaySnapshot snapshot = GameState.ReadShopDaySnapshot();
                return snapshot == null || !snapshot.Readable || !snapshot.InGame
                    ? null
                    : snapshot.ShopState;
            }
            catch (Exception)
            {
                return null;
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

        private static PromptSnapshot Unreadable(string error)
        {
            return new PromptSnapshot
            {
                Readable = false,
                Open = false,
                Error = error
            };
        }

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

        private static bool IsClickable(GameObject root)
        {
            try
            {
                if (root == null || !root.activeInHierarchy) return false;
                Button button = root.GetComponentInChildren<Button>(true);
                return button == null || button.interactable;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
