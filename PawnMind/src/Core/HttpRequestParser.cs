using System;
using System.Collections.Generic;

namespace PawnMind.Core
{
    public sealed class HttpRequest
    {
        public string Method { get; set; }
        public string Target { get; set; }
        public string Path { get; set; }
        public string Query { get; set; }
        public IDictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
    }

    /// <summary>
    /// Parses an already-read HTTP/1.1 request (headers + body) into a request object.
    /// The socket layer is responsible for size limits and for reading exactly
    /// Content-Length bytes of body.
    /// </summary>
    public static class HttpRequestParser
    {
        public const int MaxHeaderBytes = 8192;
        public const int MaxBodyBytes = 65536;

        public static bool TryParse(string raw, out HttpRequest request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrEmpty(raw))
            {
                error = "empty request";
                return false;
            }

            int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headerPart;
            string body;

            if (headerEnd >= 0)
            {
                headerPart = raw.Substring(0, headerEnd);
                body = raw.Substring(headerEnd + 4);
            }
            else
            {
                headerEnd = raw.IndexOf("\n\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    error = "missing header terminator";
                    return false;
                }

                headerPart = raw.Substring(0, headerEnd);
                body = raw.Substring(headerEnd + 2);
            }

            string[] lines = headerPart.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0)
            {
                error = "missing request line";
                return false;
            }

            string[] parts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = "malformed request line";
                return false;
            }

            string method = parts[0].Trim();
            string target = parts[1].Trim();
            if (method.Length == 0 || target.Length == 0 || target[0] != '/')
            {
                error = "malformed request target";
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    error = "malformed header line";
                    return false;
                }

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Length == 0)
                {
                    error = "empty header name";
                    return false;
                }

                if (!headers.ContainsKey(name)) headers[name] = value;
            }

            int question = target.IndexOf('?');
            string path = question >= 0 ? target.Substring(0, question) : target;
            string query = question >= 0 ? target.Substring(question + 1) : string.Empty;

            request = new HttpRequest
            {
                Method = method.ToUpperInvariant(),
                Target = target,
                Path = path,
                Query = query,
                Headers = headers,
                Body = body
            };
            return true;
        }
    }
}
