"""Loopback bridge stand-ins shared by the MCP mode tests.

R4/F5b made the registered MCP surface depend on the *reported* run mode, so any
test that lists or calls bridge-backed tools must talk to a bridge that actually
reports one. These helpers keep that fixture in one place.
"""

import json
import os
import socket
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class FakeBridge:
    """A loopback bridge stand-in that only answers the health probe.

    R4/F5b made the MCP surface depend on the *reported* run mode, so the mode
    tests must talk to a bridge that actually reports one. The mode is mutable so
    a test can change it while the MCP server is running.
    """

    def __init__(self, mode="test", policy_sha256=None, include_mode=True):
        self.mode = mode
        self.policy_sha256 = policy_sha256
        self.include_mode = include_mode
        bridge = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):  # keep the test output clean
                pass

            def _send(self, payload):
                body = json.dumps(payload).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                if self.path == "/health":
                    payload = {"ok": True}
                    if bridge.include_mode:
                        payload["run_mode"] = bridge.mode
                    if bridge.policy_sha256:
                        payload["policy_sha256"] = bridge.policy_sha256
                    self._send(payload)
                    return
                self._send({"ok": True})

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                self.rfile.read(length)
                self._send({"ok": True, "run_mode": bridge.mode})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def port(self):
        return self.server.server_address[1]

    def config(self):
        handle, path = tempfile.mkstemp(suffix=".conf")
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (self.port, "0" * 32))
        return path

    def lock(self):
        """Report the locked mode without dropping the connection."""
        self.mode = "locked"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def dead_config():
    """A config for a port nothing listens on.

    The port is probed, not hard-coded: a developer machine may well have the
    real bridge (or anything else) on the default 15528, which would turn this
    fixture into a live endpoint and silently invalidate the test.
    """
    probe = socket.socket()
    try:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    finally:
        probe.close()

    handle, path = tempfile.mkstemp(suffix=".conf")
    with os.fdopen(handle, "w") as config:
        config.write("port=%d\ntoken=%s\n" % (port, "0" * 32))
    return path
