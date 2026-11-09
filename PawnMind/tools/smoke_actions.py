#!/usr/bin/env python3
"""Live smoke test for the M1 action layer.

Opens the shop and calls one customer, re-reading state after every step.
It never trades, bargains or ends the day. Use a test save.

  python tools/smoke_actions.py --config "<...>/bridge.conf"
"""

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agent")))

from pawnmind_client import BridgeClient  # noqa: E402


def wait_for(client, predicate, timeout=12.0):
    deadline = time.time() + timeout
    state = client.get_state()
    while time.time() < deadline:
        if predicate(state):
            return state
        time.sleep(0.5)
        state = client.get_state()
    return state


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=None)
    options = parser.parse_args()

    client = BridgeClient(options.config)
    state = client.get_state()
    print("initial:", state)

    if not state.get("in_game"):
        print("not in a loaded save; load one first")
        return 2

    if not state.get("can_call_next"):
        revision = state.get("rev", -1)
        print("open_shop:", client.action("open_shop", rev=revision))
        state = wait_for(client, lambda s: s.get("rev") != revision or s.get("can_call_next"))
        print("after open:", state)

    if state.get("client") is None:
        revision = state.get("rev", -1)
        print("call_next_customer:", client.action("call_next_customer", rev=revision))
        state = wait_for(client, lambda s: s.get("client") is not None, 20.0)
        print("after call:", state)

    ok = state.get("client") is not None
    print("RESULT:", "client arrived" if ok else "no client yet")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
