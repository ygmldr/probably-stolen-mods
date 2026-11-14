using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// S5 game adapter. The bridge service performs every guard and readback;
    /// this class only exposes the read-only projections and the two single
    /// native callbacks (SelectChoice / EndDay) through GameState.
    /// </summary>
    internal sealed class FirstWeekAdapter : IDialoguePort, IRentPort, IShopDayPort
    {
        public static readonly FirstWeekAdapter Instance = new FirstWeekAdapter();

        public DialogueView ReadDialogue()
        {
            return GameState.ReadDialogueView();
        }

        public void SelectChoice(int index)
        {
            GameState.SelectDialogueChoice(index);
        }

        public RentSnapshot ReadRent()
        {
            return GameState.ReadRentSnapshot();
        }

        public ShopDaySnapshot ReadShopDay()
        {
            return GameState.ReadShopDaySnapshot();
        }

        public void EndDay()
        {
            GameState.EndDay();
        }
    }
}
