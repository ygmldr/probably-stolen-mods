using System;
using System.Collections.Generic;
using PawnMind.Core;
using PawnMind.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        public static ActionResult ChooseDialogue(int index)
        {
            DialogUIManager ui = GetDialogUi();
            if (ui == null) return ActionResult.Failure("choose_dialogue", "no_dialogue");
            if (!ui.IsChoice()) return ActionResult.Failure("choose_dialogue", "no_choice");

            Dialogue current = ui.GetCurrentDialog();
            if (current == null || current.choices == null)
                return ActionResult.Failure("choose_dialogue", "no_choice");
            if (index < 0 || index >= current.choices.Count)
                return ActionResult.Failure("choose_dialogue", "bad_index");

            ui.SelectChoice(index);
            return ActionResult.Success("choose_dialogue");
        }

        public static ActionResult EndDay()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("end_day", "not_in_game");
            if (!store.CanEndDay()) return ActionResult.Failure("end_day", "cannot_end_day");

            store.EndDay();
            return ActionResult.Success("end_day");
        }

        public static ActionResult NewGame()
        {
            PlayerStore existing;
            if (TryStore(out existing)) return ActionResult.Failure("new_game", "already_in_game");

            NewGameData data = NewGameData.Instance;
            if (data == null) return ActionResult.Failure("new_game", "no_new_game_data");

            data.saveSlot = -1;
            data.saveFile = null;
            data.isLoadFromMainMenu = false;
            data.startType = NewGameData.StartType.APPRENTICE;
            data.skipIntro = true;

            EmporiumEntry.sceneLoadStartTime = Time.realtimeSinceStartup;
            SceneManager.LoadScene("InventoryScene", LoadSceneMode.Single);
            return ActionResult.Success("new_game");
        }

        public static ActionResult LoadGame(int slot)
        {
            PlayerStore existing;
            if (TryStore(out existing)) return ActionResult.Failure("load_game", "already_in_game");

            NewGameData data = NewGameData.Instance;
            if (data == null) return ActionResult.Failure("load_game", "no_new_game_data");

            MainMenuUIController controller = MainMenuUIController.Instance;
            if (controller == null) return ActionResult.Failure("load_game", "no_main_menu");

            data.saveSlot = slot;
            data.saveFile = null;
            data.isLoadFromMainMenu = true;
            controller.StartNewGameClick();
            return ActionResult.Success("load_game");
        }

        public static ActionResult Sleep()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("sleep", "not_in_game");

            EndOfDayUIManager ui = EndOfDayUIManager.Instance;
            if (ui == null) return ActionResult.Failure("sleep", "no_end_of_day_ui");

            ui.OnEndDayButtonClick();
            return ActionResult.Success("sleep");
        }

        public static ActionResult SaveGame()
        {
            PlayerStore store;
            if (!TryStore(out store)) return ActionResult.Failure("save_game", "not_in_game");

            store.SaveGame();
            return ActionResult.Success("save_game");
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
                return result == null
                    ? ActionResult.Failure("move_item", "no_space")
                    : ActionResult.Success("move_item");
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
