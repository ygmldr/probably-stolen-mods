"""S4 fake tests for the recording-save MCP tools.

Everything here is offline: a local fake HTTP bridge records the exact action
payloads, and the real MCP server is started over stdio against that fake.
No game port, game process, save file or real bridge is touched.
"""

import asyncio
import inspect
import json
import os
import sys
import tempfile
import threading
import unittest
import uuid
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
sys.path.insert(0, ROOT)

RECORDING_TOOLS = (
    "get_recording_save_status",
    "save_recording_game",
    "load_recording_game",
    "confirm_new_game",
)
WRITE_TOOLS = (
    "save_recording_game",
    "load_recording_game",
    "confirm_new_game",
)
FORBIDDEN_PARAMS = {
    "action", "args", "action_id", "op", "slot", "raw",
    "recording_slot", "path", "paths", "preview", "previews",
}
STATUS_KEYS = {
    "ok",
    "op",
    "recording_mode",
    "configured_slot",
    "bound",
    "binding",
    "slot_matches",
    "run_matches",
    "last_save_time",
    "error",
}

STATUS_PAYLOAD = {
    "ok": True,
    "op": "get_recording_save_status",
    "recording_mode": "bound",
    "configured_slot": 17,
    "bound": True,
    "binding": {
        "slot": 17,
        "run_id": "run-0001",
        "run_number": 1,
        "start_type": "apprentice",
        "start_scenario_id": "fence",
    },
    "slot_matches": True,
    "run_matches": True,
    "last_save_time": "2026-11-14T12:00:00",
    "error": None,
}


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
            self._json({"ok": True, "name": "pawnmind",
                        "run_mode": "test",
                        "policy_sha256": _policy_sha256()})
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
        if op == "get_recording_save_status":
            self._json(STATUS_PAYLOAD)
        else:
            self._json(
                {
                    "ok": True,
                    "op": op,
                    "rev": 8,
                    "dispatched": True,
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
class RecordingMcpTests(unittest.TestCase):
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

    def test_recording_tools_are_registered_with_strict_schemas(self):
        tools = self._tool_map()

        for name in RECORDING_TOOLS:
            self.assertIn(name, tools, "missing recording-save tool: " + name)

        # The generic escape hatches stay where they were; the recording tools
        # must not widen them or add a raw action/slot parameter.
        for name in ("get_state", "open_shop", "act", "press", "load_game", "new_game",
                     "save_game", "get_recording_save_status"):
            self.assertIn(name, tools, "existing tool disappeared: " + name)

        expected = {
            "get_recording_save_status": (set(), set()),
            "save_recording_game": ({"rev"}, {"rev"}),
            "load_recording_game": ({"rev"}, {"rev"}),
            "confirm_new_game": ({"rev"}, {"rev"}),
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

        for name in WRITE_TOOLS:
            self.assertEqual(
                "integer",
                tools[name].input_schema["properties"]["rev"]["type"],
                name + " must take a strict integer rev",
            )

        status_description = tools["get_recording_save_status"].description.lower()
        for fragment in ("read-only", "locked by default", "binding",
                         "other slots", "previews", "paths"):
            self.assertIn(fragment, status_description)

        for name in ("save_recording_game", "load_recording_game"):
            description = tools[name].description.lower()
            for fragment in ("locked by default", "bound", "no slot parameter", "re-read"):
                self.assertIn(fragment, description, name + " docstring is missing " + fragment)

        confirm_description = tools["confirm_new_game"].description.lower()
        for fragment in ("locked by default", "initialsave", "first-run", "native yes",
                         "exactly one", "does not retry", "re-read"):
            self.assertIn(fragment, confirm_description)

    def test_each_call_is_one_authenticated_action_with_rev_and_action_id(self):
        calls = [
            ("get_recording_save_status", {}),
            ("save_recording_game", {"rev": 7}),
            ("load_recording_game", {"rev": 7}),
            ("confirm_new_game", {"rev": 7}),
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
            for forbidden in FORBIDDEN_PARAMS - {"action_id", "op"}:
                self.assertNotIn(forbidden, body, name + " leaked forbidden parameter " + forbidden)

            if name == "get_recording_save_status":
                self.assertEqual({"op", "action_id"}, set(body), "read must not carry rev or slot")
                continue

            self.assertEqual({"op", "rev", "action_id"}, set(body), name + " leaked extra parameters")
            self.assertEqual(7, body.get("rev"), name + " lost rev")
            action_id = body.get("action_id")
            self.assertIsInstance(action_id, str, name + " has no action_id")
            uuid.UUID(action_id)
            self.assertNotIn(action_id, action_ids, "action_id was reused")
            action_ids.add(action_id)

        status_payload = json.loads(results[0][1].content[0].text)
        self.assertEqual(STATUS_PAYLOAD, status_payload)
        self.assertEqual(
            STATUS_KEYS,
            set(status_payload),
            "status must stay the whitelisted binding surface",
        )
        for write_name, result in results[1:]:
            payload = json.loads(result.content[0].text)
            self.assertTrue(payload["ok"])
            self.assertEqual(write_name, payload["op"])
            self.assertTrue(payload["dispatched"])

    def test_invalid_rev_is_rejected_before_any_bridge_call(self):
        cases = (
            ("save_recording_game", {"rev": -1}),
            ("save_recording_game", {"rev": "7"}),
            ("save_recording_game", {"rev": True}),
            ("save_recording_game", {"rev": 1.5}),
            ("save_recording_game", {"rev": None}),
            ("save_recording_game", {}),
            ("load_recording_game", {"rev": -1}),
            ("load_recording_game", {"rev": "7"}),
            ("load_recording_game", {"rev": True}),
            ("load_recording_game", {"rev": 1.5}),
            ("load_recording_game", {}),
            ("confirm_new_game", {"rev": -1}),
            ("confirm_new_game", {"rev": "7"}),
            ("confirm_new_game", {"rev": True}),
            ("confirm_new_game", {"rev": 1.5}),
            ("confirm_new_game", {}),
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

    def test_slot_and_raw_arguments_never_reach_the_bridge(self):
        """The tool schema/signature exposes no slot/raw escape hatch.

        This MCP runtime (checked: func_metadata builds a default Pydantic
        model, extra="ignore") drops unknown kwargs before the tool body. The
        enforceable invariant is that neither the tool nor the bridge payload
        ever carries a caller-supplied slot/raw value.
        """
        tools = self._tool_map()
        for name in RECORDING_TOOLS:
            self.assertIn(name, tools, "missing recording-save tool: " + name)

        cases = (
            ("save_recording_game", {"rev": 7, "slot": 17}),
            ("save_recording_game", {"rev": 7, "recording_slot": 17}),
            ("load_recording_game", {"rev": 7, "slot": 17}),
            ("load_recording_game", {"rev": 7, "raw": "load"}),
            ("confirm_new_game", {"rev": 7, "raw": "start"}),
            ("confirm_new_game", {"rev": 7, "action": "confirm_new_game"}),
            ("get_recording_save_status", {"rev": 7}),
            ("get_recording_save_status", {"slot": 17}),
        )

        async def scenario(session):
            outcomes = []
            for name, arguments in cases:
                result = await session.call_tool(name, arguments)
                outcomes.append((name, arguments, result))
            return outcomes

        outcomes = self._run(scenario)
        successful = 0
        for name, arguments, result in outcomes:
            with self.subTest(name=name, arguments=arguments):
                if result.is_error:
                    continue
                payload = json.loads(result.content[0].text)
                if payload.get("ok"):
                    successful += 1
                else:
                    self.assertEqual("invalid_argument", payload.get("error"))

        self.assertEqual(successful, len(self.httpd.requests), "one action per accepted call")
        for record in self.httpd.requests:
            body = record["body"]
            self.assertEqual("Bearer " + TOKEN, record["authorization"])
            self.assertFalse(
                (FORBIDDEN_PARAMS - {"action_id", "op"}) & set(body),
                "slot/raw parameter leaked to the bridge: %r" % (body,),
            )
            if body.get("op") == "get_recording_save_status":
                self.assertEqual({"op", "action_id"}, set(body))
            else:
                self.assertEqual({"op", "rev", "action_id"}, set(body))
                self.assertEqual(7, body["rev"])

    def test_generic_tools_still_work_alongside_the_recording_surface(self):
        async def scenario(session):
            state = await session.call_tool("get_state", {})
            opened = await session.call_tool("open_shop", {"rev": 7})
            status = await session.call_tool("get_recording_save_status", {})
            return state, opened, status

        state, opened, status = self._run(scenario)
        for result in (state, opened, status):
            self.assertFalse(result.is_error, result.content[0].text)

        state_payload = json.loads(state.content[0].text)
        self.assertTrue(state_payload["in_game"])
        opened_payload = json.loads(opened.content[0].text)
        self.assertEqual("open_shop", opened_payload["op"])
        status_payload = json.loads(status.content[0].text)
        self.assertEqual("get_recording_save_status", status_payload["op"])

        writes = [record for record in self.httpd.requests if record["body"].get("op") != "get_recording_save_status"]
        self.assertEqual(1, len(writes), "only open_shop should be a write here")
        self.assertEqual("open_shop", writes[0]["body"]["op"])
        self.assertEqual(7, writes[0]["body"]["rev"])
        self.assertIn("action_id", writes[0]["body"])


class RecordingToolBodyValidationTests(unittest.TestCase):
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

    def _assert_rejected(self, name, **kwargs):
        result = self._fn(name)(**kwargs)
        self.assertFalse(result.get("ok"), "%s accepted invalid rev: %r" % (name, kwargs))
        self.assertEqual("invalid_argument", result.get("error"))
        self.assertEqual("rev", result.get("field"))
        self.assertEqual([], self.client.calls, name + " reached the bridge with invalid rev")

    def test_body_rejects_non_strict_or_negative_rev(self):
        for name in WRITE_TOOLS:
            aspects = (
                {"rev": -1},
                {"rev": "7"},
                {"rev": True},
                {"rev": 1.5},
                {"rev": None},
            )
            for kwargs in aspects:
                with self.subTest(name=name, kwargs=kwargs):
                    self._assert_rejected(name, **kwargs)

    def test_body_rejects_slot_and_raw_parameters_for_direct_callers(self):
        for name in WRITE_TOOLS:
            for forbidden in ("slot", "recording_slot", "raw", "action", "op", "path", "preview"):
                with self.subTest(name=name, forbidden=forbidden):
                    with self.assertRaises(TypeError):
                        self._fn(name)(rev=7, **{forbidden: "x"})
                    self.assertEqual([], self.client.calls)

        for forbidden in ("rev", "slot", "raw"):
            with self.subTest(name="get_recording_save_status", forbidden=forbidden):
                with self.assertRaises(TypeError):
                    self._fn("get_recording_save_status")(**{forbidden: "x"})
                self.assertEqual([], self.client.calls)

    def test_tool_signatures_expose_only_rev_or_nothing(self):
        self.assertEqual([], list(inspect.signature(self._fn("get_recording_save_status")).parameters))
        for name in WRITE_TOOLS:
            self.assertEqual(["rev"], list(inspect.signature(self._fn(name)).parameters), name)

    def test_body_forwards_exactly_one_valid_call(self):
        for name in WRITE_TOOLS:
            result = self._fn(name)(rev=7)
            self.assertTrue(result["ok"])
        self.assertEqual(
            [
                ("save_recording_game", {"rev": 7}),
                ("load_recording_game", {"rev": 7}),
                ("confirm_new_game", {"rev": 7}),
            ],
            self.client.calls,
        )

        self.client.calls.clear()
        result = self._fn("get_recording_save_status")()
        self.assertTrue(result["ok"])
        self.assertEqual([("get_recording_save_status", {})], self.client.calls)


if __name__ == "__main__":
    unittest.main()
