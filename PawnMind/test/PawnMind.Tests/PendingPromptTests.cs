using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// S8 pure prompt rules: which answers may reach a native confirmation
    /// button, and what a closed readback means. No Unity type is referenced.
    /// </summary>
    internal static class PendingPromptTests
    {
        [Test]
        public static void Choice_Normalizes_Confirm_And_Cancel()
        {
            string choice;
            Assert.True(PromptChoices.TryNormalize("confirm", out choice), "confirm is legal");
            Assert.Equal("confirm", choice, "confirm stays confirm");

            Assert.True(PromptChoices.TryNormalize(" cancel ", out choice), "cancel is legal after trim");
            Assert.Equal("cancel", choice, "cancel stays cancel");
        }

        [Test]
        public static void Choice_Rejects_Unknown_And_Case_Variants()
        {
            string choice;
            Assert.False(PromptChoices.TryNormalize(null, out choice), "null is not a choice");
            Assert.False(PromptChoices.TryNormalize("", out choice), "empty is not a choice");
            Assert.False(PromptChoices.TryNormalize("maybe", out choice), "maybe is not a choice");
            Assert.False(PromptChoices.TryNormalize("CONFIRM", out choice), "choices are lower-case");
            Assert.False(PromptChoices.TryNormalize("confirm now", out choice), "no free-form text");
        }

        [Test]
        public static void Validate_No_Prompt_When_Closed()
        {
            Assert.Equal(
                PromptErrors.NoPrompt,
                PromptPolicy.ValidateAnswer(PromptTestData.Closed(), "confirm"),
                "a closed panel has no answer to dispatch");
        }

        [Test]
        public static void Validate_No_Prompt_When_Unreadable()
        {
            Assert.Equal(
                PromptErrors.NoPrompt,
                PromptPolicy.ValidateAnswer(PromptTestData.Unreadable(), "cancel"),
                "an unreadable panel must never be answered blindly");
            Assert.Equal(
                PromptErrors.NoPrompt,
                PromptPolicy.ValidateAnswer(null, "confirm"),
                "a missing snapshot has no answer to dispatch");
        }

        [Test]
        public static void Validate_Confirm_On_Two_Button_Prompt()
        {
            Assert.Null(
                PromptPolicy.ValidateAnswer(PromptTestData.Open(), "confirm"),
                "confirm is available on a two-button prompt");
        }

        [Test]
        public static void Validate_Cancel_On_Two_Button_Prompt()
        {
            Assert.Null(
                PromptPolicy.ValidateAnswer(PromptTestData.Open(), "cancel"),
                "cancel is available on a two-button prompt");
        }

        [Test]
        public static void Validate_Cancel_Rejected_On_Single_Button_Prompt()
        {
            Assert.Equal(
                PromptErrors.ChoiceUnavailable,
                PromptPolicy.ValidateAnswer(PromptTestData.Open(true), "cancel"),
                "single_button has no native cancel button");

            Assert.Null(
                PromptPolicy.ValidateAnswer(PromptTestData.Open(true), "confirm"),
                "single_button still allows confirm");
        }

        [Test]
        public static void Validate_Rejects_Unknown_Choice()
        {
            Assert.Equal(
                PromptErrors.BadChoice,
                PromptPolicy.ValidateAnswer(PromptTestData.Open(), "maybe"),
                "unknown choice is a protocol error");
        }

        [Test]
        public static void Readback_Is_Closed_Only_When_Readable_And_Closed()
        {
            Assert.True(PromptPolicy.IsClosed(PromptTestData.Closed()), "closed and readable");
            Assert.False(PromptPolicy.IsClosed(PromptTestData.Open()), "still open");
            Assert.False(PromptPolicy.IsClosed(PromptTestData.Unreadable()), "unreadable is not closed");
            Assert.False(PromptPolicy.IsClosed(null), "missing snapshot is not closed");
        }

        [Test]
        public static void Prompt_Is_Open_Only_When_Readable_With_Details()
        {
            Assert.True(PromptPolicy.IsOpen(PromptTestData.Open()), "readable open prompt");
            Assert.False(PromptPolicy.IsOpen(PromptTestData.Closed()), "closed is not open");
            Assert.False(PromptPolicy.IsOpen(PromptTestData.Unreadable()), "unreadable is not open");
            Assert.False(PromptPolicy.IsOpen(null), "missing snapshot is not open");

            var blank = new PromptSnapshot { Readable = true, Open = true, Prompt = null };
            Assert.False(PromptPolicy.IsOpen(blank), "a panel without details is not answerable");
        }

        [Test]
        public static void Stable_Error_Codes_Do_Not_Drift()
        {
            Assert.Equal("prompt_pending", PromptErrors.PromptPending, "prompt_pending");
            Assert.Equal("no_prompt", PromptErrors.NoPrompt, "no_prompt");
            Assert.Equal("prompt_answer_failed", PromptErrors.AnswerFailed, "prompt_answer_failed");
            Assert.Equal("prompt_readback_mismatch", PromptErrors.ReadbackMismatch, "prompt_readback_mismatch");
            Assert.Equal("prompt_read_failed", PromptErrors.ReadFailed, "prompt_read_failed");
            Assert.Equal("bad_choice", PromptErrors.BadChoice, "bad_choice");
            Assert.Equal("choice_unavailable", PromptErrors.ChoiceUnavailable, "choice_unavailable");
            Assert.Equal("confirmation_required", PromptErrors.ConfirmationRequired, "confirmation_required");
            Assert.Equal("confirmation", PromptKinds.Confirmation, "confirmation kind");
        }
    }
}
