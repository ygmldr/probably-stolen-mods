using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class FakeWindowPort : IWindowPort
    {
        public WindowSnapshot Snapshot = new WindowSnapshot();
        public readonly List<string> Closed = new List<string>();
        public int HideCalls;
        public bool ThrowOnClose;
        public WindowSnapshot AfterOverride;

        public WindowSnapshot ReadWindows()
        {
            return AfterOverride != null && Closed.Count > 0 ? AfterOverride : Snapshot;
        }

        public void CloseWindow(string kind)
        {
            if (ThrowOnClose) throw new InvalidOperationException("native_error");
            Closed.Add(kind);
        }

        public void HideMovableWindows()
        {
            if (ThrowOnClose) throw new InvalidOperationException("native_error");
            Closed.Add(WindowKinds.Movable);
        }
    }

    internal sealed class WindowHarness
    {
        public readonly FakeWindowPort Port = new FakeWindowPort();
        public readonly WindowService Service;

        public WindowHarness()
        {
            Service = new WindowService(Port);
        }

        public JObject Json(ActionResult result)
        {
            return JObject.Parse(result.ToJson(7));
        }
    }

    /// <summary>
    /// close_windows orchestration with a fake port: it must close exactly the
    /// panels that were open, never claim success when the screen is still dirty,
    /// and never retry a native throw.
    /// </summary>
    internal static class WindowServiceTests
    {
        [Test]
        public static void Nothing_Open_Is_An_Ok_No_Op()
        {
            var harness = new WindowHarness();

            JObject json = harness.Json(harness.Service.Close());

            Assert.True((bool)json["ok"], "empty screen is fine");
            Assert.False((bool)json["dispatched"], "nothing dispatched");
            Assert.False((bool)json["changed"], "nothing changed");
            Assert.Equal(0, harness.Port.Closed.Count, "no native call");
        }

        [Test]
        public static void Open_Paper_Is_Closed_Through_The_Native_Method()
        {
            var harness = new WindowHarness();
            harness.Port.Snapshot = new WindowSnapshot { PaperOpen = true };
            harness.Port.AfterOverride = new WindowSnapshot();

            JObject json = harness.Json(harness.Service.Close());

            Assert.True((bool)json["ok"], "clean after close");
            Assert.True((bool)json["dispatched"], "dispatched");
            Assert.True((bool)json["changed"], "changed");
            Assert.Equal(1, harness.Port.Closed.Count, "one close");
            Assert.Equal(WindowKinds.Paper, harness.Port.Closed[0], "paper closed");
        }

        [Test]
        public static void Movable_Windows_Are_Hidden_Last()
        {
            var harness = new WindowHarness();
            harness.Port.Snapshot = new WindowSnapshot { BookOpen = true, MovableVisible = 3 };
            harness.Port.AfterOverride = new WindowSnapshot();

            harness.Json(harness.Service.Close());

            Assert.Equal(2, harness.Port.Closed.Count, "book then movable");
            Assert.Equal(WindowKinds.Book, harness.Port.Closed[0], "book first");
            Assert.Equal(WindowKinds.Movable, harness.Port.Closed[1], "movable last");
        }

        [Test]
        public static void Still_Dirty_After_Close_Is_A_Readback_Mismatch()
        {
            var harness = new WindowHarness();
            harness.Port.Snapshot = new WindowSnapshot { PaperOpen = true };
            harness.Port.AfterOverride = new WindowSnapshot { PaperOpen = true };

            JObject json = harness.Json(harness.Service.Close());

            Assert.False((bool)json["ok"], "not ok while still open");
            Assert.Equal(WindowErrors.ReadbackMismatch, (string)json["error"], "stable code");
            Assert.True((bool)json["dispatched"], "native was called");
        }

        [Test]
        public static void Native_Throw_Is_A_Mismatch_And_Never_Retried()
        {
            var harness = new WindowHarness();
            harness.Port.Snapshot = new WindowSnapshot { PaperOpen = true };
            harness.Port.ThrowOnClose = true;

            JObject json = harness.Json(harness.Service.Close());

            Assert.False((bool)json["ok"], "throw is a failure");
            Assert.Equal(WindowErrors.ReadbackMismatch, (string)json["error"], "stable code");
            Assert.Equal(0, harness.Port.Closed.Count, "the throw aborted before recording");
        }

        [Test]
        public static void Unreadable_Screen_Is_A_Failure_With_No_Native_Call()
        {
            var harness = new WindowHarness();
            harness.Port.Snapshot = new WindowSnapshot
            {
                Readable = false,
                Error = WindowErrors.Unavailable,
                PaperOpen = true
            };

            JObject json = harness.Json(harness.Service.Close());

            Assert.False((bool)json["ok"], "cannot close what cannot be read");
            Assert.Equal(WindowErrors.Unavailable, (string)json["error"], "stable code");
            Assert.Equal(0, harness.Port.Closed.Count, "no native call");
        }
    }
}
