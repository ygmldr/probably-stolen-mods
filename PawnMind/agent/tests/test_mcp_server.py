import asyncio
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

def _policy_sha256():
    """The bridge reports the policy build it embeds; the client refuses writes
    when that fingerprint is missing or different (fail closed)."""
    try:
        agent_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        if agent_dir not in sys.path:
            sys.path.insert(0, agent_dir)
        import tool_policy
        return tool_policy.policy_sha256(tool_policy.load_policy())
    except Exception:
        return None

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError:  # the mcp package is optional for the stdlib-only client tests
    ClientSession = None
    StdioServerParameters = None
    stdio_client = None

TOKEN = "0123456789abcdef0123456789abcdef"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MCP_SERVER = os.path.join(ROOT, "mcp_server.py")
RECEIVED = []


class _BridgeHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _json(self, payload, status=200):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._json({"ok": True, "name": "pawnmind",
                        "run_mode": "test",
                        "policy_sha256": _policy_sha256()})
        elif self.path == "/state":
            self._json({
                "in_game": True,
                "cash": 550,
                "rev": 3,
                "client": None,
                "negotiated_item": None,
            })
        else:
            self._json({"ok": False}, 404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        RECEIVED.append(body)
        self._json({"ok": True, "op": body.get("op"), "rev": 3})


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class McpServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), _BridgeHandler)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (cls.port, TOKEN))

        cls.params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", cls.config_path],
        )

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        os.unlink(cls.config_path)

    def test_tools_are_listed_and_state_roundtrips(self):
        async def scenario():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    names = {tool.name for tool in tools.tools}
                    for expected in (
                        "get_state", "ping", "open_shop", "call_next_customer",
                        "advance_dialogue", "choose_dialogue", "end_day", "bargain",
                        "confirm_trade", "save_game", "load_game", "new_game", "sleep",
                        "get_legal_actions", "get_run_summary", "get_action_history",
                        "act", "wait_for_events", "wait_until", "get_ui_snapshot", "press",
                        "list_interactions", "dispatch_expedition", "advance_expedition", "contraband_status", "security_status", "list_services", "toggle_service", "husbandry_status", "expedition_status", "events_status", "list_signs", "report_client", "deliver_request", "barter_accept", "arrest_wanted", "list_factions", "health_status", "list_requests", "wanted_status", "barter_status", "list_wine", "wine_add_water", "wine_add_ingredient", "wine_status", "start_fermentation", "list_machines", "machine_grow_status", "machine_status", "machine_insert", "machine_collect", "list_upgrades", "buy_upgrade", "lottery_status", "list_tickets", "inspect_ticket", "redeem_ticket", "list_contacts", "call", "phone_state", "phone_advance", "phone_choose", "end_call", "list_labels", "set_label", "list_analyzers", "analyze_item", "set_pen_mode", "list_marks", "activate_item", "toggle_item", "use_tool", "list_goods", "buy_wholesale", "list_books", "read_book", "read_item", "list_locations", "go_out", "scavenge", "return_home", "commissary_buy", "list_containers", "list_inventory", "get_item", "move_item", "get_layout", "wait_for_change", "get_pending_prompt", "answer_prompt", "stage_negotiation_item",
                        "get_day_report", "get_calendar", "get_clientele",
                        "dismiss_tutorial_hints",
                    ):
                        self.assertIn(expected, names)

                    state = await session.call_tool("get_state", {})
                    payload = json.loads(state.content[0].text)
                    self.assertTrue(payload["in_game"])
                    self.assertEqual(3, payload["rev"])

                    result = await session.call_tool("open_shop", {"rev": 3})
                    body = json.loads(result.content[0].text)
                    self.assertTrue(body["ok"])
                    self.assertEqual("open_shop", body["op"])

        asyncio.run(scenario())

        actions = [item for item in RECEIVED if item.get("op") == "open_shop"]
        self.assertEqual(1, len(actions))
        self.assertEqual(3, actions[0]["rev"])
        self.assertIn("action_id", actions[0])

    def test_list_contacts_hidden_is_documented_debug_only(self):
        async def scenario():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    contact = next(tool for tool in tools.tools if tool.name == "list_contacts")
                    description = (contact.description or "") + " " + json.dumps(contact.input_schema)
                    self.assertIn("allow_test_items", description)
                    self.assertIn("hidden_contacts_forbidden", description)

                    result = await session.call_tool("list_contacts", {"include_hidden": True})
                    payload = json.loads(result.content[0].text)
                    self.assertTrue(payload["ok"])

        asyncio.run(scenario())

        calls = [
            item for item in RECEIVED
            if item.get("op") == "list_contacts" and item.get("include_hidden") is True
        ]
        self.assertEqual(1, len(calls))


    def test_intel_tools_roundtrip_and_validate_days(self):
        before = len(RECEIVED)

        async def scenario():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()

                    report = await session.call_tool("get_day_report", {})
                    report_payload = json.loads(report.content[0].text)
                    self.assertTrue(report_payload["ok"])
                    self.assertEqual("get_day_report", report_payload["op"])

                    calendar = await session.call_tool("get_calendar", {"days": 14})
                    calendar_payload = json.loads(calendar.content[0].text)
                    self.assertTrue(calendar_payload["ok"])
                    self.assertEqual("get_calendar", calendar_payload["op"])

                    clientele = await session.call_tool("get_clientele", {})
                    clientele_payload = json.loads(clientele.content[0].text)
                    self.assertTrue(clientele_payload["ok"])
                    self.assertEqual("get_clientele", clientele_payload["op"])

                    too_many = await session.call_tool("get_calendar", {"days": 15})
                    too_many_payload = json.loads(too_many.content[0].text)
                    self.assertFalse(too_many_payload["ok"])
                    self.assertEqual("invalid_argument", too_many_payload["error"])
                    self.assertEqual("days", too_many_payload["field"])

                    zero = await session.call_tool("get_calendar", {"days": 0})
                    zero_payload = json.loads(zero.content[0].text)
                    self.assertEqual("invalid_argument", zero_payload["error"])

                    bad_rev = await session.call_tool(
                        "dismiss_tutorial_hints", {"rev": -1})
                    bad_rev_payload = json.loads(bad_rev.content[0].text)
                    self.assertEqual("invalid_argument", bad_rev_payload["error"])
                    self.assertEqual("rev", bad_rev_payload["field"])

        asyncio.run(scenario())

        calls = RECEIVED[before:]
        calendar_calls = [item for item in calls if item.get("op") == "get_calendar"]
        self.assertEqual(1, len(calendar_calls), "only the valid calendar call reaches the bridge")
        self.assertEqual(14, calendar_calls[0]["days"])
        self.assertEqual(1, len([c for c in calls if c.get("op") == "get_day_report"]))
        self.assertEqual(1, len([c for c in calls if c.get("op") == "get_clientele"]))
        self.assertEqual([], [c for c in calls if c.get("op") == "dismiss_tutorial_hints"],
                         "invalid rev must never reach the bridge")

    def test_prompt_tools_reject_invalid_arguments_before_the_bridge(self):
        before = len(RECEIVED)

        async def scenario():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()

                    pending = await session.call_tool("get_pending_prompt", {})
                    pending_payload = json.loads(pending.content[0].text)
                    self.assertTrue(pending_payload["ok"])
                    self.assertEqual("get_pending_prompt", pending_payload["op"])

                    bad_choice = await session.call_tool(
                        "answer_prompt", {"choice": "maybe", "rev": 3})
                    bad_choice_payload = json.loads(bad_choice.content[0].text)
                    self.assertFalse(bad_choice_payload["ok"])
                    self.assertEqual("invalid_argument", bad_choice_payload["error"])
                    self.assertEqual("choice", bad_choice_payload["field"])
                    self.assertEqual(["confirm", "cancel"], bad_choice_payload["allowed"])

                    bad_rev = await session.call_tool(
                        "answer_prompt", {"choice": "confirm", "rev": -1})
                    bad_rev_payload = json.loads(bad_rev.content[0].text)
                    self.assertFalse(bad_rev_payload["ok"])
                    self.assertEqual("invalid_argument", bad_rev_payload["error"])
                    self.assertEqual("rev", bad_rev_payload["field"])

        asyncio.run(scenario())

        prompt_writes = [
            item for item in RECEIVED[before:] if item.get("op") == "answer_prompt"
        ]
        self.assertEqual([], prompt_writes,
                         "invalid answer_prompt arguments must never reach the bridge")


if __name__ == "__main__":
    unittest.main()
