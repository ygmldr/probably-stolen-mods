using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// Store calendar projection. The game's calendar UI derives the current
    /// week from StoreStation.dayCounter exactly as <see cref="CalendarRules.WeekStart"/>
    /// does, and asks StoreEventManager.GetActiveEventOnDay for each visible
    /// day. Rent day itself is queued by AdvCalendarUIManager.HandleInnateEvent,
    /// which mutates the event manager, so this read-only projection derives the
    /// rent day from the same day counter arithmetic instead of opening the UI.
    /// </summary>
    public static class CalendarErrors
    {
        public const string Unavailable = "calendar_unavailable";
    }

    public sealed class CalendarEventRaw
    {
        public string Id;
        public string Name;
        public string Type;
        public string Area;
        public int StartDay;
        public int CurrentDay;
        public int TotalDuration;
        public int Importance;
        public bool Known;
    }

    public sealed class CalendarDayRaw
    {
        public int Day;
        public List<CalendarEventRaw> Events = new List<CalendarEventRaw>();
    }

    public sealed class CalendarRaw
    {
        public bool Readable;
        public string Error;
        public int Day;
        public int RentDaysLeft;
        public int RentValue;
        public bool RentEnabled;
        public bool PayingMortgage;
        public bool PropertyPaid;
        public List<CalendarDayRaw> Days = new List<CalendarDayRaw>();
    }

    public sealed class CalendarEventView
    {
        public string Id;
        public string Name;
        public string Type;
        public string Area;
        public int StartDay;
        public int CurrentDay;
        public int TotalDuration;
        public int Importance;
        public int DayInEvent;
        public string Part;
        public bool Known;
    }

    public sealed class CalendarDayView
    {
        public int Day;
        public bool Today;
        public bool RentDue;
        public List<CalendarEventView> Events = new List<CalendarEventView>();
    }

    public sealed class CalendarWeek
    {
        public bool Readable;
        public string Error;
        public int Day;
        public int Days;
        public int RequestedDays;
        public bool Clamped;
        public int WeekStart;
        public int WeekEnd;
        public int NextRentDay;
        public int RentDaysLeft;
        public int RentValue;
        public bool RentEnabled;
        public bool PayingMortgage;
        public bool PropertyPaid;
        public bool RentDueThisWeek;
        public List<CalendarDayView> DayList = new List<CalendarDayView>();
    }

    public interface ICalendarPort
    {
        /// <summary>
        /// Reads the day counter, rent state and GetActiveEventOnDay for the
        /// requested number of days. Must never open or refresh the calendar UI.
        /// </summary>
        CalendarRaw ReadCalendar(int days);
    }

    public static class CalendarRules
    {
        public const int DefaultDays = 7;
        public const int MaxDays = 14;

        private const int MaxScanEvents = 200;

        public static int ResolveDays(int requested)
        {
            if (requested <= 0) return DefaultDays;
            return requested > MaxDays ? MaxDays : requested;
        }

        public static bool IsClamped(int requested)
        {
            return requested > MaxDays;
        }

        /// <summary>Same arithmetic as AdvCalendarUIManager.OpenUI.</summary>
        public static int WeekStart(int day)
        {
            if (day <= 0) return 0;

            int weekday = day % 7;
            if (weekday == 0) weekday = 7;
            return day - weekday + 1;
        }

        /// <summary>
        /// Next rent day (0 when the day counter is unknown). The game fires
        /// rent_day when dayCounter % 7 == 0, so a rent day returns itself.
        /// </summary>
        public static int NextRentDay(int day)
        {
            if (day <= 0) return 0;

            return day + (7 - day % 7) % 7;
        }

        public static string EventTypeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            // StoreEvent.EventType.NORMALE is spelled with the game's legacy
            // enum name; expose the stable English token instead.
            switch (raw.Trim().ToUpperInvariant())
            {
                case "NORMALE": return "normal";
                case "THREAT": return "threat";
                case "COSMETIC": return "cosmetic";
                case "INNATE": return "innate";
                default: return raw.Trim().ToLowerInvariant();
            }
        }

        public static string EventAreaName(string raw)
        {
            return NormalizeEnumName(raw);
        }

        public static string PartOf(int startDay, int totalDuration, int day)
        {
            if (totalDuration < 1) return "outside";
            if (day < startDay || day > startDay + totalDuration - 1) return "outside";
            if (totalDuration == 1) return "single";
            if (day == startDay) return "first";
            if (day == startDay + totalDuration - 1) return "last";
            return "middle";
        }

        public static CalendarWeek Build(CalendarRaw raw, int requestedDays)
        {
            int requested = requestedDays;
            int days = ResolveDays(requested);
            bool clamped = IsClamped(requested);

            if (raw == null || !raw.Readable || raw.Day <= 0)
            {
                return new CalendarWeek
                {
                    Readable = false,
                    Error = raw == null || string.IsNullOrEmpty(raw.Error)
                        ? CalendarErrors.Unavailable
                        : raw.Error,
                    Day = raw == null ? 0 : raw.Day,
                    Days = 0,
                    RequestedDays = requested,
                    Clamped = clamped
                };
            }

            int today = raw.Day;
            int weekStart = WeekStart(today);
            int nextRentDay = NextRentDay(today);
            bool rentDueThisWeek = raw.RentEnabled
                && nextRentDay >= today
                && nextRentDay <= weekStart + 6;

            var week = new CalendarWeek
            {
                Readable = true,
                Day = today,
                Days = days,
                RequestedDays = requested,
                Clamped = clamped,
                WeekStart = weekStart,
                WeekEnd = weekStart + 6,
                NextRentDay = nextRentDay,
                RentDaysLeft = raw.RentDaysLeft < 0 ? 0 : raw.RentDaysLeft,
                RentValue = raw.RentValue,
                RentEnabled = raw.RentEnabled,
                PayingMortgage = raw.PayingMortgage,
                PropertyPaid = raw.PropertyPaid,
                RentDueThisWeek = rentDueThisWeek
            };

            Dictionary<int, CalendarDayRaw> byDay = IndexDays(raw.Days);
            for (int i = 0; i < days; i++)
            {
                int day = today + i;
                CalendarDayRaw dayRaw;
                byDay.TryGetValue(day, out dayRaw);

                week.DayList.Add(new CalendarDayView
                {
                    Day = day,
                    Today = day == today,
                    // Rent fires on every dayCounter % 7 == 0, not only the
                    // nearest one, so a two-week range flags both rent days.
                    RentDue = raw.RentEnabled && day % 7 == 0,
                    Events = BuildEvents(dayRaw, day)
                });
            }

            return week;
        }

        private static Dictionary<int, CalendarDayRaw> IndexDays(List<CalendarDayRaw> days)
        {
            var index = new Dictionary<int, CalendarDayRaw>();
            if (days == null) return index;

            int scanned = 0;
            for (int i = 0; i < days.Count; i++)
            {
                if (++scanned > MaxScanEvents) break;
                CalendarDayRaw day = days[i];
                if (day == null || day.Day <= 0 || index.ContainsKey(day.Day)) continue;
                index[day.Day] = day;
            }

            return index;
        }

        private static List<CalendarEventView> BuildEvents(CalendarDayRaw dayRaw, int day)
        {
            var views = new List<CalendarEventView>();
            if (dayRaw == null || dayRaw.Events == null) return views;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int scanned = 0;
            for (int i = 0; i < dayRaw.Events.Count; i++)
            {
                if (++scanned > MaxScanEvents) break;

                CalendarEventRaw ev = dayRaw.Events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Id) || ev.Id.Trim().Length == 0) continue;
                if (!seen.Add(ev.Id)) continue;

                int duration = ev.TotalDuration < 1 ? 1 : ev.TotalDuration;
                views.Add(new CalendarEventView
                {
                    Id = ev.Id,
                    Name = string.IsNullOrEmpty(ev.Name) ? ev.Id : ev.Name,
                    Type = EventTypeName(ev.Type),
                    Area = EventAreaName(ev.Area),
                    StartDay = ev.StartDay,
                    CurrentDay = ev.CurrentDay,
                    TotalDuration = duration,
                    Importance = ev.Importance,
                    DayInEvent = day - ev.StartDay + 1,
                    Part = PartOf(ev.StartDay, duration, day),
                    Known = ev.Known
                });
            }

            views.Sort(CompareEvents);
            return views;
        }

        private static int CompareEvents(CalendarEventView left, CalendarEventView right)
        {
            int byImportance = right.Importance.CompareTo(left.Importance);
            if (byImportance != 0) return byImportance;

            int byStart = left.StartDay.CompareTo(right.StartDay);
            if (byStart != 0) return byStart;

            return string.CompareOrdinal(left.Id, right.Id);
        }

        private static string NormalizeEnumName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return raw.Trim().ToLowerInvariant();
        }
    }
}
