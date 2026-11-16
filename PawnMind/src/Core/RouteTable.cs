using System;

namespace PawnMind.Core
{
    public enum RouteKind
    {
        Health,
        State,
        Action,
        Events,
        Operation,
        NotFound,
        MethodNotAllowed
    }

    public static class RouteTable
    {
        public static RouteKind Match(string method, string path)
        {
            string normalizedMethod = (method ?? string.Empty).ToUpperInvariant();
            string normalizedPath = Normalize(path);

            if (normalizedPath == "/health")
                return normalizedMethod == "GET" ? RouteKind.Health : RouteKind.MethodNotAllowed;

            if (normalizedPath == "/state")
                return normalizedMethod == "GET" ? RouteKind.State : RouteKind.MethodNotAllowed;

            if (normalizedPath == "/action")
                return normalizedMethod == "POST" ? RouteKind.Action : RouteKind.MethodNotAllowed;

            if (normalizedPath == "/events")
                return normalizedMethod == "GET" ? RouteKind.Events : RouteKind.MethodNotAllowed;

            // D2: read-only operation query. It never builds the game snapshot and
            // is not a SNAPSHOT_READER; it only reads the in-process action cache.
            if (normalizedPath == "/operation")
                return normalizedMethod == "GET" ? RouteKind.Operation : RouteKind.MethodNotAllowed;

            return RouteKind.NotFound;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
                return path.TrimEnd('/');
            return path;
        }
    }
}
