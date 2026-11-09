#!/usr/bin/env python3
"""Minimal agent loop for PawnMind.

The loop is deliberately small: observe -> decide -> one action -> re-read.
`RulePolicy` is a deterministic baseline used to verify the plumbing; an LLM
policy can implement the same `decide(state) -> ToolCall | None` interface.

Run against a live game:
  python orchestrator.py --config "<...>/bridge.conf" --steps 8

Dry run (no tool calls, prints the first decision):
  python orchestrator.py --config "..." --dry-run
"""

import argparse
import asyncio
import json
import os
import sys

from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

HERE = os.path.dirname(os.path.abspath(__file__))
MCP_SERVER = os.path.join(HERE, "mcp_server.py")


class ToolCall:
    def __init__(self, tool, **args):
        self.tool = tool
        self.args = args


MUTATING = {
    "open_shop", "call_next_customer", "advance_dialogue",
    "choose_dialogue", "end_day", "bargain",
}


class RulePolicy:
    """Open the shop, call one customer, wait for arrival, advance dialogue."""

    def __init__(self):
        self._opened = False

    def decide(self, state):
        if not state.get("in_game"):
            return None

        if state.get("client") is None:
            if state.get("can_call_next"):
                return ToolCall("call_next_customer")
            if not self._opened:
                self._opened = True
                return ToolCall("open_shop")
            return None

        if not state.get("client_arrived"):
            return ToolCall("wait_for_change", since_rev=state.get("rev", -1), timeout_s=6)

        dialogue = state.get("dialogue") or {}
        if dialogue.get("choice"):
            return None
        if not dialogue:
            return None
        return ToolCall("advance_dialogue")


async def call(session, tool, arguments):
    result = await session.call_tool(tool, arguments)
    text = result.content[0].text if result.content else "{}"
    return json.loads(text)


async def run_session(session, policy, max_steps=8, step_timeout=8.0):
    state = await call(session, "get_state", {})
    history = [{"state": state}]
    stop_reason = "policy_done"

    for _ in range(max_steps):
        action = policy.decide(state)
        if action is None:
            break

        if action.tool == "wait_for_change":
            waited = await call(session, "wait_for_change", action.args)
            state = waited.get("state") if waited.get("error") == "timeout" else waited
            history.append({"wait": True, "state": state})
            continue

        arguments = dict(action.args)
        if action.tool in MUTATING:
            arguments["rev"] = state.get("rev", -1)

        result = await call(session, action.tool, arguments)
        history.append({"action": action.tool, "args": arguments, "result": result})

        if not result.get("ok"):
            state = await call(session, "get_state", {})
            history.append({"state": state})
            if result.get("error") == "stale_rev":
                continue
            stop_reason = "action_failed"
            break

        before_rev = state.get("rev", -1)
        changed = await call(session, "wait_for_change",
                             {"since_rev": before_rev, "timeout_s": step_timeout})
        if changed.get("error") == "timeout":
            state = changed.get("state") or await call(session, "get_state", {})
        else:
            state = changed
        history.append({"state": state})

        if state.get("rev", -1) == before_rev:
            stop_reason = "no_state_change"
            break
    else:
        stop_reason = "max_steps"

    return {"stop_reason": stop_reason, "history": history}


async def run_cli(args):
    policy = RulePolicy()
    params = StdioServerParameters(
        command=sys.executable,
        args=[MCP_SERVER, "--config", args.config] if args.config else [MCP_SERVER],
    )

    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            if args.dry_run:
                state = await call(session, "get_state", {})
                decision = policy.decide(state)
                print(json.dumps({
                    "state": state,
                    "decision": None if decision is None else {"tool": decision.tool, "args": decision.args},
                }, indent=2, ensure_ascii=False))
                return 0

            result = await run_session(session, policy, args.steps, args.step_timeout)
            print(json.dumps(result, indent=2, ensure_ascii=False))
            return 0 if result["stop_reason"] in ("policy_done", "max_steps", "no_state_change") else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=None)
    parser.add_argument("--steps", type=int, default=8)
    parser.add_argument("--step-timeout", type=float, default=8.0)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    return asyncio.run(run_cli(args))


if __name__ == "__main__":
    sys.exit(main())
