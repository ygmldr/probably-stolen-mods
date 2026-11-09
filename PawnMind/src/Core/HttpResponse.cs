using System.Text;

namespace PawnMind.Core
{
    public static class HttpResponse
    {
        public static string Build(int status, string reason, string contentType, string body)
        {
            body = body ?? string.Empty;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            var builder = new StringBuilder(bodyBytes.Length + 160);
            builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason ?? "OK").Append("\r\n");
            builder.Append("Content-Type: ").Append(contentType ?? "application/json; charset=utf-8").Append("\r\n");
            builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            builder.Append("Connection: close\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("X-Content-Type-Options: nosniff\r\n");
            builder.Append("\r\n");
            builder.Append(body);
            return builder.ToString();
        }
    }
}
