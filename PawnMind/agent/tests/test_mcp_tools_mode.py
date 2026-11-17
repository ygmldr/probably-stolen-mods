"""S7A: server-side tool trimming for recording (`--tools full|recording`).

The full server exposes 108 tools. The recording entrypoint must expose only the
minimum first-week surface, so a host-side allowlist is no longer the only thing
keeping `act`/`press`/`new_game` away from a model. These tests pin the mode
switch, the exact recording surface, the explicit exclusions, the demo
allowlist subset relation, and the argument error path.
"""

import asyncio
import json
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
import tool_policy  # noqa: E402
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mode_fixture import FakeBridge, dead_config  # noqa: E402

#: The recording surface: every tool a human player could equivalently use.
#: Keep this list explicit so a broadened server whitelist cannot silently
#: widen the recorded attack surface.
EXPECTED_RECORDING_TOOLS = frozenset({
    "activate_item",
    "advance_dialogue",
    "analyze_item",
    "answer_prompt",
    "arrest_wanted",
    "bargain",
    "bargain_preview",
    "barter_accept",
    "barter_status",
    "blur_new_game_option",
    "buy_upgrade",
    "buy_wholesale",
    "call",
    "call_next_customer",
    "capture_screenshot",
    "choose_dialogue",
    "close_windows",
    "commissary_buy",
    "confirm_new_game",
    "confirm_trade",
    "contraband_status",
    "deliver_request",
    "dismiss_current_client",
    "dismiss_tutorial_hints",
    "end_call",
    "end_day",
    "events_status",
    "expedition_status",
    "focus_new_game_selected_perk",
    "get_action_history",
    "get_calendar",
    "get_clientele",
    "get_day_report",
    "get_item",
    "get_layout",
    "get_legal_actions",
    "get_new_game_options",
    "get_operation",
    "get_pending_prompt",
    "get_recording_save_status",
    "get_run_summary",
    "get_state",
    "go_out",
    "health_status",
    "husbandry_status",
    "inspect_ticket",
    "list_analyzers",
    "list_books",
    "list_contacts",
    "list_containers",
    "list_factions",
    "list_goods",
    "list_interactions",
    "list_inventory",
    "list_labels",
    "list_locations",
    "list_machines",
    "list_marks",
    "list_requests",
    "list_screenshots",
    "list_services",
    "list_signs",
    "list_tickets",
    "list_upgrades",
    "list_wine",
    "load_game",
    "load_recording_game",
    "lottery_status",
    "machine_collect",
    "machine_grow_status",
    "machine_insert",
    "machine_status",
    "memory_bootstrap",
    "memory_get",
    "memory_retrieve",
    "memory_status",
    "move_item",
    "new_game_continue",
    "new_game_next_page",
    "open_new_game_menu",
    "open_shop",
    "phone_advance",
    "phone_choose",
    "phone_state",
    "ping",
    "read_book",
    "read_item",
    "redeem_ticket",
    "report_client",
    "return_home",
    "scavenge",
    "security_status",
    "select_new_game_option",
    "set_label",
    "set_pen_mode",
    "sleep",
    "start_fermentation",
    "toggle_item",
    "toggle_service",
    "use_tool",
    "wait_for_change",
    "wait_for_events",
    "wait_operation",
    "wanted_status",
    "wine_add_ingredient",
    "wine_status",
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


class ModeTestCase(unittest.TestCase):
    """Shared fake-bridge plumbing for the mode fixtures."""

    def setUp(self):
        self._bridges = []
        self._configs = []

    def tearDown(self):
        for bridge in self._bridges:
            bridge.close()
        for path in self._configs:
            if os.path.exists(path):
                os.unlink(path)

    def bridge(self, mode="test", policy_sha256=None, include_mode=True):
        instance = FakeBridge(mode, policy_sha256, include_mode)
        self._bridges.append(instance)
        path = instance.config()
        self._configs.append(path)
        return instance, path

    def _env(self, **extra):
        env = dict(os.environ)
        env.update({key: str(value) for key, value in extra.items()})
        return env

    def _list_tools(self, *extra_args, bridge=None, config_path=None, env=None):
        if config_path is None:
            if bridge is None:
                bridge, config_path = self.bridge("test")
            else:
                config_path = bridge.config()
                self._configs.append(config_path)

        params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", config_path] + list(extra_args),
            env=env,
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    return {tool.name: tool for tool in tools.tools}

        return asyncio.run(scenario())

    def _list_tool_names(self, *extra_args, bridge=None, config_path=None, env=None):
        return set(self._list_tools(*extra_args, bridge=bridge, config_path=config_path,
                                    env=env))

    def _call_tool(self, name, arguments, config_path, *extra_args, env=None,
                   mutate=None):
        """Start one MCP session and call a tool, optionally mutating the bridge."""

        params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", config_path] + list(extra_args),
            env=env,
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    first = await session.call_tool(name, arguments)
                    if mutate is not None:
                        mutate()
                    second = await session.call_tool(name, arguments)
                    return first, second

        return asyncio.run(scenario())

    @staticmethod
    def _payload(result):
        text = result.content[0].text
        try:
            return json.loads(text)
        except ValueError:
            return {"raw": text}


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class ToolsModeTests(ModeTestCase):
    def test_full_mode_is_the_default_and_keeps_the_whole_surface(self):
        default_names = self._list_tool_names()
        explicit_names = self._list_tool_names("--tools", "full")

        self.assertEqual(116, len(default_names))
        self.assertEqual(default_names, explicit_names,
                         "omitting --tools must behave exactly like --tools full")
        for expected in ("act", "press", "get_ui_snapshot", "new_game", "save_game"):
            self.assertIn(expected, explicit_names)

    def test_recording_mode_registers_only_the_recording_surface(self):
        names = self._list_tool_names("--tools", "recording")

        self.assertEqual(106, len(EXPECTED_RECORDING_TOOLS))
        self.assertEqual(set(EXPECTED_RECORDING_TOOLS), names)
        self.assertEqual(EXPECTED_RECORDING_TOOLS, mcp_server.RECORDING_TOOL_NAMES)
        for required in EXPECTED_RECORDING_TOOLS:
            self.assertIn(required, names, "missing recording tool: " + required)

    def test_default_memories_db_lives_beside_the_events_db(self):
        path = mcp_server._default_memories_db()
        self.assertTrue(path.endswith(
            os.path.join("play", "memory", "memories.db")), path)
        self.assertFalse(path.endswith(
            os.path.join("play", "memory", "memory.db")), path)

    def test_memory_tools_are_read_only_and_need_no_rev(self):
        tools = self._list_tools("--tools", "recording")

        expected = {
            "memory_bootstrap": {"limit", "scope"},
            "memory_retrieve": {"query", "limit", "scope", "state",
                                "token_budget"},
            "memory_get": {"memory_id"},
            "memory_status": set(),
        }
        for name, properties in expected.items():
            self.assertIn(name, tools)
            self.assertEqual(
                properties,
                set(tools[name].input_schema.get("properties", {})),
                name + " parameter surface")
            self.assertNotIn(
                "rev", tools[name].input_schema.get("properties", {}),
                name + " is read-only and must not require a rev")
            self.assertIn("read", (tools[name].description or "").lower())
        self.assertEqual(
            {"memory_id"}, set(tools["memory_get"].input_schema["required"]))
        self.assertEqual(
            {"query"}, set(tools["memory_retrieve"].input_schema["required"]))

    def test_prompt_tools_are_registered_with_closed_choices(self):
        tools = self._list_tools("--tools", "recording")

        pending = tools["get_pending_prompt"]
        answer = tools["answer_prompt"]

        self.assertEqual(
            set(),
            set(pending.input_schema.get("properties", {    "dispatch_expedition",
    "advance_expedition",
})),
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

    def test_allowed_tools_for_mode_matches_the_policy(self):
        self.assertEqual(set(), mcp_server.allowed_tools_for_mode("play"),
                         "play exposes only live-evidenced tools, and there are none")
        bootstrap = mcp_server.allowed_tools_for_mode("bootstrap")
        self.assertNotIn("get_state", bootstrap, "bootstrap never reads the snapshot")
        self.assertIn("get_new_game_options", bootstrap)
        self.assertIsNone(mcp_server.allowed_tools_for_mode("test"), "test is unfiltered")

    def test_play_mode_exposes_only_local_tools(self):
        _, config_path = self.bridge("play")
        names = self._list_tool_names("--mode", "play", config_path=config_path)
        self.assertNotIn("get_state", names)
        self.assertNotIn("open_shop", names)
        self.assertNotIn("confirm_trade", names)
        self.assertTrue(names <= set(mcp_server.LOCAL_TOOL_NAMES),
                        "only local tools survive a play-mode filter: %r" % (sorted(names),))

    def test_bootstrap_mode_hides_snapshot_reads(self):
        _, config_path = self.bridge("bootstrap")
        names = self._list_tool_names("--mode", "bootstrap", config_path=config_path)
        for hidden in ("get_state", "get_run_summary", "get_legal_actions",
                       "wait_for_change", "wait_for_events", "wait_until"):
            self.assertNotIn(hidden, names, "bootstrap must not expose " + hidden)
        self.assertIn("get_new_game_options", names)

    def test_explicit_test_mode_keeps_the_full_surface(self):
        _, config_path = self.bridge("test")
        self.assertEqual(116, len(self._list_tool_names("--mode", "test",
                                                       config_path=config_path)))

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
        config_path = dead_config()
        self._configs.append(config_path)
        completed = subprocess.run(
            [sys.executable, MCP_SERVER,
             "--config", config_path, "--tools", "recording-only"],
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


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class ModeLockdownTests(ModeTestCase):
    """R4/F5b: the registered surface is fail-closed and cannot go stale."""

    def test_r4_f5_01_unreachable_bridge_is_locked(self):
        config_path = dead_config()
        self._configs.append(config_path)

        names = self._list_tool_names("--tools", "full", config_path=config_path)

        self.assertTrue(names <= set(mcp_server.LOCAL_TOOL_NAMES),
                        "an unreachable bridge must not expose game tools: %r"
                        % (sorted(names),))
        self.assertNotIn("get_state", names)
        self.assertNotIn("open_shop", names)
        self.assertNotIn("new_game", names)
        self.assertIn("memory_status", names, "the local tools stay available")

    def test_r4_f5_02_unusable_health_is_locked(self):
        for mode, include in (("test", False), ("locked", True), ("debug", True), ("", True)):
            _, config_path = self.bridge(mode, include_mode=include)
            names = self._list_tool_names(config_path=config_path)
            self.assertTrue(
                names <= set(mcp_server.LOCAL_TOOL_NAMES),
                "run_mode=%r (present=%r) must lock the surface, got %r"
                % (mode, include, sorted(names)),
            )

    def test_r4_f5_03_play_without_live_evidence_exposes_nothing(self):
        _, config_path = self.bridge("play")
        names = self._list_tool_names("--mode", "play", config_path=config_path)

        policy = tool_policy.load_policy()
        self.assertEqual(set(), set(tool_policy.play_allowlist(policy)),
                         "the fixture assumes an empty live-evidence allowlist")
        self.assertEqual(set(mcp_server.LOCAL_TOOL_NAMES), names,
                         "play exposes only the local tools while the list is empty")

    def test_r4_f5_04_requested_test_mode_needs_a_test_bridge(self):
        _, config_path = self.bridge("play")
        names = self._list_tool_names("--mode", "test", config_path=config_path)

        self.assertTrue(names <= set(mcp_server.LOCAL_TOOL_NAMES),
                        "explicit --mode test must not override the bridge: %r"
                        % (sorted(names),))
        self.assertNotEqual(116, len(names))

    def test_r4_f5_05_mode_change_refuses_the_stale_surface(self):
        bridge, config_path = self.bridge("bootstrap")

        first, second = self._call_tool(
            "get_new_game_options", {}, config_path, "--mode", "bootstrap",
            env=self._env(PAWNMIND_MODE_RECHECK_SECONDS=0),
            mutate=bridge.lock,
        )

        self.assertFalse(first.is_error, "the registered tool works while it matches")
        payload = self._payload(second)
        self.assertEqual("mode_changed", payload.get("error"),
                         "a stale surface refuses instead of calling the bridge: %r"
                         % (payload,))
        self.assertIn("restart", (payload.get("detail") or "").lower())

    def test_r4_f5_06_bootstrap_still_hides_every_snapshot_reader(self):
        _, config_path = self.bridge("bootstrap")
        names = self._list_tool_names("--mode", "bootstrap", config_path=config_path)

        for hidden in ("get_state", "get_run_summary", "get_legal_actions",
                       "wait_for_change", "wait_for_events", "wait_until",
                       "get_ui_snapshot", "press"):
            self.assertNotIn(hidden, names, "bootstrap must not expose " + hidden)
        self.assertIn("get_new_game_options", names)


if __name__ == "__main__":
    unittest.main()
