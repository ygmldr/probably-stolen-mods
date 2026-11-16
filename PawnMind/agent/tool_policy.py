"""Declarative tool policy: one source, generated allowlists, deny by default.

``agent/policy/tool_policy.json`` is the only place that decides whether a tool
may appear in a mode. It is generated from the real ``tools/list`` snapshot and
the entry matrix, then reviewed by hand. Nothing here infers safety from
``readOnlyHint`` (it is null for every tool this server exports) and nothing
hand-writes an allowlist.
"""

import json
import os

POLICY_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "policy", "tool_policy.json")

MODES = ("bootstrap", "play", "test")

#: Tools whose implementation reads GET /state (or the long-poll that builds it).
#: D0a removed the Refresh()-backed getters from that path; the list is asserted
#: against the MCP server source so a new alias cannot be forgotten.
SNAPSHOT_READERS = frozenset({
    "get_state",
    "get_run_summary",
    "get_legal_actions",
    "wait_for_change",
    "wait_for_events",
    "wait_until",
})
DECISIONS = ("allow", "deny", "needs_evidence")


def load_policy(path=None):
    with open(path or POLICY_PATH, encoding="utf-8") as handle:
        return json.load(handle)


def decision_for(policy, tool_name):
    """Unknown tools are denied: a new tool must be reviewed, not inherited."""
    entry = policy.get("tools", {}).get(tool_name)
    if entry is None:
        return "deny"
    return entry.get("decision", "deny")


def allowlist(policy, mode):
    """Tools a mode may expose, sorted. Unknown modes expose nothing."""
    if mode not in MODES:
        return []
    return sorted(
        name for name, entry in policy.get("tools", {}).items()
        if entry.get("decision") == "allow" and entry.get("modes", {}).get(mode) is True
    )


def live_ready(policy):
    """Tools with real-machine evidence; empty until live tests are recorded."""
    return sorted(
        name for name, entry in policy.get("tools", {}).items()
        if entry.get("live_evidence") and entry.get("decision") == "allow"
    )


def play_allowlist(policy):
    """Strict-player exposure: allowed *and* backed by live evidence."""
    live = set(live_ready(policy))
    return [name for name in allowlist(policy, "play") if name in live]


def candidate_allowlist(policy):
    """Statically allowed set; NOT permission to play. Kept for reporting."""
    return allowlist(policy, "play")


def validate(policy):
    """Structural checks; returns a list of problems (empty means valid)."""
    problems = []
    tools = policy.get("tools", {})
    if not tools:
        problems.append("policy has no tools")
    for name, entry in tools.items():
        decision = entry.get("decision")
        purity = entry.get("purity")
        if decision not in DECISIONS:
            problems.append("%s: bad decision %r" % (name, decision))
        if not entry.get("reason"):
            problems.append("%s: allow/deny without a reason" % name)
        if purity == "privileged" and decision != "deny":
            problems.append("%s: privileged tool must be denied" % name)
        if purity in ("mutating",) and decision == "allow":
            problems.append("%s: mutating read must be denied" % name)
        if decision == "allow" and purity == "write" and entry.get("status") != "fixed":
            problems.append("%s: allowed write without fixed evidence" % name)
        if entry.get("modes", {}).get("play") is True and decision != "allow":
            problems.append("%s: play mode with non-allow decision" % name)
        if entry.get("modes", {}).get("bootstrap") is True and name in SNAPSHOT_READERS:
            problems.append("%s: bootstrap must not read the state snapshot" % name)
    state = tools.get("get_state", {})
    if state.get("modes", {}).get("play") is True and state.get("status") not in ("d0a_fixed", "verified_live"):
        problems.append("get_state: play mode without D0a evidence")
    return problems
