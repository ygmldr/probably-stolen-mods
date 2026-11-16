using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    internal static class ShopBellPolicyTests
    {
        [Test]
        public static void Ringing_While_The_Bell_Is_Locked_Is_Refused()
        {
            Equal(ShopBellPolicy.BellBusy,
                ShopBellPolicy.Evaluate(true, true, "OPEN", true),
                "the native bell ignores the click while locked");
        }

        [Test]
        public static void Ringing_Outside_Opening_Hours_Is_Refused()
        {
            Equal(ShopBellPolicy.ShopNotOpen,
                ShopBellPolicy.Evaluate(true, false, "CLOSED", true),
                "RingBell returns unless the shop is OPEN");
            Equal(ShopBellPolicy.ShopNotOpen,
                ShopBellPolicy.Evaluate(true, false, "MORNING", true),
                "morning is not open either");
        }

        [Test]
        public static void Ringing_When_No_Client_Can_Be_Called_Is_Refused()
        {
            Equal(ShopBellPolicy.CannotCallNext,
                ShopBellPolicy.Evaluate(true, false, "OPEN", false),
                "empty queue still reports the native refusal");
        }

        [Test]
        public static void Unreadable_State_Wins_Over_Everything()
        {
            Equal(DayEndErrors.ReadbackUnavailable,
                ShopBellPolicy.Evaluate(false, true, "CLOSED", false),
                "without a snapshot nothing may be dispatched");
        }

        [Test]
        public static void A_Legal_Ring_Has_No_Refusal()
        {
            Equal(null, ShopBellPolicy.Evaluate(true, false, "OPEN", true), "legal");
        }
    }
}
