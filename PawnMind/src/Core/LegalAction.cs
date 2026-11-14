using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class LegalAction
    {
        public string Action { get; private set; }
        public Dictionary<string, object> Args { get; private set; }
        public string Hint { get; private set; }

        public LegalAction(string action)
        {
            Action = action;
            Args = new Dictionary<string, object>();
        }

        public LegalAction With(string key, object value)
        {
            Args[key] = value;
            return this;
        }

        public LegalAction WithHint(string hint)
        {
            Hint = hint;
            return this;
        }
    }

    /// <summary>
    /// Minimal state projection used to derive the currently legal actions.
    /// Keeping it a plain POCO makes the rule set unit-testable without Unity.
    /// </summary>
    public sealed class GameFlags
    {
        public bool InGame;
        public string ShopState = string.Empty;
        public bool CanCallNext;
        public bool CanEndDay;
        public bool HasClient;
        public bool ClientArrived;
        public bool DialogueActive;
        public bool DialogueTyping;
        public bool DialogueChoice;
        public List<int> ChoiceIndices = new List<int>();
        public List<int> DisabledChoiceIndices = new List<int>();
        public bool BargainOpen;
        public bool CanSellDiscount;
        public bool CanSellMarkup;
        public bool CanBuyMarkup;
        public bool CanBuyDiscount;
        public List<int> BuyItemIds = new List<int>();
        public List<int> SellItemIds = new List<int>();
    }

    public static class LegalActionBuilder
    {
        public static List<LegalAction> Build(GameFlags flags)
        {
            var actions = new List<LegalAction>();
            if (flags == null) return actions;

            if (!flags.InGame)
            {
                actions.Add(new LegalAction("new_game"));
                actions.Add(new LegalAction("load_game")
                    .WithHint("pass slot from the game's load menu; never touch a player save without confirmation"));
                return actions;
            }

            if (string.Equals(flags.ShopState, "MORNING", System.StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new LegalAction("open_shop"));
            }

            if (flags.CanCallNext)
            {
                actions.Add(new LegalAction("call_next_customer"));
            }

            if (flags.HasClient && flags.DialogueChoice)
            {
                for (int i = 0; i < flags.ChoiceIndices.Count; i++)
                {
                    int index = flags.ChoiceIndices[i];
                    if (flags.DisabledChoiceIndices != null
                        && flags.DisabledChoiceIndices.Contains(index)) continue;

                    actions.Add(new LegalAction("choose_dialogue").With("index", index));
                }
            }
            else if (flags.HasClient && flags.DialogueActive)
            {
                actions.Add(new LegalAction("advance_dialogue"));
            }

            if (flags.HasClient && flags.ClientArrived)
            {
                for (int i = 0; i < flags.BuyItemIds.Count && i < 8; i++)
                {
                    actions.Add(new LegalAction("confirm_trade")
                        .With("item_id", flags.BuyItemIds[i]).With("side", "buy"));
                }

                for (int i = 0; i < flags.SellItemIds.Count && i < 8; i++)
                {
                    actions.Add(new LegalAction("confirm_trade")
                        .With("item_id", flags.SellItemIds[i]).With("side", "sell"));
                }

                if (flags.BargainOpen)
                {
                    if (flags.CanSellDiscount) actions.Add(Bargain("sell_discount"));
                    if (flags.CanSellMarkup) actions.Add(Bargain("sell_markup"));
                    if (flags.CanBuyMarkup) actions.Add(Bargain("buy_markup"));
                    if (flags.CanBuyDiscount) actions.Add(Bargain("buy_discount"));
                }
            }

            if (string.Equals(flags.ShopState, "OPEN", System.StringComparison.OrdinalIgnoreCase))
            {
                if (flags.CanEndDay) actions.Add(new LegalAction("end_day"));
                actions.Add(new LegalAction("sleep"));
            }

            actions.Add(new LegalAction("save_game"));
            return actions;
        }

        private static LegalAction Bargain(string direction)
        {
            return new LegalAction("bargain")
                .With("direction", direction)
                .With("percent_options", new List<int> { 10, 25, 50, 75 });
        }
    }
}
