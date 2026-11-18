using System;
using System.Runtime.CompilerServices;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// C module adapter: read-only trade guard snapshot plus exactly one native
    /// accept click. Every guard value comes from a native member listed in
    /// docs/audit/PLAYER_ENTRY_MATRIX.csv (B10-B16); unavailable reads fail closed
    /// (false) so the bridge refuses instead of guessing.
    /// </summary>
    internal sealed class GameTradePort : ITradePort
    {
        public TradeGuardInput Read(int itemId, string side)
        {
            var input = new TradeGuardInput
            {
                InGame = GameState.IsInGame,
                RequestedItemId = itemId,
                Side = side
            };

            StoreClient client = GameState.GetCurrentClient();
            input.ClientPresent = client != null;
            input.NegotiationOpen = GameState.IsNegotiationOpen();
            input.TradeLocked = SafeTradeLocked();

            GameItem staged = GameState.GetNegotiatedItem();
            input.StagedItemId = staged == null ? 0 : staged.GetUniqueID();
            input.StagedItemOwned = SafeNegociatingItemOwned();

            if (client == null) return input;

            input.ClientIntent = client.clientIntent.ToString();
            input.BusinessComplete = GameState.IsCurrentClientBusinessComplete();
            input.Introduced = client.isIntroduced;
            input.BarterOfferPresent = client.barterOffer != null;
            input.BarterAcceptable = SafeBarterAcceptable(client);
            input.ServiceCost = client.serviceCost;
            input.CanPay = SafeCanPay(client.serviceCost);
            input.GunOrderPresent = client.gunOrderData != null;
            input.GunOrderAccepted = SafeGunOrderAccepted(client);
            input.CanBuyFromTable = SafeCanBuyFromTable();
            GameItem item = GameState.FindItem(itemId);
            input.CanSellItem = item != null && GameState.CanSellItem(item);

            // R5/A3: mirror the native sell budget gate with player-visible values
            // only — StoreClient.useClientBudget plus GetBudget() (the client.budget
            // snapshot field) and the negotiation price GetNegociatedValue() shown
            // on the tablet. The trade-time enforcement happens in
            // PlayerStore.SellItemFromTable -> CanSellThisItem -> HasBudgetLeftToBuy,
            // which ignores the accept click without reporting anything.
            input.ClientUsesBudget = client.useClientBudget;
            input.ClientBudget = GameState.GetClientBudget(client);
            input.StagedItemValue = SafeNegociatedValue(staged);
            return input;
        }

        /// <summary>
        /// Player-visible negotiation price of the staged item. This is the same
        /// value the native sell budget gate compares with StoreClient.GetBudget();
        /// the state snapshot already reads it for every item. An unreadable price
        /// fails closed (long.MaxValue) so the sale is refused instead of silently
        /// ignored by the game.
        /// </summary>
        private static long SafeNegociatedValue(GameItem item)
        {
            if (item == null) return 0L;
            try
            {
                return item.GetNegociatedValue();
            }
            catch (Exception)
            {
                return long.MaxValue;
            }
        }

        public TradeEvidence Accept()
        {
            var evidence = new TradeEvidence
            {
                CashBefore = GameState.GetCash(),
                ClientUnchanged = false
            };

            int clientBefore = GameState.GetCurrentClientHandle();
            GameItem stagedBefore = GameState.GetNegotiatedItem();
            int stagedIdBefore = stagedBefore == null ? 0 : stagedBefore.GetUniqueID();

            // Throws when the negotiation panel is gone; the service maps that to
            // uncertain, never success.
            GameState.AcceptNegotiationOffer();

            int clientAfter = GameState.GetCurrentClientHandle();
            GameItem stagedAfter = GameState.GetNegotiatedItem();
            int stagedIdAfter = stagedAfter == null ? 0 : stagedAfter.GetUniqueID();

            evidence.Called = true;
            evidence.CashAfter = GameState.GetCash();
            evidence.BusinessComplete = GameState.IsCurrentClientBusinessComplete();
            evidence.ClientUnchanged = clientBefore != 0 && clientBefore == clientAfter;
            evidence.StagedItemIdAfter = stagedIdAfter;
            evidence.ItemsTransferred = stagedIdBefore != 0 && stagedIdAfter != stagedIdBefore;
            return evidence;
        }

        private static bool SafeTradeLocked()
        {
            try
            {
                return NegociationUIManager.IsTradeLocked();
            }
            catch (Exception)
            {
                // Fail closed: an unreadable lock is treated as locked.
                return true;
            }
        }

        private static bool SafeNegociatingItemOwned()
        {
            try
            {
                NegociationUIManager ui = NegociationUIManager.Instance;
                return ui != null && ui.IsNegociatingItemOwned();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeBarterAcceptable(StoreClient client)
        {
            try
            {
                return client.barterOffer != null && client.barterOffer.IsBarterAcceptable();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeGunOrderAccepted(StoreClient client)
        {
            try
            {
                return client.gunOrderData != null && client.gunOrderData.IsAccepted();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeCanPay(int price)
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.CanPay(price);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeCanBuyFromTable()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store != null && store.CanBuyItemFromTable();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>In-process identity for the current client (never serialized).</summary>
        public static int ClientHandle(StoreClient client)
        {
            return client == null ? 0 : RuntimeHelpers.GetHashCode(client);
        }
    }
}
