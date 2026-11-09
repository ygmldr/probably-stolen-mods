using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class LegalActionTests
    {
        private static List<LegalAction> Build(GameFlags flags)
        {
            return LegalActionBuilder.Build(flags);
        }

        private static bool Has(List<LegalAction> actions, string name)
        {
            foreach (LegalAction action in actions)
            {
                if (action.Action == name) return true;
            }

            return false;
        }

        private static LegalAction Find(List<LegalAction> actions, string name, string key, object value)
        {
            foreach (LegalAction action in actions)
            {
                if (action.Action != name) continue;
                object actual;
                if (action.Args != null && action.Args.TryGetValue(key, out actual)
                    && Equals(actual, value)) return action;
            }

            return null;
        }

        [Test]
        public static void Menu_Offers_New_And_Load()
        {
            var actions = Build(new GameFlags { InGame = false });
            Assert.True(Has(actions, "new_game"), "new game at menu");
            Assert.True(Has(actions, "load_game"), "load game at menu");
            Assert.False(Has(actions, "open_shop"), "no shop actions at menu");
        }

        [Test]
        public static void Morning_Offers_Open_Shop()
        {
            var actions = Build(new GameFlags { InGame = true, ShopState = "MORNING" });
            Assert.True(Has(actions, "open_shop"), "open shop in the morning");
            Assert.True(Has(actions, "save_game"), "save is always offered in game");
        }

        [Test]
        public static void CanCallNext_Offers_Call_Customer()
        {
            var actions = Build(new GameFlags { InGame = true, ShopState = "OPEN", CanCallNext = true });
            Assert.True(Has(actions, "call_next_customer"), "call next customer");
        }

        [Test]
        public static void Active_Dialogue_Offers_Advance()
        {
            var actions = Build(new GameFlags
            {
                InGame = true,
                ShopState = "OPEN",
                HasClient = true,
                ClientArrived = true,
                DialogueActive = true
            });
            Assert.True(Has(actions, "advance_dialogue"), "advance dialogue");
                    }

        [Test]
        public static void Choices_Offer_Choose_For_Each_Index()
        {
            var flags = new GameFlags
            {
                InGame = true,
                HasClient = true,
                ClientArrived = true,
                DialogueChoice = true,
                ChoiceIndices = new List<int> { 0, 1 }
            };
            var actions = Build(flags);
            Assert.NotNull(Find(actions, "choose_dialogue", "index", 0), "choice 0");
            Assert.NotNull(Find(actions, "choose_dialogue", "index", 1), "choice 1");
        }

        [Test]
        public static void Counter_Items_Offer_Buy_And_Sell()
        {
            var flags = new GameFlags
            {
                InGame = true,
                HasClient = true,
                ClientArrived = true,
                BuyItemIds = new List<int> { 56 },
                SellItemIds = new List<int> { 12 }
            };
            var actions = Build(flags);
            Assert.NotNull(Find(actions, "confirm_trade", "item_id", 56), "buy item");
            Assert.NotNull(Find(actions, "confirm_trade", "item_id", 12), "sell item");
        }

        [Test]
        public static void Bargain_Offers_Only_Available_Directions()
        {
            var flags = new GameFlags
            {
                InGame = true,
                HasClient = true,
                ClientArrived = true,
                BargainOpen = true,
                CanSellDiscount = true,
                CanBuyDiscount = false
            };
            var actions = Build(flags);
            Assert.NotNull(Find(actions, "bargain", "direction", "sell_discount"), "available direction");
            Assert.Null(Find(actions, "bargain", "direction", "buy_discount"), "unavailable direction");
        }

        [Test]
        public static void Open_Shop_Offers_End_Day_And_Sleep()
        {
            var actions = Build(new GameFlags { InGame = true, ShopState = "OPEN", CanEndDay = true });
            Assert.True(Has(actions, "end_day"), "end day");
            Assert.True(Has(actions, "sleep"), "sleep");
        }
    }
}
