using System;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// R5-B1 game adapter: read-only staging pre-check plus exactly one
    /// player-equivalent native staging step.
    ///
    /// Native path (decompile evidence, 0.46D):
    ///  - EmporiumEntry wires backInvinvElement.onSlotAddItemEndFunc to
    ///    PlayerStore.AddedItemToWeightedArea(item), which sets
    ///    currentNegociatedItem, calls GameItem.OnAddedToWeightedArea,
    ///    PlayerStore.OnWeightedAreaChanged (negotiation UI refresh),
    ///    TutorialUIManager.BuyItemDrag and HandleAutoNegoUIDisplay (opens the
    ///    tablet when the client intent/ownership allows it);
    ///  - onSlotRemoveItemEndFunc maps to RemovedItemToWeightedArea(item), which
    ///    re-points currentNegociatedItem to the counter's last item (or null);
    ///  - a player drag between inventories is implemented by
    ///    SlotMarker.TryAcceptOnce -> parent.Expel(item) + inventory.UncheckedAccept(item);
    ///    the bridge therefore uses the very same Expel/UncheckedAccept pair for
    ///    an item that is already on the weighted counter (drag off + back on),
    ///    and TryAcceptAll for an item that still sits elsewhere.
    ///
    /// The adapter never writes currentNegociatedItem, ownership, client intent
    /// or price directly: only the native callbacks above do that. Every readback
    /// is re-read afterwards and the service decides completed/uncertain.
    /// </summary>
    internal sealed class GameNegotiationStagingPort : INegotiationStagingPort
    {
        public StagingGuardInput Read(int itemId)
        {
            var input = new StagingGuardInput
            {
                InGame = GameState.IsInGame,
                RequestedItemId = itemId
            };
            if (!input.InGame) return input;

            StoreClient client = null;
            try
            {
                client = GameState.GetCurrentClient();
            }
            catch (Exception)
            {
                client = null;
            }

            input.ClientPresent = client != null;
            try
            {
                int handle = GameState.GetCurrentClientHandle();
                input.ClientReadable = client != null && handle != 0;
                if (client != null) input.ClientIntent = client.clientIntent.ToString();
            }
            catch (Exception)
            {
                input.ClientReadable = false;
            }

            NegociationUIManager ui = NegotiationUi();
            input.NegotiationReadable = ui != null && ui.uiObject != null;
            if (input.NegotiationReadable)
            {
                try
                {
                    input.NegotiationOpen = ui.IsOpen;
                }
                catch (Exception)
                {
                    input.NegotiationReadable = false;
                }
            }

            input.ModalOpen = GameState.IsStagingModalOpen();

            GameItem item;
            string container;
            bool found;
            try
            {
                found = GameState.TryLocateItem(itemId, out item, out container);
            }
            catch (Exception)
            {
                found = false;
                item = null;
                container = null;
            }

            input.ItemExists = found;
            if (found)
            {
                input.ItemContainer = container;
                try
                {
                    // parentInventory null means the item is not in an inventory
                    // right now (in flight); staging would be guesswork.
                    input.ItemReadable = item != null && container != null && item.parentInventory != null;
                    if (input.ItemReadable) input.ItemOwned = GeneralHelper.IsItemOwned(item);
                }
                catch (Exception)
                {
                    input.ItemReadable = false;
                }

                GameGridInventory back = GameState.GetBackCounterInventory();
                input.OnWeightedCounter = back != null && item != null && item.parentInventory == back;

                try
                {
                    if (back != null && item != null && !input.OnWeightedCounter)
                    {
                        input.BackCounterMayAccept = !back.IsInsertLocked()
                            && back.MayHaveValidInventorySlot(item);
                    }
                }
                catch (Exception)
                {
                    input.BackCounterMayAccept = false;
                }
            }

            input.BackCounterReadable = GameState.GetBackCounterInventory() != null;

            try
            {
                PlayerStore store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                GameItem staged = store == null ? null : store.GetCurrentlyNegociatedItem();
                input.StagedItemId = staged == null ? 0 : staged.GetUniqueID();
                input.StagedReadable = store != null;
            }
            catch (Exception)
            {
                input.StagedReadable = false;
            }

            return input;
        }

        public StagingEvidence Stage(int itemId)
        {
            var evidence = new StagingEvidence { NativePrimitive = StagingPrimitives.None };
            PlayerStore store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            GameGridInventory back = GameState.GetBackCounterInventory();
            if (store == null || back == null)
            {
                // Not dispatched: the service reports this as uncertain
                // native_not_called, never as a staging success.
                evidence.Error = "staging_unavailable";
                return evidence;
            }

            evidence.ClientHandleBefore = GameState.GetCurrentClientHandle();
            evidence.StagedItemIdBefore = StagedId(store);

            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null)
            {
                evidence.Error = "item_not_found";
                return evidence;
            }

            GameInventory source = item.parentInventory;
            bool onCounter = item.parentInventory == back;
            evidence.Called = true;
            try
            {
                if (onCounter)
                {
                    // The two native callbacks of a player drag off + back on the
                    // weighted counter: remove re-points the staged item, add
                    // re-stages the requested one and refreshes/opens the tablet.
                    if (!back.Expel(item))
                    {
                        evidence.Error = "detach_failed";
                    }
                    else if (!back.UncheckedAccept(item))
                    {
                        // Defensive restore: UncheckedAccept can only fail for an
                        // unexpected state, but the item must not stay detached.
                        back.TryAcceptAll(item);
                        evidence.Error = item.parentInventory == back
                            ? "accept_retried"
                            : "restore_failed";
                    }
                    else
                    {
                        evidence.NativePrimitive = StagingPrimitives.WeightedAreaRemoveAdd;
                    }
                }
                else
                {
                    // The native accept a player drag onto the counter uses; this is
                    // the same primitive move_item(main -> back) already performs.
                    back.TryAcceptAll(item);
                    if (item.parentInventory == null && source != null)
                    {
                        // The native accept detached the item without placing it on
                        // the counter (native silent failure). Put it back where it
                        // came from so no floating item is left behind; the service
                        // still reports uncertain, never success.
                        if (source.UncheckedAccept(item))
                        {
                            evidence.Error = "accept_failed";
                        }
                        else
                        {
                            evidence.Error = "restore_failed";
                        }
                    }
                    else
                    {
                        evidence.NativePrimitive = StagingPrimitives.WeightedAreaAdd;
                    }
                }
            }
            catch (Exception)
            {
                // A native callback threw mid-way. Do not leave the item detached:
                // restore it through the native accept before reporting uncertainty.
                evidence.Error = "native_exception";
                try
                {
                    if (item.parentInventory == null && !back.UncheckedAccept(item))
                    {
                        evidence.Error = "native_exception_restore_failed";
                    }
                }
                catch (Exception)
                {
                    evidence.Error = "native_exception_restore_failed";
                }
            }

            ReadAfter(evidence, itemId, store);
            return evidence;
        }

        /// <summary>Post-dispatch readback; anything unreadable stays unproven.</summary>
        private static void ReadAfter(StagingEvidence evidence, int itemId, PlayerStore store)
        {
            try
            {
                evidence.StagedItemIdAfter = StagedId(store);

                NegociationUIManager ui = NegotiationUi();
                evidence.NegotiationReadableAfter = ui != null && ui.uiObject != null;
                if (evidence.NegotiationReadableAfter)
                {
                    try
                    {
                        evidence.NegotiationOpenAfter = ui.IsOpen;
                    }
                    catch (Exception)
                    {
                        evidence.NegotiationReadableAfter = false;
                    }
                }

                GameItem item = GameState.FindItemAnywhere(itemId);
                GameGridInventory back = GameState.GetBackCounterInventory();
                evidence.ItemOnCounterAfter = item != null && back != null
                    && item.parentInventory == back;

                evidence.ClientHandleAfter = GameState.GetCurrentClientHandle();
                evidence.ClientReadableAfter = true;
                evidence.AfterReadable = true;
            }
            catch (Exception)
            {
                evidence.AfterReadable = false;
            }
        }

        private static int StagedId(PlayerStore store)
        {
            try
            {
                GameItem staged = store == null ? null : store.GetCurrentlyNegociatedItem();
                return staged == null ? 0 : staged.GetUniqueID();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static NegociationUIManager NegotiationUi()
        {
            try
            {
                return NegociationUIManager.Instance;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
