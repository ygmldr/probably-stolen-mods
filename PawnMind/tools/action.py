#!/usr/bin/env python3
"""Send one action to a running PawnMind bridge.

Usage:
  python tools/action.py ping
  python tools/action.py open_shop
  python tools/action.py call_next_customer
  python tools/action.py advance_dialogue
  python tools/action.py choose_dialogue index=0
  python tools/action.py bargain direction=sell_discount percent=10
  python tools/action.py end_day

The script reads the current /state first so the action carries a fresh revision.
It prints the response; verify the outcome with a follow-up /state read.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agent")))

from pawnmind_client import BridgeClient  # noqa: E402

MUTATING = {"open_shop", "call_next_customer", "advance_dialogue", "choose_dialogue", "end_day", "bargain"}


def parse_args(items):
    parsed = {}
    for item in items:
        if "=" not in item:
            raise SystemExit("argument must be key=value: " + item)
        key, value = item.split("=", 1)
        try:
            parsed[key] = int(value)
        except ValueError:
            parsed[key] = value
    return parsed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("op")
    parser.add_argument("args", nargs="*")
    parser.add_argument("--config", default=None)
    options = parser.parse_args()

    client = BridgeClient(options.config)
    state = client.get_state()
    args = parse_args(options.args)

    if options.op in MUTATING or options.op == "ping":
        if "rev" not in args and options.op in MUTATING:
            args["rev"] = state.get("rev", -1)

    print("state rev:", state.get("rev"), "in_game:", state.get("in_game"))
    response = client.action(options.op, **args)
    print("response: ", response)
    return 0 if response.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
