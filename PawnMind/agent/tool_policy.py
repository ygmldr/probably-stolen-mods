"""Per-tool purity / mode policy, derived from the audit matrix.

Every MCP tool must be classified here before it may appear in a mode's
allowlist. The consistency test fails when a tool is added to the server but not
classified, so a new tool cannot silently inherit "safe by default".
"""

# Reads that change game state on the read path. Kept explicit even while the
# write paths are being fixed: a tool here never enters strict_player.
IMPURE_READS = {
    "bargain_preview": "mutating",   # GetBargainSuccessChance -> Refresh()
    "get_state": "cached_impure",    # snapshot getters call Refresh() when the client changes
    "get_clientele": "cached_impure",  # first read rolls RNG twice, then caches per day
    "barter_status": "cached_impure",  # delegate-based, currently pure but not proven
}

# Tools that exist for diagnostics/privilege and have no player-reachable path.
PRIVILEGED = {
    "save_game",
    "save_recording_game",
    "new_game",
    "act",
    "press",
    "get_ui_snapshot",
    "wait_until",
    "wine_add_water",
    "dispatch_expedition",
    "advance_expedition",
}

# Never exported in a recording session, regardless of what the server registers.
STRICT_DENY = PRIVILEGED | {"bargain_preview", "get_clientele"}


def purity(tool_name, read_only_hint):
    """Classify a tool: pure | cached_impure | mutating | write."""
    if tool_name in IMPURE_READS:
        return IMPURE_READS[tool_name]
    if read_only_hint:
        return "pure"
    return "write"


def allowed_in(mode, tool_name, read_only_hint):
    """The one authoritative answer for whether a mode may expose a tool."""
    if mode == "test":
        return True
    if tool_name in STRICT_DENY:
        return False
    if mode == "assisted":
        return True
    if mode == "strict_player":
        return purity(tool_name, read_only_hint) in ("pure", "write")
    raise ValueError("unknown mode %s" % mode)


def strict_allowlist(full_tools):
    """Recording allowlist generated from the registry, never hand written."""
    return sorted(
        t["name"] for t in full_tools
        if allowed_in("strict_player", t["name"], t.get("readOnlyHint"))
    )
