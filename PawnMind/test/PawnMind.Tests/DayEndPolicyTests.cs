using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// The dry recording skipped the landlord by sleeping while end_day was
    /// refused. These tests pin the rule that closes that hole.
    /// </summary>
    internal static class DayEndPolicyTests
    {
        [Test]
        public static void Important_Client_Blocks_EndDay_And_Sleep()
        {
            Equal(DayEndErrors.CannotEndDay,
                DayEndPolicy.EvaluateEndDay(true, false, 0, false), "end_day blocked");
            Equal(DayEndErrors.CannotEndDay,
                DayEndPolicy.EvaluateSleep(true, false, 0, true, "OPEN", false), "sleep blocked too");
        }

        [Test]
        public static void Waiting_Customers_Need_An_Explicit_Dismiss()
        {
            Equal(DayEndErrors.CustomersWaiting,
                DayEndPolicy.EvaluateEndDay(true, true, 2, false), "queue blocks by default");
            Equal(null,
                DayEndPolicy.EvaluateEndDay(true, true, 2, true), "explicit dismiss allows");
        }

        [Test]
        public static void Sleep_Requires_A_Closed_Shop()
        {
            Equal(DayEndErrors.NotClosed,
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "OPEN", false), "cannot sleep while open");
            Equal(null,
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "CLOSED", false), "closed is fine");
        }

        [Test]
        public static void Sleeping_Outside_Loses_The_Afterhour_Bag()
        {
            Equal(DayEndErrors.NotInStore,
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "CLOSED", true),
                "must walk back inside first");
            Equal(null,
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "CLOSED", false),
                "inside is fine");
        }

        [Test]
        public static void Only_Open_Shop_Can_End_The_Day()
        {
            True(DayEndPolicy.IsOpenState("OPEN"), "open");
            True(DayEndPolicy.IsOpenState("open"), "case tolerant");
            False(DayEndPolicy.IsOpenState("MORNING"), "morning is not open");
            False(DayEndPolicy.IsOpenState("CLOSED"), "closed is not open");
            False(DayEndPolicy.IsOpenState(null), "unknown is not open");
        }

        [Test]
        public static void Unknown_Shop_State_Is_Not_Closed()
        {
            False(DayEndPolicy.IsClosed(null), "null is not closed");
            False(DayEndPolicy.IsClosed(""), "empty is not closed");
            False(DayEndPolicy.IsClosed("MORNING"), "morning is not closed");
            True(DayEndPolicy.IsClosed("closed"), "case tolerant");
        }

        [Test]
        public static void Unreadable_State_Never_Advances()
        {
            Equal(DayEndErrors.ReadbackUnavailable,
                DayEndPolicy.EvaluateEndDay(false, true, 0, true), "unreadable end_day");
            Equal(DayEndErrors.ReadbackUnavailable,
                DayEndPolicy.EvaluateSleep(false, true, 0, true, "CLOSED", false), "unreadable sleep");
        }

        private static ShopDaySnapshot Snapshot(string shopState, bool inGame = true, bool readable = true)
        {
            return new ShopDaySnapshot
            {
                Readable = readable,
                InGame = inGame,
                ShopState = shopState,
            };
        }

        /// <summary>
        /// F2: the async verifier used to treat "not OPEN" as finished, so MORNING,
        /// an unknown state or an out-of-game readback all marked end_day completed.
        /// Only a real CLOSED readback may prove the shutter request finished.
        /// </summary>
        [Test]
        public static void Readback_Needs_A_Real_Closed_State()
        {
            True(DayEndPolicy.ReadbackProvesEndDay(Snapshot("CLOSED")), "CLOSED proves completion");
            True(DayEndPolicy.ReadbackProvesEndDay(Snapshot("closed")), "case tolerant");

            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("OPEN")), "OPEN means still animating");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("CLOSING")), "CLOSING is not proof");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("MORNING")), "next morning is not this end_day");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot(null)), "unknown state is not proof");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("")), "empty state is not proof");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("CLOSED", inGame: false)), "out of game");
            False(DayEndPolicy.ReadbackProvesEndDay(Snapshot("CLOSED", readable: false)), "unreadable");
            False(DayEndPolicy.ReadbackProvesEndDay(null), "no readback");
        }
    }
}
