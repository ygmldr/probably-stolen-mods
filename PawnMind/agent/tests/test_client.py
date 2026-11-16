import http.client
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
BAD_TOKEN = "sentinel-bad-token-do-not-leak-0123456789"


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
        self.addCleanup(setattr, self.client, "token", TOKEN)
        self.client.token = BAD_TOKEN
        with self.assertRaises(BridgeError) as context:
            self.client.get_state()
        self.assertEqual(401, context.exception.status)
        self.assertNotIn(BAD_TOKEN, str(context.exception))
        self.assertNotIn(BAD_TOKEN, repr(context.exception))
        self.assertNotIn(BAD_TOKEN, context.exception.body)
        self.assertEqual(
            {"ok": False, "error": "unauthorized"},
            json.loads(context.exception.body),
        )

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


class _RedirectSourceHandler(BaseHTTPRequestHandler):
    """First service: always answers with a redirect to the second service."""

    def log_message(self, *args):
        pass

    def _record(self):
        length = int(self.headers.get("Content-Length", "0") or 0)
        body = self.rfile.read(length) if length else b""
        self.server.records.append(
            {
                "method": self.command,
                "path": self.path,
                "authorization_ok": self.headers.get("Authorization") == "Bearer " + TOKEN,
                "body": body.decode("utf-8", "replace"),
            }
        )

    def _redirect(self):
        self._record()
        data = b'{"ok": false, "error": "moved"}'
        self.send_response(self.server.redirect_status)
        self.send_header("Location", self.server.location + self.path)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._redirect()

    def do_POST(self):
        self._redirect()


class _RedirectTargetHandler(BaseHTTPRequestHandler):
    """Second service: a live endpoint that would happily accept a redirect."""

    def log_message(self, *args):
        pass

    def _reply(self):
        length = int(self.headers.get("Content-Length", "0") or 0)
        body = self.rfile.read(length) if length else b""
        self.server.records.append(
            {
                "method": self.command,
                "path": self.path,
                "authorization_ok": self.headers.get("Authorization") == "Bearer " + TOKEN,
                "body": body.decode("utf-8", "replace"),
            }
        )
        data = json.dumps({"ok": True, "target": self.path}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._reply()

    def do_POST(self):
        self._reply()


class RedirectSafetyTests(unittest.TestCase):
    """A redirect must never move a request, or the bearer token, off the bridge."""

    REDIRECT_CODES = (301, 302, 307, 308)

    @classmethod
    def setUpClass(cls):
        cls.target_server = HTTPServer(("127.0.0.1", 0), _RedirectTargetHandler)
        cls.target_server.records = []
        cls.target_port = cls.target_server.server_address[1]
        cls.target_thread = threading.Thread(target=cls.target_server.serve_forever, daemon=True)
        cls.target_thread.start()

        cls.source_server = HTTPServer(("127.0.0.1", 0), _RedirectSourceHandler)
        cls.source_server.records = []
        cls.source_server.redirect_status = 302
        cls.source_server.location = "http://127.0.0.1:%d" % cls.target_port
        cls.source_port = cls.source_server.server_address[1]
        cls.source_thread = threading.Thread(target=cls.source_server.serve_forever, daemon=True)
        cls.source_thread.start()

        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (cls.source_port, TOKEN))

        cls.client = BridgeClient(cls.config_path)

    @classmethod
    def tearDownClass(cls):
        cls.source_server.shutdown()
        cls.source_server.server_close()
        cls.target_server.shutdown()
        cls.target_server.server_close()
        os.unlink(cls.config_path)

    def setUp(self):
        self.source_server.records.clear()
        self.target_server.records.clear()
        self.source_server.redirect_status = 302

    def _assert_no_token(self, error):
        rendered = "\n".join((str(error), repr(error), str(error.body)))
        self.assertNotIn(TOKEN, rendered)

    def test_second_service_is_live(self):
        # Guards the negative tests: the redirect destination really answers,
        # so "no target records" means the client refused, not a dead server.
        connection = http.client.HTTPConnection("127.0.0.1", self.target_port, timeout=5)
        try:
            connection.request("GET", "/probe")
            response = connection.getresponse()
            self.assertEqual(200, response.status)
            self.assertTrue(json.loads(response.read().decode("utf-8"))["ok"])
        finally:
            connection.close()
        self.assertEqual(1, len(self.target_server.records))
        self.assertEqual("/probe", self.target_server.records[0]["path"])

    def test_redirect_responses_are_never_followed(self):
        for method in ("GET", "POST"):
            for code in self.REDIRECT_CODES:
                with self.subTest(method=method, code=code):
                    self.source_server.redirect_status = code
                    self.source_server.records.clear()
                    self.target_server.records.clear()

                    error = None
                    try:
                        if method == "GET":
                            self.client.get_state()
                        else:
                            self.client.action("ping")
                    except BridgeError as caught:
                        error = caught

                    self.assertEqual(
                        [],
                        self.target_server.records,
                        "client followed HTTP %d and forwarded the request to the "
                        "second service: %r" % (code, self.target_server.records),
                    )
                    self.assertIsInstance(
                        error, BridgeError, "redirect must fail with BridgeError"
                    )
                    self.assertEqual(code, error.status)
                    self._assert_no_token(error)

                    self.assertEqual(
                        1, len(self.source_server.records), "first service was not called"
                    )
                    source = self.source_server.records[0]
                    self.assertEqual(method, source["method"])
                    self.assertTrue(
                        source["authorization_ok"],
                        "bearer token must still reach the configured bridge",
                    )
                    if method == "POST":
                        body = json.loads(source["body"])
                        self.assertEqual("ping", body["op"])
                        self.assertTrue(body["action_id"])


class _EchoFailureHandler(BaseHTTPRequestHandler):
    """Fake bridge that echoes the request's Authorization header in its body."""

    def log_message(self, *args):
        pass

    def _payload(self):
        authorization = self.headers.get("Authorization", "")
        token = authorization[len("Bearer "):] if authorization.startswith("Bearer ") else ""
        return json.dumps(
            {
                "ok": False,
                "error": "echo test",
                "authorization": authorization,
                "token": token,
            }
        ).encode("utf-8")

    def _send(self, status, location=None):
        data = self._payload()
        self.send_response(status)
        if location is not None:
            self.send_header("Location", location)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/state":
            self._send(403)
        elif self.path == "/leak":
            self._send(200)
        else:
            self._send(404)

    def do_POST(self):
        if self.path == "/action":
            self._send(302, self.server.location)
        else:
            self._send(404)


class TokenRedactionTests(unittest.TestCase):
    """BridgeError must never hand an echoed bearer token to the caller."""

    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), _EchoFailureHandler)
        cls.port = cls.server.server_address[1]
        cls.server.location = "http://127.0.0.1:%d/leak" % cls.port
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

    def _assert_redacted_body(self, error, status):
        self.assertEqual(status, error.status)
        self.assertNotIn(TOKEN, error.body, "BridgeError.body still echoes the bearer token")
        self.assertNotIn(TOKEN, str(error))
        self.assertNotIn(TOKEN, repr(error))
        payload = json.loads(error.body)
        self.assertFalse(payload["ok"])
        self.assertEqual("echo test", payload["error"])

    def test_4xx_body_that_echoes_bearer_is_redacted(self):
        with self.assertRaises(BridgeError) as context:
            self.client.get_state()
        self._assert_redacted_body(context.exception, 403)

    def test_3xx_body_that_echoes_bearer_is_redacted(self):
        with self.assertRaises(BridgeError) as context:
            self.client.action("ping")
        self._assert_redacted_body(context.exception, 302)


if __name__ == "__main__":
    unittest.main()


class _PolicyHandler(BaseHTTPRequestHandler):
    sha = "deadbeef"
    posts = []

    def log_message(self, *args):
        pass

    def _send(self, status, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"ok": True, "policy_sha256": _PolicyHandler.sha})
        else:
            self._send(404, {"ok": False})

    def do_POST(self):
        _PolicyHandler.posts.append(self.path)
        self._send(200, {"ok": True, "op": "ping"})


class PolicyFingerprintTests(unittest.TestCase):
    """A Python/C# policy mismatch must refuse the write before it is sent."""

    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), _PolicyHandler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (cls.port, TOKEN))

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        os.unlink(cls.config_path)

    def setUp(self):
        _PolicyHandler.posts = []

    def test_mismatched_policy_refuses_without_sending(self):
        client = BridgeClient(self.config_path, expected_policy_sha256="0" * 64)
        result = client.action("open_shop")
        self.assertFalse(result["ok"])
        self.assertEqual("policy_mismatch", result["error"])
        self.assertEqual([], _PolicyHandler.posts, "no write reached the bridge")

    def test_matching_policy_sends_the_action(self):
        _PolicyHandler.sha = "a" * 64
        client = BridgeClient(self.config_path, expected_policy_sha256="a" * 64)
        result = client.action("open_shop")
        self.assertTrue(result["ok"])
        self.assertEqual(["/action"], _PolicyHandler.posts)

    def test_no_expectation_keeps_the_old_behaviour(self):
        client = BridgeClient(self.config_path)
        client.action("open_shop")
        self.assertEqual(["/action"], _PolicyHandler.posts)

    def test_bridge_without_a_fingerprint_does_not_block(self):
        _PolicyHandler.sha = None  # older mod build reports nothing
        client = BridgeClient(self.config_path, expected_policy_sha256="b" * 64)
        result = client.action("open_shop")
        self.assertTrue(result["ok"])
