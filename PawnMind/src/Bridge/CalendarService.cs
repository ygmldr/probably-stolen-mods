using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Read-only calendar orchestration. Day ranges are always derived from the
    /// live day counter by CalendarRules; nothing here opens the calendar UI
    /// (which would queue rent-day events as a side effect).
    /// </summary>
    public sealed class CalendarService
    {
        private const int StateLookaheadDays = 7;
        private const int StateItemLimit = 3;

        private static CalendarService current;

        private readonly ICalendarPort port;

        public CalendarService(ICalendarPort port)
        {
            this.port = port;
        }

        public static CalendarService Current
        {
            get { return current; }
        }

        public static void SetCurrent(CalendarService service)
        {
            current = service;
        }

        public CalendarWeek Read(int requestedDays)
        {
            int days = CalendarRules.ResolveDays(requestedDays);
            CalendarRaw raw = null;

            if (port != null)
            {
                try
                {
                    raw = port.ReadCalendar(days);
                }
                catch (Exception)
                {
                    raw = new CalendarRaw
                    {
                        Readable = false,
                        Error = CalendarErrors.Unavailable
                    };
                }
            }

            if (raw == null)
            {
                raw = new CalendarRaw
                {
                    Readable = false,
                    Error = CalendarErrors.Unavailable
                };
            }

            return CalendarRules.Build(raw, requestedDays);
        }

        public string Json(int requestedDays)
        {
            CalendarWeek week = Read(requestedDays);

            var row = new JObject();
            row["ok"] = true;
            row["readable"] = week.Readable;
            row["day"] = week.Day;
            row["requested_days"] = week.RequestedDays;
            row["days"] = week.Days;
            row["clamped"] = week.Clamped;
            row["error"] = week.Readable ? JValue.CreateNull() : new JValue(week.Error);

            var weekRow = new JObject();
            weekRow["start"] = week.WeekStart;
            weekRow["end"] = week.WeekEnd;
            row["week"] = weekRow;

            var rent = new JObject();
            rent["enabled"] = week.RentEnabled;
            rent["days_left"] = week.RentDaysLeft;
            rent["next_day"] = week.NextRentDay;
            rent["amount"] = week.RentValue;
            rent["due_this_week"] = week.RentDueThisWeek;
            rent["paying_mortgage"] = week.PayingMortgage;
            rent["property_paid"] = week.PropertyPaid;
            row["rent"] = rent;

            var days = new JArray();
            for (int i = 0; i < week.DayList.Count; i++)
            {
                CalendarDayView day = week.DayList[i];
                var dayRow = new JObject();
                dayRow["day"] = day.Day;
                dayRow["today"] = day.Today;
                dayRow["rent_due"] = day.RentDue;
                dayRow["events"] = EventsJson(day.Events);
                days.Add(dayRow);
            }

            row["day_rows"] = days;
            row["count"] = days.Count;
            return row.ToString(Formatting.None);
        }

        /// <summary>
        /// Compact /state projection: the next 1-3 event/rent days. Any failure
        /// degrades to {readable:false}; it never throws into the state builder.
        /// </summary>
        public Dictionary<string, object> StateNext()
        {
            CalendarWeek week;
            try
            {
                week = Read(StateLookaheadDays);
            }
            catch (Exception)
            {
                return new Dictionary<string, object> { { "readable", false } };
            }

            if (week == null || !week.Readable)
            {
                return new Dictionary<string, object> { { "readable", false } };
            }

            var items = new List<Dictionary<string, object>>();
            var seenEvents = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < week.DayList.Count; i++)
            {
                CalendarDayView day = week.DayList[i];
                for (int e = 0; e < day.Events.Count; e++)
                {
                    CalendarEventView ev = day.Events[e];
                    if (!seenEvents.Add(ev.Id)) continue;

                    items.Add(new Dictionary<string, object>
                    {
                        { "kind", "event" },
                        { "id", ev.Id },
                        { "name", ev.Name },
                        { "type", ev.Type },
                        { "day", day.Day },
                        { "days_left", day.Day - week.Day },
                        { "start_day", ev.StartDay },
                        { "total_duration", ev.TotalDuration },
                        { "importance", ev.Importance },
                        { "part", ev.Part }
                    });
                }
            }

            if (week.RentEnabled
                && week.NextRentDay >= week.Day
                && !seenEvents.Contains("rent_day"))
            {
                items.Add(new Dictionary<string, object>
                {
                    { "kind", "rent" },
                    { "id", "rent_day" },
                    { "day", week.NextRentDay },
                    { "days_left", week.NextRentDay - week.Day },
                    { "amount", week.RentValue }
                });
            }

            items.Sort(CompareItems);
            if (items.Count > StateItemLimit)
            {
                items.RemoveRange(StateItemLimit, items.Count - StateItemLimit);
            }

            return new Dictionary<string, object>
            {
                { "readable", true },
                { "day", week.Day },
                { "count", items.Count },
                { "items", items }
            };
        }

        private static int CompareItems(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            int byDay = ((int)left["day"]).CompareTo((int)right["day"]);
            if (byDay != 0) return byDay;

            // A rent day is the hard deadline; rank it before events on the
            // same day so a three-item slice never hides it.
            bool leftRent = object.Equals(left["kind"], "rent");
            bool rightRent = object.Equals(right["kind"], "rent");
            if (leftRent != rightRent) return leftRent ? -1 : 1;

            int leftImportance = left.ContainsKey("importance") ? (int)left["importance"] : 0;
            int rightImportance = right.ContainsKey("importance") ? (int)right["importance"] : 0;
            int byImportance = rightImportance.CompareTo(leftImportance);
            if (byImportance != 0) return byImportance;

            return string.CompareOrdinal(
                left.ContainsKey("id") ? (string)left["id"] : string.Empty,
                right.ContainsKey("id") ? (string)right["id"] : string.Empty);
        }

        private static JArray EventsJson(List<CalendarEventView> events)
        {
            var rows = new JArray();
            for (int i = 0; i < events.Count; i++)
            {
                CalendarEventView ev = events[i];
                var row = new JObject();
                row["id"] = ev.Id;
                row["name"] = ev.Name;
                row["type"] = ev.Type;
                row["area"] = ev.Area;
                row["start_day"] = ev.StartDay;
                row["current_day"] = ev.CurrentDay;
                row["total_duration"] = ev.TotalDuration;
                row["importance"] = ev.Importance;
                row["day_in_event"] = ev.DayInEvent;
                row["part"] = ev.Part;
                row["known"] = ev.Known;
                rows.Add(row);
            }

            return rows;
        }
    }
}
