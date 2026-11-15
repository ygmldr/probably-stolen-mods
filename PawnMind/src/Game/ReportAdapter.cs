using System;
using System.Collections.Generic;
using TMPro;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Read-only morning report adapter. The structured source is
    /// PlayerStore.nightLogs; the game consumes it in
    /// StartOfDayUIManager.InitPanel, which instantiates one TMP line per log
    /// and then clears the list. This adapter therefore also reads the visible
    /// start-of-day lines and keeps a day-scoped copy so a report remains
    /// observable after the panel consumed the source list.
    ///
    /// Only reads: it never opens the calendar or calls ShowMorningReport,
    /// InitPanel, EndDay or HandleInnateEvent.
    /// </summary>
    internal sealed class ReportAdapter : IDayReportPort
    {
        public static readonly ReportAdapter Instance = new ReportAdapter();

        private static int _cachedDay = -1;
        private static List<string> _cachedLines = new List<string>();

        public DayReportRaw ReadDayReport()
        {
            var raw = new DayReportRaw();
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    ClearCache();
                    raw.Error = "not_in_game";
                    return raw;
                }

                PlayerStore store = PlayerStore.Instance;
                StoreStation station = StoreStation.Instance;
                if (store == null || station == null)
                {
                    ClearCache();
                    raw.Error = DayReportErrors.Unavailable;
                    return raw;
                }

                raw.Readable = true;
                raw.Day = station.dayCounter;
                raw.NightLogs = CopyStrings(store.nightLogs);

                StartOfDayUIManager ui = StartOfDayUIManager.Instance;
                if (ui != null && ui.contentGroupObject != null)
                {
                    raw.PanelReadable = true;
                    raw.PanelOpen = ui.UIPanel != null && ui.UIPanel.activeSelf;
                    raw.PanelLines = ReadPanelLines(ui.contentGroupObject);
                }

                if (raw.NightLogs.Count > 0)
                {
                    Cache(raw.Day, raw.NightLogs);
                }
                else if (_cachedDay == raw.Day)
                {
                    raw.CachedDay = _cachedDay;
                    raw.CachedLines = new List<string>(_cachedLines);
                }
                else if (_cachedDay > raw.Day)
                {
                    ClearCache();
                }
            }
            catch (Exception)
            {
                raw = new DayReportRaw
                {
                    Readable = false,
                    Error = DayReportErrors.Unavailable
                };
            }

            return raw;
        }

        private static List<string> CopyStrings(List<string> source)
        {
            var copy = new List<string>();
            if (source == null) return copy;

            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null) continue;
                copy.Add(source[i]);
            }

            return copy;
        }

        /// <summary>
        /// StartOfDayUIManager.InitPanel instantiates the line prefabs directly
        /// under contentGroupObject and destroys them on the next refresh.
        /// Reading the direct children keeps unrelated labels out.
        /// </summary>
        private static List<string> ReadPanelLines(UnityEngine.GameObject contentGroup)
        {
            var lines = new List<string>();
            if (contentGroup == null) return lines;

            UnityEngine.Transform transform = contentGroup.transform;
            if (transform == null) return lines;

            for (int i = 0; i < transform.childCount; i++)
            {
                UnityEngine.Transform child = transform.GetChild(i);
                if (child == null) continue;

                TextMeshProUGUI text = child.GetComponent<TextMeshProUGUI>();
                if (text == null || string.IsNullOrEmpty(text.text)) continue;
                lines.Add(text.text);
            }

            return lines;
        }

        private static void Cache(int day, List<string> lines)
        {
            _cachedDay = day;
            _cachedLines = lines == null ? new List<string>() : new List<string>(lines);
        }

        private static void ClearCache()
        {
            _cachedDay = -1;
            _cachedLines = new List<string>();
        }
    }
}
