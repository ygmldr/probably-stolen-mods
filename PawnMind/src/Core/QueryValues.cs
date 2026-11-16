using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Minimal query-string reader for the loopback endpoints. The bridge only
    /// accepts flat name/value pairs (no arrays), values are URL-decoded, and a
    /// missing name returns null instead of throwing.
    /// </summary>
    public static class QueryValues
    {
        public static string Get(string query, string name)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(name)) return null;

            string value = query;
            int question = value.IndexOf('?');
            if (question >= 0) value = value.Substring(question + 1);
            if (value.StartsWith("#", StringComparison.Ordinal)) return null;

            string[] parts = value.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0) continue;

                int equals = part.IndexOf('=');
                string key = equals < 0 ? part : part.Substring(0, equals);
                if (key.Length == 0) continue;

                if (!string.Equals(Decode(key), name, StringComparison.Ordinal)) continue;
                return equals < 0 ? string.Empty : Decode(part.Substring(equals + 1));
            }

            return null;
        }

        private static string Decode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
