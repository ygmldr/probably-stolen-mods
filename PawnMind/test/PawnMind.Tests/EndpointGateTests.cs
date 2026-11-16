using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PawnMind.Bridge;
using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// The MCP tool allowlist is not the only way in: anything on the machine can
    /// address the loopback port. These tests prove GET /state and GET /events are
    /// refused in Bootstrap/Play/Locked *before* the snapshot provider runs, so a
    /// raw HTTP caller cannot read what the tool layer hides.
    /// </summary>
    internal static class EndpointGateTests
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

        private sealed class Counters
        {
            public int StateCalls;
            public int ActionCalls;
        }

        private static void WithServer(Counters counters, Action<int> body)
        {
            int port = FreePort();
            var logs = new List<string>();
            var server = new BridgeServer(
                BridgeConfig.Create(port, Token),
                delegate { counters.StateCalls++; return "{\"rev\":1}"; },
                delegate(string request) { counters.ActionCalls++; return "{\"ok\":true}"; },
                logs.Add,
                "9.9.9");

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
                ModePolicy.Reset();
            }
        }

        private static string SendRaw(int port, string raw)
        {
            string response = null;
            Exception failure = null;

            var thread = new Thread(delegate()
            {
                try
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
                            response = reader.ReadToEnd();
                        }
                    }
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

        private static string Get(int port, string path)
        {
            return SendRaw(port, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + "\r\nAuthorization: Bearer " + Token + "\r\n\r\n");
        }

        private static string Post(int port, string path, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            return SendRaw(port, "POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + "\r\nAuthorization: Bearer " + Token
                + "\r\nContent-Type: application/json\r\nContent-Length: " + bodyBytes.Length
                + "\r\n\r\n" + body);
        }

        [Test]
        public static void Bootstrap_State_Is_Refused_Before_The_Provider_Runs()
        {
            RunModeBinding.Apply("bootstrap", false);
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string response = Get(port, "/state");
                Contains(response, "403 Forbidden", "raw HTTP cannot read the snapshot");
                Contains(response, "mode_forbidden", "stable refusal code");
                Equal(0, counters.StateCalls, "the snapshot was never built");
            });
        }

        [Test]
        public static void Bootstrap_Events_Is_Refused_Before_The_Provider_Runs()
        {
            RunModeBinding.Apply("bootstrap", false);
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string response = Get(port, "/events?since_rev=0&timeout_ms=50");
                Contains(response, "403 Forbidden", "long poll is a snapshot reader too");
                Contains(response, "mode_forbidden", "stable refusal code");
                Equal(0, counters.StateCalls, "no snapshot build");
            });
        }

        [Test]
        public static void Play_Does_Not_Inherit_The_Candidate_Allowlist()
        {
            // Play resolves to PolicyCatalog.PlayOps, which is empty until reviewed
            // live evidence exists. It must not fall back to the 73-tool candidate set.
            RunModePlan plan = RunModeBinding.Apply("play", false);
            Equal(0, plan.Allow.Count, "no live evidence");
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string response = Get(port, "/state");
                Contains(response, "403 Forbidden", "play stays closed");
                Equal(0, counters.StateCalls, "no snapshot build");
            });
        }

        [Test]
        public static void Locked_Refuses_State_And_Writes()
        {
            ModePolicy.Reset();
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string state = Get(port, "/state");
                Contains(state, "403 Forbidden", "locked state");
                Contains(state, "mode_locked", "locked code");

                string action = Post(port, "/action", "{\"op\":\"open_shop\"}");
                Contains(action, "403 Forbidden", "locked action");
                Contains(action, "mode_locked", "locked code");

                Equal(0, counters.StateCalls, "no snapshot build");
                Equal(0, counters.ActionCalls, "no dispatch");
            });
        }

        [Test]
        public static void Health_Stays_Open_And_Reports_The_Gate()
        {
            // Same path as startup with a missing run_mode in bridge.conf.
            RunModePlan plan = RunModeBinding.Apply(null, false);
            Equal(RunMode.Locked, plan.Mode, "missing mode locks");
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string response = Get(port, "/health");
                Contains(response, "200 OK", "health is always reachable");
                Contains(response, "\"run_mode\":\"locked\"", "current mode");
                Contains(response, "\"run_mode_error\":\"run_mode_missing\"", "why it is locked");
                Contains(response, "\"policy_schema\":\"2\"", "policy schema");
                Contains(response, "\"policy_sha256\":\"", "policy fingerprint");
                Contains(response, "\"play_ready\":false", "no live evidence yet");
                Equal(0, counters.StateCalls, "health never builds the snapshot");
            });
        }

        [Test]
        public static void Test_Mode_Keeps_The_Endpoints_Available()
        {
            ModePolicy.Configure(RunMode.Test, null);
            var counters = new Counters();

            WithServer(counters, delegate(int port)
            {
                string response = Get(port, "/state");
                Contains(response, "200 OK", "explicit test mode");
                Equal(1, counters.StateCalls, "provider ran once");
            });
        }
    }
}
