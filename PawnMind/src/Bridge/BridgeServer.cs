using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PawnMind.Core;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Minimal HTTP/1.1 server bound to 127.0.0.1, driven entirely from the Unity
    /// main thread (Tick). No threads, thread pool, tasks or coroutines: the game's
    /// next scanner refuses mods that keep code running in the background.
    /// </summary>
    public sealed class BridgeServer
    {
        private sealed class Client
        {
            public Socket Socket;
            public MemoryStream Buffer;
            public DateTime Deadline;
        }

        private readonly BridgeConfig _config;
        private readonly Func<string> _stateProvider;
        private readonly Func<string, string> _actionHandler;
        private readonly Action<string> _log;
        private readonly string _version;
        private readonly List<Client> _clients = new List<Client>();

        private TcpListener _listener;
        private bool _running;

        public BridgeServer(
            BridgeConfig config,
            Func<string> stateProvider,
            Func<string, string> actionHandler,
            Action<string> log,
            string version = "unknown")
        {
            if (config == null) throw new ArgumentNullException("config");
            if (stateProvider == null) throw new ArgumentNullException("stateProvider");
            if (actionHandler == null) throw new ArgumentNullException("actionHandler");

            _config = config;
            _stateProvider = stateProvider;
            _actionHandler = actionHandler;
            _log = log ?? delegate { };
            _version = string.IsNullOrEmpty(version) ? "unknown" : version;
        }

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
                try
                {
                    _clients[i].Socket.Close();
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

            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                Client client = _clients[i];
                try
                {
                    if (DateTime.UtcNow > client.Deadline)
                    {
                        Close(client);
                        continue;
                    }

                    if (!ReadAvailable(client))
                    {
                        Close(client);
                        continue;
                    }

                    string raw;
                    if (!TryComplete(client, out raw)) continue;

                    HandleRaw(client.Socket, raw);
                    Close(client);
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

            for (int guard = 0; guard < 8; guard++)
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

                socket.Blocking = false;
                _clients.Add(new Client
                {
                    Socket = socket,
                    Buffer = new MemoryStream(),
                    Deadline = DateTime.UtcNow.AddSeconds(10)
                });
            }
        }

        private bool ReadAvailable(Client client)
        {
            int guard = 0;
            while (client.Socket.Available > 0 && guard++ < 16)
            {
                int size = Math.Min(client.Socket.Available, 4096);
                byte[] chunk = new byte[size];
                int read = client.Socket.Receive(chunk);
                if (read <= 0) return false;

                client.Buffer.Write(chunk, 0, read);
                if (client.Buffer.Length > HttpRequestParser.MaxHeaderBytes + HttpRequestParser.MaxBodyBytes)
                {
                    Respond(client.Socket, 413, "Payload Too Large", ErrorJson("body_too_large"));
                    return false;
                }
            }

            return true;
        }

        private bool TryComplete(Client client, out string raw)
        {
            raw = null;

            byte[] data = client.Buffer.ToArray();
            if (data.Length == 0) return false;

            int headerLength = FindHeaderEnd(data);
            if (headerLength < 0)
            {
                return false;
            }

            string headerText = Encoding.ASCII.GetString(data, 0, headerLength);
            int contentLength = ParseContentLength(headerText);
            if (contentLength < 0)
            {
                Respond(client.Socket, 400, "Bad Request", ErrorJson("unsupported_transfer_encoding"));
                return true;
            }

            if (contentLength > HttpRequestParser.MaxBodyBytes)
            {
                Respond(client.Socket, 413, "Payload Too Large", ErrorJson("body_too_large"));
                return true;
            }

            if (data.Length < headerLength + contentLength) return false;

            raw = Encoding.UTF8.GetString(data, 0, headerLength + contentLength);
            return true;
        }

        private void HandleRaw(Socket socket, string raw)
        {
            HttpRequest request;
            string parseError;
            if (!HttpRequestParser.TryParse(raw, out request, out parseError))
            {
                Respond(socket, 400, "Bad Request", ErrorJson(parseError));
                return;
            }

            if (!IsLocalHost(request))
            {
                Respond(socket, 403, "Forbidden", ErrorJson("host_not_allowed"));
                return;
            }

            switch (RouteTable.Match(request.Method, request.Path))
            {
                case RouteKind.Health:
                    Respond(socket, 200, "OK",
                        "{\"ok\":true,\"name\":\"pawnmind\",\"version\":\"" + _version + "\"}");
                    return;

                case RouteKind.State:
                    if (!Authorized(request))
                    {
                        Respond(socket, 401, "Unauthorized", ErrorJson("unauthorized"));
                        return;
                    }

                    Respond(socket, 200, "OK", _stateProvider());
                    return;

                case RouteKind.Action:
                    if (!Authorized(request))
                    {
                        Respond(socket, 401, "Unauthorized", ErrorJson("unauthorized"));
                        return;
                    }

                    Respond(socket, 200, "OK", _actionHandler(request.Body));
                    return;

                case RouteKind.MethodNotAllowed:
                    Respond(socket, 405, "Method Not Allowed", ErrorJson("method_not_allowed"));
                    return;

                default:
                    Respond(socket, 404, "Not Found", ErrorJson("not_found"));
                    return;
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
            try
            {
                client.Socket.Close();
            }
            catch
            {
            }

            _clients.Remove(client);
        }

        private static void Respond(Socket socket, int status, string reason, string body)
        {
            string response = HttpResponse.Build(status, reason, "application/json; charset=utf-8", body);
            byte[] bytes = Encoding.UTF8.GetBytes(response);

            try
            {
                socket.Blocking = true;
                socket.SendTimeout = 2000;
                socket.Send(bytes);
            }
            catch (Exception)
            {
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
