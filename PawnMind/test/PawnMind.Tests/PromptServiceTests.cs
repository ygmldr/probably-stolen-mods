using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class PromptHarness
    {
        public readonly FakePromptPort Prompt = new FakePromptPort();
        public readonly FakePromptStatePort State = new FakePromptStatePort();
        public readonly PromptService Service;

        public PromptHarness()
        {
            Service = new PromptService(Prompt, State);
        }

        public JObject Json(ActionResult result)
        {
            return JObject.Parse(result.ToJson(42));
        }
    }

    /// <summary>
    /// S8 bridge orchestration with fake ports. These tests pin the important
    /// safety property: one native click at most, never while the prompt is
    /// unreadable/closed, and never an ok:true when the panel did not close.
    /// </summary>
    internal static class PromptServiceGetTests
    {
        private static void AssertJsonNull(JToken token, string message)
        {
            Assert.True(
                token == null || token.Type == JTokenType.Null,
                message + " (was " + (token == null ? "missing" : token.Type.ToString()) + ")");
        }

        [Test]
        public static void Get_Closed_Panel_Returns_Null_And_No_Native_Call()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Closed();

            JObject json = harness.Json(harness.Service.GetPending());

            Assert.True((bool)json["ok"], "a closed panel is a successful read");
            AssertJsonNull(json["prompt"], "no prompt");
            Assert.Equal(1, harness.Prompt.Reads, "one read");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "reading never clicks");
        }

        [Test]
        public static void Get_Open_Panel_Returns_Body_And_Button_Labels()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();

            JObject json = harness.Json(harness.Service.GetPending());
            JObject prompt = (JObject)json["prompt"];

            Assert.True((bool)json["ok"], "open prompt is readable");
            Assert.Equal("confirmation", (string)prompt["kind"], "kind");
            Assert.Equal(PromptTestData.WarningBody, (string)prompt["body"], "body text");
            Assert.Equal("离开", (string)prompt["confirm_text"], "confirm label");
            Assert.Equal("取消", (string)prompt["cancel_text"], "cancel label");
            Assert.False((bool)prompt["single_button"], "two buttons");
            AssertJsonNull(prompt["source_op"], "a plain read has no source op");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "reading never clicks");
        }

        [Test]
        public static void Get_Single_Button_Prompt_Reports_Single_Button()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open(true);

            JObject json = harness.Json(harness.Service.GetPending());
            JObject prompt = (JObject)json["prompt"];

            Assert.True((bool)prompt["single_button"], "single button flag");
        }

        [Test]
        public static void Get_Unreadable_Panel_Reports_Read_Failure()
        {
            var harness = new PromptHarness();
            harness.Prompt.ThrowOnRead = true;

            JObject json = harness.Json(harness.Service.GetPending());

            Assert.False((bool)json["ok"], "unreadable is not a success");
            Assert.Equal(PromptErrors.ReadFailed, (string)json["error"], "stable read error");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "reading never clicks");
        }

        [Test]
        public static void State_Prompt_Is_Null_Without_A_Panel()
        {
            JToken token = PromptJson.ForState(PromptTestData.Closed());

            AssertJsonNull(token, "state.prompt is null when no panel is open");
        }

        [Test]
        public static void State_Prompt_Is_Readable_False_On_Read_Exception()
        {
            JToken token = PromptJson.ForState(PromptTestData.Unreadable());

            Assert.NotNull(token, "unreadable state is an object");
            Assert.Equal(JTokenType.Object, token.Type, "unreadable state is an object");
            Assert.False((bool)token["readable"], "readable=false");
            Assert.Null(token["body"], "no invented body");
        }

        [Test]
        public static void State_Prompt_Uses_The_Same_Shape_As_Get_Pending()
        {
            JToken token = PromptJson.ForState(PromptTestData.Open());

            Assert.NotNull(token, "open prompt object");
            Assert.Equal(JTokenType.Object, token.Type, "open prompt object");
            var prompt = (JObject)token;
            Assert.Equal(6, prompt.Count, "exactly the six prompt fields");
            foreach (string field in new[]
            {
                "kind", "body", "confirm_text", "cancel_text", "single_button", "source_op"
            })
            {
                Assert.True(prompt[field] != null, "missing prompt field: " + field);
            }

            Assert.Equal("confirmation", (string)prompt["kind"], "kind");
            Assert.Equal(PromptTestData.WarningBody, (string)prompt["body"], "body");
            AssertJsonNull(prompt["source_op"], "source_op null in state");
        }
    }

    internal static class PromptServiceAnswerTests
    {
        private static void AssertJsonNull(JToken token, string message)
        {
            Assert.True(
                token == null || token.Type == JTokenType.Null,
                message + " (was " + (token == null ? "missing" : token.Type.ToString()) + ")");
        }

        [Test]
        public static void Answer_No_Prompt_Returns_NoPrompt_And_Zero_Calls()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Closed();

            JObject json = harness.Json(harness.Service.AnswerPrompt("confirm"));

            Assert.False((bool)json["ok"], "no prompt means no success");
            Assert.Equal(PromptErrors.NoPrompt, (string)json["error"], "no_prompt");
            Assert.False((bool)json["dispatched"], "nothing was dispatched");
            AssertJsonNull(json["prompt"], "no prompt read back");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "zero native clicks");
        }

        [Test]
        public static void Answer_Unreadable_Prompt_Returns_NoPrompt_And_Zero_Calls()
        {
            var harness = new PromptHarness();
            harness.Prompt.ThrowOnRead = true;

            JObject json = harness.Json(harness.Service.AnswerPrompt("cancel"));

            Assert.False((bool)json["ok"], "unreadable means no success");
            Assert.Equal(PromptErrors.NoPrompt, (string)json["error"], "no_prompt");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "zero native clicks");
        }

        [Test]
        public static void Answer_Invalid_Choice_Does_Not_Click()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();

            JObject json = harness.Json(harness.Service.AnswerPrompt("maybe"));

            Assert.False((bool)json["ok"], "invalid choice is a refusal");
            Assert.Equal(PromptErrors.BadChoice, (string)json["error"], "bad_choice");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "zero native clicks");
        }

        [Test]
        public static void Answer_Cancel_On_Single_Button_Does_Not_Click()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open(true);

            JObject json = harness.Json(harness.Service.AnswerPrompt("cancel"));

            Assert.False((bool)json["ok"], "single button has no cancel");
            Assert.Equal(PromptErrors.ChoiceUnavailable, (string)json["error"], "choice_unavailable");
            Assert.Equal(0, harness.Prompt.AnswerCalls, "zero native clicks");
        }

        [Test]
        public static void Answer_Confirm_Single_Button_Clicks_Once_And_Reads_State()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open(true);
            harness.Prompt.AfterAnswer = PromptTestData.Closed();
            harness.State.Summary = PromptTestData.State(4, 480, true, "dumping_ground");

            JObject json = harness.Json(harness.Service.AnswerPrompt("confirm"));

            Assert.True((bool)json["ok"], "closed readback is a success");
            Assert.True((bool)json["dispatched"], "native click was dispatched");
            Assert.Equal("confirm", (string)json["choice"], "choice echoed");
            Assert.True((bool)json["prompt_closed"], "closed readback");
            AssertJsonNull(json["prompt"], "prompt is gone");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native click");
            Assert.Equal("confirm", harness.Prompt.LastChoice, "native confirm path");
            Assert.Equal(1, harness.State.Reads, "one state readback");

            JObject state = (JObject)json["state"];
            Assert.True((bool)state["readable"], "state readable");
            Assert.Equal(4L, (long)state["day"], "day readback");
            Assert.Equal(480L, (long)state["cash"], "cash readback");
            Assert.True((bool)state["outside"], "outside readback");
            Assert.Equal("dumping_ground", (string)state["location"], "location readback");
        }

        [Test]
        public static void Answer_Cancel_Two_Button_Clicks_Once_And_Closes()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.AfterAnswer = PromptTestData.Closed();

            JObject json = harness.Json(harness.Service.AnswerPrompt("cancel"));

            Assert.True((bool)json["ok"], "cancel closes the panel");
            Assert.Equal("cancel", (string)json["choice"], "choice echoed");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native click");
            Assert.Equal("cancel", harness.Prompt.LastChoice, "native cancel path");
        }

        [Test]
        public static void Answer_Panel_Still_Open_Is_Readback_Mismatch()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.AfterAnswer = PromptTestData.Open();

            JObject json = harness.Json(harness.Service.AnswerPrompt("confirm"));

            Assert.False((bool)json["ok"], "an open panel is not success");
            Assert.Equal(PromptErrors.ReadbackMismatch, (string)json["error"], "readback mismatch");
            Assert.True((bool)json["dispatched"], "the native click already happened");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native click, no retry");
            JObject prompt = (JObject)json["prompt"];
            Assert.Equal(PromptTestData.WarningBody, (string)prompt["body"], "current prompt is returned");
        }

        [Test]
        public static void Answer_Unreadable_After_Click_Is_Readback_Mismatch()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.AfterAnswer = PromptTestData.Unreadable();

            JObject json = harness.Json(harness.Service.AnswerPrompt("cancel"));

            Assert.False((bool)json["ok"], "unreadable readback is not success");
            Assert.Equal(PromptErrors.ReadbackMismatch, (string)json["error"], "readback mismatch");
            Assert.True((bool)json["dispatched"], "native click happened");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "exactly one native click");
        }

        [Test]
        public static void Answer_Native_Exception_Is_AnswerFailed_And_Does_Not_Retry()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.ThrowOnAnswer = true;

            JObject json = harness.Json(harness.Service.AnswerPrompt("confirm"));

            Assert.False((bool)json["ok"], "native exception is not success");
            Assert.Equal(PromptErrors.AnswerFailed, (string)json["error"], "prompt_answer_failed");
            Assert.True((bool)json["dispatched"], "the click was attempted");
            Assert.Equal(1, harness.Prompt.AnswerCalls, "no automatic retry");
        }

        [Test]
        public static void Answer_State_Read_Failure_Still_Reports_Verified_Close()
        {
            var harness = new PromptHarness();
            harness.Prompt.Snapshot = PromptTestData.Open();
            harness.Prompt.AfterAnswer = PromptTestData.Closed();
            harness.State.Throw = true;

            JObject json = harness.Json(harness.Service.AnswerPrompt("confirm"));

            Assert.True((bool)json["ok"], "the panel close was verified");
            JObject state = (JObject)json["state"];
            Assert.False((bool)state["readable"], "state summary says it was unreadable");
        }
    }
}
