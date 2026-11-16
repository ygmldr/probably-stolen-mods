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
            // save_game is not player reachable (the game auto-saves on sleep), so it
            // must not be advertised as a legal player action.
            Assert.False(Has(actions, "save_game"), "save is not a player action");
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
        public static void Disabled_Choices_Are_Not_Offered()
        {
            var flags = new GameFlags
            {
                InGame = true,
                HasClient = true,
                ClientArrived = true,
                DialogueChoice = true,
                ChoiceIndices = new List<int> { 0, 1, 2 },
                DisabledChoiceIndices = new List<int> { 1, 2 }
            };
            var actions = Build(flags);
            Assert.NotNull(Find(actions, "choose_dialogue", "index", 0), "enabled choice offered");
            Assert.Null(Find(actions, "choose_dialogue", "index", 1), "disabled choice not offered");
            Assert.Null(Find(actions, "choose_dialogue", "index", 2), "disabled choice not offered");
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
        public static void Bargain_Directions_Are_Candidates_Not_Legal()
        {
            // PP-READ-05: the panel being open only means bargaining *may* be
            // possible. The per-direction flags live behind Refresh()-backed
            // getters, so the read path must report candidates with
            // availability=unknown and let the native guard decide on write.
            var flags = new GameFlags
            {
                InGame = true,
                HasClient = true,
                ClientArrived = true,
                BargainOpen = true
            };
            var actions = Build(flags);

            foreach (string direction in new[] { "sell_discount", "sell_markup", "buy_markup", "buy_discount" })
            {
                LegalAction action = Find(actions, "bargain", "direction", direction);
                Assert.NotNull(action, direction);
                Assert.Equal(LegalAction.AvailabilityCandidate, action.Availability,
                    direction + " is a candidate");
                Assert.Equal("unknown", action.ReportedAvailability,
                    direction + " must not claim availability=available");
                Assert.Equal("native_guard", action.Requires, direction + " needs a native guard");
                Assert.False(action.IsAvailable, direction + " is not available");
            }

            Assert.Equal(0, LegalActionBuilder.Available(actions).Count - CountNonBargain(actions),
                "no bargain direction is reported as legal");
            Assert.Equal(4, LegalActionBuilder.Candidates(actions).Count,
                "all four directions are candidates");
        }

        private static int CountNonBargain(List<LegalAction> actions)
        {
            int count = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Action != "bargain") count++;
            }

            return count;
        }

        [Test]
        public static void Closed_Bargain_Offers_Nothing()
        {
            var flags = new GameFlags { InGame = true, HasClient = true, ClientArrived = true };
            Assert.Null(Find(Build(flags), "bargain", "direction", "sell_discount"), "panel closed");
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
