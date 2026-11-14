"""S7A: server-side tool trimming for recording (`--tools full|recording`).

The full server exposes 100 tools. The recording entrypoint must expose only the
minimum first-week surface, so a host-side allowlist is no longer the only thing
keeping `act`/`press`/`new_game` away from a model. These tests pin the mode
switch, the exact recording surface, the explicit exclusions, the demo
allowlist subset relation, and the argument error path.
"""

import asyncio
import os
import subprocess
import sys
import tempfile
import unittest

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError:  # the mcp package is optional for the stdlib-only client tests
    ClientSession = None
    StdioServerParameters = None
    stdio_client = None

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
ROOT = os.path.abspath(os.path.join(AGENT_DIR, ".."))
MCP_SERVER = os.path.join(AGENT_DIR, "mcp_server.py")
sys.path.insert(0, AGENT_DIR)

import demo_agent  # noqa: E402
import mcp_server  # noqa: E402

#: The recording surface promised in the S7A plan. Keep this list explicit so a
#: broadened server whitelist cannot silently widen the recorded attack surface.
EXPECTED_RECORDING_TOOLS = frozenset({
    "ping",
    "get_state",
    "get_legal_actions",
    "get_run_summary",
    "get_action_history",
    "wait_for_events",
    "wait_for_change",
    "get_new_game_options",
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_next_page",
    "new_game_continue",
    "focus_new_game_selected_perk",
    "blur_new_game_option",
    "confirm_new_game",
    "get_recording_save_status",
    "save_recording_game",
    "load_recording_game",
    "choose_dialogue",
    "advance_dialogue",
    "confirm_trade",
    "open_shop",
    "call_next_customer",
    "list_goods",
    "get_item",
    "list_inventory",
    "end_day",
    "sleep",
})

#: Tools that exist in full mode and must never appear in the recording mode.
#: `act`/`press`/`get_ui_snapshot` are arbitrary-target escape hatches; the
#: legacy/new-game and hidden-state tools are outside the recorded flow.
FORBIDDEN_IN_RECORDING = (
    "act",
    "press",
    "get_ui_snapshot",
    "new_game",
    "load_game",
    "save_game",
    "wine_add_water",
    "machine_insert",
    "machine_collect",
    "move_item",
    "set_label",
    "analyze_item",
    "go_out",
    "buy_wholesale",
    "deliver_request",
    "arrest_wanted",
    "redeem_ticket",
    "buy_upgrade",
    "toggle_service",
    "activate_item",
    "toggle_item",
    "use_tool",
    "bargain",
)


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class ToolsModeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=15528\ntoken=%s\n" % ("0" * 32))

    @classmethod
    def tearDownClass(cls):
        if os.path.exists(cls.config_path):
            os.unlink(cls.config_path)

    def _list_tool_names(self, *extra_args):
        params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", self.config_path] + list(extra_args),
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    return {tool.name for tool in tools.tools}

        return asyncio.run(scenario())

    def test_full_mode_is_the_default_and_keeps_100_tools(self):
        default_names = self._list_tool_names()
        explicit_names = self._list_tool_names("--tools", "full")

        self.assertEqual(100, len(default_names))
        self.assertEqual(default_names, explicit_names,
                         "omitting --tools must behave exactly like --tools full")
        for expected in ("act", "press", "get_ui_snapshot", "new_game", "save_game"):
            self.assertIn(expected, explicit_names)

    def test_recording_mode_registers_only_the_recording_surface(self):
        names = self._list_tool_names("--tools", "recording")

        self.assertEqual(set(EXPECTED_RECORDING_TOOLS), names)
        self.assertEqual(EXPECTED_RECORDING_TOOLS, mcp_server.RECORDING_TOOL_NAMES)
        for required in EXPECTED_RECORDING_TOOLS:
            self.assertIn(required, names, "missing recording tool: " + required)

    def test_recording_mode_withholds_the_high_risk_surface(self):
        full_names = self._list_tool_names("--tools", "full")
        recording_names = self._list_tool_names("--tools", "recording")

        for forbidden in FORBIDDEN_IN_RECORDING:
            self.assertIn(forbidden, full_names, "stale forbidden-tool fixture: " + forbidden)
            self.assertNotIn(forbidden, recording_names,
                             "recording mode leaked " + forbidden)
        self.assertEqual(
            set(),
            {name for name in recording_names if name.startswith("debug_")},
            "recording mode must never register debug_* tools",
        )

    def test_demo_allowlist_is_a_subset_of_the_recording_surface(self):
        demo_tools = demo_agent.ALLOWED_OPS

        self.assertTrue(demo_tools, "the demo allowlist is empty")
        self.assertTrue(
            demo_tools <= mcp_server.RECORDING_TOOL_NAMES,
            "demo allowlist drifted outside the recording surface: "
            + repr(sorted(demo_tools - mcp_server.RECORDING_TOOL_NAMES)),
        )
        self.assertTrue(set(demo_agent.REQUIRED_TOOLS) <= demo_tools)
        self.assertTrue(demo_agent.RECORDING_TOOLS <= mcp_server.RECORDING_TOOL_NAMES)

    def test_invalid_tools_value_exits_non_zero_with_a_clear_error(self):
        completed = subprocess.run(
            [sys.executable, MCP_SERVER,
             "--config", self.config_path, "--tools", "recording-only"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=60,
        )
        stderr = completed.stderr.decode("utf-8", "replace")

        self.assertNotEqual(0, completed.returncode,
                            "an illegal --tools value must fail closed")
        self.assertIn("full", stderr)
        self.assertIn("recording", stderr)
        self.assertNotIn("Traceback", stderr)


if __name__ == "__main__":
    unittest.main()
