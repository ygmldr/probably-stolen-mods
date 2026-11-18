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
AGENT_DIR = os.path.join(ROOT, "agent")
MCP_SERVER = os.path.join(AGENT_DIR, "mcp_server.py")
sys.path.insert(0, AGENT_DIR)

from fake_game import FakeGame  # noqa: E402
import trace_recorder  # noqa: E402


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

    def test_unwritable_trace_recording_path_fails_closed(self):
        workdir = tempfile.TemporaryDirectory()
        self.addCleanup(workdir.cleanup)
        blocker = os.path.join(workdir.name, "not-a-directory")
        with open(blocker, "w", encoding="utf-8") as handle:
            handle.write("x")
        bad_path = os.path.join(blocker, "recording.jsonl")

        completed = subprocess.run(
            [sys.executable, "-m", "agent.mcp_server",
             "--config", self.config_path,
             "--trace-recording", bad_path],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=60,
        )
        stderr = completed.stderr.decode("utf-8", "replace")
        self.assertNotEqual(0, completed.returncode,
                            "an unrecordable trace path must fail closed;"
                            " stderr=%s" % stderr)
        self.assertIn("trace recording", stderr)
        self.assertIn(bad_path, stderr)
        self.assertIn("refusing to run", stderr)
        self.assertFalse(os.path.exists(bad_path))

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


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class TraceRecordingEntrypointTests(unittest.TestCase):
    """R5/C2: ``--trace-recording`` records real MCP calls from the host layer."""

    def setUp(self):
        self.fake = FakeGame(recording_mode="armed", recording_slot=17)
        self.fake.start()
        self.addCleanup(self.fake.stop)
        handle, self.config_path = tempfile.mkstemp()
        os.close(handle)
        self.fake.write_config(self.config_path)
        self.addCleanup(
            lambda: os.path.exists(self.config_path)
            and os.unlink(self.config_path))
        self._tmpdir = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmpdir.cleanup)
        self.trace_path = os.path.join(self._tmpdir.name, "mcp-trace.jsonl")

    def _call_get_state(self, mode="package"):
        if mode == "package":
            args = [sys.executable, "-m", "agent.mcp_server",
                    "--config", self.config_path,
                    "--trace-recording", self.trace_path]
        else:
            args = [sys.executable, MCP_SERVER,
                    "--config", self.config_path,
                    "--trace-recording", self.trace_path]
        params = StdioServerParameters(command=args[0], args=args[1:],
                                       cwd=ROOT)

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    state = await session.call_tool("get_state", {})
                    return json.loads(state.content[0].text)

        return asyncio.run(scenario())

    def test_flag_records_jsonl_intent_and_result_for_a_tool_call(self):
        payload = self._call_get_state()
        self.assertFalse(payload["in_game"])

        self.assertTrue(os.path.exists(self.trace_path),
                        "the recording path passed on the command line was"
                        " not honored")
        events, warnings = trace_recorder.load_events(self.trace_path)
        self.assertEqual([], warnings)
        self.assertEqual(2, len(events),
                         "one MCP tool call must produce exactly one intent"
                         " and one result event: %r" % (events,))
        intent, result = events
        self.assertEqual("intent", intent["phase"])
        self.assertEqual("result", result["phase"])
        self.assertEqual(intent["call_id"], result["call_id"])
        self.assertEqual("get_state", intent["op"])
        # run context is wired from the MCP entrypoint
        self.assertEqual("mcp", intent["actor"])
        self.assertEqual("full", intent["tool_surface"])
        self.assertEqual("auto", intent["host_version"])
        self.assertIsNotNone(intent["policy_sha256"])
        # the result is the exact response the bridge returned
        self.assertEqual("received", result["response_status"])
        self.assertEqual("dispatched", result["dispatch_status"])
        self.assertFalse(result["result"]["in_game"])
        self.assertIsInstance(result["duration_ms"], int)
        # the bridge bearer token never reaches the recording
        with open(self.trace_path, "r", encoding="utf-8") as handle:
            raw = handle.read()
        self.assertNotIn(self.fake.token, raw)

    def test_script_form_entrypoint_records_as_well(self):
        payload = self._call_get_state(mode="script")
        self.assertFalse(payload["in_game"])
        events, warnings = trace_recorder.load_events(self.trace_path)
        self.assertEqual([], warnings)
        self.assertEqual(["intent", "result"],
                         [event["phase"] for event in events])
        self.assertEqual("get_state", events[0]["op"])

    def test_without_the_flag_nothing_is_recorded(self):
        params = StdioServerParameters(
            command=sys.executable,
            args=["-m", "agent.mcp_server", "--config", self.config_path],
            cwd=ROOT,
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    state = await session.call_tool("get_state", {})
                    return json.loads(state.content[0].text)

        payload = asyncio.run(scenario())
        self.assertFalse(payload["in_game"])
        self.assertFalse(os.path.exists(self.trace_path))
        self.assertEqual([], os.listdir(self._tmpdir.name),
                         "no recording artifact may appear without the flag")

    def test_trace_wraps_after_the_memory_recorder(self):
        """The trace layer must sit outside memory: it records MCP calls only."""
        params = StdioServerParameters(
            command=sys.executable,
            args=[
                "-m", "agent.mcp_server", "--config", self.config_path,
                "--memory", "on",
                "--memory-db", os.path.join(self._tmpdir.name, "memory.db"),
                "--memories-db", os.path.join(self._tmpdir.name,
                                              "memories.db"),
                "--trace-recording", self.trace_path,
            ],
            cwd=ROOT,
        )

        async def scenario():
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    await session.call_tool("get_state", {})

        asyncio.run(scenario())

        self.assertTrue(os.path.exists(
            os.path.join(self._tmpdir.name, "memory.db")))
        events, warnings = trace_recorder.load_events(self.trace_path)
        self.assertEqual([], warnings)
        ops = [event["op"] for event in events
               if event["phase"] == "intent"]
        self.assertEqual(["get_state"], ops,
                         "the trace must see MCP tool calls, not the memory"
                         " recorder's internal reads")
        self.assertEqual(
            len([e for e in events if e["phase"] == "intent"]),
            len([e for e in events if e["phase"] == "result"]))
        self.assertEqual("mcp", events[0]["actor"])

    def test_unwritable_path_fails_closed_without_serving(self):
        workdir = tempfile.TemporaryDirectory()
        self.addCleanup(workdir.cleanup)
        blocker = os.path.join(workdir.name, "not-a-directory")
        with open(blocker, "w", encoding="utf-8") as handle:
            handle.write("x")
        bad_path = os.path.join(blocker, "mcp-trace.jsonl")

        completed = subprocess.run(
            [sys.executable, "-m", "agent.mcp_server",
             "--config", self.config_path,
             "--trace-recording", bad_path],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=60,
        )
        stderr = completed.stderr.decode("utf-8", "replace")
        self.assertNotEqual(0, completed.returncode, stderr)
        self.assertIn("trace recording", stderr)
        self.assertIn(bad_path, stderr)
        self.assertIn("refusing to run", stderr)
        self.assertFalse(os.path.exists(bad_path))


if __name__ == "__main__":
    unittest.main()
