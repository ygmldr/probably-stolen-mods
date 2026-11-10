using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class EventQueryTests
    {
        [Test]
        public static void Empty_Query_Uses_Defaults()
        {
            EventQuery query = EventQueryParser.Parse("");
            Assert.Equal(-1L, query.Since, "default since");
            Assert.Equal(25000, query.TimeoutMs, "default timeout");
        }

        [Test]
        public static void Query_Parses_Since_And_Timeout()
        {
            EventQuery query = EventQueryParser.Parse("since=7&timeout_ms=1500");
            Assert.Equal(7L, query.Since, "since");
            Assert.Equal(1500, query.TimeoutMs, "timeout");
        }

        [Test]
        public static void Timeout_Is_Clamped()
        {
            Assert.Equal(100, EventQueryParser.Parse("timeout_ms=1").TimeoutMs, "min");
            Assert.Equal(30000, EventQueryParser.Parse("timeout_ms=999999").TimeoutMs, "max");
        }

        [Test]
        public static void Events_Route_Is_Registered()
        {
            Assert.Equal(RouteKind.Events, RouteTable.Match("GET", "/events"), "GET events");
            Assert.Equal(RouteKind.MethodNotAllowed, RouteTable.Match("POST", "/events"), "POST events");
        }
    }
}
