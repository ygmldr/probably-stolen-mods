"""Stdlib-only client for the PawnMind in-game bridge.

The MCP server is a thin wrapper around this class, so the transport can be
tested without the `mcp` package installed.
"""

import json
import os
import time
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


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Surface every 3xx as an HTTPError instead of following it."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class BridgeClient:
    #: How long a /health fingerprint is trusted before it is re-read.
    POLICY_CACHE_SECONDS = 30.0

    def __init__(self, config_path=None, timeout=8.0, expected_policy_sha256=None):
        self.config_path = config_path or DEFAULT_CONFIG
        # The game-side catalog embeds the policy hash it was built from. When the
        # MCP server runs a different policy build, writes are refused locally
        # instead of being sent with rules the bridge does not implement.
        self.expected_policy_sha256 = expected_policy_sha256
        self._policy_checked_at = 0.0
        config = load_config(self.config_path)
        self.port = config["port"]
        self.token = config["token"]
        self.timeout = timeout
        # Never let a local bridge follow a redirect and forward the bearer token.
        # ProxyHandler({}) ignores ambient proxies; _NoRedirectHandler turns 3xx
        # into an HTTPError so the token stays on the configured port.
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            _NoRedirectHandler(),
        )

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
            if self.token:
                body = body.replace(self.token, "[redacted]")
            raise BridgeError("bridge returned HTTP %d" % error.code, error.code, body)
        except (urllib.error.URLError, OSError) as error:
            raise BridgeError("bridge unavailable: %s" % error)

    def health(self):
        return self.request("GET", "/health", auth=False)

    def get_state(self):
        return self.request("GET", "/state")

    def wait_events(self, since_rev=-1, timeout_ms=25000):
        path = "/events?since=%d&timeout_ms=%d" % (int(since_rev), int(timeout_ms))
        try:
            return self.request("GET", path, timeout=timeout_ms / 1000.0 + 5.0)
        except BridgeError as error:
            # A long poll that dies must come back as data, not as a transport
            # exception: hosts render those as "Error: [object Object]".
            return {"ok": False, "error": "wait_failed", "detail": str(error)}

    def _policy_refusal(self):
        """None when the bridge policy matches, else the stable refusal code."""
        if not self.expected_policy_sha256:
            return None
        now = time.monotonic()
        if now - self._policy_checked_at < self.POLICY_CACHE_SECONDS:
            return None
        try:
            health = self.health()
        except BridgeError:
            # The bridge is unreachable; the action itself will report that.
            return None
        self._policy_checked_at = now
        reported = (health or {}).get("policy_sha256")
        if reported and reported != self.expected_policy_sha256:
            return "policy_mismatch"
        return None

    def action(self, op, **args):
        refusal = self._policy_refusal()
        if refusal:
            return {
                "ok": False,
                "op": op,
                "error": refusal,
                "detail": "the in-game policy build differs from the MCP policy;"
                          " restart the mod with a matching build before writing",
            }
        payload = {"op": op, "action_id": args.pop("action_id", str(uuid.uuid4()))}
        payload.update(args)
        return self.request("POST", "/action", payload)
