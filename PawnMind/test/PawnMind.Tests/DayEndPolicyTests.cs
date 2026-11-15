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
                DayEndPolicy.EvaluateSleep(true, false, 0, true, "OPEN"), "sleep blocked too");
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
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "OPEN"), "cannot sleep while open");
            Equal(null,
                DayEndPolicy.EvaluateSleep(true, true, 0, false, "CLOSED"), "closed is fine");
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
                DayEndPolicy.EvaluateSleep(false, true, 0, true, "CLOSED"), "unreadable sleep");
        }
    }
}
