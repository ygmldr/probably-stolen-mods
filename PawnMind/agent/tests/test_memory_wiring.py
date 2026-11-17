"""P1 wiring test: the real MCP server records history when --memory on.

Uses the fake game (127.0.0.1, random port) — never the real game or a real save.
"""

import asyncio
import json
import os
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from agent.fake_game import FakeGame
from agent.memory.store import EventStore


class MemoryWiringTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.game = FakeGame().start()
        self.conf = os.path.join(self.tmp.name, "bridge.conf")
        self.game.write_config(self.conf)
        self.db = os.path.join(self.tmp.name, "memory.db")

    def tearDown(self):
        self.game.stop()
        self.tmp.cleanup()

    def _call(self, tool_name, arguments=None):
        params = StdioServerParameters(
            command=sys.executable,
            args=["-m", "agent.mcp_server", "--tools", "recording",
                  "--config", self.conf, "--memory", "on",
                  "--memory-db", self.db],
            cwd=ROOT,
        )

        async def run():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    return await session.call_tool(tool_name, arguments or {})

        return asyncio.run(run())

    def test_read_call_creates_an_observation_event(self):
        self._call("get_state")
        store = EventStore(self.db)
        try:
            timeline = store._require().execute(
                "SELECT * FROM timelines").fetchone()
            events = store.history(timeline["timeline_id"], limit=50)
        finally:
            store.close()
        types = [e["event_type"] for e in events]
        self.assertIn("observation", types)
        self.assertNotIn("intent", types)

    def test_write_call_creates_intent_and_result(self):
        # Recording is unconfigured in the fake game, so this is a normal
        # rejection: it still must produce exactly one intent + result pair.
        self._call("open_new_game_menu", {"rev": 0})
        store = EventStore(self.db)
        try:
            timeline = store._require().execute(
                "SELECT * FROM timelines").fetchone()
            events = store.history(timeline["timeline_id"], limit=50)
        finally:
            store.close()
        intents = [e for e in events if e["event_type"] == "intent"]
        self.assertEqual(1, len(intents))
        self.assertEqual("open_new_game_menu", intents[0]["tool_name"])
        self.assertIn(intents[0]["result_status"], ("rejected", "unknown", "success"))
        self.assertIsNotNone(intents[0]["action_id"],
                             "action_id must be recorded for idempotency")

    def test_memory_off_writes_nothing(self):
        params = StdioServerParameters(
            command=sys.executable,
            args=["-m", "agent.mcp_server", "--tools", "recording",
                  "--config", self.conf, "--memory", "off",
                  "--memory-db", self.db],
            cwd=ROOT,
        )

        async def run():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    await session.call_tool("get_state", {})

        asyncio.run(run())
        self.assertFalse(os.path.exists(self.db),
                         "recording must be opt-in")


if __name__ == "__main__":
    unittest.main()
