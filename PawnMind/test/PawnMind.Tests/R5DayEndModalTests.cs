using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// R5-B2: the native negotiation-tablet confirmation modal
    /// (<c>NegotiationTablet/Confirm</c>, the yes/no panel created by
    /// <c>NegociacionUIManager.OpenConfirmPanel</c>) used to have no typed entry:
    /// only the test-only <c>press</c> whitelist could answer it. This suite pins
    /// the typed replacement on the existing <c>get_pending_prompt</c> /
    /// <c>answer_prompt</c> tools: identity, player-visible text, enabled state,
    /// exactly one native answer, and never a fake success when the panel stays
    /// open or another modal owns the input.
    ///
    /// Evidence level: L1-fake (fake prompt port). The real native callbacks stay
    /// L3 NOT_RUN; Play still cannot use <c>press</c>.
    /// </summary>
    internal static class R5DayEndModalTests
    {
        private static PromptHarness Harness()
        {
            return new PromptHarness();
        }

        private static JObject Answer(PromptHarness harness, string choice)
        {
            return harness.Json(harness.Service.AnswerPrompt(choice));
        }

        /// <summary>
        /// R5-B2-01: the read identifies the native modal, its player-visible text,
        /// its target window and its enabled options instead of a button index.
        /// </summary>
        [Test]
        public static void R5_MODAL_01_Get_Pending_Identifies_The_Negotiation_Confirm_Modal()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.NegotiationConfirmOpen();

            JObject json = harness.Json(harness.Service.GetPending());

            Assert.True((bool)json["ok"], "the read succeeds");
            JObject prompt = (JObject)json["prompt"];
            Assert.NotNull(prompt, "the modal is reported");
            Assert.Equal(PromptKinds.NegotiationConfirm, (string)prompt["kind"],
                "the modal kind is explicit");
            Assert.Equal(PromptTargets.NegotiationTabletConfirm, (string)prompt["target"],
                "the target window identity is explicit");
            Assert.Equal("卖出的总价超出顾客预算，仍要卖出吗？", (string)prompt["body"],
                "the player-visible body text");
            Assert.Equal("是", (string)prompt["confirm_text"], "confirm label");
            Assert.Equal("否", (string)prompt["cancel_text"], "cancel label");
            Assert.True((bool)prompt["confirm_enabled"], "confirm option enabled");
            Assert.True((bool)prompt["cancel_enabled"], "cancel option enabled");
            Assert.False((bool)prompt["single_button"], "two options");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "reading never clicks");
        }

        /// <summary>
        /// R5-B2-02: confirming dispatches exactly one native answer and reports the
        /// closed panel plus the post-answer shop state.
        /// </summary>
        [Test]
        public static void R5_MODAL_02_Answer_Confirm_Clicks_Once_And_Reads_Back()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.NegotiationConfirmOpen();
            harness.Prompt.AfterAnswer = PromptTestData.Closed();
            harness.State.Summary = PromptTestData.State(shopState: "CLOSED");

            JObject json = Answer(harness, PromptChoices.Confirm);

            Assert.True((bool)json["ok"], "the confirmed answer is ok");
            Assert.True((bool)json["dispatched"], "the native answer ran");
            Assert.Equal(PromptChoices.Confirm, (string)json["choice"], "the answered choice");
            Assert.True((bool)json["prompt_closed"], "the panel closed");
            Assert.Equal("CLOSED", (string)json["state"]["shop_state"],
                "the post-answer shop state is reported");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native answer");
            Assert.Equal(PromptKinds.NegotiationConfirm, harness.Prompt.LastKind,
                "the adapter was told which native modal to answer");
        }

        /// <summary>
        /// R5-B2-03: cancelling dispatches exactly one native answer too.
        /// </summary>
        [Test]
        public static void R5_MODAL_03_Answer_Cancel_Clicks_Once()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.NegotiationConfirmOpen();
            harness.Prompt.AfterAnswer = PromptTestData.Closed();

            JObject json = Answer(harness, PromptChoices.Cancel);

            Assert.True((bool)json["ok"], "the cancelled answer is ok");
            Assert.Equal(PromptChoices.Cancel, (string)json["choice"], "the answered choice");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native answer");
            Assert.Equal(PromptChoices.Cancel, harness.Prompt.LastChoice, "cancel reached the port");
        }

        /// <summary>
        /// R5-B2-04: two modals owning the input at once is ambiguous; the bridge
        /// must report it and never guess which one to answer.
        /// </summary>
        [Test]
        public static void R5_MODAL_04_Ambiguous_Modals_Are_Never_Answered()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = new PromptSnapshot
            {
                Readable = false,
                Open = true,
                Error = PromptErrors.PromptAmbiguous
            };

            JObject read = harness.Json(harness.Service.GetPending());
            Assert.False((bool)read["ok"], "an ambiguous read fails closed");
            Assert.Equal(PromptErrors.PromptAmbiguous, (string)read["error"],
                "the ambiguity is named, not hidden behind a generic read error");

            JObject answer = Answer(harness, PromptChoices.Confirm);
            Assert.False((bool)answer["ok"], "an ambiguous modal is not answered");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "no native click on ambiguity");
        }

        /// <summary>
        /// R5-B2-05: a disabled confirm option is not clickable; the bridge refuses
        /// instead of invoking a greyed-out button.
        /// </summary>
        [Test]
        public static void R5_MODAL_05_Disabled_Confirm_Option_Is_Refused()
        {
            PromptHarness harness = Harness();
            PendingPrompt prompt = PromptTestData.NegotiationConfirm();
            prompt.ConfirmEnabled = false;
            harness.Prompt.Snapshot = PromptTestData.OpenWith(prompt);

            JObject json = Answer(harness, PromptChoices.Confirm);

            Assert.False((bool)json["ok"], "a disabled confirm is refused");
            Assert.Equal(PromptErrors.ChoiceUnavailable, (string)json["error"], "stable code");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "the disabled option was not clicked");
        }

        /// <summary>
        /// R5-B2-06: a click that does not close the panel is a readback mismatch,
        /// never a success.
        /// </summary>
        [Test]
        public static void R5_MODAL_06_Panel_Still_Open_Is_Not_A_Success()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.NegotiationConfirmOpen();
            harness.Prompt.AfterAnswer = PromptTestData.NegotiationConfirmOpen();

            JObject json = Answer(harness, PromptChoices.Confirm);

            Assert.False((bool)json["ok"], "an open panel is not a success");
            Assert.Equal(PromptErrors.ReadbackMismatch, (string)json["error"], "readback mismatch");
            Assert.True((bool)json["dispatched"], "the native answer did run");
            Assert.Contains((string)json["readback"], "still_open", "the readback names the state");
        }

        /// <summary>
        /// R5-B2-07: if the native modal changed between the read and the answer,
        /// the adapter refuses before clicking (no dispatch) with a stable code.
        /// </summary>
        [Test]
        public static void R5_MODAL_07_Changed_Modal_Is_Refused_Without_Dispatch()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.NegotiationConfirmOpen();
            harness.Prompt.Outcome = PromptAnswerOutcome.NotDispatched(PromptErrors.PromptChanged);

            JObject json = Answer(harness, PromptChoices.Confirm);

            Assert.False((bool)json["ok"], "a changed modal is refused");
            Assert.Equal(PromptErrors.PromptChanged, (string)json["error"], "stable code");
            Assert.False((bool)json["dispatched"], "nothing was dispatched");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "the adapter was asked exactly once");
        }

        /// <summary>
        /// R5-B2-08: the existing generic confirmation keeps its identity and
        /// behaviour (no regression from adding the new modal kinds).
        /// </summary>
        [Test]
        public static void R5_MODAL_08_Generic_Confirmation_Keeps_Its_Identity()
        {
            PromptHarness harness = Harness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.AfterAnswer = PromptTestData.Closed();

            JObject read = harness.Json(harness.Service.GetPending());
            JObject prompt = (JObject)read["prompt"];
            Assert.Equal(PromptKinds.Confirmation, (string)prompt["kind"], "kind unchanged");
            Assert.Equal(PromptTargets.GenericConfirmation, (string)prompt["target"],
                "the generic modal target is explicit");

            JObject answer = Answer(harness, PromptChoices.Confirm);
            Assert.True((bool)answer["ok"], "the generic confirmation still answers");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native answer");
        }
    }
}
