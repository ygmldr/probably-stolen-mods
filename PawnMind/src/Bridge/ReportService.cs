using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Read-only morning-report orchestration. The game adapter decides which
    /// source (pending night logs, the start-of-day panel, or its day-scoped
    /// cache) is readable; this class only turns the Core projection into the
    /// stable response shapes used by get_day_report, /state and sleep.
    /// </summary>
    public sealed class ReportService
    {
        public const string MorningPending = "morning_pending";

        private static ReportService current;

        private readonly IDayReportPort port;

        public ReportService(IDayReportPort port)
        {
            this.port = port;
        }

        public static ReportService Current
        {
            get { return current; }
        }

        public static void SetCurrent(ReportService service)
        {
            current = service;
        }

        public DayReport Read(int maxLines)
        {
            DayReportRaw raw = null;
            if (port != null)
            {
                try
                {
                    raw = port.ReadDayReport();
                }
                catch (Exception)
                {
                    raw = new DayReportRaw
                    {
                        Readable = false,
                        Error = DayReportErrors.Unavailable
                    };
                }
            }

            if (raw == null)
            {
                raw = new DayReportRaw
                {
                    Readable = false,
                    Error = DayReportErrors.Unavailable
                };
            }

            return DayReportRules.Build(raw, maxLines);
        }

        public string Json(int maxLines)
        {
            DayReport report = Read(maxLines);

            var row = new JObject();
            row["ok"] = true;
            row["readable"] = report.Readable;
            row["day"] = report.Day;
            row["source"] = report.Source;
            row["pending"] = report.Pending;
            row["count"] = report.Lines.Count;
            row["total"] = report.Total;
            row["lines"] = LinesJson(report.Lines);
            row["error"] = report.Readable
                ? JValue.CreateNull()
                : new JValue(report.Error);
            return row.ToString(Formatting.None);
        }

        /// <summary>Compact slice for /state; unreadable is exactly {readable:false}.</summary>
        public Dictionary<string, object> State()
        {
            DayReport report = Read(DayReportRules.StateLines);
            if (!report.Readable)
            {
                return new Dictionary<string, object> { { "readable", false } };
            }

            var lines = new List<string>();
            for (int i = 0; i < report.Lines.Count; i++) lines.Add(report.Lines[i].Text);

            return new Dictionary<string, object>
            {
                { "readable", true },
                { "day", report.Day },
                { "source", report.Source },
                { "lines", lines }
            };
        }

        /// <summary>
        /// The sleep response is written before the game's asynchronous black
        /// screen has advanced the day, so a report is only attached when the
        /// readback already shows the new day.
        /// </summary>
        public Dictionary<string, object> SleepReport(int dayBefore, int dayAfter)
        {
            if (dayAfter <= dayBefore)
            {
                return Pending(dayBefore);
            }

            DayReport report = Read(DayReportRules.StateLines);
            if (!report.Readable)
            {
                return new Dictionary<string, object>
                {
                    { "readable", false },
                    { "reason", report.Error },
                    { "day", dayAfter },
                    { "lines", new List<string>() }
                };
            }

            if (report.Day != dayAfter)
            {
                return Pending(dayAfter);
            }

            var lines = new List<string>();
            for (int i = 0; i < report.Lines.Count; i++) lines.Add(report.Lines[i].Text);

            return new Dictionary<string, object>
            {
                { "readable", true },
                { "day", report.Day },
                { "source", report.Source },
                { "pending", report.Pending },
                { "matches_day", true },
                { "lines", lines }
            };
        }

        private static Dictionary<string, object> Pending(int day)
        {
            return new Dictionary<string, object>
            {
                { "readable", false },
                { "reason", MorningPending },
                { "day", day },
                { "lines", new List<string>() }
            };
        }

        private static JArray LinesJson(List<DayReportLine> lines)
        {
            var rows = new JArray();
            for (int i = 0; i < lines.Count; i++)
            {
                DayReportLine line = lines[i];
                var row = new JObject();
                row["text"] = line.Text;
                row["color"] = string.IsNullOrEmpty(line.Color)
                    ? JValue.CreateNull()
                    : new JValue(line.Color);
                row["tone"] = line.Tone;
                rows.Add(row);
            }

            return rows;
        }
    }
}
