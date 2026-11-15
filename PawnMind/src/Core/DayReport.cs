using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// The morning report is the player-visible list of things that happened
    /// overnight ("power restored", "the storefront was not cleaned", ...).
    /// The game stores these as strings in PlayerStore.nightLogs and then
    /// instantiates one TextMeshProUGUI per line in the start-of-day panel,
    /// clearing the list afterwards. This type names the two read paths
    /// explicitly so a caller can tell a live structured line from a line
    /// scraped off the panel.
    /// </summary>
    public static class DayReportErrors
    {
        public const string Unavailable = "report_unavailable";
    }

    public sealed class DayReportLine
    {
        public string Text;
        public string Color;
        public string Tone;
    }

    /// <summary>Raw port projection. Filled by the game adapter on the main thread.</summary>
    public sealed class DayReportRaw
    {
        public bool Readable;
        public string Error;
        public int Day;
        public List<string> NightLogs = new List<string>();
        public bool PanelReadable;
        public bool PanelOpen;
        public List<string> PanelLines = new List<string>();
        public int CachedDay = -1;
        public List<string> CachedLines = new List<string>();
    }

    public sealed class DayReport
    {
        public bool Readable;
        public string Error;
        public int Day;
        public string Source = DayReportRules.SourceNone;
        public bool Pending;
        public int Total;
        public List<DayReportLine> Lines = new List<DayReportLine>();
    }

    public interface IDayReportPort
    {
        /// <summary>Read-only; never call the game's ShowMorningReport/InitPanel.</summary>
        DayReportRaw ReadDayReport();
    }

    public static class DayReportRules
    {
        public const string SourceNone = "none";
        public const string SourceNightLogs = "night_logs";
        public const string SourceMorningPanel = "morning_panel";
        public const string SourceCache = "cache";

        /// <summary>Hard cap for get_day_report; /state uses a smaller slice.</summary>
        public const int MaxLines = 20;

        /// <summary>Compact slice used by /state and the sleep response.</summary>
        public const int StateLines = 6;

        public const int MaxLineChars = 240;

        /// <summary>Bounds pathological panel/log inputs without hiding normal nights.</summary>
        public const int MaxScanLines = 200;

        public static DayReportLine ParseLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string text = raw.Trim();
            if (text.Length == 0) return null;

            string color = null;
            if (text.StartsWith("<color=#", StringComparison.OrdinalIgnoreCase))
            {
                int tagEnd = text.IndexOf('>');
                if (tagEnd > 0)
                {
                    string candidate = text
                        .Substring("<color=#".Length, tagEnd - "<color=#".Length)
                        .Trim();
                    if (IsHexColor(candidate)) color = candidate.ToLowerInvariant();
                    text = text.Substring(tagEnd + 1);
                }
            }

            // The game closes the rich-text tag explicitly. Strip it whether or
            // not the color was valid so the caller only ever sees display text.
            if (text.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - "</color>".Length);
            }

            text = CollapseWhitespace(text);
            if (text.Length == 0) return null;
            if (text.Length > MaxLineChars)
            {
                text = text.Substring(0, MaxLineChars - 3) + "...";
            }

            return new DayReportLine
            {
                Text = text,
                Color = color,
                Tone = ToneForColor(color)
            };
        }

        public static List<DayReportLine> ParseLines(IEnumerable<string> raw, int maxLines)
        {
            List<DayReportLine> all = ParseAll(raw);
            int budget = ClampBudget(maxLines);
            if (all.Count > budget) all.RemoveRange(budget, all.Count - budget);
            return all;
        }

        public static string ToneForColor(string color)
        {
            if (string.IsNullOrEmpty(color)) return "neutral";

            switch (color.Trim().TrimStart('#').ToLowerInvariant())
            {
                case "5c6f46": return "positive";
                case "cc7755": return "warning";
                case "9c3738": return "negative";
                default: return "neutral";
            }
        }

        public static DayReport Build(DayReportRaw raw, int maxLines)
        {
            if (raw == null) return Unreadable(0, DayReportErrors.Unavailable);
            if (!raw.Readable)
            {
                return Unreadable(raw.Day, string.IsNullOrEmpty(raw.Error)
                    ? DayReportErrors.Unavailable
                    : raw.Error);
            }

            int budget = ClampBudget(maxLines);
            int day = raw.Day;

            List<DayReportLine> pending = ParseAll(raw.NightLogs);
            if (pending.Count > 0)
            {
                return Report(day, SourceNightLogs, !raw.PanelOpen, pending, budget);
            }

            if (raw.PanelReadable)
            {
                return Report(day, SourceMorningPanel, false, ParseAll(raw.PanelLines), budget);
            }

            if (raw.CachedDay == day)
            {
                List<DayReportLine> cached = ParseAll(raw.CachedLines);
                if (cached.Count > 0) return Report(day, SourceCache, false, cached, budget);
            }

            return Unreadable(day, string.IsNullOrEmpty(raw.Error)
                ? DayReportErrors.Unavailable
                : raw.Error);
        }

        private static List<DayReportLine> ParseAll(IEnumerable<string> raw)
        {
            var lines = new List<DayReportLine>();
            if (raw == null) return lines;

            int scanned = 0;
            foreach (string value in raw)
            {
                if (++scanned > MaxScanLines) break;
                DayReportLine line = ParseLine(value);
                if (line != null) lines.Add(line);
            }

            return lines;
        }

        private static int ClampBudget(int maxLines)
        {
            if (maxLines <= 0) return MaxLines;
            return maxLines > MaxLines ? MaxLines : maxLines;
        }

        private static DayReport Report(
            int day,
            string source,
            bool pending,
            List<DayReportLine> lines,
            int budget)
        {
            var report = new DayReport
            {
                Readable = true,
                Day = day,
                Source = source,
                Pending = pending,
                Total = lines.Count
            };

            if (lines.Count > budget) lines.RemoveRange(budget, lines.Count - budget);
            report.Lines = lines;
            return report;
        }

        private static DayReport Unreadable(int day, string error)
        {
            return new DayReport
            {
                Readable = false,
                Error = string.IsNullOrEmpty(error) ? DayReportErrors.Unavailable : error,
                Day = day,
                Source = SourceNone
            };
        }

        private static bool IsHexColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string hex = value.TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) return false;

            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper) return false;
            }

            return true;
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
