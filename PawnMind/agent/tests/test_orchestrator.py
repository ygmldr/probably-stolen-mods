import asyncio
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError:
    ClientSession = None
    StdioServerParameters = None
    stdio_client = None

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

TOKEN = "0123456789abcdef0123456789abcdef"
STATE = {
    "in_game": True,
    "cash": 550,
    "rev": 1,
    "client": None,
    "negotiated_item": None,
    "can_call_next": False,
    "dialogue": None,
}
LOCK = threading.Lock()
RECEIVED = []


class _Bridge(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _json(self, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._json({"ok": True})
            return
        with LOCK:
            self._json(dict(STATE))

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        op = body.get("op")
        RECEIVED.append(body)

        with LOCK:
            if op == "open_shop":
                STATE["can_call_next"] = True
                STATE["rev"] += 1
            elif op == "call_next_customer":
                STATE["client"] = {"name": "Mira", "intent": "SELL"}
                STATE["client_arrived"] = True
                STATE["can_call_next"] = False
                STATE["dialogue"] = {"choice": False, "ended": False}
                STATE["rev"] += 1
            elif op == "advance_dialogue":
                STATE["dialogue"] = {"choice": True, "ended": False}
                STATE["rev"] += 1

        self._json({"ok": True, "op": op, "rev": STATE["rev"]})


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class OrchestratorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), _Bridge)
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()
        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (cls.httpd.server_address[1], TOKEN))

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        os.unlink(cls.config_path)

    def test_rule_policy_opens_shop_calls_customer_and_advances(self):
        from orchestrator import RulePolicy, run_session

        async def scenario():
            params = StdioServerParameters(
                command=sys.executable,
                args=[os.path.join(AGENT_DIR, "mcp_server.py"), "--config", self.config_path],
            )
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    return await run_session(session, RulePolicy(), max_steps=6, step_timeout=5.0)

        result = asyncio.run(scenario())
        actions = [entry["action"] for entry in result["history"] if "action" in entry]
        self.assertEqual(["open_shop", "call_next_customer", "advance_dialogue"], actions)
        self.assertEqual("policy_done", result["stop_reason"])
        self.assertIsNotNone(STATE["client"])
        self.assertTrue(all(item.get("rev", -1) >= 1 for item in RECEIVED))


if __name__ == "__main__":
    unittest.main()
