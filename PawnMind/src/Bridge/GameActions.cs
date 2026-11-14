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
        public static ActionResult OpenShop()
        {
            // The UI button runs the full morning sequence (OnShutterOpened generates
            // the day's client stack). Calling PlayerStore.OpenShutter alone only flips
            // the state enum and leaves the shop with no customers.
            try
            {
                StoreShutterButton button = StoreShutterButton.Instance;
                if (button != null)
                {
                    button.OpenShutter();
                    return ActionResult.Success("open_shop");
                }
            }
            catch
            {
            }

            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("open_shop", "not_in_game");

            store.OnShutterOpened();
            store.OpenShutter();
            return ActionResult.Success("open_shop");
        }

        public static ActionResult CallNextCustomer()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("call_next_customer", "not_in_game");
            if (!store.CanNextClient()) return ActionResult.Failure("call_next_customer", "cannot_call_next");

            store.TryCallNextClient();
            return ActionResult.Success("call_next_customer");
        }

        public static ActionResult AdvanceDialogue()
        {
            DialogUIManager ui = GetDialogUi();
            if (ui == null) return ActionResult.Failure("advance_dialogue", "no_dialogue");
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
                return ActionResult.Failure("buy_wholesale", "wholesale_failed: " + e.Message);
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

            if (turns < 0 || turns > 3) return ActionResult.Failure("move_item", "bad_rotation");
            if (item == null) return ActionResult.Failure("move_item", "item_not_found");
            if (target == null) return ActionResult.Failure("move_item", "unknown_container");
            if (ItemPinHelper.IsPinned(item)) return ActionResult.Failure("move_item", "item_locked");
            if (target.IsInsertLocked()) return ActionResult.Failure("move_item", "container_locked");

            bool sameContainer = item.parentInventory == target;
            bool specific = x >= 0 && y >= 0;

            if (!specific)
            {
                if (sameContainer) return ActionResult.Failure("move_item", "same_container");

                GameInventory result = target.TryAcceptAll(item);
                if (result != null && item.parentInventory == target) return ActionResult.Success("move_item");

                var targetGrid = target as GameGridInventory;
                if (targetGrid != null && TryRepack(targetGrid, item) && item.parentInventory == targetGrid)
                    return ActionResult.Success("move_item");

                return ActionResult.Failure("move_item", "no_space");
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
                item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                item.ValidateShapeState();
                return ActionResult.Success("move_item");
            }

            GameInventory parent = item.parentInventory;
            if (parent == null || !parent.Expel(item)) return ActionResult.Failure("move_item", "detach_failed");

            item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
            if (!grid.UncheckedAccept(item))
            {
                item.modifiedShape = originalShape;
                parent.UncheckedAccept(item);
                return ActionResult.Failure("move_item", "no_space");
            }

            item.ValidateShapeState();
            return ActionResult.Success("move_item");
        }

        private sealed class RepackSnapshot
        {
            public GameItem Item;
            public GameInventory Parent;
            public GridShape Shape;
        }

        private static bool TryRepack(GameGridInventory grid, GameItem incoming)
        {
            int width = grid.inventoryShape.width;
            int height = grid.inventoryShape.height;
            List<GameItem> targetItems = grid.childItems;

            var blocked = new bool[width * height];
            var movables = new List<GameItem>();
            for (int i = 0; i < targetItems.Count; i++)
            {
                GameItem item = targetItems[i];
                if (item == null || item == incoming) continue;

                if (ItemPinHelper.IsPinned(item))
                {
                    MarkShape(blocked, width, height, item.modifiedShape);
                }
                else
                {
                    movables.Add(item);
                }
            }

            var solverItems = new List<RepackItem>();
            for (int i = 0; i < movables.Count; i++)
            {
                RepackItem model = ToModel(movables[i]);
                if (model != null) solverItems.Add(model);
            }

            bool incomingInside = incoming.parentInventory == grid;
            RepackItem incomingModel = ToModel(incoming);
            if (incomingModel == null) return false;
            if (!incomingInside) solverItems.Add(incomingModel);

            RepackResult solved = RepackSolver.Solve(width, height, blocked, solverItems);
            if (!solved.Success) return false;

            var snapshots = new List<RepackSnapshot>();
            for (int i = 0; i < movables.Count; i++) snapshots.Add(Snapshot(movables[i]));
            if (!incomingInside) snapshots.Add(Snapshot(incoming));

            try
            {
                for (int i = 0; i < movables.Count; i++)
                {
                    GameInventory parent = movables[i].parentInventory;
                    if (parent != null && !parent.Expel(movables[i]))
                        throw new InvalidOperationException("detach failed");
                }

                if (!incomingInside && incoming.parentInventory != null
                    && !incoming.parentInventory.Expel(incoming))
                    throw new InvalidOperationException("incoming detach failed");

                for (int i = 0; i < solved.Placements.Count; i++)
                {
                    RepackPlacement placement = solved.Placements[i];
                    GameItem item = FindById(movables, incoming, placement.Id);
                    if (item == null) throw new InvalidOperationException("item missing");

                    GridShape shape = new GridShapeBuilder(item.modifiedShape)
                        .SetOrientation(placement.Turns)
                        .SetPosition(placement.X, placement.Y)
                        .Build();
                    item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                    if (!grid.UncheckedAccept(item)) throw new InvalidOperationException("accept failed");
                }

                grid.Validate();
                for (int i = 0; i < solved.Placements.Count; i++)
                {
                    GameItem placed = FindById(movables, incoming, solved.Placements[i].Id);
                    if (placed == null || placed.parentInventory != grid)
                        throw new InvalidOperationException("item not in target after repack");
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] repack failed, rolling back: " + e.Message);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    RepackSnapshot snapshot = snapshots[i];
                    if (snapshot.Item.parentInventory != null)
                    {
                        snapshot.Item.parentInventory.Expel(snapshot.Item);
                    }
                }

                for (int i = 0; i < snapshots.Count; i++)
                {
                    RepackSnapshot snapshot = snapshots[i];
                    snapshot.Item.modifiedShape = snapshot.Shape;
                    if (snapshot.Parent != null) snapshot.Parent.UncheckedAccept(snapshot.Item);
                }

                grid.Validate();
                return false;
            }
        }

        private static RepackSnapshot Snapshot(GameItem item)
        {
            return new RepackSnapshot
            {
                Item = item,
                Parent = item.parentInventory,
                Shape = item.modifiedShape == null ? null : item.modifiedShape.Clone()
            };
        }

        private static RepackItem ToModel(GameItem item)
        {
            if (item == null || item.modifiedShape == null) return null;

            GridShape shape = item.modifiedShape;
            int width = shape.maxX - shape.minX + 1;
            int height = shape.maxY - shape.minY + 1;
            if (width <= 0 || height <= 0) return null;

            var cells = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (shape.Get(shape.minX + x, shape.minY + y) != 0) cells[y * width + x] = 1;
                }
            }

            return new RepackItem
            {
                Id = item.GetUniqueID(), Width = width, Height = height, Cells = cells
            };
        }

        private static GameItem FindById(List<GameItem> items, GameItem incoming, int id)
        {
            if (incoming != null && incoming.GetUniqueID() == id) return incoming;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].GetUniqueID() == id) return items[i];
            }

            return null;
        }

        private static void MarkShape(bool[] grid, int width, int height, GridShape shape)
        {
            if (shape == null) return;
            for (int y = shape.minY; y <= shape.maxY; y++)
            {
                if (y < 0 || y >= height) continue;
                for (int x = shape.minX; x <= shape.maxX; x++)
                {
                    if (x < 0 || x >= width) continue;
                    if (shape.Get(x, y) != 0) grid[y * width + x] = true;
                }
            }
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

        public static ActionResult ConfirmTrade(int itemId, string side)
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("confirm_trade", "not_in_game");

            GameItem item = GameState.FindItem(itemId);
            if (item == null) return ActionResult.Failure("confirm_trade", "item_not_found");

            bool owned = GameState.IsOwned(item);
            long price = item.GetNegociatedValue();

            if (string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase))
            {
                if (owned) return ActionResult.Failure("confirm_trade", "already_owned");
                if (store.GetCash() < price) return ActionResult.Failure("confirm_trade", "cannot_afford");
                store.BuyItem(item);
                return ActionResult.Success("confirm_trade");
            }

            if (string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase))
            {
                if (!owned) return ActionResult.Failure("confirm_trade", "not_owned");
                store.SellItem(item);
                return ActionResult.Success("confirm_trade");
            }

            return ActionResult.Failure("confirm_trade", "bad_side");
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
