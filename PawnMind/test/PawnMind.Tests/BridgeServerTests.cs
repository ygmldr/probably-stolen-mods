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
            int port = FreePort();
            var logs = new List<string>();
            var server = new BridgeServer(
                BridgeConfig.Create(port, Token),
                stateProvider,
                actionHandler,
                logs.Add,
                version);

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
                Assert.Contains(response, "400 Bad Request", "oversized body rejected");
            });
        }
    }
}
