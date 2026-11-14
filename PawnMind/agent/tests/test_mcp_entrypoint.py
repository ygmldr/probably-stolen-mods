"""S6B: the host-facing MCP entrypoint documented in the setup guide.

`docs/pawnmind-ai-host-setup.zh-CN.md` tells a host (OpenCode et al.) to spawn

    <PawnMind>/.venv/bin/python -m agent.mcp_server --config <bridge.conf>
    cwd = <PawnMind>

These tests pin exactly that invocation so the template cannot rot back into a
command that fails to import `pawnmind_client`.
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

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "agent"))

from fake_game import FakeGame  # noqa: E402


class ModuleEntrypointTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeGame(recording_mode="armed", recording_slot=17)
        self.fake.start()
        self.addCleanup(self.fake.stop)
        handle, self.config_path = tempfile.mkstemp()
        os.close(handle)
        self.fake.write_config(self.config_path)
        self.addCleanup(lambda: os.path.exists(self.config_path) and os.unlink(self.config_path))

    def test_probe_runs_from_the_package_root_and_reaches_the_bridge(self):
        completed = subprocess.run(
            [sys.executable, "-m", "agent.mcp_server",
             "--config", self.config_path, "--probe"],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=60,
        )
        stdout = completed.stdout.decode("utf-8", "replace")
        stderr = completed.stderr.decode("utf-8", "replace")
        self.assertEqual(0, completed.returncode,
                         "probe failed:\nstdout=%s\nstderr=%s" % (stdout, stderr))
        self.assertIn("health:", stdout)
        self.assertIn("fake_game", stdout)
        self.assertIn("pong", stdout)
        self.assertNotIn(self.fake.token, stdout, "the probe leaked the bearer token")
        self.assertNotIn(self.fake.token, stderr, "the probe leaked the bearer token")

    @unittest.skipIf(ClientSession is None, "mcp package not installed")
    def test_module_entrypoint_serves_mcp_tools_over_stdio(self):
        params = StdioServerParameters(
            command=sys.executable,
            args=["-m", "agent.mcp_server", "--config", self.config_path],
            cwd=ROOT,
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = {tool.name for tool in (await session.list_tools()).tools}
                    ping = json.loads((await session.call_tool("ping", {})).content[0].text)
                    state = json.loads((await session.call_tool("get_state", {})).content[0].text)
                    return tools, ping, state

        tools, ping, state = asyncio.run(scenario())
        self.assertIn("get_new_game_options", tools)
        self.assertIn("get_recording_save_status", tools)
        self.assertTrue(ping["ok"] and ping["pong"])
        self.assertFalse(state["in_game"])
        self.assertIsInstance(state["rev"], int)


if __name__ == "__main__":
    unittest.main()
