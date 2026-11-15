using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class CalendarServiceTests
    {
        private static JObject Json(string text)
        {
            return JObject.Parse(text);
        }

        [Test]
        public static void Json_Readable_Calendar_Exposes_Days_And_Rent()
        {
            var port = new FakeCalendarPort();
            port.Raw = IntelTestData.Calendar(12, 7,
                IntelTestData.CalendarEvent("threat_1", 12, 12, 2, 5, "THREAT"));

            var service = new CalendarService(port);
            JObject json = Json(service.Json(7));

            Assert.Equal(true, (bool)json["readable"], "readable");
            Assert.Equal(12, (int)json["day"], "today");
            Assert.Equal(7, (int)json["days"], "requested days");
            Assert.Equal(false, (bool)json["clamped"], "not clamped");
            Assert.Equal(8, (int)json["week"]["start"], "week start");
            Assert.Equal(14, (int)json["week"]["end"], "week end");
            Assert.Equal(14, (int)json["rent"]["next_day"], "next rent day");
            Assert.Equal(true, (bool)json["rent"]["enabled"], "rent enabled");
            Assert.Equal(7, ((JArray)json["day_rows"]).Count, "day rows");
            Assert.Equal(12, (int)json["day_rows"][0]["day"], "first row");
            Assert.Equal(true, (bool)json["day_rows"][0]["today"], "today flag");
            Assert.Equal("threat_1", (string)json["day_rows"][0]["events"][0]["id"], "event id");
            Assert.Equal("threat", (string)json["day_rows"][0]["events"][0]["type"], "event type");
            Assert.Equal("first", (string)json["day_rows"][0]["events"][0]["part"], "event part");
            Assert.Equal(7, port.LastDays, "port received the resolved day count");
        }

        [Test]
        public static void Json_Clamps_And_Reports_The_Requested_Count()
        {
            var port = new FakeCalendarPort();
            port.Raw = IntelTestData.Calendar(12, 20);

            var service = new CalendarService(port);
            JObject json = Json(service.Json(99));

            Assert.Equal(99, (int)json["requested_days"], "requested value");
            Assert.Equal(CalendarRules.MaxDays, (int)json["days"], "clamped days");
            Assert.Equal(true, (bool)json["clamped"], "clamped flag");
            Assert.Equal(CalendarRules.MaxDays, port.LastDays, "port gets the clamped value");
        }

        [Test]
        public static void Json_Unreadable_Is_Fail_Closed()
        {
            var port = new FakeCalendarPort();
            port.Raw = new CalendarRaw { Readable = false, Error = "not_in_game", Day = 3 };

            var service = new CalendarService(port);
            JObject json = Json(service.Json(7));

            Assert.Equal(true, (bool)json["ok"], "read op does not become a protocol error");
            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal("not_in_game", (string)json["error"], "adapter error");
            Assert.Equal(0, ((JArray)json["day_rows"]).Count, "no day rows");
        }

        [Test]
        public static void Json_Tolerates_A_Throwing_Port()
        {
            var port = new FakeCalendarPort { Throw = true };
            var service = new CalendarService(port);

            JObject json = Json(service.Json(7));

            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal(CalendarErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void Json_Without_A_Port_Is_Unreadable()
        {
            var service = new CalendarService(null);
            JObject json = Json(service.Json(7));

            Assert.Equal(false, (bool)json["readable"], "no port");
            Assert.Equal(CalendarErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void StateNext_Returns_Up_To_Three_Events_And_Rent()
        {
            var port = new FakeCalendarPort();
            port.Raw = IntelTestData.Calendar(12, 7,
                IntelTestData.CalendarEvent("threat_1", 12, 12, 1, 5, "THREAT"),
                IntelTestData.CalendarEvent("event_3", 15, 15, 1, 1, "NORMALE"),
                IntelTestData.CalendarEvent("event_4", 16, 16, 1, 1, "NORMALE"),
                IntelTestData.CalendarEvent("event_5", 17, 17, 1, 1, "NORMALE"));

            var service = new CalendarService(port);
            Dictionary<string, object> state = service.StateNext();

            Assert.Equal(true, state["readable"], "readable");
            var items = state["items"] as List<Dictionary<string, object>>;
            Assert.NotNull(items, "items list");
            Assert.Equal(3, items.Count, "at most three upcoming items");
            Assert.Equal(12, items[0]["day"], "soonest first");
            Assert.Equal("threat_1", items[0]["id"], "event id");
            Assert.Equal(0, items[0]["days_left"], "today");
            Assert.Equal("rent", items[1]["kind"], "rent day ranks before a same-day event");
            Assert.Equal("rent_day", items[1]["id"], "rent id");
            Assert.Equal(14, items[1]["day"], "rent day");
            Assert.Equal(2, items[1]["days_left"], "days left");
        }

        [Test]
        public static void StateNext_Dedupes_An_Event_Across_Days()
        {
            var port = new FakeCalendarPort();
            port.Raw = IntelTestData.Calendar(12, 7,
                IntelTestData.CalendarEvent("op", 12, 12, 3, 5, "THREAT"));

            var service = new CalendarService(port);
            Dictionary<string, object> state = service.StateNext();

            var items = state["items"] as List<Dictionary<string, object>>;
            Assert.NotNull(items, "items list");
            Assert.Equal(1, CountId(items, "op"), "one entry for the whole event");
            Assert.Equal("op", items[0]["id"], "event id");
            Assert.Equal(3, items[0]["total_duration"], "duration");
        }

        private static int CountId(List<Dictionary<string, object>> items, string id)
        {
            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].ContainsKey("id") && object.Equals(items[i]["id"], id)) count++;
            }

            return count;
        }

        [Test]
        public static void StateNext_Skips_Rent_When_It_Is_Disabled()
        {
            var port = new FakeCalendarPort();
            port.Raw = IntelTestData.Calendar(12, 7);
            port.Raw.RentEnabled = false;
            port.Raw.PropertyPaid = true;

            var service = new CalendarService(port);
            Dictionary<string, object> state = service.StateNext();

            var items = state["items"] as List<Dictionary<string, object>>;
            Assert.NotNull(items, "items list");
            Assert.Equal(0, items.Count, "no rent entry and no events");
        }

        [Test]
        public static void StateNext_Unreadable_Is_Exactly_Readable_False()
        {
            var port = new FakeCalendarPort { Throw = true };
            var service = new CalendarService(port);

            Dictionary<string, object> state = service.StateNext();

            Assert.Equal(1, state.Count, "unreadable state is minimal");
            Assert.Equal(false, state["readable"], "readable false");
        }
    }
}
