using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Read-only calendar adapter over StoreEventManager.GetActiveEventOnDay.
    /// The day range and rent day are derived from StoreStation.dayCounter,
    /// never from UI state; the calendar UI is not opened (AdvCalendarUIManager
    /// .HandleInnateEvent queues rent events, which would be a write).
    /// </summary>
    internal sealed class CalendarAdapter : ICalendarPort
    {
        public static readonly CalendarAdapter Instance = new CalendarAdapter();

        public CalendarRaw ReadCalendar(int days)
        {
            var raw = new CalendarRaw();
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    raw.Error = "not_in_game";
                    return raw;
                }

                PlayerStore store = PlayerStore.Instance;
                StoreStation station = StoreStation.Instance;
                if (store == null || station == null || station.storeEventManager == null)
                {
                    raw.Error = CalendarErrors.Unavailable;
                    return raw;
                }

                int count = CalendarRules.ResolveDays(days);
                raw.Readable = true;
                raw.Day = station.dayCounter;
                raw.PropertyPaid = store.IsPropertyPaid;
                raw.PayingMortgage = store.isPayingMortgage;
                raw.RentEnabled = !store.IsPropertyPaid
                    && (store.IsPayingRent() || store.isPayingMortgage);
                raw.RentDaysLeft = SafeRentDaysLeft(store);
                raw.RentValue = SafeRentValue(store);

                for (int i = 0; i < count; i++)
                {
                    int day = raw.Day + i;
                    var dayRaw = new CalendarDayRaw { Day = day };
                    List<StoreEvent> events = station.storeEventManager.GetActiveEventOnDay(day);
                    if (events != null)
                    {
                        for (int e = 0; e < events.Count; e++)
                        {
                            CalendarEventRaw mapped = MapEvent(events[e]);
                            if (mapped != null) dayRaw.Events.Add(mapped);
                        }
                    }

                    raw.Days.Add(dayRaw);
                }
            }
            catch (Exception)
            {
                raw = new CalendarRaw
                {
                    Readable = false,
                    Error = CalendarErrors.Unavailable
                };
            }

            return raw;
        }

        private static int SafeRentDaysLeft(PlayerStore store)
        {
            try
            {
                return store.GetRentDayCounter();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int SafeRentValue(PlayerStore store)
        {
            try
            {
                if (store.isPayingMortgage) return store.mortgageWeeklyPayment;
                return store.GetRentValue();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static CalendarEventRaw MapEvent(StoreEvent ev)
        {
            if (ev == null || string.IsNullOrEmpty(ev.identifier)) return null;

            string name = !string.IsNullOrEmpty(ev.displayName) ? ev.displayName : ev.newsName;
            return new CalendarEventRaw
            {
                Id = ev.identifier,
                Name = string.IsNullOrEmpty(name) ? ev.identifier : name,
                Type = ev.eventType.ToString(),
                Area = ev.eventArea.ToString(),
                StartDay = ev.startDay,
                CurrentDay = ev.currentDay,
                TotalDuration = ev.totalDuration,
                Importance = ev.importance,
                Known = ev.IsKnown
            };
        }
    }
}
