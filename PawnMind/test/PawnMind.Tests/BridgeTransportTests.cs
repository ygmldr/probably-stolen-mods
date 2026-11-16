using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Loopback-socket tests for the S6A transport hardening: the Unity main
    /// thread must never block on a client, writes are bounded per Tick, the
    /// connection and event-waiter counts are bounded, short writes are resumed,
    /// and a dispatched write whose response cannot be delivered is reported as
    /// an unknown outcome instead of success.
    /// These tests drive Tick() from the test thread, so they do not depend on a
    /// real frame rate and never wait on the server with an unbounded sleep.
    /// </summary>
    internal static class BridgeTransportTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";
        private const string Mvid = "11111111-2222-3333-4444-555555555555";

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string GetRequest(int port, string path)
        {
            return "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + "\r\nAuthorization: Bearer " + Token + "\r\n\r\n";
        }

        private static string PostRequest(int port, string body)
        {
            return "POST /action HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + "\r\nAuthorization: Bearer " + Token
                + "\r\nContent-Type: application/json"
                + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body)
                + "\r\n\r\n" + body;
        }

        /// <summary>
        /// A client whose reads happen on the test thread, so per-Tick progress
        /// is deterministic and the test never sleeps inside a blocking read.
        /// </summary>
        private sealed class RawClient : IDisposable
        {
            public TcpClient Tcp;
            public NetworkStream Stream;

            public RawClient(int port) : this(port, 0)
            {
            }

            public RawClient(int port, int receiveBufferBytes)
            {
                Tcp = new TcpClient();
                Tcp.NoDelay = true;
                if (receiveBufferBytes > 0) Tcp.ReceiveBufferSize = receiveBufferBytes;
                Tcp.Connect(IPAddress.Loopback, port);
                Stream = Tcp.GetStream();
            }

            public void Send(string text)
            {
                SendBytes(Encoding.UTF8.GetBytes(text));
            }

            public void SendBytes(byte[] bytes)
            {
                Stream.Write(bytes, 0, bytes.Length);
                Stream.Flush();
            }

            /// <summary>Reads only bytes the kernel already holds; never blocks.</summary>
            public byte[] DrainAvailable()
            {
                int available = Tcp.Available;
                if (available <= 0) return new byte[0];

                var buffer = new byte[available];
                int read = 0;
                while (read < available)
                {
                    int count = Stream.Read(buffer, read, available - read);
                    if (count <= 0) break;
                    read += count;
                    available = Tcp.Available;
                    if (read >= buffer.Length) break;
                }

                if (read == buffer.Length) return buffer;
                var trimmed = new byte[read];
                Array.Copy(buffer, trimmed, read);
                return trimmed;
            }

            public bool WaitForData(int timeoutMs)
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (Tcp.Available > 0) return true;
                    System.Threading.Thread.Sleep(1);
                }

                return Tcp.Available > 0;
            }

            /// <summary>True when the peer closed (or reset) the connection.</summary>
            public bool WaitForClose(int timeoutMs)
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (Tcp.Client.Poll(1000, SelectMode.SelectRead) && Tcp.Available == 0)
                            return true;
                    }
                    catch (SocketException)
                    {
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        return true;
                    }
                }

                return false;
            }

            public void Abort()
            {
                try
                {
                    Tcp.Client.LingerState = new LingerOption(true, 0);
                    Tcp.Close();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try { Tcp.Close(); } catch { }
            }
        }

        private sealed class Harness : IDisposable
        {
            public BridgeServer Server;
            public int Port;
            public readonly List<string> Logs = new List<string>();

            public void Pump(int ticks)
            {
                for (int i = 0; i < ticks; i++) Server.Tick();
            }

            public void TickUntil(Func<bool> condition, int timeoutMs, string message)
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (condition()) return;
                    Server.Tick();
                    System.Threading.Thread.Sleep(1);
                }

                if (!condition())
                {
                    throw new AssertionException(message + " (logs: " + string.Join(" | ", Logs.ToArray()) + ")");
                }
            }

            public void Dispose()
            {
                Server.Stop();
                ModePolicy.Reset();
            }
        }

        private static Harness Start(
            Func<string> state,
            Func<string, string> action,
            BridgeLimits limits = null,
            Func<long> revision = null,
            Action<string, string> undelivered = null)
        {
            // Transport tests are not mode tests: enter Test explicitly so the
            // fail-closed default (Locked) does not mask the behaviour under test.
            ModePolicy.Configure(RunMode.Test, null);

            int port = FreePort();
            var harness = new Harness { Port = port };
            harness.Server = new BridgeServer(
                BridgeConfig.Create(port, Token),
                state,
                action,
                harness.Logs.Add,
                "test",
                revision,
                null,
                limits,
                undelivered);
            harness.Server.Start();
            return harness;
        }

        private static BridgeLimits Limits()
        {
            return new BridgeLimits
            {
                MaxConnections = 8,
                MaxEventWaiters = 4,
                MaxWriteBytesPerTickPerClient = 64 * 1024,
                MaxWriteBytesPerTickTotal = 256 * 1024,
                WriteTimeoutMs = 5000,
                RequestTimeoutMs = 10000
            };
        }

        /// <summary>Drains and Ticks until a full response is available or time runs out.</summary>
        private static string ReadWholeResponse(Harness harness, RawClient client, int timeoutMs)
        {
            var received = new List<byte>();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                harness.Server.Tick();
                received.AddRange(client.DrainAvailable());

                byte[] current = received.ToArray();
                if (TryParseComplete(current, out string response)) return response;

                if (client.WaitForClose(2) && client.Tcp.Available == 0) break;
                System.Threading.Thread.Sleep(1);
            }

            return Encoding.UTF8.GetString(received.ToArray());
        }

        private static bool TryParseComplete(byte[] data, out string response)
        {
            response = null;
            int bodyStart = FindHeaderEnd(data);
            if (bodyStart < 0) return false;

            int contentLength = ContentLength(data, bodyStart);
            if (contentLength < 0 || data.Length < bodyStart + contentLength) return false;

            response = Encoding.UTF8.GetString(data, 0, bodyStart + contentLength);
            return true;
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i + 4;
            }

            return -1;
        }

        private static int ContentLength(byte[] data, int bodyStart)
        {
            string headers = Encoding.ASCII.GetString(data, 0, bodyStart);
            string[] lines = headers.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(lines[i].Substring(0, colon).Trim(), "Content-Length",
                        StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (int.TryParse(lines[i].Substring(colon + 1).Trim(), out value)) return value;
            }

            return -1;
        }

        private static string Body(string response)
        {
            int index = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return index < 0 ? string.Empty : response.Substring(index + 4);
        }

        private static bool LogContains(Harness harness, string token)
        {
            for (int i = 0; i < harness.Logs.Count; i++)
            {
                if (harness.Logs[i].IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        private static byte[] Slice(byte[] source, int start, int length)
        {
            var slice = new byte[length];
            Array.Copy(source, start, slice, 0, length);
            return slice;
        }

        // ---------------------------------------------------------------- Red-1

        [Test]
        public static void Oversized_Request_Body_Is_413()
        {
            Harness harness = Start(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{}"; });
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send("POST /action HTTP/1.1\r\nHost: 127.0.0.1:" + harness.Port
                        + "\r\nAuthorization: Bearer " + Token
                        + "\r\nContent-Length: 70000\r\n\r\n");
                    string response = ReadWholeResponse(harness, client, 3000);
                    Assert.Contains(response, "413 Payload Too Large", "oversized body is 413");
                    Assert.Contains(Body(response), "body_too_large", "stable oversized-body error code");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Oversized_Request_Headers_Are_413()
        {
            Harness harness = Start(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{}"; });
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send("GET /state HTTP/1.1\r\nHost: 127.0.0.1:" + harness.Port
                        + "\r\nAuthorization: Bearer " + Token + "\r\nX-Pad: "
                        + new string('p', 9000) + "\r\n");
                    string response = ReadWholeResponse(harness, client, 3000);
                    Assert.Contains(response, "413 Payload Too Large", "oversized headers are 413");
                    Assert.Contains(Body(response), "header_too_large", "stable oversized-header code");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        // ---------------------------------------------------------------- Writes

        [Test]
        public static void Slow_Non_Reading_Client_Gets_The_Complete_Response_Across_Ticks()
        {
            // A tiny receive buffer plus a 256 KiB body forces many short writes.
            // The server must keep the unsent bytes and finish them later instead
            // of dropping them (the pre-S6A single unchecked Send did drop them).
            string big = "{\"rev\":1,\"blob\":\"" + new string('x', 256 * 1024) + "\"}";
            Harness harness = Start(delegate { return big; }, delegate(string body) { return "{}"; }, Limits());
            try
            {
                using (var client = new RawClient(harness.Port, 2048))
                {
                    client.Send(GetRequest(harness.Port, "/state"));

                    var received = new List<byte>();
                    var watch = new Stopwatch();
                    for (int i = 0; i < 600; i++)
                    {
                        watch.Restart();
                        harness.Server.Tick();
                        watch.Stop();
                        Assert.True(watch.ElapsedMilliseconds < 250,
                            "Tick must not block on a non-reading client (took "
                            + watch.ElapsedMilliseconds + " ms)");

                        received.AddRange(client.DrainAvailable());
                        if (TryParseComplete(received.ToArray(), out string ignored)) break;
                        if (client.WaitForClose(2) && client.Tcp.Available == 0) break;
                    }

                    byte[] complete = received.ToArray();
                    Assert.True(TryParseComplete(complete, out string response),
                        "the whole response must arrive across Ticks, not be truncated by a short write");
                    int headerEnd = FindHeaderEnd(complete);
                    int length = ContentLength(complete, headerEnd);
                    Assert.Equal(Encoding.UTF8.GetByteCount(big), length,
                        "Content-Length must be the UTF-8 byte count");
                    Assert.Equal(big, Encoding.UTF8.GetString(complete, headerEnd, length),
                        "the complete body must match the provider string");
                    Assert.Contains(response, "200 OK", "state status");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Each_Tick_Writes_At_Most_The_Configured_Budget()
        {
            string big = "{\"rev\":1,\"blob\":\"" + new string('x', 128 * 1024) + "\"}";
            BridgeLimits limits = Limits();
            limits.MaxWriteBytesPerTickPerClient = 4096;
            limits.MaxWriteBytesPerTickTotal = 4096;
            Harness harness = Start(delegate { return big; }, delegate(string body) { return "{}"; }, limits);
            try
            {
                using (var client = new RawClient(harness.Port, 2048))
                {
                    client.Send(GetRequest(harness.Port, "/state"));

                    long total = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        harness.Server.Tick();
                        byte[] chunk = client.DrainAvailable();
                        Assert.True(chunk.Length <= limits.MaxWriteBytesPerTickPerClient,
                            "one Tick wrote " + chunk.Length + " bytes, budget is "
                            + limits.MaxWriteBytesPerTickPerClient);
                        total += chunk.Length;
                    }

                    Assert.True(total > 0, "the server must make progress without the client reading first");
                    Assert.True(total < Encoding.UTF8.GetByteCount(big),
                        "a 128 KiB response must not be flushed in six 4 KiB Ticks");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Utf8_Content_Length_Matches_Byte_Count()
        {
            string body = "{\"rev\":1,\"text\":\"中文标签-水质\"}";
            BridgeLimits limits = Limits();
            limits.MaxWriteBytesPerTickPerClient = 8;
            limits.MaxWriteBytesPerTickTotal = 8;
            Harness harness = Start(delegate { return body; }, delegate(string request) { return "{}"; }, limits);
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send(GetRequest(harness.Port, "/state"));
                    string response = ReadWholeResponse(harness, client, 3000);
                    int headerEnd = FindHeaderEnd(Encoding.UTF8.GetBytes(response));
                    Assert.True(headerEnd > 0, "headers arrive");
                    Assert.Equal(Encoding.UTF8.GetByteCount(body), ContentLength(Encoding.UTF8.GetBytes(response), headerEnd),
                        "Content-Length counts UTF-8 bytes, not chars");
                    Assert.Equal(body, Body(response), "multibyte body arrives intact");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Response_Larger_Than_The_Pending_Limit_Is_Dropped_And_Logged()
        {
            string big = "{\"rev\":1,\"blob\":\"" + new string('x', 100 * 1024) + "\"}";
            BridgeLimits limits = Limits();
            limits.MaxPendingBytesPerClient = 2048;
            Harness harness = Start(delegate { return big; }, delegate(string body) { return "{}"; }, limits);
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send(GetRequest(harness.Port, "/state"));
                    harness.Pump(4);
                    Assert.True(client.WaitForClose(1000),
                        "a response over the pending limit must close the connection, not buffer forever");
                    Assert.True(LogContains(harness, "output limit") || LogContains(harness, "pending"),
                        "the drop must be logged");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        // ------------------------------------------------------------ Connections

        [Test]
        public static void Connection_Limit_Rejects_New_Clients_And_Releases_Slots()
        {
            BridgeLimits limits = Limits();
            limits.MaxConnections = 2;
            Harness harness = Start(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{}"; }, limits);
            try
            {
                using (var first = new RawClient(harness.Port))
                using (var second = new RawClient(harness.Port))
                {
                    // Both stay open with an incomplete request.
                    first.Send("GET /state HTTP/1.1\r\nHost: 127.0.0.1:" + harness.Port
                        + "\r\nAuthorization: Bearer " + Token + "\r\nContent-Length: 50\r\n\r\n");
                    second.Send("GET /state HTTP/1.1\r\nHost: 127.0.0.1:" + harness.Port
                        + "\r\nAuthorization: Bearer " + Token + "\r\nContent-Length: 50\r\n\r\n");
                    harness.TickUntil(delegate { return harness.Server.ConnectionCount == 2; }, 1000,
                        "both clients accepted");

                    using (var third = new RawClient(harness.Port))
                    {
                        third.Send(GetRequest(harness.Port, "/health"));
                        harness.Pump(3);
                        Assert.True(third.WaitForClose(1000),
                            "a connection over the limit must be refused, not accepted and queued");
                        Assert.Equal(0, third.Tcp.Available, "rejected clients receive no response bytes");
                    }

                    Assert.True(harness.Server.RejectedConnections >= 1, "rejections are counted");
                    Assert.True(LogContains(harness, "connection limit"), "rejections are logged");

                    first.Dispose();
                    harness.TickUntil(delegate { return harness.Server.ConnectionCount <= 1; }, 1000,
                        "closing a client releases its slot");

                    using (var fourth = new RawClient(harness.Port))
                    {
                        fourth.Send(GetRequest(harness.Port, "/health"));
                        string response = ReadWholeResponse(harness, fourth, 3000);
                        Assert.Contains(response, "200 OK", "a released slot accepts the next client");
                    }
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Partial_Request_Is_Buffered_Until_Complete()
        {
            Harness harness = Start(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{\"ok\":true,\"echo\":true}"; }, Limits());
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    string body = "{\"op\":\"ping\"}";
                    byte[] request = Encoding.UTF8.GetBytes(PostRequest(harness.Port, body));
                    int cut = request.Length / 2;
                    client.SendBytes(Slice(request, 0, cut));
                    harness.Pump(3);
                    Assert.Equal(0, client.Tcp.Available, "no response before the request is complete");

                    client.SendBytes(Slice(request, cut, request.Length - cut));
                    string response = ReadWholeResponse(harness, client, 3000);
                    Assert.Contains(response, "200 OK", "completed request answers");
                    Assert.Contains(Body(response), "\"echo\":true", "the whole body was assembled");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Aborted_Client_Does_Not_Poison_Reconnects()
        {
            Harness harness = Start(delegate { return "{\"rev\":1}"; }, delegate(string body) { return "{}"; }, Limits());
            try
            {
                using (var first = new RawClient(harness.Port))
                {
                    first.Send(GetRequest(harness.Port, "/state"));
                    harness.Pump(1);
                    first.Abort();
                }

                harness.Pump(5);
                using (var second = new RawClient(harness.Port))
                {
                    second.Send(GetRequest(harness.Port, "/health"));
                    string response = ReadWholeResponse(harness, second, 3000);
                    Assert.Contains(response, "200 OK", "server still serves after a client abort");
                }

                harness.TickUntil(delegate { return harness.Server.ConnectionCount == 0; }, 1000,
                    "an aborted client must be reaped");
            }
            finally
            {
                harness.Dispose();
            }
        }

        // ---------------------------------------------------------------- Events

        [Test]
        public static void Events_Use_A_Stable_Envelope_And_Release_On_Timeout()
        {
            Harness harness = Start(delegate { return "{\"rev\":5,\"in_game\":true}"; },
                delegate(string body) { return "{}"; }, Limits(), delegate { return 5; });
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send(GetRequest(harness.Port, "/events?since=5&timeout_ms=250"));
                    string response = ReadWholeResponse(harness, client, 3000);
                    string body = Body(response);
                    Assert.Contains(body, "\"ok\":true", "timeout envelope ok; body=" + body);
                    Assert.Contains(body, "\"rev\":5", "timeout envelope rev");
                    Assert.Contains(body, "\"changed\":false", "timeout envelope changed");
                    Assert.Contains(body, "\"timeout\":true", "timeout envelope flag");
                    Assert.False(body.Contains("\"state\":"), "timeout must not ship a state body");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Events_Release_With_State_When_The_Revision_Changes()
        {
            long revision = 5;
            Harness harness = Start(
                delegate { return "{\"rev\":" + revision + ",\"in_game\":true}"; },
                delegate(string body) { return "{}"; },
                Limits(),
                delegate { return revision; });
            try
            {
                using (var client = new RawClient(harness.Port))
                {
                    client.Send(GetRequest(harness.Port, "/events?since=5&timeout_ms=3000"));
                    harness.TickUntil(delegate { return harness.Server.EventWaiterCount == 1; }, 1000,
                        "client waits for a change");

                    revision = 9;
                    string response = ReadWholeResponse(harness, client, 3000);
                    string body = Body(response);
                    Assert.Contains(body, "\"changed\":true", "change envelope");
                    Assert.Contains(body, "\"rev\":9", "new revision");
                    Assert.Contains(body, "\"state\":{\"rev\":9", "changed state snapshot is included");
                    Assert.Contains(body, "\"ok\":true", "change envelope ok");
                }

                harness.TickUntil(delegate { return harness.Server.EventWaiterCount == 0; }, 1000,
                    "the waiter is released after reporting the change");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Events_Concurrency_Is_Capped_And_Refuses_Immediately()
        {
            BridgeLimits limits = Limits();
            limits.MaxEventWaiters = 1;
            Harness harness = Start(delegate { return "{\"rev\":5,\"in_game\":true}"; },
                delegate(string body) { return "{}"; }, limits, delegate { return 5; });
            try
            {
                using (var waiter = new RawClient(harness.Port))
                using (var overflow = new RawClient(harness.Port))
                {
                    waiter.Send(GetRequest(harness.Port, "/events?since=5&timeout_ms=2000"));
                    harness.TickUntil(delegate { return harness.Server.EventWaiterCount == 1; }, 1000,
                        "first long poll waits");

                    overflow.Send(GetRequest(harness.Port, "/events?since=5&timeout_ms=2000"));
                    var watch = new Stopwatch();
                    watch.Start();
                    string response = ReadWholeResponse(harness, overflow, 1000);
                    watch.Stop();
                    Assert.True(watch.ElapsedMilliseconds < 900,
                        "a capped long poll must be refused immediately, not wait its timeout");
                    Assert.Contains(Body(response), "\"busy\":true", "the refusal is explicit");
                    Assert.Contains(Body(response), "\"timeout\":true", "the envelope stays stable");
                    Assert.Contains(Body(response), "\"changed\":false", "no change is reported");
                    Assert.Equal(1, harness.Server.EventWaiterCount, "the overflow client never waits");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }

        // ----------------------------------------------------------- Undelivered

        [Test]
        public static void Oversized_Action_Response_Is_Recorded_As_Undelivered()
        {
            int dispatches = 0;
            long revision = 3;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return revision; },
                delegate(string op, JObject request)
                {
                    dispatches++;
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":true,\"blob\":\"" + new string('z', 100 * 1024) + "\"}");
                },
                new ActionHistory(10),
                delegate { return revision; },
                Token,
                delegate { return WriteGateDecision.Allow("VERSION 1-Rev1", Mvid, "VERSION 1-Rev1", Mvid); });

            BridgeLimits limits = Limits();
            limits.MaxPendingBytesPerClient = 2048;

            Harness harness = Start(delegate { return "{\"rev\":3}"; }, router.Handle, limits,
                delegate { return revision; }, router.MarkResponseUndelivered);
            try
            {
                const string body = "{\"op\":\"store_huge\",\"action_id\":\"act-huge\",\"rev\":3}";

                using (var client = new RawClient(harness.Port))
                {
                    client.Send(PostRequest(harness.Port, body));
                    harness.Pump(2);
                    Assert.True(client.WaitForClose(1000),
                        "an action response over the pending limit must be refused, not buffered");
                }

                harness.TickUntil(delegate { return harness.Server.UndeliveredResponses >= 1; }, 1000,
                    "the refused response is reported as undelivered");
                Assert.True(LogContains(harness, "output limit"), "the refusal is logged");

                // The response body itself is larger than the pending limit, so it
                // can never be redelivered. D1/D2: the execution evidence stays
                // queryable through GET /operation instead of being rewritten to
                // outcome_unknown, and a retry does not dispatch again.
                Newtonsoft.Json.Linq.JObject operation =
                    router.GetOperationJson("act-huge");
                Assert.True((bool)operation["ok"], "the completed business fact is preserved");
                Assert.Equal("completed", (string)operation["execution_status"],
                    "execution stays completed");
                Assert.Equal("undelivered", (string)operation["delivery_status"],
                    "the lost response is recorded on the delivery axis");
                Assert.False(operation.ToString().Contains("outcome_unknown"),
                    "the execution outcome is no longer rewritten");
                Assert.Equal(1, dispatches, "no second dispatch");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [Test]
        public static void Undelivered_Write_Keeps_Completed_Evidence_On_Retry()
        {
            int dispatches = 0;
            long revision = 3;
            var router = new ActionRouter(
                new ActionGate(),
                delegate { return revision; },
                delegate(string op, JObject request)
                {
                    dispatches++;
                    return ActionResult.SuccessRaw(op,
                        "{\"ok\":true,\"blob\":\"" + new string('y', 4 * 1024) + "\"}");
                },
                new ActionHistory(10),
                delegate { return revision; },
                Token,
                delegate { return WriteGateDecision.Allow("VERSION 1-Rev1", Mvid, "VERSION 1-Rev1", Mvid); });

            BridgeLimits limits = Limits();
            limits.MaxWriteBytesPerTickPerClient = 1024;
            limits.MaxWriteBytesPerTickTotal = 1024;
            limits.WriteTimeoutMs = 100;

            Harness harness = Start(delegate { return "{\"rev\":3}"; }, router.Handle, limits,
                delegate { return revision; }, router.MarkResponseUndelivered);
            try
            {
                const string body = "{\"op\":\"store_blob\",\"action_id\":\"act-1\",\"rev\":3}";

                using (var client = new RawClient(harness.Port))
                {
                    client.Send(PostRequest(harness.Port, body));
                    harness.Pump(1);
                    Assert.Equal(1, dispatches, "the write was dispatched once");
                    Assert.True(client.WaitForData(1000), "the response started to arrive");

                    // Kill the connection after the dispatch but before the whole
                    // 400 KiB response is flushed: the outcome becomes unknown.
                    client.Abort();
                }

                harness.TickUntil(delegate { return harness.Server.UndeliveredResponses >= 1; }, 3000,
                    "a dispatched response that cannot be delivered must be recorded");
                Assert.True(LogContains(harness, "undelivered"), "undelivered responses are logged");

                using (var retry = new RawClient(harness.Port))
                {
                    retry.Send(PostRequest(harness.Port, body));
                    string response = ReadWholeResponse(harness, retry, 5000);
                    string responseBody = Body(response);
                    Assert.Contains(responseBody, "\"ok\":true",
                        "D1: the completed business fact survives the lost response");
                    Assert.Contains(responseBody, "\"delivery_status\":\"undelivered\"",
                        "the delivery axis records the lost response");
                    Assert.Contains(responseBody, "\"redelivered\":true",
                        "the retry is flagged as a re-delivery");
                    Assert.Equal(1, dispatches, "the retry must not dispatch the write again");
                }
            }
            finally
            {
                harness.Dispose();
            }
        }
    }
}
