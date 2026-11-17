"""Contract: every state-changing MCP tool must require an explicit `rev`.

A write tool whose `rev` is optional cannot be called correctly by a model and
would be rejected server-side anyway; it must not be advertised that way.
"""

import asyncio
import json
import os
import sys
import unittest

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
CONF = os.path.join(ROOT, "test-artifacts", "audit-bridge.conf")

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


def _write_tool_schemas():
    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "agent.mcp_server", "--tools", "full", "--config", CONF],
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
        cls.schemas = _write_tool_schemas()

    def test_every_write_tool_requires_rev(self):
        offenders = []
        for name, schema in sorted(self.schemas.items()):
            if name in READ_ONLY:
                continue
            props = schema.get("properties") or {}
            if "rev" not in props:
                continue
            if "rev" not in (schema.get("required") or []):
                offenders.append({
                    "tool": name,
                    "rev_schema": props.get("rev"),
                })
        self.assertEqual([], offenders,
                         "write tools must declare rev as required: %s" % json.dumps(offenders))

    def test_wine_add_water_is_not_advertised_with_optional_rev(self):
        schema = self.schemas["wine_add_water"]
        self.assertIn("rev", schema.get("required") or [],
                      "wine_add_water mutates a bottle and needs rev")


if __name__ == "__main__":
    unittest.main()
