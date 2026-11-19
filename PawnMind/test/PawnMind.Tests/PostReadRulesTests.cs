using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5.3/B: four player actions used to return success without reading anything
    /// back. "The token did not change" must stay a pending observation, never a
    /// failure, because the natives are asynchronous.
    /// </summary>
    internal static class PostReadRulesTests
    {
        [Test]
        public static void Open_Shop_Completes_Only_When_The_Shutter_Is_Open()
        {
            Assert.Equal(PostReadOutcome.Completed, PostReadRules.Evaluate(
                PostReadRules.OpenShop, "MORNING", "OPEN", true), "morning -> open");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.OpenShop, "MORNING", "MORNING", true), "still animating");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.OpenShop, "OPEN", "OPEN", true), "already open is not a new open");
        }

        [Test]
        public static void Call_Next_Completes_When_A_Client_Appears()
        {
            Assert.Equal(PostReadOutcome.Completed, PostReadRules.Evaluate(
                PostReadRules.CallNextCustomer, "none", "client", true), "the customer walked in");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.CallNextCustomer, "none", "none", true), "not arrived yet");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.CallNextCustomer, "client", "client", true), "someone was already there");
        }

        [Test]
        public static void Dismiss_Completes_When_The_Client_Leaves()
        {
            Assert.Equal(PostReadOutcome.Completed, PostReadRules.Evaluate(
                PostReadRules.DismissCurrentClient, "client", "none", true), "the client left");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.DismissCurrentClient, "client", "client", true), "still standing there");
        }

        [Test]
        public static void Advance_Completes_When_The_Token_Changes()
        {
            Assert.Equal(PostReadOutcome.Completed, PostReadRules.Evaluate(
                PostReadRules.AdvanceDialogue, "dlg:1111", "dlg:2222", true), "next bubble");
            Assert.Equal(PostReadOutcome.Completed, PostReadRules.Evaluate(
                PostReadRules.AdvanceDialogue, "dlg:1111", "none", true), "dialogue ended");
            Assert.Equal(PostReadOutcome.Unchanged, PostReadRules.Evaluate(
                PostReadRules.AdvanceDialogue, "dlg:1111", "dlg:1111", true), "same bubble");
        }

        [Test]
        public static void An_Unreadable_State_Says_Nothing()
        {
            foreach (string op in new[]
            {
                PostReadRules.OpenShop, PostReadRules.CallNextCustomer,
                PostReadRules.DismissCurrentClient, PostReadRules.AdvanceDialogue
            })
            {
                Assert.Equal(PostReadOutcome.Pending,
                    PostReadRules.Evaluate(op, "x", "y", false),
                    "unreadable must never claim a change: " + op);
            }
        }

        [Test]
        public static void Unknown_Ops_Are_Not_Covered()
        {
            Assert.False(PostReadRules.IsCovered("confirm_trade"), "trades have their own evidence");
            Assert.Equal(PostReadOutcome.Pending,
                PostReadRules.Evaluate("confirm_trade", "a", "b", true),
                "an op without a rule must not be judged");
        }
    }
}
