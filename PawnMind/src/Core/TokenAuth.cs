using System;

namespace PawnMind.Core
{
    public static class TokenAuth
    {
        private const string Scheme = "Bearer ";

        public static bool IsAuthorized(string authorizationHeader, string expectedToken)
        {
            if (string.IsNullOrEmpty(expectedToken)) return false;
            if (string.IsNullOrEmpty(authorizationHeader)) return false;
            if (authorizationHeader.Length <= Scheme.Length) return false;
            if (!authorizationHeader.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

            string presented = authorizationHeader.Substring(Scheme.Length).Trim();
            return FixedTimeEquals(presented, expectedToken);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;

            int diff = a.Length ^ b.Length;
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}
