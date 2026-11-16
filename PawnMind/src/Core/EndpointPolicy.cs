namespace PawnMind.Core
{
    /// <summary>
    /// Mode gate for the raw HTTP endpoints. The MCP tool allowlist alone is not
    /// enough: a caller can address the loopback port directly, so "bootstrap
    /// must not read the state snapshot" has to hold for GET /state and
    /// GET /events as well as for the six MCP aliases.
    /// </summary>
    public static class EndpointPolicy
    {
        /// <summary>The operation name the snapshot endpoints map to.</summary>
        public const string StateReadOp = "get_state";

        /// <summary>The operation name GET /operation maps to.</summary>
        public const string OperationReadOp = "get_operation";

        /// <summary>Null when the endpoint may run, otherwise the refusal code.</summary>
        public static string Refusal(RouteKind kind)
        {
            switch (kind)
            {
                case RouteKind.Health:
                    // The only endpoint that stays open: an operator and the MCP
                    // client both need it to discover the current gate state.
                    return null;

                case RouteKind.NotFound:
                case RouteKind.MethodNotAllowed:
                    // No state is built; the generic responses leak nothing.
                    return null;

                case RouteKind.State:
                case RouteKind.Events:
                    return ModePolicy.Refusal(StateReadOp);

                case RouteKind.Operation:
                    // Querying an already-issued operation is a protocol necessity
                    // (it answers "what happened to my write?"), not a snapshot
                    // read: it is allowed in Bootstrap but never in Locked, and the
                    // per-mode allowlist still decides.
                    return ModePolicy.Refusal(OperationReadOp);

                case RouteKind.Action:
                    // Per-operation authorization happens inside ActionRouter
                    // before the cache probe and before any state refresh; here
                    // only the "nothing is configured" state is refused outright.
                    return ModePolicy.Mode == RunMode.Locked ? ModePolicy.Locked : null;

                default:
                    return ModePolicy.Forbidden;
            }
        }
    }
}
