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
    /// D2 module: GET /operation?id= is a read-only query endpoint. It must be
    /// behind the same bearer token as /state, must not be classified as a
    /// snapshot reader (it never builds the game snapshot), and must answer an
    /// unknown id with structured data instead of an HTTP error.
    ///
    /// Evidence level: L2-integration-no-game (real loopback socket, no game).
    /// </summary>
    internal static class OperationEndpointTests
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

        private sealed class Calls
        {
            public int StateCalls;
            public int OperationCalls;
            public string LastId;
        }

        private static void WithServer(Calls calls, Action<int> body)
        {
            int port = FreePort();
            var logs = new List<string>();
            var server = new BridgeServer(
                BridgeConfig.Create(port, Token),
                delegate { calls.StateCalls++; return "{\"rev\":1}"; },
                delegate(string request) { return "{\"ok\":true}"; },
                logs.Add,
                "9.9.9",
                null,
                null,
                null,
                null,
                delegate(string id)
                {
                    calls.OperationCalls++;
                    calls.LastId = id;
                    return "{\"ok\":false,\"known\":true,\"operation_id\":\"" + id
                        + "\",\"execution_status\":\"running\",\"delivery_status\":\"delivered\"}";
                });

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

        private static string Get(int port, string path, bool auth = true)
        {
            return SendRaw(port, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + (auth ? "\r\nAuthorization: Bearer " + Token : "")
                + "\r\n\r\n");
        }

        [Test]
        public static void Test_Mode_Operation_Query_Is_Served_Without_Building_The_Snapshot()
        {
            ModePolicy.Configure(RunMode.Test, null);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = Get(port, "/operation?id=op-1");
                Contains(response, "200 OK", "query served");
                Contains(response, "\"execution_status\":\"running\"", "two-axis status");
                Equal("op-1", calls.LastId, "id parsed from the query string");
                Equal(0, calls.StateCalls, "no game snapshot is built for a query");
            });
        }

        [Test]
        public static void Missing_Operation_Id_Is_A_Stable_Bad_Request()
        {
            ModePolicy.Configure(RunMode.Test, null);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = Get(port, "/operation");
                Contains(response, "400 Bad Request", "id is required");
                Contains(response, "missing_operation_id", "stable code");
                Equal(0, calls.OperationCalls, "the provider is not called without an id");
            });
        }

        [Test]
        public static void Operation_Query_Requires_The_Bearer_Token()
        {
            ModePolicy.Configure(RunMode.Test, null);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = Get(port, "/operation?id=op-1", auth: false);
                Contains(response, "401 Unauthorized", "token required");
                Equal(0, calls.OperationCalls, "no provider call without auth");
            });
        }

        [Test]
        public static void Operation_Is_Get_Only()
        {
            ModePolicy.Configure(RunMode.Test, null);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = SendRaw(port,
                    "POST /operation?id=op-1 HTTP/1.1\r\nHost: 127.0.0.1:" + port
                    + "\r\nAuthorization: Bearer " + Token
                    + "\r\nContent-Length: 0\r\n\r\n");
                Contains(response, "405 Method Not Allowed", "read-only endpoint");
            });
        }

        [Test]
        public static void Bootstrap_May_Query_Operations_But_Not_The_Snapshot()
        {
            ModePolicy.Configure(RunMode.Bootstrap, PolicyCatalog.BootstrapOps);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string operation = Get(port, "/operation?id=op-1");
                Contains(operation, "200 OK", "operation query is a protocol necessity");
                Equal(1, calls.OperationCalls, "served in bootstrap");

                string state = Get(port, "/state");
                Contains(state, "403 Forbidden", "the snapshot stays closed");
                Equal(0, calls.StateCalls, "no snapshot build");
            });
        }

        [Test]
        public static void Locked_Mode_Refuses_Operation_Queries()
        {
            ModePolicy.Reset();
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = Get(port, "/operation?id=op-1");
                Contains(response, "403 Forbidden", "lock stays closed");
                Contains(response, "mode_locked", "stable code");
                Equal(0, calls.OperationCalls, "nothing is served");
            });
        }

        [Test]
        public static void Operation_Query_Does_Not_Read_The_Action_Cache_Through_Action()
        {
            // A GET must never be routed to the write dispatcher.
            ModePolicy.Configure(RunMode.Test, null);
            var calls = new Calls();

            WithServer(calls, delegate(int port)
            {
                string response = Get(port, "/operation?id=missing-op");
                Contains(response, "200 OK", "an unknown id is data, not an HTTP failure");
                Equal(0, calls.StateCalls, "no snapshot");
            });
        }
    }
}
