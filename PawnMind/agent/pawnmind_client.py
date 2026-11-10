"""Stdlib-only client for the PawnMind in-game bridge.

The MCP server is a thin wrapper around this class, so the transport can be
tested without the `mcp` package installed.
"""

import json
import os
import urllib.error
import uuid
import urllib.request

DEFAULT_CONFIG = os.path.join(
    os.path.expanduser("~"),
    "AppData", "LocalLow", "Questing Goose Studio", "Probably Stolen",
    "ModData", "pawnmind", "bridge.conf",
)


class BridgeError(Exception):
    def __init__(self, message, status=None, body=None):
        super().__init__(message)
        self.status = status
        self.body = body


def load_config(path):
    config = {"port": 15528, "token": ""}
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            config[key.strip()] = value.strip()

    try:
        config["port"] = int(config["port"])
    except (TypeError, ValueError):
        raise BridgeError("invalid port in " + path)

    if not 1024 <= config["port"] <= 65535:
        raise BridgeError("port out of range in " + path)
    if len(config["token"]) < 32:
        raise BridgeError("token missing or too short in " + path)

    return config


class BridgeClient:
    def __init__(self, config_path=None, timeout=8.0):
        self.config_path = config_path or DEFAULT_CONFIG
        config = load_config(self.config_path)
        self.port = config["port"]
        self.token = config["token"]
        self.timeout = timeout
        # Never let a local bridge follow a redirect and forward the bearer token.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(self, method, path, payload=None, auth=True, timeout=None):
        url = "http://127.0.0.1:%d%s" % (self.port, path)
        headers = {"Content-Type": "application/json"}
        if auth:
            headers["Authorization"] = "Bearer " + self.token

        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(url, data=data, method=method, headers=headers)

        try:
            with self.opener.open(request, timeout=self.timeout if timeout is None else timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            body = error.read().decode("utf-8", "replace")
            raise BridgeError("bridge returned HTTP %d" % error.code, error.code, body)
        except (urllib.error.URLError, OSError) as error:
            raise BridgeError("bridge unavailable: %s" % error)

    def health(self):
        return self.request("GET", "/health", auth=False)

    def get_state(self):
        return self.request("GET", "/state")

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        path = "/events?since=%d&timeout_ms=%d" % (int(since_rev), int(timeout_ms))
        return self.request("GET", path, timeout=timeout_ms / 1000.0 + 5.0)

    def action(self, op, **args):
        payload = {"op": op, "action_id": args.pop("action_id", str(uuid.uuid4()))}
        payload.update(args)
        return self.request("POST", "/action", payload)
