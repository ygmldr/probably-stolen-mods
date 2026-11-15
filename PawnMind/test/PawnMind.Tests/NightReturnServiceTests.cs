using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class NightReturnHarness
    {
        public readonly FakeNightReturnPort Night = new FakeNightReturnPort();
        public readonly FakePromptPort Prompt = new FakePromptPort();
        public readonly NightReturnService Service;

        public NightReturnHarness()
        {
            Service = new NightReturnService(Night, Prompt);
        }

        public JObject Json(ActionResult result)
        {
            return JObject.Parse(result.ToJson(42));
        }
    }

    /// <summary>
    /// S8 return_home: the native Return() may open a confirmation modal. The
    /// bridge must not report success until it has read the prompt and proven
    /// the panel is closed.
    /// </summary>
    internal static class NightReturnServiceTests
    {
        [Test]
        public static void Return_Not_Outside_Does_Not_Call_Native()
        {
            var harness = new NightReturnHarness();
            harness.Night.Outside = false;

            JObject json = harness.Json(harness.Service.ReturnHome());

            Assert.False((bool)json["ok"], "cannot return from the shop");
            Assert.Equal("not_outside", (string)json["error"], "stable error");
            Assert.Equal(0, harness.Night.ReturnCalls, "zero native calls");
            Assert.Equal(0, harness.Prompt.Reads, "no prompt read");
        }

        [Test]
        public static void Return_Without_Prompt_Reports_Success_And_Items()
        {
            var harness = new NightReturnHarness();
            harness.Night.Items = 2;
            harness.Prompt.Snapshot = PromptTestData.Closed();

            JObject json = harness.Json(harness.Service.ReturnHome());

            Assert.True((bool)json["ok"], "no prompt means the return started");
            Assert.True((bool)json["return_requested"], "return requested");
            Assert.Equal(2L, (long)json["items_on_ground"], "items_on_ground retained");
            Assert.Equal(1, harness.Night.ReturnCalls, "exactly one native Return");
            Assert.Equal(1, harness.Prompt.Reads, "one prompt read after Return");
        }

        [Test]
        public static void Return_With_Open_Prompt_Is_Confirmation_Required()
        {
            var harness = new NightReturnHarness();
            harness.Night.Items = 2;
            harness.Prompt.Snapshot = PromptTestData.Closed();
            harness.Night.OnReturn = delegate
            {
                harness.Prompt.Snapshot = PromptTestData.Open();
            };

            JObject json = harness.Json(harness.Service.ReturnHome());

            Assert.False((bool)json["ok"], "a blocking prompt is not a completed return");
            Assert.Equal(PromptErrors.ConfirmationRequired, (string)json["error"],
                "confirmation_required");
            Assert.True((bool)json["dispatched"], "the native Return was dispatched");
            Assert.Equal(2L, (long)json["items_on_ground"], "items_on_ground retained");
            Assert.Equal(1, harness.Night.ReturnCalls, "exactly one native Return");
            JObject prompt = (JObject)json["prompt"];
            Assert.Equal(PromptTestData.WarningBody, (string)prompt["body"], "prompt body");
            Assert.Equal("return_home", (string)prompt["source_op"], "prompt source op");
        }

        [Test]
        public static void Return_Unreadable_Prompt_Does_Not_Claim_Success()
        {
            var harness = new NightReturnHarness();
            harness.Prompt.Snapshot = PromptTestData.Closed();
            harness.Night.OnReturn = delegate
            {
                harness.Prompt.ThrowOnRead = true;
            };

            JObject json = harness.Json(harness.Service.ReturnHome());

            Assert.False((bool)json["ok"], "an unreadable panel must not be reported as success");
            Assert.Equal(PromptErrors.ReadFailed, (string)json["error"], "prompt_read_failed");
            Assert.True((bool)json["dispatched"], "the native Return was dispatched");
            Assert.Equal(1, harness.Night.ReturnCalls, "exactly one native Return");
        }

        [Test]
        public static void Return_Native_Exception_Is_Return_Failed()
        {
            var harness = new NightReturnHarness();
            harness.Night.ThrowOnReturn = true;

            JObject json = harness.Json(harness.Service.ReturnHome());

            Assert.False((bool)json["ok"], "native exception is not success");
            Assert.Equal("return_failed", (string)json["error"], "stable error");
            Assert.True((bool)json["dispatched"], "the call was attempted");
            Assert.Equal(1, harness.Night.ReturnCalls, "exactly one native Return");
        }

        [Test]
        public static void Return_Does_Not_Read_Prompt_Before_The_Native_Call()
        {
            var harness = new NightReturnHarness();
            harness.Prompt.Snapshot = PromptTestData.Closed();
            harness.Night.OnReturn = delegate
            {
                Assert.Equal(0, harness.Prompt.Reads, "the prompt is read after Return, not before");
            };

            harness.Json(harness.Service.ReturnHome());

            Assert.Equal(1, harness.Prompt.Reads, "one post-Return read");
        }
    }
}
