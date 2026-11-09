#!/usr/bin/env python3
"""Read-only probe for the PawnMind in-game bridge.

Usage:
  python tools/probe.py
  python tools/probe.py --config "C:/Users/<you>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/pawnmind/bridge.conf"

The script never mutates game state: it calls /health, /state and a "ping" action.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_CONFIG = os.path.join(
    os.path.expanduser("~"),
    "AppData", "LocalLow", "Questing Goose Studio", "Probably Stolen",
    "ModData", "pawnmind", "bridge.conf",
)


def load_config(path):
    config = {"port": 15528, "token": ""}
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            config[key.strip()] = value.strip()
    config["port"] = int(config["port"])
    if len(config["token"]) < 32:
        raise ValueError("token missing or too short in " + path)
    return config


def call(config, method, path, payload=None, auth=True):
    url = "http://127.0.0.1:%d%s" % (config["port"], path)
    headers = {"Content-Type": "application/json"}
    if auth:
        headers["Authorization"] = "Bearer " + config["token"]
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    try:
        with opener.open(request, timeout=8) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=DEFAULT_CONFIG)
    args = parser.parse_args()

    if not os.path.exists(args.config):
        print("config not found: " + args.config)
        print("start the game once with PawnMind enabled; it creates the file.")
        return 2

    config = load_config(args.config)
    print("config: " + args.config)
    print("port:   %d" % config["port"])

    status, body = call(config, "GET", "/health", auth=False)
    print("health: %d %s" % (status, body))

    status, body = call(config, "GET", "/state")
    print("state:  %d %s" % (status, body))

    status, body = call(config, "POST", "/action", {"op": "ping"})
    print("ping:   %d %s" % (status, body))

    return 0 if status == 200 else 1


if __name__ == "__main__":
    sys.exit(main())
