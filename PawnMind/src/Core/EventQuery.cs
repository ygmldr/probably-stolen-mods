using System;

namespace PawnMind.Core
{
    public sealed class EventQuery
    {
        public long Since = -1;
        public int TimeoutMs = 25000;
    }

    public static class EventQueryParser
    {
        public const int DefaultTimeoutMs = 25000;
        public const int MinTimeoutMs = 100;
        public const int MaxTimeoutMs = 30000;

        public static EventQuery Parse(string query)
        {
            var result = new EventQuery();
            if (string.IsNullOrEmpty(query)) return result;

            string[] parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;

                string key = part.Substring(0, equals);
                string value = Uri.UnescapeDataString(part.Substring(equals + 1));

                if (string.Equals(key, "since", StringComparison.OrdinalIgnoreCase))
                {
                    long since;
                    if (long.TryParse(value, out since)) result.Since = since;
                }
                else if (string.Equals(key, "timeout_ms", StringComparison.OrdinalIgnoreCase))
                {
                    int timeout;
                    if (int.TryParse(value, out timeout))
                    {
                        if (timeout < MinTimeoutMs) timeout = MinTimeoutMs;
                        if (timeout > MaxTimeoutMs) timeout = MaxTimeoutMs;
                        result.TimeoutMs = timeout;
                    }
                }
            }

            return result;
        }
    }
}
