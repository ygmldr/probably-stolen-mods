"""S7A: server-side tool trimming for recording (`--tools full|recording`).

The full server exposes 108 tools. The recording entrypoint must expose only the
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

#: The recording surface: every tool a human player could equivalently use.
#: Keep this list explicit so a broadened server whitelist cannot silently
#: widen the recorded attack surface.
EXPECTED_RECORDING_TOOLS = frozenset({
    "activate_item", "advance_dialogue", "advance_expedition", "analyze_item",
    "answer_prompt", "arrest_wanted", "bargain", "bargain_preview", "barter_accept",
    "barter_status", "blur_new_game_option", "buy_upgrade", "buy_wholesale",
    "call", "call_next_customer", "choose_dialogue", "commissary_buy",
    "confirm_new_game", "confirm_trade", "contraband_status", "deliver_request",
    "dismiss_tutorial_hints", "dispatch_expedition", "end_call", "end_day",
    "events_status", "expedition_status", "focus_new_game_selected_perk",
    "get_action_history", "get_calendar", "get_clientele", "get_day_report",
    "get_item", "get_layout", "get_legal_actions", "get_new_game_options",
    "get_pending_prompt", "get_recording_save_status",
    "get_run_summary", "get_state", "go_out", "health_status",
    "husbandry_status", "inspect_ticket", "list_analyzers", "list_books",
    "list_contacts", "list_containers", "list_factions", "list_goods",
    "list_interactions", "list_inventory", "list_labels", "list_locations",
    "list_machines", "list_marks", "list_requests", "list_services",
    "list_signs", "list_tickets", "list_upgrades", "list_wine",
    "load_game", "load_recording_game", "lottery_status", "machine_collect",
    "machine_grow_status", "machine_insert", "machine_status", "move_item",
    "new_game_continue", "new_game_next_page", "open_new_game_menu", "open_shop",
    "phone_advance", "phone_choose", "phone_state", "ping",
    "read_book", "read_item", "redeem_ticket", "report_client",
    "return_home", "save_game", "save_recording_game", "scavenge",
    "security_status", "select_new_game_option", "set_label", "set_pen_mode",
    "sleep", "start_fermentation", "toggle_item", "toggle_service",
    "use_tool", "wait_for_change", "wait_for_events", "wanted_status",
    "wine_add_ingredient", "wine_status",
    "capture_screenshot",
    "list_screenshots",
    "close_windows",
})

#: Escape hatches and shortcuts that exist in full mode and must never appear
#: in recording mode. Each entry has a written reason in mcp_server.py.
FORBIDDEN_IN_RECORDING = (
    "act",              # generic any-op entry: bypasses every gate
    "press",           # arbitrary UI control invocation
    "get_ui_snapshot",  # control enumerator that only feeds press
    "wait_until",       # host polling helper, not a game action
    "wine_add_water",   # injects pure water; not a player action
    "new_game",         # hardcodes apprentice + skipIntro, cannot express choice
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

    def _list_tools(self, *extra_args):
        params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", self.config_path] + list(extra_args),
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    return {tool.name: tool for tool in tools.tools}

        return asyncio.run(scenario())

    def _list_tool_names(self, *extra_args):
        return set(self._list_tools(*extra_args))

    def test_full_mode_is_the_default_and_keeps_the_whole_surface(self):
        default_names = self._list_tool_names()
        explicit_names = self._list_tool_names("--tools", "full")

        self.assertEqual(109, len(default_names))
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

    def test_prompt_tools_are_registered_with_closed_choices(self):
        tools = self._list_tools("--tools", "recording")

        pending = tools["get_pending_prompt"]
        answer = tools["answer_prompt"]

        self.assertEqual(
            set(),
            set(pending.input_schema.get("properties", {})),
            "get_pending_prompt must take no parameters (read-only)",
        )
        self.assertEqual(
            {"choice", "rev"},
            set(answer.input_schema.get("properties", {})),
            "answer_prompt exposes only choice and rev",
        )
        self.assertEqual(
            {"choice", "rev"},
            set(answer.input_schema.get("required", [])),
            "answer_prompt requires both choice and rev",
        )
        self.assertEqual(
            ["confirm", "cancel"],
            answer.input_schema["properties"]["choice"]["enum"],
            "answer_prompt choice is a closed set",
        )
        self.assertEqual(
            "integer",
            answer.input_schema["properties"]["rev"]["type"],
            "answer_prompt rev type",
        )

        pending_text = (pending.description or "").lower()
        answer_text = (answer.description or "").lower()
        self.assertIn("read", pending_text)
        self.assertIn("prompt", pending_text)
        self.assertIn("re-read", answer_text)
        self.assertIn("prompt", answer_text)

    def test_intel_tools_are_read_only_and_calendar_days_is_bounded(self):
        tools = self._list_tools("--tools", "recording")

        for name in ("get_day_report", "get_clientele"):
            self.assertEqual(
                set(),
                set(tools[name].input_schema.get("properties", {})),
                name + " must take no parameters (read-only)",
            )

        calendar = tools["get_calendar"]
        self.assertEqual(
            {"days"},
            set(calendar.input_schema.get("properties", {})),
            "get_calendar exposes only days",
        )
        self.assertEqual(
            7,
            calendar.input_schema["properties"]["days"].get("default"),
            "get_calendar defaults to one week",
        )
        self.assertEqual(
            [],
            calendar.input_schema.get("required", []),
            "get_calendar days is optional",
        )

        dismiss = tools["dismiss_tutorial_hints"]
        self.assertEqual(
            {"rev"},
            set(dismiss.input_schema.get("properties", {})),
            "dismiss_tutorial_hints exposes only rev",
        )
        self.assertEqual(
            {"rev"},
            set(dismiss.input_schema.get("required", [])),
            "dismiss_tutorial_hints requires rev",
        )
        text = (dismiss.description or "").lower()
        self.assertIn("hint", text)
        self.assertIn("rev", text)
        self.assertIn("skip the intro", text)

        self.assertIsNone(mcp_server._check_days(7))
        self.assertIsNone(mcp_server._check_days(14))
        self.assertEqual("invalid_argument", mcp_server._check_days(0)["error"])
        self.assertEqual("invalid_argument", mcp_server._check_days(15)["error"])
        self.assertEqual("invalid_argument", mcp_server._check_days(True)["error"])

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
