using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class BridgeServerTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";
        private static BridgeServer _server;

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void WithServer(Func<string> stateProvider, Func<string, string> actionHandler, Action<int> body)
        {
            WithServer(stateProvider, actionHandler, "9.9.9", body);
        }

        private static void WithServer(Func<string> stateProvider, Func<string, string> actionHandler, string version, Action<int> body)
        {
            WithServer(stateProvider, actionHandler, version, null, body);
        }

        private static void WithServer(
            Func<string> stateProvider,
            Func<string, string> actionHandler,
            string version,
            Func<BridgeHealth> healthProvider,
            Action<int> body)
        {
            int port = FreePort();
            var logs = new List<string>();
            var server = new BridgeServer(
                BridgeConfig.Create(port, Token),
                stateProvider,
                actionHandler,
                logs.Add,
                version,
                null,
                healthProvider);

            _server = server;
            server.Start();
            try
            {
                body(port);
            }
            finally
            {
                server.Stop();
                _server = null;
            }

            foreach (string log in logs) Console.WriteLine("     server log: " + log);
        }

        private static string SendRaw(int port, string raw)
        {
            string response = null;
            Exception failure = null;

            var thread = new Thread(delegate()
            {
                try
                {
                    response = SendRawClient(port, raw);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            thread.IsBackground = true;
            thread.Start();

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (thread.IsAlive && DateTime.UtcNow < deadline)
            {
                if (_server != null) _server.Tick();
                Thread.Sleep(2);
            }

            thread.Join(500);
            if (failure != null) throw failure;
            return response ?? string.Empty;
        }

        private static string SendRawClient(int port, string raw)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.ReceiveTimeout = 5000;
                NetworkStream stream = client.GetStream();
                byte[] bytes = Encoding.ASCII.GetBytes(raw);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string Get(int port, string path, string token)
        {
            string auth = token == null ? string.Empty : "Authorization: Bearer " + token + "\r\n";
            return SendRaw(port, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n"
                + auth + "\r\n");
        }

        private static string Post(int port, string path, string body, string token)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string auth = token == null ? string.Empty : "Authorization: Bearer " + token + "\r\n";
            return SendRaw(port, "POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n"
                + auth + "Content-Type: application/json\r\nContent-Length: " + bodyBytes.Length + "\r\n\r\n" + body);
        }

        [Test]
        public static void Http_Health_Is_Open()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Get(port, "/health", null);
                Assert.Contains(response, "200 OK", "health status");
                Assert.Contains(response, "pawnmind", "health body");
            });
        }

        [Test]
        public static void Http_Health_Reports_Version()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, "9.9.9", delegate(int port)
            {
                string response = Get(port, "/health", null);
                Assert.Contains(response, "\"version\":\"9.9.9\"", "version is injected");
            });
        }

        [Test]
        public static void Http_Health_Reports_Identity_Gate_And_Unverified_Latest()
        {
            const string mvid = "11111111-2222-3333-4444-555555555555";
            var health = new BridgeHealth
            {
                GameVersion = "VERSION 1-Rev1",
                GameAssemblyMvid = mvid,
                ExpectedGameVersion = "VERSION 1-Rev1",
                ExpectedAssemblyMvid = mvid,
                WriteGateState = GameVersionGate.StateOpen,
                WriteGateReason = null
            };

            WithServer(
                delegate { return "{}"; },
                delegate(string body) { return "{}"; },
                "0.2.1",
                delegate { return health; },
                delegate(int port)
                {
                    string response = Get(port, "/health", null);
                    Assert.Contains(response, "200 OK", "health status");
                    Assert.Contains(response, "\"version\":\"0.2.1\"", "legacy version field");
                    Assert.Contains(response, "\"mod_version\":\"0.2.1\"", "mod version field");
                    Assert.Contains(response, "\"game_version\":\"VERSION 1-Rev1\"", "actual game version");
                    Assert.Contains(response, "\"game_assembly_mvid\":\"" + mvid + "\"", "actual assembly mvid");
                    Assert.Contains(response, "\"expected_game_version\":\"VERSION 1-Rev1\"", "expected game version");
                    Assert.Contains(response, "\"expected_assembly_mvid\":\"" + mvid + "\"", "expected assembly mvid");
                    Assert.Contains(response, "\"write_gate\":\"open\"", "write gate state");
                    Assert.Contains(response, "\"latest\":\"UNVERIFIED\"", "latest is not claimed as verified");
                    Assert.Contains(response, "\"latest_verified\":false", "latest verification flag");
                    Assert.False(response.Contains(Token), "health must not leak the bearer token");
                });
        }

        [Test]
        public static void Http_Health_Scrubs_Token_From_Configured_Fields()
        {
            var health = new BridgeHealth
            {
                GameVersion = "VERSION 1-Rev1",
                GameAssemblyMvid = "11111111-2222-3333-4444-555555555555",
                ExpectedGameVersion = "VERSION " + Token,
                ExpectedAssemblyMvid = "11111111-2222-3333-4444-555555555555",
                WriteGateState = GameVersionGate.StateOpen,
                WriteGateReason = null
            };

            WithServer(
                delegate { return "{}"; },
                delegate(string body) { return "{}"; },
                "0.2.1",
                delegate { return health; },
                delegate(int port)
                {
                    string response = Get(port, "/health", null);
                    Assert.False(response.Contains(Token), "health must never echo the bearer token");
                    Assert.Contains(response, "[redacted]", "token-looking config text is redacted");
                });
        }

        [Test]
        public static void Http_Health_Provider_Failure_Fails_Closed()
        {
            WithServer(
                delegate { return "{}"; },
                delegate(string body) { return "{}"; },
                "0.2.1",
                delegate { throw new InvalidOperationException("health boom"); },
                delegate(int port)
                {
                    string response = Get(port, "/health", null);
                    Assert.Contains(response, "200 OK", "health endpoint still answers");
                    Assert.Contains(response, "\"write_gate\":\"locked\"", "provider failure locks writes");
                    Assert.Contains(
                        response,
                        "\"write_gate_reason\":\"version_gate_unconfigured\"",
                        "stable locked reason");
                    Assert.Contains(response, "\"latest\":\"UNVERIFIED\"", "latest remains unverified");
                    Assert.False(response.Contains("health boom"), "provider detail must not leak");
                });
        }

        [Test]
        public static void Http_Health_Reports_Locked_Gate_Without_Expected_Identity()
        {
            var health = new BridgeHealth
            {
                GameVersion = "VERSION 1-Rev1",
                GameAssemblyMvid = "11111111-2222-3333-4444-555555555555",
                ExpectedGameVersion = string.Empty,
                ExpectedAssemblyMvid = string.Empty,
                WriteGateState = GameVersionGate.StateLocked,
                WriteGateReason = GameVersionGate.ReasonUnconfigured
            };

            WithServer(
                delegate { return "{}"; },
                delegate(string body) { return "{}"; },
                "0.2.1",
                delegate { return health; },
                delegate(int port)
                {
                    string response = Get(port, "/health", null);
                    Assert.Contains(response, "\"write_gate\":\"locked\"", "missing expectation locks writes");
                    Assert.Contains(
                        response,
                        "\"write_gate_reason\":\"version_gate_unconfigured\"",
                        "stable locked reason");
                    Assert.Contains(response, "\"expected_game_version\":null", "missing expected version");
                    Assert.Contains(response, "\"expected_assembly_mvid\":null", "missing expected mvid");
                    Assert.Contains(response, "\"latest\":\"UNVERIFIED\"", "latest remains unverified");
                });
        }

        [Test]
        public static void Http_Health_Without_Identity_Provider_Fails_Closed()
        {
            WithServer(
                delegate { return "{}"; },
                delegate(string body) { return "{}"; },
                "0.2.1",
                delegate(int port)
                {
                    string response = Get(port, "/health", null);
                    Assert.Contains(response, "\"game_version\":\"UNKNOWN\"", "missing identity is explicit");
                    Assert.Contains(response, "\"write_gate\":\"locked\"", "missing provider locks writes");
                    Assert.Contains(
                        response,
                        "\"write_gate_reason\":\"version_gate_unconfigured\"",
                        "stable locked reason");
                    Assert.Contains(response, "\"latest\":\"UNVERIFIED\"", "latest remains unverified");
                });
        }

        [Test]
        public static void Http_State_Requires_Token()
        {
            WithServer(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Get(port, "/state", null);
                Assert.Contains(response, "401 Unauthorized", "missing token rejected");

                response = Get(port, "/state", "wrong-token");
                Assert.Contains(response, "401 Unauthorized", "wrong token rejected");
            });
        }

        [Test]
        public static void Http_State_Returns_Provider_Json()
        {
            WithServer(delegate { return "{\"rev\":7,\"in_game\":true}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Get(port, "/state", Token);
                Assert.Contains(response, "200 OK", "state status");
                Assert.Contains(response, "\"rev\":7", "provider json is returned");
            });
        }

        [Test]
        public static void Http_Events_Times_Out()
        {
            WithServer(delegate { return "{\"rev\":0}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Get(port, "/events?since=0&timeout_ms=300", Token);
                Assert.Contains(response, "200 OK", "event status");
                Assert.Contains(response, "\"timeout\":true", "timeout body");
            });
        }

        [Test]
        public static void Http_Events_Returns_On_Revision_Change()
        {
            int rev = 0;
            WithServer(delegate { return "{\"rev\":" + rev + "}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                var bump = new System.Threading.Thread(delegate()
                {
                    System.Threading.Thread.Sleep(150);
                    rev = 1;
                });
                bump.IsBackground = true;
                bump.Start();

                string response = Get(port, "/events?since=0&timeout_ms=3000", Token);
                Assert.Contains(response, "200 OK", "event status");
                Assert.Contains(response, "\"changed\":true", "changed body");
                Assert.Contains(response, "\"rev\":1", "new revision");
                bump.Join(1000);
            });
        }

        [Test]
        public static void Http_Action_Ping_Roundtrip()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{\"ok\":true,\"pong\":true}"; }, delegate(int port)
            {
                string response = Post(port, "/action", "{\"op\":\"ping\"}", Token);
                Assert.Contains(response, "200 OK", "action status");
                Assert.Contains(response, "pong", "action body");
            });
        }

        [Test]
        public static void Http_Action_Requires_Token()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{\"ok\":true}"; }, delegate(int port)
            {
                string response = Post(port, "/action", "{\"op\":\"ping\"}", null);
                Assert.Contains(response, "401 Unauthorized", "action without token rejected");
            });
        }

        [Test]
        public static void Http_Unknown_Path_Is_404()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Get(port, "/nope", Token);
                Assert.Contains(response, "404 Not Found", "unknown path");
            });
        }

        [Test]
        public static void Http_Wrong_Method_Is_405()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string response = Post(port, "/state", "{}", Token);
                Assert.Contains(response, "405 Method Not Allowed", "wrong method");
            });
        }

        [Test]
        public static void Http_Rejects_Foreign_Host_Header()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string raw = "GET /health HTTP/1.1\r\nHost: evil.example\r\n\r\n";
                string response = SendRaw(port, raw);
                Assert.Contains(response, "403 Forbidden", "foreign host rejected");
            });
        }

        [Test]
        public static void Http_Rejects_Too_Large_Body()
        {
            WithServer(delegate { return "{}"; }, delegate(string body) { return "{}"; }, delegate(int port)
            {
                string raw = "POST /action HTTP/1.1\r\nHost: 127.0.0.1:" + port
                    + "\r\nAuthorization: Bearer " + Token
                    + "\r\nContent-Length: 70000\r\n\r\n";
                string response = SendRaw(port, raw);
                // S6A: an oversized declared body is a 413, not a generic 400.
                Assert.Contains(response, "413 Payload Too Large", "oversized body rejected");
                Assert.Contains(response, "\"error\":\"body_too_large\"", "stable oversized error code");
            });
        }
    }
}
