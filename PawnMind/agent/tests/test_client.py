import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from pawnmind_client import BridgeClient, BridgeError  # noqa: E402

TOKEN = "0123456789abcdef0123456789abcdef"


class _Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _authorized(self):
        return self.headers.get("Authorization") == "Bearer " + TOKEN

    def _send(self, status, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"ok": True, "name": "pawnmind"})
        elif self.path == "/state":
            if not self._authorized():
                self._send(401, {"ok": False, "error": "unauthorized"})
            else:
                self._send(200, {"rev": 3, "in_game": True})
        else:
            self._send(404, {"ok": False})

    def do_POST(self):
        if self.path != "/action":
            self._send(404, {"ok": False})
            return
        if not self._authorized():
            self._send(401, {"ok": False, "error": "unauthorized"})
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        self._send(200, {"ok": True, "op": body.get("op")})


class BridgeClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), _Handler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (cls.port, TOKEN))

        cls.client = BridgeClient(cls.config_path)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        os.unlink(cls.config_path)

    def test_health_needs_no_token(self):
        self.assertTrue(self.client.health()["ok"])

    def test_state_roundtrip(self):
        state = self.client.get_state()
        self.assertEqual(3, state["rev"])
        self.assertTrue(state["in_game"])

    def test_action_payload(self):
        result = self.client.action("ping")
        self.assertTrue(result["ok"])
        self.assertEqual("ping", result["op"])

    def test_bad_token_raises_bridge_error(self):
        self.client.token = "b" * 32
        with self.assertRaises(BridgeError) as context:
            self.client.get_state()
        self.assertEqual(401, context.exception.status)
        self.client.token = TOKEN

    def test_missing_config_raises(self):
        with self.assertRaises(OSError):
            BridgeClient("/nonexistent/pawnmind/bridge.conf")

    def test_invalid_config_raises(self):
        handle, path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=99\ntoken=" + TOKEN + "\n")
        try:
            with self.assertRaises(BridgeError):
                BridgeClient(path)
        finally:
            os.unlink(path)


if __name__ == "__main__":
    unittest.main()
