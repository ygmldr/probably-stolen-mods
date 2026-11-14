"""S3B fake tests for the semantic new-game menu MCP tools.

Everything here is offline: a local fake HTTP bridge records the exact action
payloads, and the real MCP server is started over stdio against that fake.
No game port, game process, save file or real bridge is touched.
"""

import asyncio
import json
import os
import sys
import tempfile
import threading
import unittest
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

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
sys.path.insert(0, ROOT)

NEW_GAME_TOOLS = (
    "get_new_game_options",
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_next_page",
    "focus_new_game_selected_perk",
    "blur_new_game_option",
    "new_game_continue",
)
WRITE_TOOLS = (
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_next_page",
    "focus_new_game_selected_perk",
    "blur_new_game_option",
    "new_game_continue",
)
FORBIDDEN_PARAMS = {"action", "args", "action_id", "op", "slot", "raw"}


class _BridgeHandler(BaseHTTPRequestHandler):
    """Fake local bridge: records /action bodies and answers with success."""

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
            self._json({"ok": True, "name": "pawnmind"})
        elif self.path == "/state":
            self._json({"ok": True, "in_game": True, "rev": 7})
        else:
            self._json({"ok": False, "error": "not_found"}, 404)

    def do_POST(self):
        if self.path != "/action":
            self._json({"ok": False, "error": "not_found"}, 404)
            return

        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        self.server.requests.append(
            {
                "authorization": self.headers.get("Authorization"),
                "body": body,
            }
        )

        if self.headers.get("Authorization") != "Bearer " + TOKEN:
            self._json({"ok": False, "error": "unauthorized"}, 401)
            return

        op = body.get("op")
        if op == "get_new_game_options":
            self._json(
                {
                    "ok": True,
                    "op": op,
                    "rev": 7,
                    "phase": "perks",
                    "options": [],
                    "selected_options": [],
                    "perks": {"points_used": 0, "points_max": 3},
                    "continue": {"visible": True, "enabled": True},
                }
            )
        else:
            self._json(
                {
                    "ok": True,
                    "op": op,
                    "rev": 8,
                    "readback_required": True,
                }
            )


class _SpyClient:
    """Records client.action calls for direct (non-MCP) tool validation tests."""

    def __init__(self):
        self.calls = []

    def action(self, op, **args):
        self.calls.append((op, args))
        return {"ok": True, "op": op}

    def get_state(self):
        return {"ok": True, "in_game": True, "rev": 7}


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class NewGameMcpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), _BridgeHandler)
        cls.httpd.requests = []
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
        cls.thread.join(timeout=5)
        os.unlink(cls.config_path)

    def setUp(self):
        self.httpd.requests.clear()

    def _run(self, scenario):
        async def runner():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    return await scenario(session)

        return asyncio.run(runner())

    def _tool_map(self):
        async def scenario(session):
            tools = await session.list_tools()
            return {tool.name: tool for tool in tools.tools}

        return self._run(scenario)

    def _assert_validation_failure(self, result):
        """A rejected argument must be an MCP error or an explicit stable error."""
        if result.is_error:
            return
        payload = json.loads(result.content[0].text)
        self.assertFalse(payload.get("ok"), "invalid argument payload: %r" % (payload,))
        self.assertEqual("invalid_argument", payload.get("error"))

    def test_new_game_tools_are_registered_with_strict_schemas(self):
        tools = self._tool_map()

        for name in NEW_GAME_TOOLS:
            self.assertIn(name, tools, "missing semantic new-game tool: " + name)

        # The generic escape hatches stay where they were; the new tools must
        # not widen them or add a raw action/slot parameter.
        for name in ("get_state", "open_shop", "act", "press", "load_game", "new_game"):
            self.assertIn(name, tools, "existing tool disappeared: " + name)

        expected = {
            "get_new_game_options": (set(), set()),
            "open_new_game_menu": ({"rev"}, {"rev"}),
            "select_new_game_option": ({"kind", "id", "rev"}, {"kind", "id", "rev"}),
            "new_game_next_page": ({"row", "direction", "rev"}, {"row", "direction", "rev"}),
            "focus_new_game_selected_perk": ({"id", "rev"}, {"id", "rev"}),
            "blur_new_game_option": ({"id", "rev"}, {"id", "rev"}),
            "new_game_continue": ({"rev"}, {"rev"}),
        }

        for name, (properties, required) in expected.items():
            schema = tools[name].input_schema
            props = schema.get("properties", {})
            self.assertEqual(
                properties,
                set(props),
                "%s exposes unexpected parameters: %r" % (name, sorted(props)),
            )
            self.assertFalse(
                FORBIDDEN_PARAMS & set(props),
                "%s must not expose raw action/slot parameters" % name,
            )
            self.assertEqual(
                required,
                set(schema.get("required", [])),
                "%s required parameters differ" % name,
            )

        self.assertEqual("integer", tools["open_new_game_menu"].input_schema["properties"]["rev"]["type"])
        self.assertEqual("integer", tools["new_game_continue"].input_schema["properties"]["rev"]["type"])
        self.assertEqual("string", tools["select_new_game_option"].input_schema["properties"]["id"]["type"])
        self.assertEqual(
            ["scenario", "perk", "difficulty"],
            tools["select_new_game_option"].input_schema["properties"]["kind"]["enum"],
        )
        self.assertEqual(
            ["available", "selected"],
            tools["new_game_next_page"].input_schema["properties"]["row"]["enum"],
        )
        self.assertEqual(
            [-1, 1],
            tools["new_game_next_page"].input_schema["properties"]["direction"]["enum"],
        )

        # Every write is single-step and tells the caller to re-read the menu.
        for name in WRITE_TOOLS:
            description = tools[name].description.lower()
            self.assertIn("re-read", description, name + " must document a readback")

        blur_description = tools["blur_new_game_option"].description.lower()
        self.assertIn("pointer-exit", blur_description)
        self.assertIn("does not change", blur_description)

    def test_each_write_is_one_authenticated_action_with_rev_and_action_id(self):
        calls = [
            ("open_new_game_menu", {"rev": 7}),
            ("select_new_game_option", {"kind": "scenario", "id": "fence", "rev": 7}),
            ("new_game_next_page", {"row": "available", "direction": 1, "rev": 7}),
            ("focus_new_game_selected_perk", {"id": "tough", "rev": 7}),
            ("blur_new_game_option", {"id": "tough", "rev": 7}),
            ("new_game_continue", {"rev": 7}),
            ("get_new_game_options", {}),
        ]

        async def scenario(session):
            results = []
            for name, arguments in calls:
                result = await session.call_tool(name, arguments)
                results.append((name, result))
            return results

        results = self._run(scenario)
        for name, result in results:
            self.assertFalse(result.is_error, name + " failed: " + result.content[0].text)

        self.assertEqual(len(calls), len(self.httpd.requests), "each MCP call must send exactly one action")

        action_ids = set()
        for (name, arguments), record in zip(calls, self.httpd.requests):
            body = record["body"]
            self.assertEqual("Bearer " + TOKEN, record["authorization"], name + " lost the bearer token")
            self.assertEqual(name, body.get("op"), name + " sent a different op")
            self.assertNotEqual("act", body.get("op"), "new-game tools must not use the raw act escape hatch")
            for forbidden in FORBIDDEN_PARAMS - {"action_id", "op"}:
                self.assertNotIn(forbidden, body, name + " leaked forbidden parameter " + forbidden)

            if name == "get_new_game_options":
                self.assertNotIn("rev", body, "read must not carry rev")
                self.assertNotIn("id", body, "read must not carry id")
                continue

            self.assertEqual(7, body.get("rev"), name + " lost rev")
            action_id = body.get("action_id")
            self.assertIsInstance(action_id, str, name + " has no action_id")
            uuid.UUID(action_id)
            self.assertNotIn(action_id, action_ids, "action_id was reused")
            action_ids.add(action_id)

            if name == "select_new_game_option":
                self.assertEqual("scenario", body.get("kind"))
                self.assertEqual("fence", body.get("id"))
            elif name == "new_game_next_page":
                self.assertEqual("available", body.get("row"))
                self.assertEqual(1, body.get("direction"))
            elif name in ("focus_new_game_selected_perk", "blur_new_game_option"):
                self.assertEqual("tough", body.get("id"))

    def test_second_row_and_previous_page_are_forwarded_exactly(self):
        async def scenario(session):
            select = await session.call_tool(
                "select_new_game_option",
                {"kind": "perk", "id": "saving", "rev": 11},
            )
            page = await session.call_tool(
                "new_game_next_page",
                {"row": "selected", "direction": -1, "rev": 11},
            )
            self.assertFalse(select.is_error, select.content[0].text)
            self.assertFalse(page.is_error, page.content[0].text)

        self._run(scenario)
        self.assertEqual(2, len(self.httpd.requests))
        select_body = self.httpd.requests[0]["body"]
        page_body = self.httpd.requests[1]["body"]
        self.assertEqual({"op", "kind", "id", "rev", "action_id"}, set(select_body))
        self.assertEqual("perk", select_body["kind"])
        self.assertEqual("saving", select_body["id"])
        self.assertEqual(11, select_body["rev"])
        self.assertEqual({"op", "row", "direction", "rev", "action_id"}, set(page_body))
        self.assertEqual("selected", page_body["row"])
        self.assertEqual(-1, page_body["direction"])
        self.assertEqual(11, page_body["rev"])

    def test_invalid_parameters_are_rejected_before_any_bridge_call(self):
        tools = self._tool_map()
        for name in ("select_new_game_option", "new_game_next_page",
                     "focus_new_game_selected_perk", "blur_new_game_option",
                     "open_new_game_menu", "new_game_continue"):
            self.assertIn(name, tools, "missing semantic new-game tool: " + name)

        cases = (
            ("select_new_game_option", {"kind": "ship", "id": "fence", "rev": 7}),
            ("select_new_game_option", {"kind": "scenario", "id": "", "rev": 7}),
            ("select_new_game_option", {"kind": "scenario", "id": "fence", "rev": -1}),
            ("select_new_game_option", {"kind": "scenario", "id": "fence", "rev": "7"}),
            ("new_game_next_page", {"row": "hidden", "direction": 1, "rev": 7}),
            ("new_game_next_page", {"row": "available", "direction": 0, "rev": 7}),
            ("new_game_next_page", {"row": "available", "direction": 2, "rev": 7}),
            ("new_game_next_page", {"row": "available", "direction": True, "rev": 7}),
            ("focus_new_game_selected_perk", {"id": "", "rev": 7}),
            ("focus_new_game_selected_perk", {"id": "   ", "rev": 7}),
            ("blur_new_game_option", {"id": "", "rev": 7}),
            ("blur_new_game_option", {"id": "   ", "rev": 7}),
            ("blur_new_game_option", {"id": "tough", "rev": -1}),
            ("blur_new_game_option", {"id": "tough", "rev": "7"}),
            ("open_new_game_menu", {"rev": "7"}),
            ("new_game_continue", {"rev": -1}),
        )

        async def scenario(session):
            outcomes = []
            for name, arguments in cases:
                result = await session.call_tool(name, arguments)
                outcomes.append((name, arguments, result))
            return outcomes

        outcomes = self._run(scenario)
        for name, arguments, result in outcomes:
            with self.subTest(name=name, arguments=arguments):
                self._assert_validation_failure(result)
        self.assertEqual([], self.httpd.requests, "invalid arguments must not reach the bridge")

    def test_generic_tools_still_work_alongside_the_new_surface(self):
        async def scenario(session):
            state = await session.call_tool("get_state", {})
            opened = await session.call_tool("open_shop", {"rev": 7})
            options = await session.call_tool("get_new_game_options", {})
            return state, opened, options

        state, opened, options = self._run(scenario)
        for result in (state, opened, options):
            self.assertFalse(result.is_error, result.content[0].text)

        state_payload = json.loads(state.content[0].text)
        self.assertTrue(state_payload["in_game"])
        opened_payload = json.loads(opened.content[0].text)
        self.assertEqual("open_shop", opened_payload["op"])
        options_payload = json.loads(options.content[0].text)
        self.assertEqual("get_new_game_options", options_payload["op"])

        writes = [record for record in self.httpd.requests if record["body"].get("op") != "get_new_game_options"]
        self.assertEqual(1, len(writes), "only open_shop should be a write here")
        self.assertEqual("open_shop", writes[0]["body"]["op"])
        self.assertEqual(7, writes[0]["body"]["rev"])
        self.assertIn("action_id", writes[0]["body"])


class NewGameToolBodyValidationTests(unittest.TestCase):
    """Defense in depth for direct Python callers bypassing MCP/Pydantic."""

    def setUp(self):
        try:
            import mcp_server
        except ImportError as error:  # mcp/pydantic are optional for the stdlib-only client tests
            self.skipTest("mcp server dependencies unavailable: %s" % (error,))

        self.mcp_server = mcp_server
        self.client = _SpyClient()
        self.server = mcp_server.build_server(self.client)
        self.manager = getattr(self.server, "_tool_manager", None)
        if self.manager is None or not hasattr(self.manager, "get_tool"):
            self.skipTest("mcp server does not expose tool manager")

    def _fn(self, name):
        tool = self.manager.get_tool(name)
        self.assertIsNotNone(tool, "missing tool " + name)
        return tool.fn

    def _assert_rejected(self, name, field, **kwargs):
        result = self._fn(name)(**kwargs)
        self.assertFalse(result.get("ok"), "%s accepted invalid %s: %r" % (name, field, kwargs))
        self.assertEqual("invalid_argument", result.get("error"))
        self.assertEqual(field, result.get("field"))
        self.assertEqual([], self.client.calls, name + " reached the bridge with invalid " + field)

    def test_body_rejects_kind_row_direction_and_ids(self):
        self._assert_rejected("select_new_game_option", "kind", kind="ship", id="fence", rev=7)
        self._assert_rejected("select_new_game_option", "id", kind="perk", id="", rev=7)
        self._assert_rejected("select_new_game_option", "id", kind="perk", id="   ", rev=7)
        self._assert_rejected("new_game_next_page", "row", row="other", direction=1, rev=7)
        self._assert_rejected("new_game_next_page", "direction", row="available", direction=0, rev=7)
        self._assert_rejected("new_game_next_page", "direction", row="available", direction=True, rev=7)
        self._assert_rejected("focus_new_game_selected_perk", "id", id="", rev=7)
        self._assert_rejected("blur_new_game_option", "id", id="", rev=7)
        self._assert_rejected("blur_new_game_option", "id", id="   ", rev=7)
        self._assert_rejected("blur_new_game_option", "rev", id="tough", rev="7")
        self._assert_rejected("blur_new_game_option", "rev", id="tough", rev=-1)
        self._assert_rejected("open_new_game_menu", "rev", rev="7")
        self._assert_rejected("new_game_continue", "rev", rev=-1)

    def test_body_still_forwards_one_valid_call(self):
        result = self._fn("select_new_game_option")(kind="scenario", id="fence", rev=7)
        self.assertTrue(result["ok"])
        self.assertEqual(
            [("select_new_game_option", {"kind": "scenario", "id": "fence", "rev": 7})],
            self.client.calls,
        )

    def test_body_blur_forwards_one_valid_call(self):
        result = self._fn("blur_new_game_option")(id="tough", rev=7)
        self.assertTrue(result["ok"])
        self.assertEqual(
            [("blur_new_game_option", {"id": "tough", "rev": 7})],
            self.client.calls,
        )


if __name__ == "__main__":
    unittest.main()
