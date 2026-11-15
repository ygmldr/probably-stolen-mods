using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class TutorialServiceTests
    {
        private static JObject Json(ActionResult result)
        {
            Assert.NotNull(result.RawJson, "raw json exists");
            return JObject.Parse(result.RawJson);
        }

        [Test]
        public static void Dismiss_Already_Off_Does_Not_Write()
        {
            var port = new FakeTutorialPort
            {
                Before = new TutorialSnapshot
                {
                    Readable = true,
                    Enabled = false,
                    HintsHidden = true,
                    GroupReadable = true
                }
            };

            var service = new TutorialService(port);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.True(result.Ok, "already dismissed is a success");
            Assert.Equal(false, (bool)json["dispatched"], "no write");
            Assert.Equal(false, (bool)json["changed"], "nothing changed");
            Assert.Equal(false, (bool)json["tutorial_enabled"], "tutorial disabled");
            Assert.Equal(true, (bool)json["hints_hidden"], "hints hidden");
            Assert.Equal(0, port.DisableCalls, "no native write");
        }

        [Test]
        public static void Dismiss_Writes_Exactly_Once()
        {
            var port = new FakeTutorialPort
            {
                After = new TutorialSnapshot
                {
                    Readable = true,
                    Enabled = false,
                    HintsHidden = true,
                    GroupReadable = true
                }
            };

            var service = new TutorialService(port);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.True(result.Ok, "dismiss succeeds");
            Assert.Equal(true, (bool)json["dispatched"], "one write");
            Assert.Equal(true, (bool)json["changed"], "the flag changed");
            Assert.Equal(false, (bool)json["tutorial_enabled"], "tutorial disabled");
            Assert.Equal(true, (bool)json["hints_hidden"], "hints hidden");
            Assert.Equal(1, port.DisableCalls, "exactly one native write");
        }

        [Test]
        public static void Dismiss_Stale_Visible_Group_Writes_Without_A_Flag_Change()
        {
            var port = new FakeTutorialPort
            {
                Before = new TutorialSnapshot
                {
                    Readable = true,
                    Enabled = false,
                    HintsHidden = false,
                    GroupReadable = true
                },
                After = new TutorialSnapshot
                {
                    Readable = true,
                    Enabled = false,
                    HintsHidden = true,
                    GroupReadable = true
                }
            };

            var service = new TutorialService(port);
            JObject json = Json(service.Dismiss());

            Assert.Equal(true, (bool)json["ok"], "success");
            Assert.Equal(true, (bool)json["dispatched"], "the stale group is hidden");
            Assert.Equal(false, (bool)json["changed"], "the tutorial flag itself was already off");
        }

        [Test]
        public static void Dismiss_Readback_Mismatch_Fails_Closed()
        {
            var port = new FakeTutorialPort
            {
                After = new TutorialSnapshot
                {
                    Readable = true,
                    Enabled = true,
                    HintsHidden = false,
                    GroupReadable = true
                }
            };

            var service = new TutorialService(port);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.False(result.Ok, "mismatch is a failure");
            Assert.Equal(TutorialRules.ReadbackMismatch, (string)json["error"], "stable error");
            Assert.Equal(true, (bool)json["dispatched"], "the write happened");
            Assert.Equal(1, port.DisableCalls, "never retried");
        }

        [Test]
        public static void Dismiss_Write_Failure_Is_An_Explicit_Mismatch()
        {
            var port = new FakeTutorialPort { ThrowOnWrite = true };

            var service = new TutorialService(port);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.False(result.Ok, "write failure is a failure");
            Assert.Equal(TutorialRules.ReadbackMismatch, (string)json["error"], "stable error");
            Assert.Equal("native_error", (string)json["detail"], "detail");
            Assert.Equal(1, port.DisableCalls, "never retried");
        }

        [Test]
        public static void Dismiss_Unreadable_Before_Is_Unavailable()
        {
            var port = new FakeTutorialPort
            {
                Before = new TutorialSnapshot { Readable = false, Error = "not_in_game" }
            };

            var service = new TutorialService(port);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.False(result.Ok, "cannot dismiss without reading");
            Assert.Equal("not_in_game", (string)json["error"], "adapter error");
            Assert.Equal(false, (bool)json["dispatched"], "no write");
            Assert.Equal(0, port.DisableCalls, "no native write");
        }

        [Test]
        public static void Dismiss_Without_A_Port_Is_Unavailable()
        {
            var service = new TutorialService(null);
            ActionResult result = service.Dismiss();
            JObject json = Json(result);

            Assert.False(result.Ok, "no port");
            Assert.Equal(TutorialRules.Unavailable, (string)json["error"], "stable error");
        }
    }
}
