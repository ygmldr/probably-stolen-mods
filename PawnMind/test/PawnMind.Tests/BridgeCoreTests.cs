using System;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class BridgeConfigTests
    {
        private static string Token(int length = BridgeConfig.MinTokenLength)
        {
            return new string('a', length);
        }

        [Test]
        public static void Config_Default_Is_Valid()
        {
            BridgeConfig config = BridgeConfig.Create(BridgeConfig.DefaultPort, Token());
            string error;
            Assert.True(config.TryValidate(out error), "default config should be valid: " + error);
            Assert.Equal(15528, config.Port, "default port");
        }

        [Test]
        public static void Config_Rejects_Port_Out_Of_Range()
        {
            string error;
            Assert.False(BridgeConfig.Create(80, Token()).TryValidate(out error), "port 80 must be rejected");
            Assert.Contains(error, "port", "error mentions port");
            Assert.False(BridgeConfig.Create(70000, Token()).TryValidate(out error), "port 70000 must be rejected");
        }

        [Test]
        public static void Config_Rejects_Short_Token()
        {
            string error;
            Assert.False(BridgeConfig.Create(15527, "short").TryValidate(out error), "short token must be rejected");
            Assert.Contains(error, "token", "error mentions token");
        }

        [Test]
        public static void Config_RoundTrips_Through_Serialize_And_Parse()
        {
            BridgeConfig original = BridgeConfig.Create(16000, Token(40));
            string text = original.Serialize();

            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error), "parse should succeed: " + error);
            Assert.Equal(original.Port, parsed.Port, "port round-trip");
            Assert.Equal(original.Token, parsed.Token, "token round-trip");
        }

        [Test]
        public static void Parse_Reads_Test_Items_Flag()
        {
            string text = "# config\nport=16000\ntoken=" + Token(40) + "\nallow_test_items=true\n";
            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error), "parse: " + error);
            Assert.True(parsed.AllowTestItems, "flag enabled");
        }

        [Test]
        public static void Parse_Defaults_Test_Items_Off()
        {
            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse("port=16000\ntoken=" + Token(40) + "\n", out parsed, out error), "parse");
            Assert.False(parsed.AllowTestItems, "flag defaults off");
        }

        [Test]
        public static void Config_Parse_Rejects_Malformed_Line()
        {
            BridgeConfig parsed;
            string error;
            Assert.False(BridgeConfig.TryParse("port=15527\nnot-a-key-value\n", out parsed, out error),
                "malformed line must fail");
            Assert.NotNull(error, "error should be set");
        }
    }

    internal static class HttpRequestParserTests
    {
        [Test]
        public static void Parser_Parses_Get_Request()
        {
            string raw = "GET /health HTTP/1.1\r\nHost: 127.0.0.1:15527\r\nAuthorization: Bearer abc\r\n\r\n";
            HttpRequest request;
            string error;
            Assert.True(HttpRequestParser.TryParse(raw, out request, out error), "parse GET: " + error);
            Assert.Equal("GET", request.Method, "method");
            Assert.Equal("/health", request.Path, "path");
            Assert.Equal("Bearer abc", request.Headers["authorization"], "header lookup is case-insensitive");
        }

        [Test]
        public static void Parser_Parses_Post_Body()
        {
            string raw = "POST /action HTTP/1.1\r\nHost: localhost\r\nContent-Length: 17\r\n\r\n{\"op\":\"ping\"}   ";
            HttpRequest request;
            string error;
            Assert.True(HttpRequestParser.TryParse(raw, out request, out error), "parse POST: " + error);
            Assert.Equal("POST", request.Method, "method");
            Assert.Equal("/action", request.Path, "path");
            Assert.Contains(request.Body, "\"ping\"", "body preserved");
        }

        [Test]
        public static void Parser_Splits_Query_String()
        {
            string raw = "GET /state?since=12 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
            HttpRequest request;
            string error;
            Assert.True(HttpRequestParser.TryParse(raw, out request, out error), "parse query: " + error);
            Assert.Equal("/state", request.Path, "path without query");
            Assert.Equal("since=12", request.Query, "query string");
        }

        [Test]
        public static void Parser_Rejects_Request_Without_Terminator()
        {
            HttpRequest request;
            string error;
            Assert.False(HttpRequestParser.TryParse("GET /health HTTP/1.1\r\nHost: x\r\n", out request, out error),
                "missing terminator must fail");
            Assert.NotNull(error, "error should be set");
        }

        [Test]
        public static void Parser_Rejects_Non_Slash_Target()
        {
            HttpRequest request;
            string error;
            Assert.False(HttpRequestParser.TryParse("GET health HTTP/1.1\r\nHost: x\r\n\r\n", out request, out error),
                "target must start with /");
        }

        [Test]
        public static void Parser_Rejects_Malformed_Header()
        {
            HttpRequest request;
            string error;
            Assert.False(HttpRequestParser.TryParse("GET / HTTP/1.1\r\nHost 127.0.0.1\r\n\r\n", out request, out error),
                "header without colon must fail");
        }
    }

    internal static class TokenAuthTests
    {
        [Test]
        public static void Auth_Accepts_Bearer_Token()
        {
            string token = new string('k', 32);
            Assert.True(TokenAuth.IsAuthorized("Bearer " + token, token), "valid bearer token");
        }

        [Test]
        public static void Auth_Is_Case_Insensitive_For_Scheme()
        {
            string token = new string('k', 32);
            Assert.True(TokenAuth.IsAuthorized("bearer " + token, token), "lowercase scheme accepted");
        }

        [Test]
        public static void Auth_Rejects_Wrong_Token()
        {
            string token = new string('k', 32);
            Assert.False(TokenAuth.IsAuthorized("Bearer " + new string('x', 32), token), "wrong token rejected");
        }

        [Test]
        public static void Auth_Rejects_Missing_Scheme()
        {
            string token = new string('k', 32);
            Assert.False(TokenAuth.IsAuthorized(token, token), "raw token without scheme rejected");
        }

        [Test]
        public static void Auth_Rejects_Empty_Expected_Token()
        {
            Assert.False(TokenAuth.IsAuthorized("Bearer anything", ""), "empty expected token rejected");
        }
    }

    internal static class RevisionTrackerTests
    {
        [Test]
        public static void Revision_First_Observe_Does_Not_Increment()
        {
            var tracker = new RevisionTracker();
            Assert.False(tracker.Observe("a"), "first observe is not a change");
            Assert.Equal(0L, tracker.Revision, "revision starts at 0");
        }

        [Test]
        public static void Revision_Stable_When_Fingerprint_Same()
        {
            var tracker = new RevisionTracker();
            tracker.Observe("a");
            Assert.False(tracker.Observe("a"), "same fingerprint is not a change");
            Assert.Equal(0L, tracker.Revision, "revision unchanged");
        }

        [Test]
        public static void Revision_Increments_On_Change()
        {
            var tracker = new RevisionTracker();
            tracker.Observe("a");
            Assert.True(tracker.Observe("b"), "changed fingerprint is a change");
            Assert.Equal(1L, tracker.Revision, "revision incremented");
            Assert.True(tracker.Observe("c"), "second change");
            Assert.Equal(2L, tracker.Revision, "revision incremented again");
        }
    }

    internal static class RouteTableTests
    {
        [Test]
        public static void Route_Matches_Health_State_Action()
        {
            Assert.Equal(RouteKind.Health, RouteTable.Match("GET", "/health"), "health");
            Assert.Equal(RouteKind.State, RouteTable.Match("GET", "/state"), "state");
            Assert.Equal(RouteKind.Action, RouteTable.Match("POST", "/action"), "action");
        }

        [Test]
        public static void Route_Wrong_Method_Is_MethodNotAllowed()
        {
            Assert.Equal(RouteKind.MethodNotAllowed, RouteTable.Match("POST", "/health"), "post health");
            Assert.Equal(RouteKind.MethodNotAllowed, RouteTable.Match("GET", "/action"), "get action");
        }

        [Test]
        public static void Route_Unknown_Is_NotFound()
        {
            Assert.Equal(RouteKind.NotFound, RouteTable.Match("GET", "/nope"), "unknown path");
        }

        [Test]
        public static void Route_Normalizes_Trailing_Slash()
        {
            Assert.Equal(RouteKind.Health, RouteTable.Match("GET", "/health/"), "trailing slash");
        }
    }

    internal static class HttpResponseTests
    {
        [Test]
        public static void Response_ContentLength_Uses_Utf8_Bytes()
        {
            string response = HttpResponse.Build(200, "OK", "application/json; charset=utf-8", "{\"x\":\"你好\"}");
            int bodyBytes = System.Text.Encoding.UTF8.GetByteCount("{\"x\":\"你好\"}");
            Assert.Contains(response, "Content-Length: " + bodyBytes, "content-length must count utf8 bytes");
        }

        [Test]
        public static void Response_Is_Connection_Close_And_No_Store()
        {
            string response = HttpResponse.Build(401, "Unauthorized", "application/json", "{}");
            Assert.Contains(response, "HTTP/1.1 401 Unauthorized", "status line");
            Assert.Contains(response, "Connection: close", "connection close");
            Assert.Contains(response, "Cache-Control: no-store", "no store");
        }
    }
}
