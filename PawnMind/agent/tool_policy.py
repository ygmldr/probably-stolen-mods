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
        if entry.get("status") == "verified_live" and entry.get("decision") == "allow"
    )


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
    if tools.get("get_state", {}).get("modes", {}).get("play") is True:
        problems.append("get_state: play mode before D0a purity evidence")
    return problems
