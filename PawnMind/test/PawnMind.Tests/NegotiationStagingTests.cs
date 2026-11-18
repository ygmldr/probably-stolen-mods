using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5-B1: first-class player-equivalent negotiation staging.
    ///
    /// Evidence level: L1-fake. The fake staging port counts native staging
    /// calls, so "rejected before dispatch" is a counted fact, and the
    /// before/after readback contract is exercised without Unity. The real
    /// native drag path is NOT_RUN (L3, no Windows host in this round).
    /// </summary>
    internal static class NegotiationStagingTests
    {
        // ------------------------------------------------------------ fakes

        private sealed class FakeStagingPort : INegotiationStagingPort
        {
            public StagingGuardInput Guard = PassingGuard();
            public StagingEvidence Evidence = CompletedEvidence();
            public bool ThrowOnRead;
            public bool ThrowOnStage;
            public int ReadCalls;
            public int StageCalls;
            public readonly List<int> StagedItemIds = new List<int>();

            public StagingGuardInput Read(int itemId)
            {
                ReadCalls++;
                if (ThrowOnRead) throw new InvalidOperationException("read boom");
                Guard.RequestedItemId = itemId;
                return Guard;
            }

            public StagingEvidence Stage(int itemId)
            {
                StageCalls++;
                StagedItemIds.Add(itemId);
                if (ThrowOnStage) throw new InvalidOperationException("stage boom");
                return Evidence;
            }
        }

        /// <summary>
        /// Stateful fake that behaves like the native weighted-counter pointer:
        /// staging item X makes X the current staged item (0 = none). Used for
        /// A -> B -> A switching and several trades with one client.
        /// </summary>
        private sealed class StatefulStagingPort : INegotiationStagingPort
        {
            public int Staged;
            public int ClientHandle = 77;
            public bool NegotiationOpen = true;
            public int StageCalls;
            public readonly List<string> Transitions = new List<string>();

            public StagingGuardInput Read(int itemId)
            {
                return new StagingGuardInput
                {
                    InGame = true,
                    ClientPresent = true,
                    ClientReadable = true,
                    ClientIntent = "BUY",
                    NegotiationReadable = true,
                    NegotiationOpen = NegotiationOpen,
                    ModalOpen = false,
                    RequestedItemId = itemId,
                    ItemExists = true,
                    ItemReadable = true,
                    ItemContainer = Staged == itemId ? "back" : "main",
                    ItemOwned = true,
                    OnWeightedCounter = Staged == itemId,
                    BackCounterReadable = true,
                    BackCounterMayAccept = true,
                    StagedReadable = true,
                    StagedItemId = Staged
                };
            }

            public StagingEvidence Stage(int itemId)
            {
                StageCalls++;
                int before = Staged;
                Transitions.Add(before + "->" + itemId);
                Staged = itemId;
                NegotiationOpen = true;
                return new StagingEvidence
                {
                    Called = true,
                    StagedItemIdBefore = before,
                    StagedItemIdAfter = itemId,
                    AfterReadable = true,
                    NegotiationOpenAfter = NegotiationOpen,
                    NegotiationReadableAfter = true,
                    ClientHandleBefore = ClientHandle,
                    ClientHandleAfter = ClientHandle,
                    ClientReadableAfter = true,
                    ItemOnCounterAfter = true,
                    NativePrimitive = StagingPrimitives.WeightedAreaAdd
                };
            }
        }

        private static StagingGuardInput PassingGuard()
        {
            return new StagingGuardInput
            {
                InGame = true,
                ClientPresent = true,
                ClientReadable = true,
                ClientIntent = "BUY",
                NegotiationReadable = true,
                NegotiationOpen = true,
                ModalOpen = false,
                RequestedItemId = 56,
                ItemExists = true,
                ItemReadable = true,
                ItemContainer = "main",
                ItemOwned = true,
                OnWeightedCounter = false,
                BackCounterReadable = true,
                BackCounterMayAccept = true,
                StagedReadable = true,
                StagedItemId = 0
            };
        }

        private static StagingEvidence CompletedEvidence()
        {
            return new StagingEvidence
            {
                Called = true,
                StagedItemIdBefore = 0,
                StagedItemIdAfter = 56,
                AfterReadable = true,
                NegotiationOpenAfter = true,
                NegotiationReadableAfter = true,
                ClientHandleBefore = 77,
                ClientHandleAfter = 77,
                ClientReadableAfter = true,
                ItemOnCounterAfter = true,
                NativePrimitive = StagingPrimitives.WeightedAreaAdd
            };
        }

        private static JObject Run(NegotiationStagingService service, int itemId = 56)
        {
            return JObject.Parse(service.Stage(itemId, 42).ToJson(42));
        }

        private static JObject RunWith(StagingEvidence evidence, Action<StagingGuardInput> mutate = null)
        {
            var port = new FakeStagingPort { Evidence = evidence };
            if (mutate != null) mutate(port.Guard);
            return Run(new NegotiationStagingService(port));
        }

        private static JObject Refused(Action<StagingGuardInput> mutate)
        {
            var port = new FakeStagingPort();
            mutate(port.Guard);
            JObject json = Run(new NegotiationStagingService(port));
            Assert.False((bool)json["ok"], "a refused staging attempt is not ok");
            Assert.Equal(0, port.StageCalls, "a refused staging attempt dispatches zero native calls");
            Assert.Equal("rejected", (string)json["execution_status"], "refused staging is rejected");
            Assert.Equal(false, (bool)json["dispatched"], "refused staging did not dispatch");
            Assert.Equal(JTokenType.Null, json["client_identity_unchanged"].Type,
                "no post-dispatch client identity exists on a refusal");
            return json;
        }

        // ------------------------------------------------------ Core rules

        [Test]
        public static void R5_STAGE_01_Passing_Input_Has_No_Refusal()
        {
            Assert.Null(StagingGuardPolicy.Evaluate(PassingGuard()), "a fully read guard allows");
        }

        [Test]
        public static void R5_STAGE_01b_Null_Input_Is_A_Read_Failure()
        {
            Assert.Equal(StagingErrors.ReadFailed, StagingGuardPolicy.Evaluate(null),
                "a null guard snapshot fails closed");
        }

        [Test]
        public static void R5_STAGE_02_Not_In_Game_Refuses()
        {
            // At the main menu there is no PlayerStore, so nothing about the
            // counter (not even the last staged id) is readable.
            JObject json = Refused(g => { g.InGame = false; g.StagedReadable = false; });
            Assert.Equal(StagingErrors.NotInGame, (string)json["error"], "not_in_game");
            Assert.Equal(JTokenType.Null, json["staged_item_id_before"].Type,
                "an unreadable pre-state is null, not 0");
        }

        [Test]
        public static void R5_STAGE_03_No_Client_Refuses()
        {
            JObject json = Refused(g => g.ClientPresent = false);
            Assert.Equal(StagingErrors.NoClient, (string)json["error"], "no_client");
        }

        [Test]
        public static void R5_STAGE_04_Unreadable_Client_Refuses()
        {
            JObject json = Refused(g => g.ClientReadable = false);
            Assert.Equal(StagingErrors.ClientUnreadable, (string)json["error"], "client_unreadable");
        }

        [Test]
        public static void R5_STAGE_05_Unreadable_Staged_Item_Refuses()
        {
            JObject json = Refused(g => g.StagedReadable = false);
            Assert.Equal(StagingErrors.ReadFailed, (string)json["error"], "staging_read_failed");
            Assert.Equal(JTokenType.Null, json["staged_item_id_before"].Type,
                "the unreadable staged id is reported as null");
        }

        [Test]
        public static void R5_STAGE_06_Unreadable_Negotiation_Ui_Refuses()
        {
            JObject json = Refused(g => g.NegotiationReadable = false);
            Assert.Equal(StagingErrors.NegotiationUnreadable, (string)json["error"],
                "negotiation_unreadable");
            Assert.Contains((string)json["detail"], "negotiation", "the detail names the field");
        }

        [Test]
        public static void R5_STAGE_07_Modal_Occlusion_Refuses()
        {
            JObject json = Refused(g => g.ModalOpen = true);
            Assert.Equal(StagingErrors.ModalOpen, (string)json["error"], "modal_open");
        }

        [Test]
        public static void R5_STAGE_08_Missing_Or_Unknown_Item_Refuses()
        {
            JObject missing = Refused(g => g.ItemExists = false);
            Assert.Equal(StagingErrors.ItemNotFound, (string)missing["error"], "item_not_found");

            var zeroPort = new FakeStagingPort();
            JObject badId = JObject.Parse(
                new NegotiationStagingService(zeroPort).Stage(0, 42).ToJson(42));
            Assert.Equal(StagingErrors.ItemNotFound, (string)badId["error"],
                "item_id 0 is not found");
            Assert.Equal(0, zeroPort.StageCalls, "item_id 0 never dispatches");
        }

        [Test]
        public static void R5_STAGE_09_Sold_Item_Refuses()
        {
            JObject json = Refused(g => g.ItemContainer = "sold");
            Assert.Equal(StagingErrors.ItemSold, (string)json["error"], "item_sold");
        }

        [Test]
        public static void R5_STAGE_10_Unreachable_Container_Refuses()
        {
            foreach (string container in new[] { "machine", "afterhour", "vending", "fountain", "hireling", "" })
            {
                JObject json = Refused(g => g.ItemContainer = container);
                Assert.Equal(StagingErrors.ItemUnreachable, (string)json["error"],
                    "container '" + container + "' is not reachable from the shop counter");
            }
        }

        [Test]
        public static void R5_STAGE_11_Unreadable_Item_Refuses()
        {
            JObject json = Refused(g => g.ItemReadable = false);
            Assert.Equal(StagingErrors.ItemUnreadable, (string)json["error"], "item_unreadable");
        }

        [Test]
        public static void R5_STAGE_12_Buy_Direction_Needs_An_Owned_Item()
        {
            JObject json = Refused(g => { g.ClientIntent = "BUY"; g.ItemOwned = false; });
            Assert.Equal(StagingErrors.NotOwned, (string)json["error"], "not_owned");
        }

        [Test]
        public static void R5_STAGE_13_Sell_Direction_Rejects_An_Owned_Item()
        {
            JObject json = Refused(g => { g.ClientIntent = "SELL"; g.ItemOwned = true; });
            Assert.Equal(StagingErrors.AlreadyOwned, (string)json["error"], "already_owned");
        }

        [Test]
        public static void R5_STAGE_14_SellNBUY_Allows_Either_Ownership()
        {
            Assert.Null(StagingGuardPolicy.Evaluate(new StagingGuardInput
            {
                InGame = true, ClientPresent = true, ClientReadable = true,
                ClientIntent = "SELLNBUY", NegotiationReadable = true,
                RequestedItemId = 56, ItemExists = true, ItemReadable = true,
                ItemContainer = "main", ItemOwned = false, BackCounterReadable = true,
                BackCounterMayAccept = true, StagedReadable = true
            }), "SELLNBUY can stage the client's own item for a purchase");
        }

        [Test]
        public static void R5_STAGE_15_Undefined_Intent_Refuses()
        {
            JObject json = Refused(g => g.ClientIntent = "UNDEFINED");
            Assert.Equal(StagingErrors.IntentUndefined, (string)json["error"], "intent_undefined");
        }

        [Test]
        public static void R5_STAGE_16_Unreadable_Back_Counter_Refuses()
        {
            JObject json = Refused(g => g.BackCounterReadable = false);
            Assert.Equal(StagingErrors.BackCounterUnreadable, (string)json["error"],
                "back_counter_unreadable");
        }

        [Test]
        public static void R5_STAGE_17_Full_Or_Rejecting_Back_Counter_Refuses()
        {
            JObject json = Refused(g => g.BackCounterMayAccept = false);
            Assert.Equal(StagingErrors.BackCounterFull, (string)json["error"],
                "back_counter_full when the native accept predicate says no");
        }

        [Test]
        public static void R5_STAGE_18_Already_On_Counter_Skips_The_Accept_Precheck()
        {
            JObject json = RunWith(CompletedEvidence(), g =>
            {
                g.ItemContainer = "back";
                g.OnWeightedCounter = true;
                g.BackCounterMayAccept = false; // already there: the accept path is not used
                g.StagedItemId = 56;
            });
            Assert.True((bool)json["ok"], "restaging an item already on the counter is allowed");
        }

        [Test]
        public static void R5_STAGE_19_Call_Scoped_Pending_Fingerprint_Includes_Staging()
        {
            Assert.True(PendingScope.IsCallScoped("confirm_trade"), "existing call-scoped op");
            Assert.True(PendingScope.IsCallScoped(StagingOps.Name),
                "staging must be fingerprinted per item like confirm_trade");
            Assert.False(PendingScope.IsCallScoped("end_day"), "op-name-scoped ops stay coarse");
            Assert.Equal("stage_negotiation_item", StagingOps.Name, "the public op name is stable");
        }

        // ------------------------------------------------ service: readback

        [Test]
        public static void R5_STAGE_20_Completed_Requires_The_Readback()
        {
            var port = new FakeStagingPort();
            JObject json = Run(new NegotiationStagingService(port));

            Assert.True((bool)json["ok"], "matching readback completes");
            Assert.Equal("stage_negotiation_item", (string)json["op"], "op");
            Assert.Equal("completed", (string)json["execution_status"], "completed");
            Assert.Equal(true, (bool)json["dispatched"], "dispatched");
            Assert.Equal(56, (int)json["item_id"], "requested item");
            Assert.Equal(0, (int)json["staged_item_id_before"], "before");
            Assert.Equal(56, (int)json["staged_item_id_after"], "after");
            Assert.Equal(true, (bool)json["negotiation_open"], "negotiation open");
            Assert.Equal(true, (bool)json["client_identity_unchanged"], "same client");
            Assert.Equal(42L, (long)json["rev"], "revision echoed");
            Assert.Equal(1, port.StageCalls, "exactly one native staging call");
            Assert.Equal(1, port.ReadCalls, "one pre-read");
        }

        [Test]
        public static void R5_STAGE_21_A_To_B_To_A_Switching_Completes()
        {
            var port = new StatefulStagingPort();
            var service = new NegotiationStagingService(port);

            JObject first = Run(service, 56);
            Assert.True((bool)first["ok"], "A staged");
            Assert.Equal(56, (int)first["staged_item_id_after"], "A");

            JObject second = Run(service, 57);
            Assert.True((bool)second["ok"], "B staged");
            Assert.Equal(56, (int)second["staged_item_id_before"], "B sees A staged before");
            Assert.Equal(57, (int)second["staged_item_id_after"], "B");

            JObject back = Run(service, 56);
            Assert.True((bool)back["ok"], "A staged again");
            Assert.Equal(57, (int)back["staged_item_id_before"], "A switch sees B before");
            Assert.Equal(56, (int)back["staged_item_id_after"], "A after switching back");

            Assert.Equal("0->56,56->57,57->56", string.Join(",", port.Transitions.ToArray()),
                "the native pointer really transitions A->B->A");
            Assert.Equal(3, port.StageCalls, "one native call per staging");
        }

        [Test]
        public static void R5_STAGE_22_Several_Trades_For_One_Client_Keep_Staging()
        {
            var port = new StatefulStagingPort();
            var service = new NegotiationStagingService(port);

            for (int i = 0; i < 3; i++)
            {
                JObject json = Run(service, 100 + i);
                Assert.True((bool)json["ok"], "trade " + i + " staging completes");
                Assert.Equal(true, (bool)json["client_identity_unchanged"],
                    "the client identity stays the same across trades");
            }

            Assert.Equal(102, (int)Run(service, 100)["staged_item_id_before"],
                "the previous trade left its item staged");
            Assert.Equal(4, port.StageCalls, "one native call per trade");
        }

        [Test]
        public static void R5_STAGE_23_Readback_Mismatch_Is_Uncertain_Never_Success()
        {
            StagingEvidence evidence = CompletedEvidence();
            evidence.StagedItemIdAfter = 999;
            JObject json = RunWith(evidence);

            Assert.False((bool)json["ok"], "a mismatched readback is not ok");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal(true, (bool)json["uncertain"], "the uncertain marker is explicit");
            Assert.Equal(true, (bool)json["dispatched"], "the native call may have run");
            Assert.Equal("outcome_unverified", (string)json["error"], "stable error");
            Assert.Equal("staged_item_mismatch", (string)json["detail"], "precise detail");
            Assert.Equal(0, (int)json["staged_item_id_before"], "before is still truthful");
            Assert.Equal(999, (int)json["staged_item_id_after"], "after is the observed value");
            Assert.Contains((string)json["next"], "do not retry", "the next step forbids a blind retry");
        }

        [Test]
        public static void R5_STAGE_24_Silent_Native_Return_Is_Uncertain()
        {
            StagingEvidence evidence = CompletedEvidence();
            evidence.Called = false;
            JObject json = RunWith(evidence);

            Assert.False((bool)json["ok"], "an uncalled native path is not success");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("outcome_unverified", (string)json["error"], "stable error");
            Assert.Equal("native_not_called", (string)json["detail"], "native_not_called");
        }

        [Test]
        public static void R5_STAGE_25_Native_Exception_Is_Uncertain()
        {
            var port = new FakeStagingPort { ThrowOnStage = true };
            JObject json = Run(new NegotiationStagingService(port));

            Assert.False((bool)json["ok"], "a native exception is not success");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("outcome_unverified", (string)json["error"], "stable error");
            Assert.Equal("native_error", (string)json["detail"], "native_error");
        }

        [Test]
        public static void R5_STAGE_26_Negotiation_Not_Open_After_Is_Uncertain()
        {
            StagingEvidence evidence = CompletedEvidence();
            evidence.NegotiationOpenAfter = false;
            JObject json = RunWith(evidence);

            Assert.False((bool)json["ok"], "staged but no negotiation UI is not a usable stage");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("negotiation_not_open", (string)json["detail"],
                "the UI state mismatch is named");
            Assert.Equal(false, (bool)json["negotiation_open"], "the observed UI state is reported");
        }

        [Test]
        public static void R5_STAGE_27_Client_Changed_Is_Uncertain()
        {
            StagingEvidence evidence = CompletedEvidence();
            evidence.ClientHandleAfter = 88;
            JObject json = RunWith(evidence);

            Assert.False((bool)json["ok"], "another client's counter state must not back this stage");
            Assert.Equal("client_changed", (string)json["error"], "stable error");
            Assert.Equal(false, (bool)json["client_identity_unchanged"], "identity changed");
        }

        [Test]
        public static void R5_STAGE_28_Unreadable_Post_Readback_Is_Uncertain()
        {
            StagingEvidence unreadable = CompletedEvidence();
            unreadable.AfterReadable = false;
            JObject json = RunWith(unreadable);
            Assert.False((bool)json["ok"], "an unreadable post-state is not success");
            Assert.Equal("uncertain", (string)json["execution_status"], "uncertain");
            Assert.Equal("readback_unreadable", (string)json["detail"], "readback_unreadable");
            Assert.Equal(JTokenType.Null, json["staged_item_id_after"].Type,
                "an unreadable after value is null, never a guess");
            Assert.Equal(JTokenType.Null, json["client_identity_unchanged"].Type,
                "an unreadable post client is null");

            StagingEvidence clientGone = CompletedEvidence();
            clientGone.ClientReadableAfter = false;
            JObject gone = RunWith(clientGone);
            Assert.Equal("client_identity_unreadable", (string)gone["detail"],
                "client identity unreadable is distinguished from changed");
        }

        [Test]
        public static void R5_STAGE_29_Item_Not_On_Counter_After_Is_Uncertain()
        {
            StagingEvidence evidence = CompletedEvidence();
            evidence.ItemOnCounterAfter = false;
            JObject json = RunWith(evidence);

            Assert.False((bool)json["ok"], "a pointer without the physical item is not staged");
            Assert.Equal("item_not_on_counter", (string)json["detail"],
                "the item/pointer mismatch is named");
        }

        [Test]
        public static void R5_STAGE_30_Read_Exception_Is_Rejected_Without_Dispatch()
        {
            var port = new FakeStagingPort { ThrowOnRead = true };
            JObject json = Run(new NegotiationStagingService(port));

            Assert.False((bool)json["ok"], "an unreadable pre-state cannot dispatch");
            Assert.Equal("staging_read_failed", (string)json["error"], "stable error");
            Assert.Equal(0, port.StageCalls, "zero native calls");
        }

        [Test]
        public static void R5_STAGE_31_Refusal_Reports_Before_And_Null_After()
        {
            var port = new FakeStagingPort();
            port.Guard.ModalOpen = true;
            port.Guard.StagedItemId = 41;
            JObject json = Run(new NegotiationStagingService(port));

            Assert.Equal(41, (int)json["staged_item_id_before"], "the observed before is reported");
            Assert.Equal(JTokenType.Null, json["staged_item_id_after"].Type,
                "no after state was observed on a refusal");
            Assert.Equal(true, (bool)json["negotiation_open"], "the pre-read UI state is reported");
        }

        [Test]
        public static void R5_STAGE_36_Sell_Direction_Stages_The_Clients_Item()
        {
            // Client intent SELL: the customer sells to the player. The item must
            // be the client's (not owned) and the staging still completes with the
            // same readback contract.
            var port = new FakeStagingPort();
            port.Guard.ClientIntent = "SELL";
            port.Guard.ItemOwned = false;
            port.Guard.ItemContainer = "main";

            JObject json = Run(new NegotiationStagingService(port));

            Assert.True((bool)json["ok"], "the sell direction stages");
            Assert.Equal("SELL", (string)json["client_intent"], "intent is reported");
            Assert.Equal(56, (int)json["staged_item_id_after"], "the client's item is staged");
            Assert.Equal(1, port.StageCalls, "one native call");
        }

        [Test]
        public static void R5_STAGE_37_Restaging_The_Staged_Item_Reports_Same_Before_And_After()
        {
            var port = new FakeStagingPort();
            port.Guard.ItemContainer = "back";
            port.Guard.OnWeightedCounter = true;
            port.Guard.StagedItemId = 56;
            port.Evidence.StagedItemIdBefore = 56;
            port.Evidence.StagedItemIdAfter = 56;
            port.Evidence.NativePrimitive = StagingPrimitives.WeightedAreaRemoveAdd;

            JObject json = Run(new NegotiationStagingService(port));

            Assert.True((bool)json["ok"], "restaging the already-staged item completes");
            Assert.Equal(56, (int)json["staged_item_id_before"], "before is truthful");
            Assert.Equal(56, (int)json["staged_item_id_after"], "after is truthful");
            Assert.Equal("weighted_area_remove_add", (string)json["native_primitive"],
                "the remove+add primitive is named in the evidence");
            Assert.Equal(1, port.StageCalls, "one native remove+add");
        }

        // ------------------------------------------------ router protocol

        private const string Token = "0123456789abcdef0123456789abcdef";
        private const string GameVersion = "VERSION 1-Rev1";
        private const string GameMvid = "11111111-2222-3333-4444-555555555555";

        private static WriteGateDecision OpenGate()
        {
            return GameVersionGate.Evaluate(
                new GameIdentity(GameVersion, GameMvid), GameVersion, GameMvid);
        }

        private static ActionRouter Router(long revision, Func<string, JObject, ActionResult> dispatch)
        {
            return new ActionRouter(
                new ActionGate(),
                delegate { return revision; },
                dispatch,
                new ActionHistory(),
                delegate { return revision; },
                Token,
                delegate { return OpenGate(); });
        }

        private static string Write(int itemId, string actionId, long rev)
        {
            return "{\"op\":\"stage_negotiation_item\",\"item_id\":" + itemId
                + ",\"action_id\":\"" + actionId + "\",\"rev\":" + rev + "}";
        }

        private static ActionResult CompletedResult(int itemId)
        {
            var port = new FakeStagingPort { Evidence = CompletedEvidence() };
            port.Evidence.StagedItemIdAfter = itemId;
            return new NegotiationStagingService(port).Stage(itemId, 42);
        }

        [Test]
        public static void R5_STAGE_32_Router_Requires_Action_Id_And_Revision()
        {
            int calls = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                calls++;
                return CompletedResult(56);
            });

            string missingId = router.Handle(
                "{\"op\":\"stage_negotiation_item\",\"item_id\":56,\"rev\":3}");
            Assert.Contains(missingId, "missing_action_id", "action id is required");

            string missingRev = router.Handle(
                "{\"op\":\"stage_negotiation_item\",\"item_id\":56,\"action_id\":\"a1\"}");
            Assert.Contains(missingRev, "missing_rev", "rev is required");

            string stale = router.Handle(Write(56, "a2", 2));
            Assert.Contains(stale, "stale_rev", "stale revision is refused before dispatch");

            Assert.Equal(0, calls, "no invalid staging request may dispatch");
        }

        [Test]
        public static void R5_STAGE_33_Router_Response_Carries_The_Staging_Contract()
        {
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                return CompletedResult((int)request["item_id"]);
            });

            JObject json = JObject.Parse(router.Handle(Write(56, "stage-contract", 3)));

            Assert.True((bool)json["ok"], "ok");
            Assert.Equal("stage_negotiation_item", (string)json["op"], "op");
            Assert.Equal(3L, (long)json["rev"], "rev");
            Assert.Equal(56, (int)json["item_id"], "item_id");
            Assert.Equal(0, (int)json["staged_item_id_before"], "staged_item_id_before");
            Assert.Equal(56, (int)json["staged_item_id_after"], "staged_item_id_after");
            Assert.Equal(true, (bool)json["negotiation_open"], "negotiation_open");
            Assert.Equal(true, (bool)json["client_identity_unchanged"], "client_identity_unchanged");
            Assert.Equal("completed", (string)json["execution_status"], "execution_status");
            Assert.Equal("dispatched", (string)json["dispatch_status"], "dispatch_status");
            Assert.Equal(true, (bool)json["dispatched"], "dispatched boolean");
        }

        [Test]
        public static void R5_STAGE_34_Duplicate_Action_Id_Replays_Without_Staging_Again()
        {
            int calls = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                calls++;
                return CompletedResult(56);
            });

            string body = Write(56, "stage-once", 3);
            JObject first = JObject.Parse(router.Handle(body));
            JObject second = JObject.Parse(router.Handle(body));

            Assert.True((bool)first["ok"], "first staging completes");
            Assert.True((bool)second["ok"], "the duplicate returns the cached completion");
            Assert.Equal(1, calls, "the native staging call is not repeated for a duplicate id");
        }

        [Test]
        public static void R5_STAGE_35_Uncertain_Same_Item_Retry_Is_Blocked_Different_Item_Allowed()
        {
            int calls = 0;
            ActionRouter router = Router(3, delegate(string op, JObject request)
            {
                calls++;
                if ((int)request["item_id"] == 56)
                {
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":false,\"op\":\"stage_negotiation_item\",\"error\":\"outcome_unverified\","
                        + "\"detail\":\"staged_item_mismatch\",\"dispatched\":true,\"uncertain\":true,"
                        + "\"execution_status\":\"uncertain\",\"item_id\":56,"
                        + "\"staged_item_id_before\":0,\"staged_item_id_after\":0,"
                        + "\"negotiation_open\":true,\"client_identity_unchanged\":true}");
                }

                return CompletedResult((int)request["item_id"]);
            });

            JObject first = JObject.Parse(router.Handle(Write(56, "stage-uncertain", 3)));
            Assert.Equal("uncertain", (string)first["execution_status"], "first attempt is uncertain");

            JObject sameItem = JObject.Parse(router.Handle(Write(56, "stage-retry-new-id", 3)));
            Assert.Equal("operation_pending", (string)sameItem["error"],
                "the same item must not be retried under a new action id");
            Assert.Equal(1, calls, "the blocked retry dispatches nothing");

            JObject otherItem = JObject.Parse(router.Handle(Write(57, "stage-other-item", 3)));
            Assert.True((bool)otherItem["ok"], "a different item is still allowed");
            Assert.Equal(2, calls, "the different item really dispatched");
        }
    }
}
