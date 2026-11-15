using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ReportServiceTests
    {
        private static JObject Json(string text)
        {
            return JObject.Parse(text);
        }

        [Test]
        public static void Json_Readable_Report_Exposes_Lines_And_Source()
        {
            var port = new FakeDayReportPort();
            port.Raw = new DayReportRaw
            {
                Readable = true,
                Day = 9,
                NightLogs = new List<string>
                {
                    "<color=#5c6f46>Power restored.</color>",
                    "The storefront was not cleaned."
                }
            };

            var service = new ReportService(port);
            JObject json = Json(service.Json(20));

            Assert.Equal(true, (bool)json["ok"], "read is ok");
            Assert.Equal(true, (bool)json["readable"], "readable");
            Assert.Equal(9, (int)json["day"], "day");
            Assert.Equal("night_logs", (string)json["source"], "source");
            Assert.Equal(true, (bool)json["pending"], "pending logs");
            Assert.Equal(2, (int)json["count"], "count");
            Assert.Equal(2, (int)json["total"], "total");
            Assert.Equal("Power restored.", (string)json["lines"][0]["text"], "line text");
            Assert.Equal("5c6f46", (string)json["lines"][0]["color"], "line color");
            Assert.Equal("positive", (string)json["lines"][0]["tone"], "line tone");
            Assert.Equal(JTokenType.Null, json["error"].Type, "no error");
            Assert.Equal(1, port.Calls, "one port read");
        }

        [Test]
        public static void Json_Unreadable_Report_Is_Not_An_Error_Status()
        {
            var port = new FakeDayReportPort();
            port.Raw = new DayReportRaw { Readable = false, Error = "not_in_game", Day = 4 };

            var service = new ReportService(port);
            JObject json = Json(service.Json(6));

            Assert.Equal(true, (bool)json["ok"], "a missing report is not a protocol error");
            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal("not_in_game", (string)json["error"], "adapter error");
            Assert.Equal(0, (int)json["count"], "no lines");
        }

        [Test]
        public static void Json_Tolerates_A_Throwing_Port()
        {
            var port = new FakeDayReportPort { Throw = true };
            var service = new ReportService(port);

            JObject json = Json(service.Json(6));

            Assert.Equal(true, (bool)json["ok"], "never throws");
            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal(DayReportErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void Json_Without_A_Port_Is_Unreadable()
        {
            var service = new ReportService(null);

            JObject json = Json(service.Json(6));

            Assert.Equal(false, (bool)json["readable"], "no port");
            Assert.Equal(DayReportErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void State_Uses_String_Lines_And_Six_Line_Budget()
        {
            var port = new FakeDayReportPort();
            var logs = new List<string>();
            for (int i = 0; i < 9; i++) logs.Add("log " + i);
            port.Raw = new DayReportRaw
            {
                Readable = true,
                Day = 2,
                PanelReadable = true,
                PanelLines = logs
            };

            var service = new ReportService(port);
            Dictionary<string, object> state = service.State();

            Assert.Equal(true, state["readable"], "readable");
            Assert.Equal(2, state["day"], "day");
            Assert.Equal("morning_panel", state["source"], "source");
            var lines = state["lines"] as List<string>;
            Assert.NotNull(lines, "state lines are plain strings");
            Assert.Equal(6, lines.Count, "state line budget");
            Assert.Equal("log 0", lines[0], "first line");
        }

        [Test]
        public static void State_Unreadable_Is_Exactly_Readable_False()
        {
            var port = new FakeDayReportPort { Throw = true };
            var service = new ReportService(port);

            Dictionary<string, object> state = service.State();

            Assert.Equal(1, state.Count, "unreadable state is minimal");
            Assert.Equal(false, state["readable"], "readable false");
        }

        [Test]
        public static void SleepReport_Is_Pending_When_The_Day_Did_Not_Advance()
        {
            var port = new FakeDayReportPort();
            port.Raw = new DayReportRaw { Readable = true, Day = 5 };
            var service = new ReportService(port);

            Dictionary<string, object> report = service.SleepReport(5, 5);

            Assert.Equal(false, report["readable"], "the async transition has not finished");
            Assert.Equal("morning_pending", report["reason"], "stable reason");
            Assert.Equal(5, report["day"], "old day");
            Assert.Equal(0, port.Calls, "no point reading before the day advances");
        }

        [Test]
        public static void SleepReport_Is_Readable_After_The_Day_Advances()
        {
            var port = new FakeDayReportPort();
            port.Raw = new DayReportRaw
            {
                Readable = true,
                Day = 6,
                NightLogs = new List<string> { "Power restored." }
            };
            var service = new ReportService(port);

            Dictionary<string, object> report = service.SleepReport(5, 6);

            Assert.Equal(true, report["readable"], "new morning report");
            Assert.Equal(6, report["day"], "new day");
            Assert.Equal(true, report["matches_day"], "report belongs to the new day");
            Assert.Equal("night_logs", report["source"], "source");
            var lines = report["lines"] as List<string>;
            Assert.NotNull(lines, "plain string lines");
            Assert.Equal(1, lines.Count, "one line");
            Assert.Equal("Power restored.", lines[0], "line text");
        }

        [Test]
        public static void SleepReport_Is_Pending_When_The_Report_Is_For_Another_Day()
        {
            var port = new FakeDayReportPort();
            port.Raw = new DayReportRaw
            {
                Readable = true,
                Day = 5,
                PanelReadable = true,
                PanelLines = new List<string> { "Old report." }
            };
            var service = new ReportService(port);

            Dictionary<string, object> report = service.SleepReport(5, 6);

            Assert.Equal(false, report["readable"], "a stale report must not be attached to the new day");
            Assert.Equal("morning_pending", report["reason"], "stable reason");
            Assert.Equal(6, report["day"], "new day");
        }

        [Test]
        public static void SleepReport_Survives_An_Unreadable_Report()
        {
            var port = new FakeDayReportPort { Throw = true };
            var service = new ReportService(port);

            Dictionary<string, object> report = service.SleepReport(5, 6);

            Assert.Equal(false, report["readable"], "unreadable");
            Assert.Equal(DayReportErrors.Unavailable, report["reason"], "stable reason");
            Assert.Equal(6, report["day"], "new day");
        }
    }
}
