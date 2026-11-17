"""Contract: every state-changing MCP tool must require an explicit `rev`.

A write tool whose `rev` is optional cannot be called correctly by a model and
would be rejected server-side anyway; it must not be advertised that way.
"""

import asyncio
import json
import os
import sys
import tempfile
import unittest

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mode_fixture import FakeBridge  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
#: Written per run: test-artifacts/ is ignored, so a clean clone has no fixture.
FAKE_TOKEN = "0123456789abcdef0123456789abcdef"

# Tools that mutate game state through POST /action and therefore need `rev`.
# Read-only tools are covered by the router allowlist instead.
READ_ONLY = {
    "ping", "get_state", "get_run_summary", "get_legal_actions",
    "get_action_history", "wait_for_events", "wait_for_change", "wait_until",
    "get_operation", "wait_operation",
    "bargain_preview", "contraband_status", "events_status", "expedition_status",
    "get_item", "get_layout", "get_new_game_options", "get_recording_save_status",
    "get_pending_prompt", "get_day_report", "get_calendar", "get_clientele",
    "get_ui_snapshot", "health_status", "husbandry_status", "inspect_ticket",
    "list_analyzers", "list_books", "list_contacts", "list_containers",
    "list_factions", "list_goods", "list_interactions", "list_inventory",
    "list_labels", "list_locations", "list_machines", "list_marks",
    "list_requests", "list_services", "list_signs", "list_tickets",
    "list_upgrades", "list_wine", "lottery_status", "machine_grow_status",
    "machine_status", "phone_state", "read_book", "read_item",
    "security_status", "wanted_status", "barter_status", "wine_status",
    "capture_screenshot", "list_screenshots",
}


def _policy():
    sys.path.insert(0, os.path.join(ROOT, "agent"))
    import tool_policy
    return tool_policy.load_policy()


def _rev_offenders(schemas, write_tools):
    """Pure check so the contract itself is testable without the MCP server."""
    offenders = []
    for name in sorted(write_tools):
        schema = schemas.get(name)
        if schema is None:
            offenders.append({"tool": name, "reason": "missing from tools/list"})
            continue
        props = schema.get("properties") or {}
        required = schema.get("required") or []
        if "rev" not in props:
            offenders.append({"tool": name, "reason": "no rev property"})
        elif "rev" not in required:
            offenders.append({"tool": name, "reason": "rev is optional"})
    return offenders


def _write_tool_schemas(config_path):
    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "agent.mcp_server", "--tools", "full", "--config", config_path],
        cwd=ROOT,
    )

    async def run():
        async with stdio_client(params) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                listed = await session.list_tools()
                return {t.name: t.input_schema for t in listed.tools}

    return asyncio.run(run())


class WriteToolsRequireRevTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # R4/F5b: the registered surface follows the bridge's reported run_mode,
        # so the schema probe needs a bridge that really reports `test`.
        cls.bridge = FakeBridge("test")
        handle, cls.config_path = tempfile.mkstemp(prefix="pawnmind-schema-", suffix=".conf")
        with os.fdopen(handle, "w", encoding="utf-8") as config:
            config.write("port=%d\ntoken=%s\nrun_mode=test\nallow_test_mode=true\n"
                         % (cls.bridge.port, FAKE_TOKEN))
        try:
            cls.schemas = _write_tool_schemas(cls.config_path)
        finally:
            os.unlink(cls.config_path)

    @classmethod
    def tearDownClass(cls):
        cls.bridge.close()

    def test_every_write_tool_requires_rev(self):
        """F6: a write tool that omits rev entirely must fail, not be skipped.

        The target list comes from the policy (purity=write), so adding a write
        tool cannot silently bypass this contract by forgetting the field.
        """
        policy = _policy()
        write_tools = sorted(
            name for name, entry in policy["tools"].items() if entry["purity"] == "write")
        self.assertTrue(write_tools, "the policy lists write tools")

        self.assertEqual([], _rev_offenders(self.schemas, write_tools),
                         "write tools must require rev")

    def test_rev_checker_flags_a_tool_that_omits_rev(self):
        """The checker must fail on a missing rev, not skip it (F6)."""
        empty = {"properties": {"item_id": {}}, "required": ["item_id"]}
        optional = {"properties": {"rev": {}, "item_id": {}}, "required": ["item_id"]}
        good = {"properties": {"rev": {}, "item_id": {}}, "required": ["rev", "item_id"]}

        self.assertEqual([{"tool": "x", "reason": "no rev property"}],
                         _rev_offenders({"x": empty}, ["x"]))
        self.assertEqual([{"tool": "x", "reason": "rev is optional"}],
                         _rev_offenders({"x": optional}, ["x"]))
        self.assertEqual([], _rev_offenders({"x": good}, ["x"]))
        self.assertEqual([{"tool": "ghost", "reason": "missing from tools/list"}],
                         _rev_offenders({}, ["ghost"]))

    def test_policy_and_tool_list_agree_on_write_tools(self):
        policy = _policy()
        write_tools = {name for name, entry in policy["tools"].items()
                       if entry["purity"] == "write"}
        self.assertEqual(set(), write_tools - set(self.schemas),
                         "every policy write tool is registered")
    def test_wine_add_water_is_not_advertised_with_optional_rev(self):
        schema = self.schemas["wine_add_water"]
        self.assertIn("rev", schema.get("required") or [],
                      "wine_add_water mutates a bottle and needs rev")


if __name__ == "__main__":
    unittest.main()
