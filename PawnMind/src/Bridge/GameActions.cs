using System;
using System.Collections.Generic;
using PawnMind.Core;
using PawnMind.Game;
using UnityEngine;

namespace PawnMind.Bridge
{
    /// <summary>
    /// M1 action layer. Every method is a single, guarded game call; the caller
    /// re-reads /state to verify the outcome. Methods run on the Unity main thread.
    /// </summary>
    internal static class GameActions
    {
        /// <summary>
        /// Opens the shutter by pressing the game's own button, so the player-facing
        /// guards run: the shutter animation lock, arrest/shootout states and the
        /// MORNING state. Calling OnShutterOpened() by hand re-rolled the whole
        /// customer queue and re-issued the daily items (welfare coupons,
        /// dividends, newspapers, mentor notes), which is a save-level exploit.
        /// </summary>
        public static ActionResult OpenShop()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("open_shop", "not_in_game");

            StoreShutterButton button;
            try
            {
                button = StoreShutterButton.Instance;
            }
            catch (Exception)
            {
                return ActionResult.Failure("open_shop", "open_shop_unavailable");
            }
            if (button == null) return ActionResult.Failure("open_shop", "open_shop_unavailable");

            // Mirror the native predicates so the caller gets a stable code instead
            // of a silently ignored click. The rule lives in Core so the offline
            // suite pins it (P0-OPEN-01): the animation lock and the MORNING gate
            // must refuse a repeated open_shop, not swallow it.
            string blocked = Core.OpenShopPolicy.Evaluate(
                true,
                store.lockShutterButtonFlag > 0,
                store.isClientBeingArrested,
                store.isHandlingShootout,
                store.storeState.ToString(),
                store.CanStartDay());
            if (blocked != null)
            {
                return ActionResult.Failure("open_shop", blocked);
            }

            try
            {
                button.OnClick();
            }
            catch (Exception)
            {
                return ActionResult.Failed("open_shop", "open_shop_failed");
            }

            return ActionResult.Success("open_shop");
        }

        /// <summary>
        /// Rings for the next customer. The player path is StoreBell.RingBell, which
        /// refuses while the bell animation lock is held and while the shop is not
        /// OPEN; calling TryCallNextClient directly could ring during either.
        /// </summary>
        public static ActionResult CallNextCustomer()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("call_next_customer", "not_in_game");

            string blocked = Core.ShopBellPolicy.Evaluate(
                true,
                store.lockNextBellFlag > 0,
                store.storeState.ToString(),
                store.CanNextClient());
            if (blocked != null)
            {
                return ActionResult.Failure("call_next_customer", blocked);
            }

            store.TryCallNextClient();
            return ActionResult.Success("call_next_customer");
        }

        /// <summary>
        /// Sends the current client away. Only used when the client cannot be
        /// served (no items, no dialogue): normal service must still go through
        /// confirm_trade + the closing dialogue.
        /// </summary>
        public static ActionResult DismissClient()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("dismiss_current_client", "not_in_game");
            if (GameState.GetCurrentClient() == null)
            {
                return ActionResult.Failure("dismiss_current_client", "no_client");
            }
            if (!store.IsClientDimissable())
            {
                // Landlord and story clients are deliberately not dismissable; the
                // player cannot send them away either.
                return ActionResult.Failure("dismiss_current_client", "client_not_dismissable");
            }

            DialogUIManager ui = GetDialogUi();
            if (ui != null && ui.GetCurrentDialog() != null)
            {
                // A live dialogue is the client's turn to speak; advancing it may
                // still complete the visit. Refuse instead of yanking the client.
                return ActionResult.Failure("dismiss_current_client", "dialogue_active");
            }

            try
            {
                // Goes through the player path so intercepted dismissals (e.g. a
                // client that triggers something on leaving) still run.
                store.TryPlayerDismissCurrentClient(callNextWhenPossible: false);
            }
            catch (Exception)
            {
                return ActionResult.Failed("dismiss_current_client", "dismiss_failed");
            }

            return ActionResult.Success("dismiss_current_client");
        }

        public static ActionResult AdvanceDialogue()
        {
            DialogUIManager ui = GetDialogUi();
            if (ui == null) return ActionResult.Failure("advance_dialogue", "no_dialogue");

            // A manager without an active dialogue still answers OnAdvancePressed
            // with a silent no-op, which looked like success while nothing moved.
            Dialogue current = ui.GetCurrentDialog();
            if (current == null) return ActionResult.Failure("advance_dialogue", "no_dialogue");
            if (ui.IsChoice()) return ActionResult.Failure("advance_dialogue", "choice_required");

            ui.OnAdvancePressed();
            return ActionResult.Success("advance_dialogue");
        }

        public static ActionResult BuyWholesale()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("buy_wholesale", "not_in_game");

            StoreClient client = GameState.GetCurrentClient();
            if (client == null) return ActionResult.Failure("buy_wholesale", "no_client");
            if (!client.isOfferWholesale) return ActionResult.Failure("buy_wholesale", "no_wholesale_offer");

            List<GameItem> items = EmporiumEntry.Instance.GetAllNonOwnedItem();
            if (items.Count == 0) return ActionResult.Failure("buy_wholesale", "no_items");

            int price = store.GetWholesalePrice();
            if (price < 0) return ActionResult.Failure("buy_wholesale", "no_wholesale_offer");
            if (store.playerCash < price) return ActionResult.Failure("buy_wholesale", "cannot_afford");

            try
            {
                store.playerCash -= price;
                for (int i = 0; i < items.Count; i++)
                {
                    int cost = RNG.CalculatePercentageOff((int)items[i].GetNegociatedValue(), client.wholesaleDiscount);
                    store.OnItemBought(items[i], cost);
                }

                for (int i = 0; i < items.Count; i++) store.MoveDealItemToInventory(items[i]);
                client.isOfferWholesale = false;
            }
            catch (Exception e)
            {
                return ActionResult.Failed("buy_wholesale", "wholesale_failed: " + e.Message);
            }

            return ActionResult.Success("buy_wholesale");
        }

        public static ActionResult BuyCommissary(string identifier)
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("commissary_buy", "not_in_game");
            if (string.IsNullOrEmpty(identifier)) return ActionResult.Failure("commissary_buy", "missing_identifier");

            CommissaryItemData data = store.commissaryData.GetItemData(identifier);
            if (data == null) return ActionResult.Failure("commissary_buy", "unknown_item");
            if (store.commissaryData.redeemedCredit < data.price)
                return ActionResult.Failure("commissary_buy", "insufficient_credit");
            if (data.StockCount <= 0) return ActionResult.Failure("commissary_buy", "out_of_stock");

            store.commissaryData.BuyItem(identifier);
            return ActionResult.Success("commissary_buy");
        }

        public static ActionResult MoveItem(int itemId, string targetContainer, int x, int y, int turns)
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("move_item", "not_in_game");

            GameItem item = GameState.FindItemAnywhere(itemId);
            GameInventory target = GameState.GetContainer(targetContainer);

            // R5.3/A1: every refusal is decided before dispatch, so a refused move
            // has no side effects. This also closes the old `x >= 0 && y >= 0`
            // hole, where a half-specified request became automatic placement.
            var request = new MoveItemInput
            {
                HasX = x >= 0,
                HasY = y >= 0,
                Turns = turns,
                ItemFound = item != null,
                ContainerFound = target != null,
                ContainerId = targetContainer ?? string.Empty,
                ContainerReachable = StagingContainers.IsPlayerReachable(targetContainer),
                Pinned = item != null && ItemPinHelper.IsPinned(item),
                ContainerLocked = target != null && target.IsInsertLocked(),
                SameContainer = item != null && target != null && item.parentInventory == target
            };

            string refusal = MoveItemPolicy.Validate(request);
            if (refusal != null) return ActionResult.Failure("move_item", refusal);

            bool sameContainer = request.SameContainer;
            bool specific = request.HasX;

            if (!specific)
            {
                if (sameContainer) return ActionResult.Failure("move_item", "same_container");

                // One op, one move: the old auto path fell back to TryRepack, which
                // re-placed every unpinned item on the counter. That is several
                // native moves behind a single player action, so it is gone.
                GameInventory result = target.TryAcceptAll(item);
                if (result != null && item.parentInventory == target) return ActionResult.Success("move_item");

                return ActionResult.Failed("move_item", "no_space");
            }

            GameGridInventory grid = target as GameGridInventory;
            if (grid == null) return ActionResult.Failure("move_item", "not_a_grid_container");
            if (item.modifiedShape == null) return ActionResult.Failure("move_item", "no_shape");

            GridShape originalShape = item.modifiedShape.Clone();
            GridShape shape = new GridShapeBuilder(item.modifiedShape)
                .SetOrientation(turns)
                .SetPosition(x, y)
                .Build();
            if (shape == null) return ActionResult.Failure("move_item", "bad_shape");

            if (shape.minX < 0 || shape.minY < 0
                || shape.maxX >= grid.inventoryShape.width
                || shape.maxY >= grid.inventoryShape.height)
                return ActionResult.Failure("move_item", "out_of_bounds");

            var ignored = new List<GameItem>();
            if (sameContainer) ignored.Add(item);
            if (!grid.CheckShapePlacement(item, shape, ignored))
                return ActionResult.Failure("move_item", "invalid_placement");

            if (sameContainer)
            {
                // R5.3/A1: the in-place branch never had a rollback, so a throwing
                // ValidateShapeState left the item in the new shape anyway.
                try
                {
                    item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                    item.ValidateShapeState();
                    return ActionResult.Success("move_item");
                }
                catch (Exception)
                {
                    item.modifiedShape = originalShape;
                    return ActionResult.Failed("move_item", "native_error");
                }
            }

            GameInventory parent = item.parentInventory;
            if (parent == null || !parent.Expel(item)) return ActionResult.Failed("move_item", "detach_failed");

            item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
            if (!grid.UncheckedAccept(item))
            {
                item.modifiedShape = originalShape;
                parent.UncheckedAccept(item);
                return ActionResult.Failed("move_item", "no_space");
            }

            item.ValidateShapeState();
            return ActionResult.Success("move_item");
        }

        private static string ErrorCode(MoveCheck check)
        {
            switch (check)
            {
                case MoveCheck.ItemNotFound: return "item_not_found";
                case MoveCheck.ItemLocked: return "item_locked";
                case MoveCheck.TargetNotFound: return "unknown_container";
                case MoveCheck.TargetLocked: return "container_locked";
                case MoveCheck.SameContainer: return "same_container";
                default: return "ok";
            }
        }

        /// <summary>
        /// Accepts the offer on the negotiation table by pressing the game's own
        /// accept button. Moving cash/items directly (the old implementation)
        /// left the client's business incomplete and never started the closing
        /// dialogue, so the customer stayed in the shop forever and the queue
        /// stalled. The native click completes the business, plays the accept
        /// dialogue and lets the client walk out once that dialogue ends.
        /// </summary>
        public static ActionResult ConfirmTrade(int itemId, string side)
        {
            // C module: one guarded path. TradeService pre-checks every known silent
            // native refusal branch (B10-B16) before the click and only reports
            // completed on trusted business-complete evidence for the same client.
            return new TradeService(new GameTradePort()).Confirm(
                itemId, side, StateSnapshot.CurrentRevision);
        }

        /// <summary>
        /// R5/B1: stage one item for the current client negotiation through the
        /// player-equivalent native weighted-counter path. The service pre-checks
        /// every readable field, dispatches at most one native staging step and
        /// only reports completed when the readback proves the staged pointer, the
        /// physical counter position, the open negotiation tablet and an unchanged
        /// client identity.
        /// </summary>
        public static ActionResult StageNegotiationItem(int itemId)
        {
            return new NegotiationStagingService(new GameNegotiationStagingPort())
                .Stage(itemId, StateSnapshot.CurrentRevision);
        }

        private static NegociationUIManager GetNegotiationUi()
        {
            try
            {
                return NegociationUIManager.Instance;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryStore(out PlayerStore store)
        {
            try
            {
                // Do not call the Instance getter at the main menu: it creates an
                // empty PlayerStore and makes "not in game" checks lie.
                if (!PlayerStore.HasInstance)
                {
                    store = null;
                    return false;
                }
                store = PlayerStore.Instance;
                return store != null;
            }
            catch
            {
                store = null;
                return false;
            }
        }

        private static DialogUIManager GetDialogUi()
        {
            try
            {
                return DialogUIManager.Instance;
            }
            catch
            {
                return null;
            }
        }

        private static BargainUIManager GetBargainUi()
        {
            try
            {
                return BargainUIManager.Instance;
            }
            catch
            {
                return null;
            }
        }

        public static ActionResult Bargain(string direction, int percent)
        {
            if (percent != 10 && percent != 25 && percent != 50 && percent != 75)
                return ActionResult.Failure("bargain", "bad_percent");

            BargainUIManager ui = GetBargainUi();
            if (ui == null) return ActionResult.Failure("bargain", "no_bargain_ui");

            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "sell_discount":
                    if (!ui.CanOfferDiscount()) return ActionResult.Failure("bargain", "unavailable");
                    ui.OfferDiscount(percent);
                    break;

                case "sell_markup":
                    if (!ui.CanOfferMarkup()) return ActionResult.Failure("bargain", "unavailable");
                    ui.OfferMarkup(percent);
                    break;

                case "buy_markup":
                    if (!ui.CanOfferBuyingMarkup()) return ActionResult.Failure("bargain", "unavailable");
                    ui.OfferBuyingMarkup(percent);
                    break;

                case "buy_discount":
                    if (!ui.CanDemandBuyingDiscount()) return ActionResult.Failure("bargain", "unavailable");
                    ui.DemandBuyingDiscount(percent);
                    break;

                default:
                    return ActionResult.Failure("bargain", "bad_direction");
            }

            return ActionResult.Success("bargain");
        }
    }
}
