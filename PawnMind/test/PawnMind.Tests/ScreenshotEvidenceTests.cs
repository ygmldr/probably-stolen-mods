using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ScreenshotEvidenceTests
    {
        private static readonly DateTime FixedUtc =
            new DateTime(2026, 11, 14, 22, 15, 0, 123, DateTimeKind.Utc);

        private sealed class Clock
        {
            public DateTime Utc = FixedUtc;

            public DateTime Now()
            {
                return Utc;
            }
        }

        private static ScreenshotLedger Ledger(
            Clock clock,
            Func<ScreenshotUsage> usage = null,
            long budgetBytes = ScreenshotLedger.DefaultBudgetBytes,
            int queueCapacity = ScreenshotLedger.DefaultQueueCapacity)
        {
            if (usage == null) usage = delegate { return new ScreenshotUsage(0, 0); };
            if (clock == null) clock = new Clock();
            return new ScreenshotLedger(usage, clock.Now, budgetBytes, queueCapacity);
        }

        private static ScreenshotEntry Capture(ScreenshotLedger ledger, string op, string outcome,
            long rev = 1, int day = 1, long cash = 0)
        {
            ScreenshotRequest request = ledger.Request(op, outcome, rev, day, cash);
            if (!request.Accepted)
                throw new AssertionException("screenshot request rejected: " + request.Error);

            ScreenshotEntry entry;
            if (!ledger.TryDequeue(out entry))
                throw new AssertionException("queued screenshot missing");

            ledger.MarkCaptured(entry, 1024);
            return entry;
        }

        [Test]
        public static void Screenshot_File_Name_Includes_Op_Outcome_And_Safe_Labels()
        {
            ScreenshotLedger ledger = Ledger(new Clock());

            ScreenshotRequest request = ledger.Request(
                "../../Machine Insert!", "Choice Disabled!", 128, 3, 4321);

            Assert.True(request.Accepted, "request accepted");
            Assert.Equal(
                "screenshots/20261114-221500123_r128_d3_machine_insert_choice_disabled.jpg",
                request.Entry.Path,
                "safe file name");
            Assert.Equal("machine_insert", request.Entry.Op, "op label sanitized");
            Assert.Equal("choice_disabled", request.Entry.Outcome, "outcome label sanitized");
            Assert.Equal("machine_insert", request.Entry.Reason, "reason mirrors op");
            Assert.Equal("choice_disabled", request.Entry.Error, "error mirrors failed outcome");
            Assert.Equal(128L, request.Entry.Rev, "revision recorded");
            Assert.Equal(3, request.Entry.Day, "day recorded");
            Assert.Equal(4321L, request.Entry.Cash, "cash recorded");
            Assert.Equal("2026-11-14T22:15:00.123Z", request.Entry.Timestamp, "UTC timestamp");
            Assert.False(request.Entry.Path.Contains(".."), "no traversal");
            Assert.False(request.Entry.Path.Contains(" "), "no spaces");
            Assert.False(request.Entry.Path.Contains("!"), "no punctuation");
        }

        [Test]
        public static void Screenshot_File_Name_Uses_Ok_For_Success()
        {
            ScreenshotLedger ledger = Ledger(new Clock());

            ScreenshotRequest request = ledger.Request("machine_insert", "ok", 7, 2, 100);

            Assert.True(request.Accepted, "request accepted");
            Assert.Equal(
                "screenshots/20261114-221500123_r7_d2_machine_insert_ok.jpg",
                request.Entry.Path,
                "success label is ok");
            Assert.Equal("ok", request.Entry.Outcome, "outcome stored");
            Assert.Null(request.Entry.Error, "success has no error code");
        }

        [Test]
        public static void Screenshot_Labels_Are_Truncated_To_Length_Cap()
        {
            ScreenshotLedger ledger = Ledger(new Clock());
            string longOp = new string('A', 100);
            string longOutcome = new string('B', 100);

            ScreenshotRequest request = ledger.Request(longOp, longOutcome, 1, 1, 0);

            Assert.True(request.Accepted, "request accepted");
            Assert.Equal(ScreenshotLedger.MaxLabelLength, request.Entry.Op.Length, "op truncated");
            Assert.Equal(ScreenshotLedger.MaxLabelLength, request.Entry.Outcome.Length, "outcome truncated");
            Assert.True(request.Entry.Path.EndsWith(".jpg", StringComparison.Ordinal), "jpeg suffix");
            Assert.True(request.Entry.Path.Contains("_" + new string('a', ScreenshotLedger.MaxLabelLength) + "_"),
                "sanitized op appears in the path");
        }

        [Test]
        public static void Screenshot_File_Names_Stay_Unique_For_The_Same_Clock()
        {
            Clock clock = new Clock();
            ScreenshotLedger ledger = Ledger(clock);

            ScreenshotRequest first = ledger.Request("open_shop", "ok", 1, 1, 0);
            ScreenshotRequest second = ledger.Request("open_shop", "ok", 1, 1, 0);
            ScreenshotRequest third = ledger.Request("open_shop", "ok", 1, 1, 0);

            Assert.True(first.Accepted && second.Accepted && third.Accepted, "all accepted");
            Assert.False(first.Entry.Path == second.Entry.Path, "first and second differ");
            Assert.False(second.Entry.Path == third.Entry.Path, "second and third differ");
            Assert.Contains(second.Entry.Path, "20261114-221500124", "clock advanced by one millisecond");
            Assert.Contains(third.Entry.Path, "20261114-221500125", "clock advanced again");
        }

        [Test]
        public static void Screenshot_Rejects_When_Usage_Already_Exceeds_Budget()
        {
            long budget = ScreenshotLedger.DefaultBudgetBytes;
            ScreenshotLedger ledger = Ledger(
                new Clock(),
                delegate { return new ScreenshotUsage(12, budget); },
                budget);

            ScreenshotRequest request = ledger.Request("open_shop", "ok", 1, 1, 0);

            Assert.False(request.Accepted, "over-budget request rejected");
            Assert.Equal("screenshot_budget_exhausted", request.Error, "stable budget error");
            Assert.Null(request.Entry, "no entry queued");
            Assert.Equal(12, ledger.Status.TotalCount, "usage count retained");
            Assert.Equal(budget, ledger.Status.TotalBytes, "usage bytes retained");
            Assert.True(ledger.Status.BudgetExhausted, "budget exhausted flag");
            Assert.Equal(0, ledger.Status.Dropped, "budget rejection is not a queue drop");
        }

        [Test]
        public static void Screenshot_Stops_After_A_Capture_Crosses_The_Budget()
        {
            long budget = 1000;
            ScreenshotLedger ledger = Ledger(
                new Clock(),
                delegate { return new ScreenshotUsage(0, 900); },
                budget);

            ScreenshotRequest request = ledger.Request("open_shop", "ok", 1, 1, 0);
            Assert.True(request.Accepted, "request accepted below budget");

            ScreenshotEntry entry;
            Assert.True(ledger.TryDequeue(out entry), "entry queued");
            ledger.MarkCaptured(entry, 200);

            Assert.True(ledger.Status.BudgetExhausted, "capture crossing the budget marks exhaustion");
            Assert.Equal(1100L, ledger.Status.TotalBytes, "written bytes counted");

            ScreenshotRequest blocked = ledger.Request("open_shop", "ok", 1, 1, 0);
            Assert.False(blocked.Accepted, "next request rejected");
            Assert.Equal("screenshot_budget_exhausted", blocked.Error, "budget error code");
        }

        [Test]
        public static void Screenshot_Queue_Drops_And_Counts_When_Encoding_Falls_Behind()
        {
            ScreenshotLedger ledger = Ledger(new Clock(), null,
                ScreenshotLedger.DefaultBudgetBytes, 2);

            ScreenshotRequest first = ledger.Request("open_shop", "ok", 1, 1, 0);
            ScreenshotRequest second = ledger.Request("call_next_customer", "ok", 2, 1, 0);
            ScreenshotRequest third = ledger.Request("end_day", "stale_rev", 3, 1, 0);

            Assert.True(first.Accepted && second.Accepted, "first two accepted");
            Assert.False(third.Accepted, "third request dropped");
            Assert.Equal("screenshot_queue_full", third.Error, "queue-full error code");
            Assert.Equal(1, ledger.Status.Dropped, "dropped counter");
            Assert.Equal(2, ledger.QueuedCount, "queue still holds two");
            Assert.Equal(2, ledger.Recent(20).Count, "dropped request is not in recent entries");
        }

        [Test]
        public static void Screenshot_Usage_Is_Rescanned_Every_Fifty_Captures()
        {
            int scans = 0;
            ScreenshotLedger ledger = Ledger(
                new Clock(),
                delegate
                {
                    scans++;
                    return new ScreenshotUsage(7, 7000);
                },
                ScreenshotLedger.DefaultBudgetBytes,
                100);

            ScreenshotRequest first = ledger.Request("open_shop", "ok", 1, 1, 0);
            Assert.True(first.Accepted, "first request accepted");
            Assert.Equal(1, scans, "initial usage scan");

            for (int i = 0; i < 50; i++)
            {
                ScreenshotRequest request = ledger.Request("open_shop", "ok", i + 1, 1, 0);
                Assert.True(request.Accepted, "frame " + i + " accepted");

                ScreenshotEntry entry;
                Assert.True(ledger.TryDequeue(out entry), "frame " + i + " dequeued");
                ledger.MarkCaptured(entry, 100);
            }

            Assert.Equal(2, scans, "usage rescanned after fifty captures");
            Assert.Equal(7, ledger.Status.TotalCount, "rescan replaces the running count");
            Assert.Equal(7000L, ledger.Status.TotalBytes, "rescan replaces the running bytes");
        }

        [Test]
        public static void Screenshot_Recent_Is_Newest_First_And_Limit_Is_Clamped()
        {
            ScreenshotLedger ledger = Ledger(new Clock());
            ledger.Request("open_shop", "ok", 1, 1, 0);
            ledger.Request("call_next_customer", "ok", 2, 1, 0);
            ledger.Request("end_day", "no_client", 3, 1, 0);

            IReadOnlyList<ScreenshotEntry> two = ledger.Recent(2);
            Assert.Equal(2, two.Count, "limit respected");
            Assert.Equal("end_day", two[0].Op, "newest first");
            Assert.Equal("call_next_customer", two[1].Op, "second newest");

            Assert.Equal(3, ledger.Recent(0).Count, "zero uses the default limit");
            Assert.Equal(3, ledger.Recent(1000).Count, "huge limit is clamped to available entries");
            Assert.Equal(3, ledger.Recent(-5).Count, "negative uses the default limit");
        }

        [Test]
        public static void Screenshot_Status_Reports_Totals_Budget_And_Dropped()
        {
            ScreenshotLedger ledger = Ledger(
                new Clock(),
                delegate { return new ScreenshotUsage(4, 1000); },
                ScreenshotLedger.DefaultBudgetBytes,
                1);

            ScreenshotRequest first = ledger.Request("open_shop", "ok", 1, 1, 0);
            Assert.True(first.Accepted, "first accepted");

            ScreenshotEntry entry;
            Assert.True(ledger.TryDequeue(out entry), "first dequeued");
            ledger.MarkCaptured(entry, 500);

            ScreenshotRequest queued = ledger.Request("open_shop", "ok", 2, 1, 0);
            ScreenshotRequest dropped = ledger.Request("open_shop", "ok", 3, 1, 0);

            Assert.True(queued.Accepted, "second capture fills the single slot");
            Assert.False(dropped.Accepted, "queue is full");
            ScreenshotStatus status = ledger.Status;
            Assert.Equal(5, status.TotalCount, "starting files plus one capture");
            Assert.Equal(1500L, status.TotalBytes, "starting bytes plus capture bytes");
            Assert.Equal(ScreenshotLedger.DefaultBudgetBytes, status.BudgetBytes, "budget exposed");
            Assert.Equal(1, status.Dropped, "dropped exposed");
            Assert.False(status.BudgetExhausted, "budget still available");
        }

        [Test]
        public static void Screenshot_Image_Scales_To_Short_Side_768()
        {
            ScreenshotSize landscape = ScreenshotImage.ScaleForShortSide(1920, 1080);
            Assert.Equal(1365, landscape.Width, "landscape width");
            Assert.Equal(768, landscape.Height, "landscape short side");

            ScreenshotSize portrait = ScreenshotImage.ScaleForShortSide(1080, 1920);
            Assert.Equal(768, portrait.Width, "portrait short side");
            Assert.Equal(1365, portrait.Height, "portrait height");

            ScreenshotSize wide = ScreenshotImage.ScaleForShortSide(2560, 1440);
            Assert.Equal(1365, wide.Width, "wide width");
            Assert.Equal(768, wide.Height, "wide short side");

            ScreenshotSize square = ScreenshotImage.ScaleForShortSide(1000, 1000);
            Assert.Equal(768, square.Width, "square width");
            Assert.Equal(768, square.Height, "square height");

            ScreenshotSize small = ScreenshotImage.ScaleForShortSide(800, 600);
            Assert.Equal(800, small.Width, "small images are never upscaled");
            Assert.Equal(600, small.Height, "small images are never upscaled");

            Assert.False(ScreenshotImage.NeedsScale(800, 600), "small image needs no scale");
            Assert.True(ScreenshotImage.NeedsScale(1920, 1080), "large image needs a scale");
            Assert.Equal(768, ScreenshotImage.ShortSide, "default short side");
            Assert.Equal(80, ScreenshotImage.JpegQuality, "jpeg quality");
        }

        [Test]
        public static void Screenshot_Json_Capture_Result_Contains_Required_Fields()
        {
            ScreenshotLedger ledger = Ledger(new Clock());
            ScreenshotRequest accepted = ledger.Request("manual", "ok", 10, 4, 250);
            string json = ScreenshotJson.CaptureResult(accepted, 10);
            JObject payload = JObject.Parse(json);

            Assert.True((bool)payload["ok"], "capture ok");
            Assert.Equal(accepted.Entry.Path, (string)payload["path"], "path returned");
            Assert.Equal("manual", (string)payload["reason"], "reason returned");
            Assert.Equal("manual", (string)payload["op"], "op returned");
            Assert.Equal("ok", (string)payload["outcome"], "outcome returned");
            Assert.Equal(10L, (long)payload["rev"], "rev returned");
            Assert.Equal(4, (int)payload["day"], "day returned");
            Assert.NotNull(payload["timestamp"], "timestamp returned");
        }

        [Test]
        public static void Screenshot_Json_Capture_Rejection_Has_No_Path()
        {
            ScreenshotLedger ledger = Ledger(new Clock(), null,
                ScreenshotLedger.DefaultBudgetBytes, 1);
            Assert.True(ledger.Request("a", "ok", 1, 1, 0).Accepted, "first accepted");
            Assert.False(ledger.Request("b", "ok", 1, 1, 0).Accepted, "second dropped");
            ScreenshotRequest rejected = ledger.Request("c", "ok", 1, 1, 0);

            JObject payload = JObject.Parse(ScreenshotJson.CaptureResult(rejected, 9));

            Assert.False((bool)payload["ok"], "capture rejected");
            Assert.Equal("screenshot_queue_full", (string)payload["error"], "stable error");
            Assert.Null(payload["path"], "no path for a dropped capture");
            Assert.Equal(9L, (long)payload["rev"], "rev still returned");
        }

        [Test]
        public static void Screenshot_Json_List_Result_Contains_Entries_And_Budget_Status()
        {
            long budget = 4096;
            ScreenshotLedger ledger = Ledger(
                new Clock(),
                delegate { return new ScreenshotUsage(2, 100); },
                budget);
            Capture(ledger, "open_shop", "ok", 1, 1, 0);
            Capture(ledger, "choose_dialogue", "choice_disabled", 2, 1, 0);

            JObject payload = JObject.Parse(ScreenshotJson.ListResult(ledger, 20));

            Assert.True((bool)payload["ok"], "list ok");
            Assert.Equal(4, (int)payload["total_count"], "total_count");
            Assert.Equal(2148L, (long)payload["total_bytes"], "total_bytes");
            Assert.Equal(budget, (long)payload["budget_bytes"], "budget_bytes");
            Assert.Equal(0, (int)payload["dropped"], "dropped");
            Assert.False((bool)payload["budget_exhausted"], "budget_exhausted");

            JArray screenshots = (JArray)payload["screenshots"];
            Assert.Equal(2, screenshots.Count, "recent screenshots");
            Assert.Equal("choose_dialogue", (string)screenshots[0]["op"], "newest first");
            Assert.Equal("choice_disabled", (string)screenshots[0]["outcome"], "outcome");
            Assert.NotNull(screenshots[0]["path"], "path");
            Assert.NotNull(screenshots[0]["timestamp"], "timestamp");
            Assert.NotNull(screenshots[0]["rev"], "rev");
            Assert.NotNull(screenshots[0]["day"], "day");
            Assert.NotNull(screenshots[0]["cash"], "cash");
        }

        [Test]
        public static void Screenshot_Json_List_Unavailable_Is_Stable()
        {
            JObject payload = JObject.Parse(ScreenshotJson.ListResult(null, 20));

            Assert.False((bool)payload["ok"], "unavailable list is not ok");
            Assert.Equal("screenshot_unavailable", (string)payload["error"], "stable error");
        }

        [Test]
        public static void Failure_Evidence_Classifies_Hard_Failures_Only()
        {
            string[] hard =
            {
                "outcome_unknown",
                "state_unavailable",
                "readback_mismatch",
                "native_error",
                "exception",
                "exception:NullReferenceException",
                "bad_response",
                "recording_locked",
                "recording_outcome_unknown",
                "recording_prompt_readback_mismatch",
                "version_gate_unconfigured",
                "version_identity_unavailable",
                "game_version_mismatch",
                "game_assembly_mvid_mismatch"
            };
            string[] soft =
            {
                "cannot_afford",
                "choice_disabled",
                "option_locked",
                "stale_rev",
                "missing_rev",
                "cannot_report",
                "no_client",
                "not_fillable",
                "unknown_op",
                "screenshot_throttled",
                "screenshot_queue_full",
                "screenshot_budget_exhausted"
            };

            for (int i = 0; i < hard.Length; i++)
                Assert.True(FailureEvidence.IsHardFailure(hard[i]), "hard failure: " + hard[i]);
            for (int i = 0; i < soft.Length; i++)
                Assert.False(FailureEvidence.IsHardFailure(soft[i]), "not a hard failure: " + soft[i]);
            Assert.False(FailureEvidence.IsHardFailure(null), "null is not a hard failure");
            Assert.False(FailureEvidence.IsHardFailure(""), "empty is not a hard failure");
        }
    }
}
