using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// C module: confirm_trade guard and outcome evidence.
    ///
    /// Evidence level: L1-fake. The fake trade port counts native accept clicks,
    /// so "dispatch zero times" is a counted fact, and the evidence layering is
    /// exercised without Unity. The real click path stays NOT_RUN (PP-TRADE-real).
    /// </summary>
    internal static class TradeServiceTests
    {
        private sealed class FakeTradePort : ITradePort
        {
            public TradeGuardInput Guard = PassingGuard();
            public TradeEvidence Evidence = CompletedEvidence();
            public bool ThrowOnRead;
            public bool ThrowOnAccept;
            public int AcceptCalls;
            public int ReadCalls;

            public TradeGuardInput Read(int itemId, string side)
            {
                ReadCalls++;
                if (ThrowOnRead) throw new System.InvalidOperationException("read boom");
                Guard.RequestedItemId = itemId;
                Guard.Side = side;
                return Guard;
            }

            public TradeEvidence Accept()
            {
                AcceptCalls++;
                if (ThrowOnAccept) throw new System.InvalidOperationException("accept boom");
                return Evidence;
            }
        }

        private static TradeGuardInput PassingGuard()
        {
            return new TradeGuardInput
            {
                InGame = true,
                ClientPresent = true,
                TradeLocked = false,
                NegotiationOpen = true,
                RequestedItemId = 56,
                StagedItemId = 56,
                StagedItemOwned = false,
                Side = "buy",
                ClientIntent = "BUY",
                BusinessComplete = false,
                Introduced = true,
                CanBuyFromTable = true,
                CanSellItem = true
            };
        }

        private static TradeEvidence CompletedEvidence()
        {
            return new TradeEvidence
            {
                Called = true,
                BusinessComplete = true,
                ClientUnchanged = true,
                ItemsTransferred = true,
                CashBefore = 600,
                CashAfter = 480,
                StagedItemIdAfter = 0
            };
        }

        private static JObject Run(TradeService service, string side = "buy")
        {
            ActionResult result = service.Confirm(56, side, 42);
            return JObject.Parse(result.ToJson(42));
        }

        private static JObject On(Action<TradeGuardInput> mutate, string side = "buy")
        {
            var port = new FakeTradePort();
            mutate(port.Guard);
            var service = new TradeService(port);
            JObject json = Run(service, side);
            Assert.False((bool)json["ok"], "a refused trade is not ok");
            Assert.Equal(0, port.AcceptCalls, "a refused trade dispatches zero native clicks");
            return json;
        }

        // ------------------------------------------------------------ happy path

        [Test]
        public static void PP_TRADE_01_Normal_Trade_Completes_Before_The_Dialogue()
        {
            var port = new FakeTradePort();
            var service = new TradeService(port);

            JObject json = Run(service);

            Assert.True((bool)json["ok"], "normal trade completes");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.True((bool)json["business_complete"], "business completion is the evidence");
            Assert.True((bool)json["client_unchanged"], "same client instance");
            Assert.Equal(1, port.AcceptCalls, "exactly one native click");
            Assert.Equal(false, (bool)json["cash_changed"] == false, "cash changed here");
        }

        [Test]
        public static void PP_TRADE_05_Barter_Completes_Without_Cash_Movement()
        {
            var port = new FakeTradePort();
            port.Guard.ClientIntent = "BARTER";
            port.Guard.StagedItemOwned = false;
            port.Guard.BarterOfferPresent = true;
            port.Guard.BarterAcceptable = true;
            port.Evidence.CashBefore = 600;
            port.Evidence.CashAfter = 600;
            port.Evidence.ItemsTransferred = true;

            JObject json = Run(new TradeService(port));

            Assert.True((bool)json["ok"], "barter completes");
            Assert.False((bool)json["cash_changed"], "no cash moved");
            Assert.True((bool)json["items_transferred"], "items moved instead");
            Assert.Equal(1, port.AcceptCalls, "one native click");
        }

        // --------------------------------------------------------- B10-B16 guard

        [Test]
        public static void PP_TRADE_03a_B10_Trade_Locked_Never_Clicks()
        {
            JObject json = On(g => g.TradeLocked = true);
            Assert.Equal("trade_locked", (string)json["error"], "stable code");
            Assert.False((bool)json["dispatched"], "nothing was dispatched");
        }

        [Test]
        public static void PP_TRADE_03b_B11_Business_Already_Complete_Never_Clicks()
        {
            JObject json = On(g => g.BusinessComplete = true);
            Assert.Equal("business_complete", (string)json["error"], "stable code");

            JObject undefined = On(g => g.ClientIntent = "UNDEFINED");
            Assert.Equal("intent_undefined", (string)undefined["error"], "stable code");
        }

        [Test]
        public static void PP_TRADE_03c_B12_Staged_Item_And_Direction_Must_Match()
        {
            JObject wrongItem = On(g => g.StagedItemId = 99);
            Assert.Equal("item_not_staged", (string)wrongItem["error"], "wrong item");

            JObject wrongDirection = On(g => g.StagedItemOwned = false, "sell");
            Assert.Equal("not_owned", (string)wrongDirection["error"],
                "selling requires the native staged-item ownership");
        }

        [Test]
        public static void PP_TRADE_03d_B13_Barter_Conditions_Are_Prechecked()
        {
            JObject noOffer = On(g =>
            {
                g.ClientIntent = "BARTER";
                g.BarterOfferPresent = false;
            });
            Assert.Equal("barter_not_acceptable", (string)noOffer["error"], "no offer");

            JObject notAcceptable = On(g =>
            {
                g.ClientIntent = "BARTER";
                g.BarterOfferPresent = true;
                g.BarterAcceptable = false;
            });
            Assert.Equal("barter_not_acceptable", (string)notAcceptable["error"], "unacceptable");

            JObject notIntroduced = On(g =>
            {
                g.ClientIntent = "BARTER";
                g.BarterOfferPresent = true;
                g.BarterAcceptable = true;
                g.Introduced = false;
            });
            Assert.Equal("not_introduced", (string)notIntroduced["error"], "not introduced");
        }

        [Test]
        public static void PP_TRADE_03e_B14_Information_Dealer_Cost_Is_Prechecked()
        {
            JObject json = On(g =>
            {
                g.ClientIntent = "INFORMATION_DEALER";
                g.ServiceCost = 500;
                g.CanPay = false;
            });
            Assert.Equal("cannot_afford", (string)json["error"], "stable code");

            JObject notIntroduced = On(g =>
            {
                g.ClientIntent = "INFORMATION_DEALER";
                g.Introduced = false;
            });
            Assert.Equal("not_introduced", (string)notIntroduced["error"], "stable code");
        }

        [Test]
        public static void PP_TRADE_03f_B15_Gunsmith_Order_Is_Prechecked()
        {
            JObject noOrder = On(g => g.ClientIntent = "GUNSMITH");
            Assert.Equal("gun_order_not_accepted", (string)noOrder["error"], "no order");

            JObject notAccepted = On(g =>
            {
                g.ClientIntent = "GUNSMITH";
                g.GunOrderPresent = true;
                g.GunOrderAccepted = false;
            });
            Assert.Equal("gun_order_not_accepted", (string)notAccepted["error"], "not accepted");
        }

        [Test]
        public static void PP_TRADE_03g_B16_Buy_And_Sell_Native_Gates_Are_Prechecked()
        {
            JObject cannotBuy = On(g => g.CanBuyFromTable = false);
            Assert.Equal("cannot_buy_from_table", (string)cannotBuy["error"], "buy gate");

            JObject refuses = On(g =>
            {
                g.StagedItemOwned = true;
                g.CanSellItem = false;
            }, "sell");
            Assert.Equal("client_refuses_item", (string)refuses["error"], "sell gate");
        }

        [Test]
        public static void PP_TRADE_03_Read_Failure_Dispatches_Nothing()
        {
            var port = new FakeTradePort { ThrowOnRead = true };
            JObject json = Run(new TradeService(port));
            Assert.False((bool)json["ok"], "read failure is not success");
            Assert.Equal("trade_read_failed", (string)json["error"], "stable code");
            Assert.Equal(0, port.AcceptCalls, "no click");
        }

        // -------------------------------------------------------- evidence rules

        [Test]
        public static void PP_TRADE_06_Silent_Native_Return_Is_Uncertain_Not_Ok()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = false;
            // A silent return moved nothing and charged nothing.
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashAfter = port.Evidence.CashBefore;

            JObject json = Run(new TradeService(port));

            Assert.False((bool)json["ok"], "a silent return is not success");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.True((bool)json["unknown"], "explicit unknown");
            Assert.Contains((string)json["next"], "do not retry", "no blind retry");
            Assert.Equal(1, port.AcceptCalls, "the click was dispatched once");
        }

        [Test]
        public static void PP_TRADE_07_Native_Exception_Is_Uncertain()
        {
            var port = new FakeTradePort { ThrowOnAccept = true };
            JObject json = Run(new TradeService(port));
            Assert.False((bool)json["ok"], "exception is not success");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal(1, port.AcceptCalls, "one click attempted");
        }

        [Test]
        public static void PP_TRADE_08_Another_Client_Cannot_Back_This_Trade()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = true;
            port.Evidence.ClientUnchanged = false;

            JObject json = Run(new TradeService(port));

            Assert.False((bool)json["ok"], "a different client is not evidence");
            Assert.Equal("client_changed", (string)json["error"], "stable code");
        }

        [Test]
        public static void PP_TRADE_09_Ok_Only_For_Completed()
        {
            var port = new FakeTradePort();
            port.Evidence.Called = false; // native not called at all
            JObject json = Run(new TradeService(port));
            Assert.False((bool)json["ok"], "not called is not ok");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
        }
        // ------------------------------------------- R4: per-trade vs per-visit

        /// <summary>
        /// R4-TRADE-01: a customer buying several items finishes their visit later.
        /// Each sale still completed: the negotiated item left the table for the same
        /// client. Reporting that as uncertain made a normal sale look unproven.
        /// </summary>
        [Test]
        public static void R4_TRADE_01_Partial_Sale_Is_Completed_And_Flags_The_Visit()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = true;   // selling my own item
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = true;

            JObject json = Run(new TradeService(port), "sell");

            Assert.True((bool)json["ok"], "the sale moved the item, so it completed");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.False((bool)json["visit_complete"],
                "the customer may still buy more; the visit is not complete");
            Assert.True((bool)json["items_transferred"], "the transfer is the evidence");
            Assert.Equal(1, port.AcceptCalls, "one click");
        }

        /// <summary>
        /// R4-TRADE-02: nothing moved and the visit did not complete -> uncertain.
        /// </summary>
        [Test]
        public static void R4_TRADE_02_Nothing_Moved_Is_Still_Uncertain()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = true;
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashAfter = port.Evidence.CashBefore;

            JObject json = Run(new TradeService(port), "sell");

            Assert.False((bool)json["ok"], "nothing moved is not a sale");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("business_not_complete", (string)json["detail"], "stable detail");
        }

        /// <summary>
        /// R4-TRADE-04: buying from a customer keeps currentNegociatedItem on the
        /// bought item, so the staged-id pointer does not move. The cash delta is
        /// the evidence that the trade happened.
        /// </summary>
        [Test]
        public static void R4_TRADE_04_Purchase_Is_Proven_By_The_Cash_Delta()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = false;      // buying the customer's item
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;  // pointer stays on the item
            port.Evidence.CashBefore = 700;
            port.Evidence.CashAfter = 675;

            JObject json = Run(new TradeService(port), "buy");

            Assert.True((bool)json["ok"], "money moved, so the purchase completed");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.False((bool)json["visit_complete"], "the visit may continue");
            Assert.True((bool)json["cash_moved"], "the cash delta is the evidence");
            Assert.False((bool)json["items_transferred"], "the pointer did not move");
        }

        /// <summary>
        /// R4-TRADE-05: neither the item nor money moved -> still uncertain.
        /// </summary>
        [Test]
        public static void R4_TRADE_05_Nothing_Moved_No_Cash_Is_Uncertain()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = false;
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashBefore = 700;
            port.Evidence.CashAfter = 700;

            JObject json = Run(new TradeService(port), "buy");

            Assert.False((bool)json["ok"], "nothing moved is not a purchase");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
        }

        /// <summary>
        /// R4-TRADE-03: because the sale is terminal, a second trade with a new
        /// action id is not blocked by operation_pending. The live session deadlocked
        /// on exactly this.
        /// </summary>
        [Test]
        public static void R4_TRADE_03_Completed_Sale_Does_Not_Block_The_Next_Trade()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = true;
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = true;
            ActionRouter router = Router(port);

            string first = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-a\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"sell\"}");
            Assert.Contains(first, "\"ok\":true", "the first sale completed");

            port.Guard.StagedItemId = 57;
            port.Evidence.StagedItemIdAfter = 58;
            string second = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-b\","
                + "\"rev\":42,\"item_id\":57,\"side\":\"sell\"}");
            Assert.False(second.Contains("operation_pending"),
                "a completed sale must not lock trading: " + second);
            Assert.Equal(2, port.AcceptCalls, "the second sale reached the native click");
        }

        /// <summary>
        /// R4-TRADE-06: one uncertain trade blocks only *that* call. A different
        /// item is a different native call and must stay tradable; the same item with
        /// a new action id is still refused.
        /// </summary>
        [Test]
        public static void R4_TRADE_06_Uncertain_Trade_Blocks_Only_That_Item()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = true;
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashAfter = port.Evidence.CashBefore;
            ActionRouter router = Router(port);

            string first = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-x\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"sell\"}");
            Assert.Contains(first, "uncertain", "nothing moved, so the first trade is uncertain");

            port.Guard.StagedItemId = 57;
            string other = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-y\","
                + "\"rev\":42,\"item_id\":57,\"side\":\"sell\"}");
            Assert.False(other.Contains("operation_pending"),
                "a different item must not inherit the block: " + other);
            Assert.Equal(2, port.AcceptCalls, "the second item reached the native click");

            port.Guard.StagedItemId = 56;
            string same = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-z\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"sell\"}");
            Assert.Contains(same, "operation_pending",
                "the same call with a new action id is still refused");
            Assert.Equal(2, port.AcceptCalls, "no third native click");
        }

        // ---------------------------------------------- operation / dialogue split

        private const string GameVersion = "VERSION 1-Rev1";
        private const string GameMvid = "11111111-2222-3333-4444-555555555555";

        private static ActionRouter Router(FakeTradePort port)
        {
            var service = new TradeService(port);
            return new ActionRouter(
                new ActionGate(),
                delegate { return 42L; },
                delegate(string op, JObject request)
                {
                    if (op == "confirm_trade")
                    {
                        return service.Confirm(
                            (int)request["item_id"], (string)request["side"], 42L);
                    }

                    return ActionResult.SuccessRaw(op, "{\"ok\":true}");
                },
                new ActionHistory(),
                delegate { return 42L; },
                null,
                delegate
                {
                    return GameVersionGate.Evaluate(
                        new GameIdentity(GameVersion, GameMvid), GameVersion, GameMvid);
                });
        }

        [Test]
        public static void PP_TRADE_02_Same_Action_Id_Never_Repeats_The_Trade()
        {
            var port = new FakeTradePort();
            ActionRouter router = Router(port);

            const string body = "{\"op\":\"confirm_trade\",\"action_id\":\"trade-1\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"buy\"}";
            string first = router.Handle(body);
            Assert.Contains(first, "\"ok\":true", "first trade completes");
            Assert.Equal(1, port.AcceptCalls, "one native click");

            string retry = router.Handle(body);
            Assert.Contains(retry, "\"ok\":true", "the retry sees the completed evidence");
            Assert.Equal(1, port.AcceptCalls, "the retry must never click again");
            Assert.Equal(1, port.ReadCalls, "the retry is a cache hit, not a second pre-check");
        }

        [Test]
        public static void PP_TRADE_04_Dialogue_Is_A_Separate_Workflow()
        {
            var port = new FakeTradePort();
            ActionRouter router = Router(port);

            string trade = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-2\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"buy\"}");
            Assert.Contains(trade, "\"ok\":true,", "trade completed");

            // The trade operation ends at the commit. The closing dialogue is a
            // separate workflow and must not be locked by the completed trade.
            string dialogue = router.Handle(
                "{\"op\":\"advance_dialogue\",\"action_id\":\"dlg-1\",\"rev\":42}");
            Assert.Contains(dialogue, "\"ok\":true,", "dialogue continues after the trade");

            JObject operation = router.GetOperationJson("trade-2");
            Assert.Equal("completed", (string)operation["execution_status"],
                "the trade operation itself is completed");
        }

        [Test]
        public static void PP_TRADE_04_Uncommitted_Trade_Is_Not_Reported_Completed()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashAfter = port.Evidence.CashBefore;
            ActionRouter router = Router(port);

            string response = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"trade-3\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"buy\"}");
            JObject json = JObject.Parse(response);
            Assert.False((bool)json["ok"], "no business completion -> not ok");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");

            JObject operation = router.GetOperationJson("trade-3");
            Assert.Equal("uncertain", (string)operation["execution_status"],
                "the operation stays uncertain, never completed");
            Assert.False((bool)operation["ok"], "uncertain is not ok");

            // A different write (the dialogue) is still allowed while the trade is
            // uncertain: the workflow is not deadlocked.
            string dialogue = router.Handle(
                "{\"op\":\"advance_dialogue\",\"action_id\":\"dlg-2\",\"rev\":42}");
            Assert.Contains(dialogue, "\"ok\":true,", "dialogue not deadlocked");
        }

        // ---------------------------------------- R5/A3: per-trade completion evidence

        /// <summary>
        /// R5-TRADE-01: a whole-visit completion is attribution context, not proof
        /// that this specific trade happened. Business complete with zero per-trade
        /// item/cash movement must stay uncertain, never ok:true.
        /// </summary>
        [Test]
        public static void R5_TRADE_01_Visit_Complete_Without_Per_Trade_Evidence_Is_Uncertain()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = true;   // the whole visit finished...
            port.Evidence.ItemsTransferred = false;  // ...but this trade moved nothing
            port.Evidence.CashAfter = port.Evidence.CashBefore;

            JObject json = Run(new TradeService(port));

            Assert.False((bool)json["ok"], "visit completion alone cannot prove this trade");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain, never completed");
            Assert.Equal("outcome_unverified", (string)json["error"], "stable error code");
            Assert.Equal("no_trade_evidence", (string)json["detail"], "specific detail");
            Assert.True((bool)json["business_complete"],
                "the finished visit is still reported as attribution context");
            Assert.True((bool)json["visit_complete"], "visit_complete is the same attribution fact");
            Assert.False((bool)json["items_transferred"], "no item moved");
            Assert.False((bool)json["cash_moved"], "no cash moved");
            Assert.True((bool)json["client_unchanged"], "the same client is still there");
            Assert.True((bool)json["unknown"], "the outcome is explicitly unknown");
            Assert.Equal(1, port.AcceptCalls, "the native click was dispatched exactly once");
        }

        /// <summary>
        /// R5-TRADE-02: the same client can buy several items in one visit. Each
        /// partial sale is completed on its own per-trade evidence even though the
        /// visit stays open after every one of them.
        /// </summary>
        [Test]
        public static void R5_TRADE_02_Repeated_Partial_Sales_To_The_Same_Client_Each_Complete()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = true;
            port.Evidence.BusinessComplete = false;   // the visit continues after each sale
            port.Evidence.ItemsTransferred = true;    // each sale moves its own item
            ActionRouter router = Router(port);

            string first = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"r5-sale-a\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"sell\"}");
            JObject firstJson = JObject.Parse(first);
            Assert.True((bool)firstJson["ok"], "the first partial sale completed: " + first);
            Assert.Equal("completed", (string)firstJson["execution_status"], "completed");
            Assert.False((bool)firstJson["business_complete"], "the visit is still open");
            Assert.False((bool)firstJson["visit_complete"], "the visit is still open");
            Assert.True((bool)firstJson["items_transferred"], "the item moved");

            port.Guard.StagedItemId = 57;
            port.Evidence.StagedItemIdAfter = 58;
            string second = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"r5-sale-b\","
                + "\"rev\":42,\"item_id\":57,\"side\":\"sell\"}");
            JObject secondJson = JObject.Parse(second);
            Assert.True((bool)secondJson["ok"], "the second partial sale completed too: " + second);
            Assert.Equal("completed", (string)secondJson["execution_status"], "completed");
            Assert.False((bool)secondJson["business_complete"], "the visit is still open");
            Assert.False((bool)secondJson["visit_complete"], "the visit is still open");
            Assert.Equal(2, port.AcceptCalls, "each sale dispatched exactly one native click");
        }

        /// <summary>
        /// R5-TRADE-03: buying from the client keeps the staged pointer on the bought
        /// item, so the cash delta is the per-trade evidence for the buy direction.
        /// </summary>
        [Test]
        public static void R5_TRADE_03_Buy_Is_Proven_By_Cash_When_The_Staged_Pointer_Stays()
        {
            var port = new FakeTradePort();
            port.Guard.StagedItemOwned = false;       // buying the client's item
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = false;   // the bought item stays staged
            port.Evidence.StagedItemIdAfter = 56;     // staged pointer unchanged
            port.Evidence.CashBefore = 700;
            port.Evidence.CashAfter = 675;

            JObject json = Run(new TradeService(port), "buy");

            Assert.True((bool)json["ok"], "the cash delta proves the purchase");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.Equal("buy", (string)json["side"], "buy direction");
            Assert.True((bool)json["cash_moved"], "cash moved");
            Assert.False((bool)json["items_transferred"], "the staged pointer did not move");
            Assert.True((bool)json["client_unchanged"], "same client instance");
            Assert.Equal(56, (int)json["evidence"]["staged_item_id_after"],
                "the staged pointer still names the bought item");
        }

        /// <summary>
        /// R5-TRADE-04: a barter moves items with zero cash movement. Items carry the
        /// evidence; cash is not required.
        /// </summary>
        [Test]
        public static void R5_TRADE_04_Barter_Completes_On_Items_With_Zero_Cash()
        {
            var port = new FakeTradePort();
            port.Guard.ClientIntent = "BARTER";
            port.Guard.StagedItemOwned = false;
            port.Guard.BarterOfferPresent = true;
            port.Guard.BarterAcceptable = true;
            port.Evidence.BusinessComplete = false;
            port.Evidence.ItemsTransferred = true;
            port.Evidence.CashBefore = 600;
            port.Evidence.CashAfter = 600;

            JObject json = Run(new TradeService(port));

            Assert.True((bool)json["ok"], "the bartered items prove the trade");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.False((bool)json["cash_moved"], "no cash moved");
            Assert.False((bool)json["cash_changed"], "cash_changed agrees with cash_moved");
            Assert.True((bool)json["items_transferred"], "the items moved");
            Assert.True((bool)json["client_unchanged"], "same client instance");
            Assert.Equal(1, port.AcceptCalls, "one native click");
        }

        /// <summary>
        /// R5-TRADE-05: a different client cannot back this trade, even when the visit
        /// completion and item movement were observed.
        /// </summary>
        [Test]
        public static void R5_TRADE_05_A_Different_Client_Cannot_Back_The_Trade()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = true;
            port.Evidence.ItemsTransferred = true;
            port.Evidence.ClientUnchanged = false; // a different client is at the counter

            JObject json = Run(new TradeService(port));

            Assert.False((bool)json["ok"], "another client's completion cannot back this trade");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("client_changed", (string)json["error"], "stable error code");
            Assert.True((bool)json["dispatched"], "the click ran, so the outcome is unverified");
            Assert.Equal(1, port.AcceptCalls, "one native click");
        }

        /// <summary>
        /// R5-TRADE-06: the client's player-visible budget cannot cover the
        /// player-visible negotiation price. The native SellItemFromTable path then
        /// ignores the click silently; the guard refuses before dispatch instead.
        /// The check must not gate the buy direction, where the player pays.
        /// </summary>
        [Test]
        public static void R5_TRADE_06_Insufficient_Client_Budget_Refuses_Before_The_Accept()
        {
            JObject json = On(g =>
            {
                g.StagedItemOwned = true;   // the player sells to the client
                g.ClientUsesBudget = true;
                g.ClientBudget = 100;
                g.StagedItemValue = 250;    // GetNegociatedValue() > GetBudget()
            }, "sell");

            Assert.Equal("client_budget_insufficient", (string)json["error"], "stable error code");
            Assert.False((bool)json["dispatched"], "no native accept was dispatched");

            // Exactly enough budget is still affordable: the native gate is "value > budget".
            var affordablePort = new FakeTradePort();
            affordablePort.Guard.StagedItemOwned = true;
            affordablePort.Guard.ClientUsesBudget = true;
            affordablePort.Guard.ClientBudget = 250;
            affordablePort.Guard.StagedItemValue = 250;
            affordablePort.Evidence.BusinessComplete = false;
            affordablePort.Evidence.ItemsTransferred = true;
            JObject affordable = Run(new TradeService(affordablePort), "sell");
            Assert.True((bool)affordable["ok"], "value == budget is affordable: " + affordable);
            Assert.Equal(1, affordablePort.AcceptCalls, "the affordable sale reached the click");

            // A purchase is paid by the player; the client's budget must not gate it.
            var buyPort = new FakeTradePort();
            buyPort.Guard.ClientUsesBudget = true;
            buyPort.Guard.ClientBudget = 0;
            buyPort.Guard.StagedItemValue = 250;
            JObject bought = Run(new TradeService(buyPort), "buy");
            Assert.True((bool)bought["ok"], "a purchase is not client-budget gated: " + bought);
            Assert.Equal(1, buyPort.AcceptCalls, "the purchase reached the click");
        }

        /// <summary>
        /// R5-TRADE-07: an uncertain trade with the same item and arguments must not
        /// be blind-retried under a new action id. The pending fingerprint blocks a
        /// second native click; the caller has to read state instead.
        /// </summary>
        [Test]
        public static void R5_TRADE_07_Uncertain_Trade_Cannot_Be_Blind_Retried_With_A_New_Id()
        {
            var port = new FakeTradePort();
            port.Evidence.BusinessComplete = true;
            port.Evidence.ItemsTransferred = false;
            port.Evidence.CashAfter = port.Evidence.CashBefore;
            ActionRouter router = Router(port);

            string first = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"r5-blind-1\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"buy\"}");
            JObject firstJson = JObject.Parse(first);
            Assert.False((bool)firstJson["ok"], "the first trade is uncertain: " + first);
            Assert.Equal("uncertain", (string)firstJson["execution_status"], "uncertain");

            string retry = router.Handle(
                "{\"op\":\"confirm_trade\",\"action_id\":\"r5-blind-2\","
                + "\"rev\":42,\"item_id\":56,\"side\":\"buy\"}");
            Assert.Contains(retry, "operation_pending",
                "same item+args under a new id is a blind retry: " + retry);
            Assert.Equal(1, port.AcceptCalls, "the retry never reached the native click");
        }
    }
}
