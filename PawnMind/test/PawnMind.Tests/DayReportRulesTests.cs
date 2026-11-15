using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class DayReportRulesTests
    {
        private static DayReportRaw Raw(int day, params string[] logs)
        {
            return new DayReportRaw
            {
                Readable = true,
                Day = day,
                NightLogs = new List<string>(logs)
            };
        }

        private static DayReportRaw Panel(int day, params string[] lines)
        {
            return new DayReportRaw
            {
                Readable = true,
                Day = day,
                PanelReadable = true,
                PanelOpen = true,
                PanelLines = new List<string>(lines)
            };
        }

        [Test]
        public static void ParseLine_Plain_Text_Is_Neutral()
        {
            DayReportLine line = DayReportRules.ParseLine("Power restored.");

            Assert.NotNull(line, "plain text parses");
            Assert.Equal("Power restored.", line.Text, "text preserved");
            Assert.Null(line.Color, "no color tag");
            Assert.Equal("neutral", line.Tone, "plain text is neutral");
        }

        [Test]
        public static void ParseLine_Strips_Color_Tags_And_Classifies_Tone()
        {
            DayReportLine warning = DayReportRules.ParseLine(
                "<color=#CC7755>The storefront was not cleaned.</color>");
            Assert.Equal("The storefront was not cleaned.", warning.Text, "color markup removed");
            Assert.Equal("cc7755", warning.Color, "color normalized to lower case");
            Assert.Equal("warning", warning.Tone, "amber is a warning");

            Assert.Equal("positive",
                DayReportRules.ParseLine("<color=#5c6f46>Power restored.</color>").Tone,
                "green is positive");
            Assert.Equal("negative",
                DayReportRules.ParseLine("<color=#9c3738>Burglars took a crate.</color>").Tone,
                "red is negative");
            Assert.Equal("neutral",
                DayReportRules.ParseLine("<color=#123456>Something happened.</color>").Tone,
                "unknown colors stay neutral");
        }

        [Test]
        public static void ParseLine_Handles_Malformed_Color_Markup()
        {
            DayReportLine line = DayReportRules.ParseLine("<color=#zzzzzz>Odd markup.</color>");

            Assert.Equal("Odd markup.", line.Text, "tag is stripped even when the color is invalid");
            Assert.Null(line.Color, "invalid colors are not reported");
            Assert.Equal("neutral", line.Tone, "invalid color is neutral");
        }

        [Test]
        public static void ParseLine_Rejects_Blank_Input()
        {
            Assert.Null(DayReportRules.ParseLine(null), "null input");
            Assert.Null(DayReportRules.ParseLine("   "), "whitespace input");
            Assert.Null(DayReportRules.ParseLine("<color=#5c6f46></color>"), "empty colored text");
        }

        [Test]
        public static void ParseLine_Collapses_Whitespace_And_Truncates()
        {
            DayReportLine line = DayReportRules.ParseLine("  Power\n\nrestored\tat dawn. ");
            Assert.Equal("Power restored at dawn.", line.Text, "control characters collapse");

            string longText = new string('x', 400);
            DayReportLine truncated = DayReportRules.ParseLine(longText);
            Assert.Equal(DayReportRules.MaxLineChars, truncated.Text.Length, "line is capped");
            Assert.True(truncated.Text.EndsWith("..."), "truncated lines end with an ellipsis");
        }

        [Test]
        public static void Build_Prefers_Pending_Night_Logs()
        {
            DayReport report = DayReportRules.Build(Raw(9, "Power restored.", "Rent is due tomorrow."), 20);

            Assert.True(report.Readable, "pending logs are readable");
            Assert.Equal(DayReportRules.SourceNightLogs, report.Source, "structured source wins");
            Assert.True(report.Pending, "pending logs are not confirmed on the panel yet");
            Assert.Equal(2, report.Total, "total line count");
            Assert.Equal(2, report.Lines.Count, "lines are exposed");
            Assert.Equal(9, report.Day, "day preserved");
        }

        [Test]
        public static void Build_Uses_The_Morning_Panel_When_Logs_Were_Consumed()
        {
            DayReport report = DayReportRules.Build(Panel(9, "-Power restored.", "-Rent is due tomorrow."), 20);

            Assert.True(report.Readable, "panel lines are readable");
            Assert.Equal(DayReportRules.SourceMorningPanel, report.Source, "panel fallback source");
            Assert.False(report.Pending, "panel lines are already displayed");
            Assert.Equal(2, report.Total, "total lines");
            Assert.Equal("-Power restored.", report.Lines[0].Text, "text preserved");
        }

        [Test]
        public static void Build_Uses_The_Cache_When_The_Panel_Is_Gone()
        {
            var raw = Raw(9);
            raw.PanelReadable = false;
            raw.CachedDay = 9;
            raw.CachedLines = new List<string> { "Power restored." };

            DayReport report = DayReportRules.Build(raw, 20);

            Assert.True(report.Readable, "cache is readable");
            Assert.Equal(DayReportRules.SourceCache, report.Source, "cache source");
            Assert.Equal("Power restored.", report.Lines[0].Text, "cached text");
        }

        [Test]
        public static void Build_Ignores_The_Cache_From_Another_Day()
        {
            var raw = Raw(10);
            raw.PanelReadable = false;
            raw.CachedDay = 9;
            raw.CachedLines = new List<string> { "Power restored." };

            DayReport report = DayReportRules.Build(raw, 20);

            Assert.False(report.Readable, "stale cache must not be reported");
            Assert.Equal(DayReportRules.SourceNone, report.Source, "no source");
            Assert.Equal(DayReportErrors.Unavailable, report.Error, "stable error");
        }

        [Test]
        public static void Build_Quiet_Night_Is_A_Readable_Empty_Report()
        {
            DayReport report = DayReportRules.Build(Panel(10), 6);

            Assert.True(report.Readable, "an empty morning report is still readable");
            Assert.Equal(DayReportRules.SourceMorningPanel, report.Source, "panel source");
            Assert.Equal(0, report.Total, "no lines");
            Assert.Equal(0, report.Lines.Count, "no lines exposed");
        }

        [Test]
        public static void Build_Unreadable_Raw_Is_Fail_Closed()
        {
            var raw = new DayReportRaw { Readable = false, Error = "not_in_game", Day = 7 };
            DayReport report = DayReportRules.Build(raw, 6);

            Assert.False(report.Readable, "unreadable raw");
            Assert.Equal("not_in_game", report.Error, "adapter error is preserved");
            Assert.Equal(DayReportRules.SourceNone, report.Source, "no source");
            Assert.Equal(7, report.Day, "day still reported");
        }

        [Test]
        public static void Build_Null_Raw_Is_Unreadable()
        {
            DayReport report = DayReportRules.Build(null, 6);

            Assert.False(report.Readable, "null raw");
            Assert.Equal(DayReportErrors.Unavailable, report.Error, "stable error");
        }

        [Test]
        public static void Build_Truncates_But_Reports_The_Real_Total()
        {
            string[] logs = new string[25];
            for (int i = 0; i < logs.Length; i++) logs[i] = "log " + i;

            DayReport report = DayReportRules.Build(Raw(3, logs), 20);

            Assert.Equal(25, report.Total, "total counts every parseable line");
            Assert.Equal(20, report.Lines.Count, "lines are capped");
            Assert.Equal("log 0", report.Lines[0].Text, "order preserved");
        }

        [Test]
        public static void Build_Clamps_The_Line_Budget()
        {
            string[] logs = new string[25];
            for (int i = 0; i < logs.Length; i++) logs[i] = "log " + i;

            Assert.Equal(DayReportRules.MaxLines, DayReportRules.Build(Raw(3, logs), 0).Lines.Count,
                "zero budget falls back to the hard cap");
            Assert.Equal(DayReportRules.MaxLines, DayReportRules.Build(Raw(3, logs), 999).Lines.Count,
                "huge budget is clamped to the hard cap");
        }

        [Test]
        public static void Build_Skips_Blank_Lines_And_Panel_Open_Marks_Not_Pending()
        {
            var raw = Raw(4, "Power restored.", "   ", "<color=#5c6f46></color>");
            raw.PanelOpen = true;

            DayReport report = DayReportRules.Build(raw, 6);

            Assert.Equal(1, report.Total, "blank lines are skipped");
            Assert.False(report.Pending, "an open panel means the report is displayed");
        }

        [Test]
        public static void State_Limit_Is_Six_Lines()
        {
            Assert.Equal(6, DayReportRules.StateLines, "state only shows six lines");

            string[] logs = new string[9];
            for (int i = 0; i < logs.Length; i++) logs[i] = "log " + i;
            DayReport report = DayReportRules.Build(Raw(2, logs), DayReportRules.StateLines);
            Assert.Equal(6, report.Lines.Count, "state line budget");
            Assert.Equal(9, report.Total, "state total still counts all");
        }
    }
}
