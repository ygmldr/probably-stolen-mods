using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Transport bounds for <see cref="BridgeServer"/>. Everything is a hard cap:
    /// connections, pending output bytes, and the number of bytes written by one
    /// Tick. A client that cannot keep up is closed and logged instead of
    /// consuming unbounded memory or main-thread time.
    /// </summary>
    public sealed class BridgeLimits
    {
        public int MaxConnections { get; set; }
        public int MaxEventWaiters { get; set; }
        public int MaxPendingBytesPerClient { get; set; }
        public int MaxWriteBytesPerTickPerClient { get; set; }
        public int MaxWriteBytesPerTickTotal { get; set; }
        public int WriteTimeoutMs { get; set; }
        public int RequestTimeoutMs { get; set; }
        public int EventCheckIntervalMs { get; set; }
        public int MaxAcceptsPerTick { get; set; }
        public int MaxReadsPerTickPerClient { get; set; }
        public int ReadChunkBytes { get; set; }

        public BridgeLimits()
        {
            MaxConnections = 16;
            MaxEventWaiters = 8;
            MaxPendingBytesPerClient = 1024 * 1024;
            MaxWriteBytesPerTickPerClient = 64 * 1024;
            MaxWriteBytesPerTickTotal = 256 * 1024;
            WriteTimeoutMs = 5000;
            RequestTimeoutMs = 10000;
            EventCheckIntervalMs = 200;
            MaxAcceptsPerTick = 8;
            MaxReadsPerTickPerClient = 16;
            ReadChunkBytes = 4096;
        }

        /// <summary>
        /// Clamps every value into a range that cannot break the Tick loop
        /// (zero/negative budgets or timeouts must not spin or hang).
        /// </summary>
        internal BridgeLimits Copy()
        {
            var copy = new BridgeLimits
            {
                MaxConnections = Clamp(MaxConnections, 1, 128),
                MaxEventWaiters = Clamp(MaxEventWaiters, 1, 64),
                MaxPendingBytesPerClient = Clamp(MaxPendingBytesPerClient, 256, 16 * 1024 * 1024),
                MaxWriteBytesPerTickPerClient = Clamp(MaxWriteBytesPerTickPerClient, 1, 4 * 1024 * 1024),
                MaxWriteBytesPerTickTotal = Clamp(MaxWriteBytesPerTickTotal, 1, 16 * 1024 * 1024),
                WriteTimeoutMs = Clamp(WriteTimeoutMs, 1, 300000),
                RequestTimeoutMs = Clamp(RequestTimeoutMs, 1, 600000),
                EventCheckIntervalMs = Clamp(EventCheckIntervalMs, 1, 60000),
                MaxAcceptsPerTick = Clamp(MaxAcceptsPerTick, 1, 256),
                MaxReadsPerTickPerClient = Clamp(MaxReadsPerTickPerClient, 1, 256),
                ReadChunkBytes = Clamp(ReadChunkBytes, 1, 65536)
            };
            return copy;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1 server bound to 127.0.0.1, driven entirely from the Unity
    /// main thread (Tick). No threads, thread pool, tasks or coroutines: the game's
    /// next scanner refuses mods that keep code running in the background.
    ///
    /// S6A transport contract:
    ///  - sockets are non-blocking and every Tick writes only a bounded number of
    ///    bytes, so a slow or non-reading client can never stall a frame;
    ///  - pending output is bounded per connection; unsent data is never silently
    ///    dropped, and a client is closed only after the failure is logged;
    ///  - the number of connections and event long pollers is bounded, and a new
    ///    connection over the limit is accepted and closed immediately (never
    ///    queued into the client list);
    ///  - if a write action was dispatched but its response could not be
    ///    delivered, the transport reports it so the action cache can answer the
    ///    next retry with an explicit unknown outcome instead of success.
    /// </summary>
    public sealed class BridgeServer
    {
        private enum ClientRead
        {
            Incomplete,
            Ready,
            Rejected,
            Closed
        }

        private enum FlushResult
        {
            Drained,
            Pending,
            Aborted
        }

        private sealed class Client
        {
            public Socket Socket;
            public MemoryStream Buffer;
            public DateTime Deadline;
            public bool WaitingEvents;
            public long EventSince;
            public DateTime EventDeadline;
            public DateTime NextEventCheck;

            public byte[] OutBytes;
            public int OutOffset;
            public int OutLength;
            public DateTime OutDeadline;
            public string RequestBody;
            public string ResponseBody;
            public bool Dispatched;
            public bool UndeliveredReported;
            public bool Closed;
        }

        private readonly BridgeConfig _config;
        private readonly Func<string> _stateProvider;
        private readonly Func<string, string> _actionHandler;
        private readonly Action<string> _log;
        private readonly string _version;
        private readonly Func<long> _revision;
        private readonly Func<BridgeHealth> _healthProvider;
        private readonly BridgeLimits _limits;
        private readonly Action<string, string> _undeliveredHandler;
        private readonly List<Client> _clients = new List<Client>();

        private TcpListener _listener;
        private bool _running;

        public BridgeServer(
            BridgeConfig config,
            Func<string> stateProvider,
            Func<string, string> actionHandler,
            Action<string> log,
            string version = "unknown",
            Func<long> revisionProvider = null,
            Func<BridgeHealth> healthProvider = null,
            BridgeLimits limits = null,
            Action<string, string> undeliveredHandler = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (stateProvider == null) throw new ArgumentNullException("stateProvider");
            if (actionHandler == null) throw new ArgumentNullException("actionHandler");

            _config = config;
            _stateProvider = stateProvider;
            _actionHandler = actionHandler;
            _log = log ?? delegate { };
            _version = string.IsNullOrEmpty(version) ? "unknown" : version;
            _revision = revisionProvider;
            _healthProvider = healthProvider;
            _limits = (limits ?? new BridgeLimits()).Copy();
            _undeliveredHandler = undeliveredHandler;
        }

        /// <summary>Number of accepted (not yet closed) connections.</summary>
        public int ConnectionCount
        {
            get { return _clients.Count; }
        }

        /// <summary>Number of clients currently parked in a /events long poll.</summary>
        public int EventWaiterCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _clients.Count; i++)
                {
                    if (_clients[i].WaitingEvents) count++;
                }

                return count;
            }
        }

        /// <summary>Unsent response bytes across all clients (diagnostics).</summary>
        public int PendingOutputBytes
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _clients.Count; i++) total += _clients[i].OutLength;
                return total;
            }
        }

        public long RejectedConnections { get; private set; }

        public long UndeliveredResponses { get; private set; }

        public void Start()
        {
            string error;
            if (!_config.TryValidate(out error)) throw new InvalidOperationException(error);

            _listener = new TcpListener(IPAddress.Loopback, _config.Port);
            _listener.Server.Blocking = false;
            _listener.Start();
            _running = true;
        }

        public void Stop()
        {
            _running = false;

            for (int i = 0; i < _clients.Count; i++)
            {
                Client client = _clients[i];
                if (client.OutLength > 0) ReportUndelivered(client);
                client.Closed = true;
                try
                {
                    client.Socket.Close();
                }
                catch
                {
                }
            }

            _clients.Clear();

            try
            {
                if (_listener != null) _listener.Stop();
            }
            catch
            {
            }

            _listener = null;
        }

        public void Tick()
        {
            if (!_running) return;

            AcceptPending();

            int writeBudget = _limits.MaxWriteBytesPerTickTotal;

            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                Client client = _clients[i];
                try
                {
                    if (client.Closed) continue;

                    if (client.OutLength > 0)
                    {
                        if (Flush(client, ref writeBudget) == FlushResult.Drained) Close(client);
                        continue;
                    }

                    if (DateTime.UtcNow > client.Deadline)
                    {
                        Close(client);
                        continue;
                    }

                    if (client.WaitingEvents)
                    {
                        TickEvents(client);
                        if (client.OutLength > 0
                            && Flush(client, ref writeBudget) == FlushResult.Drained)
                        {
                            Close(client);
                        }

                        continue;
                    }

                    string raw;
                    int errorStatus;
                    string errorCode;
                    ClientRead read = TryRead(client, out raw, out errorStatus, out errorCode);
                    if (read == ClientRead.Closed)
                    {
                        Close(client);
                        continue;
                    }

                    if (read == ClientRead.Incomplete) continue;

                    if (read == ClientRead.Rejected)
                    {
                        QueueResponse(client, errorStatus, Reason(errorStatus), ErrorJson(errorCode));
                    }
                    else
                    {
                        HandleRaw(client, raw);
                    }

                    if (client.OutLength > 0)
                    {
                        if (Flush(client, ref writeBudget) == FlushResult.Drained) Close(client);
                    }
                    else if (!client.WaitingEvents)
                    {
                        // The single request/response exchange is over. Event
                        // clients stay parked until their response is queued.
                        Close(client);
                    }
                }
                catch (Exception e)
                {
                    _log("[PawnMind] connection failed: " + e.GetType().Name);
                    Close(client);
                }
            }
        }

        private void AcceptPending()
        {
            if (_listener == null) return;

            for (int guard = 0; guard < _limits.MaxAcceptsPerTick; guard++)
            {
                bool pending;
                try
                {
                    pending = _listener.Server.Poll(0, SelectMode.SelectRead);
                }
                catch (Exception)
                {
                    return;
                }

                if (!pending) return;

                Socket socket;
                try
                {
                    socket = _listener.AcceptSocket();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    socket.Blocking = false;
                }
                catch (Exception)
                {
                    try { socket.Close(); } catch { }
                    continue;
                }

                if (_clients.Count >= _limits.MaxConnections)
                {
                    // Refuse immediately: never add the socket to the client list,
                    // so a burst cannot turn into unbounded buffered work.
                    RejectedConnections++;
                    _log("[PawnMind] connection limit reached (" + _limits.MaxConnections
                        + "); refusing new client");
                    try { socket.Close(); } catch { }
                    continue;
                }

                _clients.Add(new Client
                {
                    Socket = socket,
                    Buffer = new MemoryStream(),
                    Deadline = DateTime.UtcNow.AddMilliseconds(_limits.RequestTimeoutMs)
                });
            }
        }

        private ClientRead TryRead(Client client, out string raw, out int errorStatus, out string errorCode)
        {
            raw = null;
            errorStatus = 0;
            errorCode = null;

            // A peer may close while it has no unread bytes; Socket.Available
            // alone cannot see that, so an idle connection would otherwise stay
            // in the list until the request deadline.
            try
            {
                if (client.Socket.Poll(0, SelectMode.SelectRead) && client.Socket.Available == 0)
                    return ClientRead.Closed;
            }
            catch (Exception)
            {
                return ClientRead.Closed;
            }

            for (int guard = 0; guard < _limits.MaxReadsPerTickPerClient; guard++)
            {
                int available;
                try
                {
                    available = client.Socket.Available;
                }
                catch (Exception)
                {
                    return ClientRead.Closed;
                }

                if (available <= 0) break;

                int size = Math.Min(available, _limits.ReadChunkBytes);
                var chunk = new byte[size];
                int read;
                try
                {
                    read = client.Socket.Receive(chunk);
                }
                catch (SocketException e)
                {
                    if (IsRetryable(e.SocketErrorCode)) break;
                    return ClientRead.Closed;
                }
                catch (ObjectDisposedException)
                {
                    return ClientRead.Closed;
                }

                if (read <= 0) return ClientRead.Closed;
                client.Buffer.Write(chunk, 0, read);
            }

            if (client.Buffer.Length
                > HttpRequestParser.MaxHeaderBytes + HttpRequestParser.MaxBodyBytes + 4096)
            {
                errorStatus = 413;
                errorCode = "body_too_large";
                return ClientRead.Rejected;
            }

            if (client.Buffer.Length == 0) return ClientRead.Incomplete;

            byte[] data = client.Buffer.ToArray();
            int headerLength = FindHeaderEnd(data);
            if (headerLength < 0)
            {
                if (data.Length > HttpRequestParser.MaxHeaderBytes)
                {
                    errorStatus = 413;
                    errorCode = "header_too_large";
                    return ClientRead.Rejected;
                }

                return ClientRead.Incomplete;
            }

            if (headerLength > HttpRequestParser.MaxHeaderBytes)
            {
                errorStatus = 413;
                errorCode = "header_too_large";
                return ClientRead.Rejected;
            }

            string headerText = Encoding.ASCII.GetString(data, 0, headerLength);
            int contentLength = ParseContentLength(headerText);
            if (contentLength < 0)
            {
                errorStatus = 400;
                errorCode = "unsupported_transfer_encoding";
                return ClientRead.Rejected;
            }

            if (contentLength > HttpRequestParser.MaxBodyBytes)
            {
                errorStatus = 413;
                errorCode = "body_too_large";
                return ClientRead.Rejected;
            }

            if (data.Length < headerLength + contentLength) return ClientRead.Incomplete;

            raw = Encoding.UTF8.GetString(data, 0, headerLength + contentLength);
            return ClientRead.Ready;
        }

        private void HandleRaw(Client client, string raw)
        {
            HttpRequest request;
            string parseError;
            if (!HttpRequestParser.TryParse(raw, out request, out parseError))
            {
                QueueResponse(client, 400, "Bad Request", ErrorJson(parseError));
                return;
            }

            if (!IsLocalHost(request))
            {
                QueueResponse(client, 403, "Forbidden", ErrorJson("host_not_allowed"));
                return;
            }

            switch (RouteTable.Match(request.Method, request.Path))
            {
                case RouteKind.Health:
                    QueueResponse(client, 200, "OK", HealthJson());
                    return;

                case RouteKind.State:
                    if (!Authorized(request))
                    {
                        QueueResponse(client, 401, "Unauthorized", ErrorJson("unauthorized"));
                        return;
                    }

                    QueueResponse(client, 200, "OK", _stateProvider());
                    return;

                case RouteKind.Action:
                    if (!Authorized(request))
                    {
                        QueueResponse(client, 401, "Unauthorized", ErrorJson("unauthorized"));
                        return;
                    }

                    // The handler runs to completion on this thread; the result is
                    // queued and only delivered across later Ticks. If delivery
                    // fails the router is told so a retry cannot look successful.
                    client.RequestBody = request.Body;
                    client.Dispatched = true;
                    QueueResponse(client, 200, "OK", _actionHandler(request.Body));
                    return;

                case RouteKind.Events:
                    if (!Authorized(request))
                    {
                        QueueResponse(client, 401, "Unauthorized", ErrorJson("unauthorized"));
                        return;
                    }

                    HandleEvents(client, request);
                    return;

                case RouteKind.MethodNotAllowed:
                    QueueResponse(client, 405, "Method Not Allowed", ErrorJson("method_not_allowed"));
                    return;

                default:
                    QueueResponse(client, 404, "Not Found", ErrorJson("not_found"));
                    return;
            }
        }

        private void QueueResponse(Client client, int status, string reason, string body)
        {
            if (client.Closed) return;

            body = body ?? string.Empty;
            string response = HttpResponse.Build(status, reason, "application/json; charset=utf-8", body);
            byte[] bytes = Encoding.UTF8.GetBytes(response);

            if (bytes.Length > _limits.MaxPendingBytesPerClient)
            {
                _log("[PawnMind] response dropped: output limit exceeded ("
                    + bytes.Length + " > " + _limits.MaxPendingBytesPerClient + " bytes)");
                client.ResponseBody = body;
                ReportUndelivered(client);
                Close(client);
                return;
            }

            client.OutBytes = bytes;
            client.OutOffset = 0;
            client.OutLength = bytes.Length;
            client.OutDeadline = DateTime.UtcNow.AddMilliseconds(_limits.WriteTimeoutMs);
            client.ResponseBody = body;
            client.WaitingEvents = false;
        }

        private FlushResult Flush(Client client, ref int budget)
        {
            if (client.Closed) return FlushResult.Aborted;
            if (client.OutLength <= 0) return FlushResult.Drained;

            if (DateTime.UtcNow > client.OutDeadline)
            {
                _log("[PawnMind] write timeout: client did not accept the response within "
                    + _limits.WriteTimeoutMs + " ms");
                ReportUndelivered(client);
                Close(client);
                return FlushResult.Aborted;
            }

            int allowance = Math.Min(_limits.MaxWriteBytesPerTickPerClient, budget);
            if (allowance <= 0) return FlushResult.Pending;

            int toSend = Math.Min(allowance, client.OutLength);

            try
            {
                // Non-blocking socket plus a writability probe: Send may still
                // report a short count, which is why OutOffset is advanced and the
                // rest is retried on a later Tick.
                if (!client.Socket.Poll(0, SelectMode.SelectWrite)) return FlushResult.Pending;

                int sent = client.Socket.Send(client.OutBytes, client.OutOffset, toSend, SocketFlags.None);
                if (sent <= 0)
                {
                    ReportUndelivered(client);
                    Close(client);
                    return FlushResult.Aborted;
                }

                client.OutOffset += sent;
                client.OutLength -= sent;
                budget -= sent;

                return client.OutLength == 0 ? FlushResult.Drained : FlushResult.Pending;
            }
            catch (SocketException e)
            {
                if (IsRetryable(e.SocketErrorCode)) return FlushResult.Pending;

                _log("[PawnMind] send failed: " + e.SocketErrorCode + "; response undelivered");
                ReportUndelivered(client);
                Close(client);
                return FlushResult.Aborted;
            }
            catch (ObjectDisposedException)
            {
                ReportUndelivered(client);
                Close(client);
                return FlushResult.Aborted;
            }
        }

        private void ReportUndelivered(Client client)
        {
            if (client == null || client.UndeliveredReported) return;
            client.UndeliveredReported = true;

            if (!client.Dispatched || string.IsNullOrEmpty(client.ResponseBody)) return;

            UndeliveredResponses++;
            _log("[PawnMind] undelivered response: client disconnected before the result was sent");

            Action<string, string> handler = _undeliveredHandler;
            if (handler == null) return;

            try
            {
                handler(client.RequestBody, client.ResponseBody);
            }
            catch (Exception e)
            {
                _log("[PawnMind] undelivered hook failed: " + e.GetType().Name);
            }
        }

        private string HealthJson()
        {
            var payload = new JObject();
            payload["ok"] = true;
            payload["name"] = "pawnmind";
            payload["version"] = _version;
            payload["mod_version"] = _version;

            BridgeHealth health = null;
            try
            {
                if (_healthProvider != null) health = _healthProvider();
            }
            catch (Exception)
            {
                // A health provider must never break the endpoint or unlock writes.
                health = null;
            }

            if (health == null)
            {
                payload["game_version"] = "UNKNOWN";
                payload["game_assembly_mvid"] = "UNKNOWN";
                payload["expected_game_version"] = null;
                payload["expected_assembly_mvid"] = null;
                payload["write_gate"] = GameVersionGate.StateLocked;
                payload["write_gate_reason"] = GameVersionGate.ReasonUnconfigured;
            }
            else
            {
                bool gateOpen = string.Equals(
                    health.WriteGateState,
                    GameVersionGate.StateOpen,
                    StringComparison.Ordinal);
                payload["game_version"] = NullIfEmpty(health.GameVersion);
                payload["game_assembly_mvid"] = NullIfEmpty(health.GameAssemblyMvid);
                payload["expected_game_version"] = NullIfEmpty(health.ExpectedGameVersion);
                payload["expected_assembly_mvid"] = NullIfEmpty(health.ExpectedAssemblyMvid);
                payload["write_gate"] = gateOpen
                    ? GameVersionGate.StateOpen
                    : GameVersionGate.StateLocked;
                payload["write_gate_reason"] = gateOpen
                    ? null
                    : (string.IsNullOrEmpty(health.WriteGateReason)
                        ? GameVersionGate.ReasonUnconfigured
                        : health.WriteGateReason);
            }

            // This code path cannot check the Steam stable branch. Never claim the
            // installed build is the latest; S8 needs a manual confirmation.
            payload["latest"] = GameVersionGate.LatestUnverified;
            payload["latest_verified"] = false;

            return ScrubHealth(payload.ToString(Formatting.None));
        }

        private string ScrubHealth(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            if (string.IsNullOrEmpty(_config.Token) || _config.Token.Length < 8) return json;

            string scrubbed = json.Replace(_config.Token, "[redacted]");

            // Also scrub the JSON-escaped form for unusually configured tokens.
            string escaped = JsonConvert.ToString(_config.Token);
            if (escaped.Length >= 2)
            {
                escaped = escaped.Substring(1, escaped.Length - 2);
                if (!string.Equals(escaped, _config.Token, StringComparison.Ordinal))
                {
                    scrubbed = scrubbed.Replace(escaped, "[redacted]");
                }
            }

            return scrubbed;
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private void HandleEvents(Client client, HttpRequest request)
        {
            EventQuery query = EventQueryParser.Parse(request.Query);
            long current = _revision == null ? 0 : _revision();
            if (query.Since < 0 || current != query.Since)
            {
                string state = _stateProvider();
                long rev = _revision == null ? ExtractRev(state) : _revision();
                QueueResponse(client, 200, "OK", ChangedJson(rev, state));
                return;
            }

            if (EventWaiterCount >= _limits.MaxEventWaiters)
            {
                // Refuse immediately with the stable timeout envelope. The caller
                // can retry later; nothing is parked on the main thread.
                QueueResponse(client, 200, "OK", BusyEventsJson(current));
                return;
            }

            DateTime now = DateTime.UtcNow;
            client.WaitingEvents = true;
            client.EventSince = query.Since;
            client.EventDeadline = now.AddMilliseconds(query.TimeoutMs);
            client.NextEventCheck = now;
            client.Deadline = client.EventDeadline.AddMilliseconds(_limits.WriteTimeoutMs);
        }

        private void TickEvents(Client client)
        {
            DateTime now = DateTime.UtcNow;
            if (now >= client.EventDeadline)
            {
                long rev = _revision == null ? 0 : _revision();
                QueueResponse(client, 200, "OK", TimeoutEventsJson(rev));
                return;
            }

            if (now < client.NextEventCheck) return;
            client.NextEventCheck = now.AddMilliseconds(_limits.EventCheckIntervalMs);

            string state;
            try
            {
                state = _stateProvider();
            }
            catch (Exception)
            {
                long fallback = _revision == null ? 0 : _revision();
                QueueResponse(client, 200, "OK", TimeoutEventsJson(fallback));
                return;
            }

            long current = _revision == null ? ExtractRev(state) : _revision();
            if (current == client.EventSince) return;

            QueueResponse(client, 200, "OK", ChangedJson(current, state));
        }

        private static string ChangedJson(long rev, string state)
        {
            return "{\"ok\":true,\"rev\":" + rev + ",\"changed\":true,\"state\":" + (state ?? "{}") + "}";
        }

        private static string TimeoutEventsJson(long rev)
        {
            return "{\"ok\":true,\"rev\":" + rev + ",\"changed\":false,\"timeout\":true}";
        }

        private static string BusyEventsJson(long rev)
        {
            return "{\"ok\":true,\"rev\":" + rev + ",\"changed\":false,\"timeout\":true,\"busy\":true}";
        }

        private static long ExtractRev(string state)
        {
            try
            {
                JObject json = JObject.Parse(state);
                JToken rev = json["rev"];
                return rev == null ? 0 : (long)rev;
            }
            catch
            {
                return 0;
            }
        }

        private bool Authorized(HttpRequest request)
        {
            if (!request.Headers.ContainsKey("Authorization")) return false;
            return TokenAuth.IsAuthorized(request.Headers["Authorization"], _config.Token);
        }

        private static bool IsLocalHost(HttpRequest request)
        {
            if (!request.Headers.ContainsKey("Host")) return true;

            string host = (request.Headers["Host"] ?? string.Empty).Trim().ToLowerInvariant();
            return host.StartsWith("127.0.0.1", StringComparison.Ordinal)
                || host.StartsWith("localhost", StringComparison.Ordinal)
                || host.StartsWith("[::1]", StringComparison.Ordinal);
        }

        private void Close(Client client)
        {
            if (client == null || client.Closed) return;
            client.Closed = true;

            try
            {
                client.Socket.Close();
            }
            catch
            {
            }

            _clients.Remove(client);
        }

        private static bool IsRetryable(SocketError error)
        {
            return error == SocketError.WouldBlock
                || error == SocketError.TryAgain
                || error == SocketError.NoBufferSpaceAvailable;
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                default: return "Error";
            }
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i + 4;
            }

            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 10 && data[i + 1] == 10) return i + 2;
            }

            return -1;
        }

        private static int ParseContentLength(string headerText)
        {
            int contentLength = 0;
            string[] lines = headerText.Replace("\r\n", "\n").Split('\n');

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();

                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (!int.TryParse(value, out parsed) || parsed < 0) return -1;
                    contentLength = parsed;
                }
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    return -1;
                }
            }

            return contentLength;
        }

        private static string ErrorJson(string code)
        {
            return "{\"ok\":false,\"error\":\"" + (code ?? "error") + "\"}";
        }
    }
}
