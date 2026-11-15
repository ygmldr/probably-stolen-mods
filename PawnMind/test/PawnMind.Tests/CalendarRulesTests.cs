using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class CalendarRulesTests
    {
        private static CalendarEventRaw Event(
            string id,
            int startDay,
            int day,
            int totalDuration = 1,
            int importance = 0,
            string type = "NORMALE")
        {
            return new CalendarEventRaw
            {
                Id = id,
                Name = id,
                Type = type,
                Area = "ALL",
                StartDay = startDay,
                CurrentDay = day,
                TotalDuration = totalDuration,
                Importance = importance,
                Known = true
            };
        }

        private static CalendarRaw Raw(int day, int days, params CalendarEventRaw[] events)
        {
            var raw = new CalendarRaw
            {
                Readable = true,
                Day = day,
                RentEnabled = true,
                RentDaysLeft = (7 - day % 7) % 7,
                RentValue = 350
            };

            for (int i = 0; i < days; i++)
            {
                var row = new CalendarDayRaw { Day = day + i };
                foreach (CalendarEventRaw ev in events)
                {
                    int end = ev.StartDay + ev.TotalDuration - 1;
                    if (row.Day >= ev.StartDay && row.Day <= end) row.Events.Add(ev);
                }

                raw.Days.Add(row);
            }

            return raw;
        }

        [Test]
        public static void ResolveDays_Defaults_And_Caps()
        {
            Assert.Equal(CalendarRules.DefaultDays, CalendarRules.ResolveDays(0), "zero means default");
            Assert.Equal(CalendarRules.DefaultDays, CalendarRules.ResolveDays(-3), "negative means default");
            Assert.Equal(1, CalendarRules.ResolveDays(1), "one day");
            Assert.Equal(CalendarRules.MaxDays, CalendarRules.ResolveDays(CalendarRules.MaxDays), "max kept");
            Assert.Equal(CalendarRules.MaxDays, CalendarRules.ResolveDays(99), "over max clamped");
        }

        [Test]
        public static void ResolveDays_Reports_Whether_It_Clamped()
        {
            Assert.False(CalendarRules.IsClamped(7), "default is not clamped");
            Assert.False(CalendarRules.IsClamped(14), "exactly max is not clamped");
            Assert.True(CalendarRules.IsClamped(15), "over max is clamped");
        }

        [Test]
        public static void WeekStart_Matches_The_Calendar_UI()
        {
            Assert.Equal(1, CalendarRules.WeekStart(1), "day 1");
            Assert.Equal(1, CalendarRules.WeekStart(7), "day 7");
            Assert.Equal(8, CalendarRules.WeekStart(8), "day 8");
            Assert.Equal(8, CalendarRules.WeekStart(14), "day 14");
            Assert.Equal(15, CalendarRules.WeekStart(15), "day 15");
            Assert.Equal(15, CalendarRules.WeekStart(21), "day 21");
        }

        [Test]
        public static void NextRentDay_Is_Derived_From_The_Day_Counter()
        {
            Assert.Equal(7, CalendarRules.NextRentDay(1), "day 1 -> day 7");
            Assert.Equal(7, CalendarRules.NextRentDay(6), "day 6 -> day 7");
            Assert.Equal(7, CalendarRules.NextRentDay(7), "day 7 is rent day");
            Assert.Equal(14, CalendarRules.NextRentDay(8), "day 8 -> day 14");
            Assert.Equal(14, CalendarRules.NextRentDay(14), "day 14 is rent day");
            Assert.Equal(21, CalendarRules.NextRentDay(15), "day 15 -> day 21");
        }

        [Test]
        public static void EventType_And_Area_Names_Are_Stable()
        {
            Assert.Equal("normal", CalendarRules.EventTypeName("NORMALE"), "normal event");
            Assert.Equal("threat", CalendarRules.EventTypeName("THREAT"), "threat event");
            Assert.Equal("cosmetic", CalendarRules.EventTypeName("COSMETIC"), "cosmetic event");
            Assert.Equal("innate", CalendarRules.EventTypeName("INNATE"), "innate event");
            Assert.Equal("future", CalendarRules.EventTypeName("FUTURE"), "unknown types lower-case");
            Assert.Equal("", CalendarRules.EventTypeName(null), "null type");

            Assert.Equal("upper", CalendarRules.EventAreaName("UPPER"), "upper area");
            Assert.Equal("lower", CalendarRules.EventAreaName("LOWER"), "lower area");
            Assert.Equal("all", CalendarRules.EventAreaName("ALL"), "all area");
            Assert.Equal("", CalendarRules.EventAreaName(null), "null area");
        }

        [Test]
        public static void PartOf_Describes_The_Span()
        {
            Assert.Equal("single", CalendarRules.PartOf(5, 1, 5), "single-day event");
            Assert.Equal("first", CalendarRules.PartOf(5, 3, 5), "first day");
            Assert.Equal("middle", CalendarRules.PartOf(5, 3, 6), "middle day");
            Assert.Equal("last", CalendarRules.PartOf(5, 3, 7), "last day");
            Assert.Equal("outside", CalendarRules.PartOf(5, 3, 8), "outside the span");
        }

        [Test]
        public static void Build_Computes_The_Week_And_Rent_Flags()
        {
            CalendarRaw raw = Raw(12, 7);
            CalendarWeek week = CalendarRules.Build(raw, 7);

            Assert.True(week.Readable, "readable");
            Assert.Equal(12, week.Day, "today");
            Assert.Equal(7, week.Days, "seven rows");
            Assert.Equal(8, week.WeekStart, "week starts at 8");
            Assert.Equal(14, week.WeekEnd, "week ends at 14");
            Assert.Equal(14, week.NextRentDay, "next rent day");
            Assert.True(week.RentDueThisWeek, "rent day is inside this week");
            Assert.Equal(12, week.DayList[0].Day, "first row is today");
            Assert.True(week.DayList[0].Today, "today flag");
            Assert.Equal(18, week.DayList[6].Day, "last row");
            Assert.True(week.DayList[2].RentDue, "day 14 is rent day");
            Assert.False(week.DayList[1].RentDue, "day 13 is not rent day");
        }

        [Test]
        public static void Build_Allows_Crossing_Into_The_Next_Week()
        {
            CalendarWeek week = CalendarRules.Build(Raw(12, 14), 14);

            Assert.Equal(14, week.Days, "fourteen rows");
            Assert.Equal(25, week.DayList[13].Day, "range crosses the week boundary");
            Assert.Equal(8, week.WeekStart, "anchor week still reported");
            Assert.Equal(14, week.WeekEnd, "anchor week end");
            Assert.True(week.DayList[2].RentDue, "rent day in the first week");
            Assert.True(week.DayList[9].RentDue, "rent day in the next week");
        }

        [Test]
        public static void Build_Clamps_Requested_Days_And_Flags_It()
        {
            CalendarWeek week = CalendarRules.Build(Raw(12, 20), 99);

            Assert.Equal(99, week.RequestedDays, "requested value preserved");
            Assert.Equal(CalendarRules.MaxDays, week.Days, "clamped to max");
            Assert.True(week.Clamped, "clamped flag");
        }

        [Test]
        public static void Build_Sorts_And_Deduplicates_Events()
        {
            CalendarRaw raw = Raw(10, 1,
                Event("b", 10, 10, 1, 2),
                Event("a", 10, 10, 1, 2),
                Event("high", 10, 10, 1, 9),
                Event("a", 10, 10, 1, 2));

            CalendarWeek week = CalendarRules.Build(raw, 1);

            Assert.Equal(3, week.DayList[0].Events.Count, "duplicates removed");
            Assert.Equal("high", week.DayList[0].Events[0].Id, "higher importance first");
            Assert.Equal("a", week.DayList[0].Events[1].Id, "ties sort by id");
            Assert.Equal("b", week.DayList[0].Events[2].Id, "ties sort by id");
        }

        [Test]
        public static void Build_Skips_Null_And_Empty_Events()
        {
            CalendarRaw raw = Raw(10, 1);
            raw.Days[0].Events.Add(null);
            raw.Days[0].Events.Add(new CalendarEventRaw { Id = " ", Name = "x" });
            raw.Days[0].Events.Add(Event("rent_day", 10, 10, 1, 0, "INNATE"));

            CalendarWeek week = CalendarRules.Build(raw, 1);

            Assert.Equal(1, week.DayList[0].Events.Count, "only the valid event survives");
            Assert.Equal("rent_day", week.DayList[0].Events[0].Id, "valid event kept");
            Assert.Equal("innate", week.DayList[0].Events[0].Type, "type normalized");
        }

        [Test]
        public static void Build_Computes_Day_In_Event_And_Part()
        {
            CalendarRaw raw = Raw(10, 3, Event("op", 10, 10, 3, 5, "THREAT"));

            CalendarWeek week = CalendarRules.Build(raw, 3);

            CalendarEventView first = week.DayList[0].Events[0];
            CalendarEventView middle = week.DayList[1].Events[0];
            CalendarEventView last = week.DayList[2].Events[0];

            Assert.Equal(1, first.DayInEvent, "first day of the event");
            Assert.Equal("first", first.Part, "first part");
            Assert.Equal(2, middle.DayInEvent, "second day");
            Assert.Equal("middle", middle.Part, "middle part");
            Assert.Equal(3, last.DayInEvent, "third day");
            Assert.Equal("last", last.Part, "last part");
            Assert.Equal(3, last.TotalDuration, "duration preserved");
            Assert.Equal("threat", last.Type, "type normalized");
        }

        [Test]
        public static void Build_Rent_Disabled_Has_No_Rent_Days()
        {
            CalendarRaw raw = Raw(12, 7);
            raw.RentEnabled = false;
            raw.PropertyPaid = true;

            CalendarWeek week = CalendarRules.Build(raw, 7);

            Assert.False(week.RentDueThisWeek, "paid property never has a rent day");
            for (int i = 0; i < week.DayList.Count; i++)
            {
                Assert.False(week.DayList[i].RentDue, "no rent flag on day " + week.DayList[i].Day);
            }
        }

        [Test]
        public static void Build_Missing_Days_Are_Still_Listed()
        {
            CalendarWeek week = CalendarRules.Build(Raw(12, 1), 3);

            Assert.Equal(3, week.Days, "every requested day is listed");
            Assert.Equal(0, week.DayList[1].Events.Count, "missing raw day is empty");
            Assert.Equal(13, week.DayList[1].Day, "day sequence continues");
        }

        [Test]
        public static void Build_Unreadable_Is_Fail_Closed()
        {
            var raw = new CalendarRaw { Readable = false, Error = "not_in_game", Day = 5 };

            CalendarWeek week = CalendarRules.Build(raw, 7);

            Assert.False(week.Readable, "unreadable");
            Assert.Equal("not_in_game", week.Error, "error preserved");
            Assert.Equal(0, week.Days, "no rows without a day counter");
            Assert.Equal(7, week.RequestedDays, "requested value still reported");
        }

        [Test]
        public static void Build_Null_Raw_Is_Unreadable()
        {
            CalendarWeek week = CalendarRules.Build(null, 7);

            Assert.False(week.Readable, "null raw");
            Assert.Equal(CalendarErrors.Unavailable, week.Error, "stable error");
        }
    }
}
