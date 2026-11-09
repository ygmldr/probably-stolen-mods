using System;

namespace PawnMind.Core
{
    public enum RouteKind
    {
        Health,
        State,
        Action,
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
