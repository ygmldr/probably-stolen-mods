using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal sealed class FakeDayReportPort : IDayReportPort
    {
        public DayReportRaw Raw = new DayReportRaw { Readable = true, Day = 1 };
        public bool Throw;
        public int Calls;

        public DayReportRaw ReadDayReport()
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("report read failed");
            return Raw;
        }
    }

    internal sealed class FakeCalendarPort : ICalendarPort
    {
        public CalendarRaw Raw = new CalendarRaw { Readable = true, Day = 1 };
        public bool Throw;
        public int Calls;
        public int LastDays = -1;

        public CalendarRaw ReadCalendar(int days)
        {
            Calls++;
            LastDays = days;
            if (Throw) throw new InvalidOperationException("calendar read failed");
            return Raw;
        }
    }

    internal sealed class FakeClientelePort : IClientelePort
    {
        public ClienteleRaw Raw = new ClienteleRaw { Readable = true };
        public bool Throw;
        public int Calls;

        public ClienteleRaw ReadClientele()
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("clientele read failed");
            return Raw;
        }
    }

    internal sealed class FakeTutorialPort : ITutorialPort
    {
        public TutorialSnapshot Before = new TutorialSnapshot
        {
            Readable = true,
            Enabled = true,
            HintsHidden = false,
            GroupReadable = true
        };

        public TutorialSnapshot After;
        public bool ThrowOnRead;
        public bool ThrowOnWrite;
        public int ReadCalls;
        public int DisableCalls;

        public TutorialSnapshot ReadTutorial()
        {
            ReadCalls++;
            if (ThrowOnRead) throw new InvalidOperationException("tutorial read failed");
            if (ReadCalls == 1 || After == null) return Before;
            return After;
        }

        public void DisableHints()
        {
            DisableCalls++;
            if (ThrowOnWrite) throw new InvalidOperationException("tutorial write failed");
        }
    }

    internal static class IntelTestData
    {
        public static CalendarEventRaw CalendarEvent(
            string id,
            int startDay,
            int day,
            int duration = 1,
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
                TotalDuration = duration,
                Importance = importance,
                Known = true
            };
        }

        public static CalendarRaw Calendar(int day, int days, params CalendarEventRaw[] events)
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

        public static ClienteleRaw Clientele()
        {
            return new ClienteleRaw
            {
                Readable = true,
                Day = 3,
                Types = new List<ClienteleTypeRow>
                {
                    new ClienteleTypeRow { Id = "LUXURY_ITEM", Name = "Luxury" }
                },
                DiscerningChance = 10,
                DiscerningCooldownOpenings = 0,
                Predictions = new List<ClientelePredictionRow>
                {
                    new ClientelePredictionRow
                    {
                        ClientId = "ulDiscerningShopper",
                        ClientName = "Discerning",
                        Chance = 10,
                        ItemId = "vase",
                        ItemName = "Vase"
                    }
                },
                NormalCustomerCount = 2,
                Warnings = new List<string>()
            };
        }
    }
}
