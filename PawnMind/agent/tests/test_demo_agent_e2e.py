"""S6B end-to-end: scripted policy -> real MCP stdio server -> fake game bridge.

Boundary: offline only. A fake game bridge listens on a random 127.0.0.1 port,
`mcp_server.py` is spawned as a real stdio MCP server against that fake, and the
scripted policy talks to it through a real `mcp` ClientSession. No game process,
no real bridge, no save file and no external network are involved.

The scripted policy is **not** an LLM. Passing this test proves the transport,
tool surface, action discipline and first-week bookkeeping work end to end; it
does not prove anything about a real model playing the real game (S8).
"""

import asyncio
import json
import os
import sys
import tempfile
import unittest
import urllib.request

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError:  # the mcp package is optional for the stdlib-only client tests
    ClientSession = None
    StdioServerParameters = None
    stdio_client = None

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MCP_SERVER = os.path.join(AGENT_DIR, "mcp_server.py")
sys.path.insert(0, AGENT_DIR)

import demo_agent  # noqa: E402  (Red: the harness does not exist yet)
from fake_game import FakeGame  # noqa: E402


class _Content:
    def __init__(self, text):
        self.text = text


class _Result:
    def __init__(self, payload, is_error=False):
        self.content = [_Content(payload if isinstance(payload, str) else json.dumps(payload))]
        self.is_error = is_error


class _StubSession:
    """MCP session double used only by the stop-condition tests."""

    def __init__(self, payloads):
        self.payloads = {
            name: list(queue) if isinstance(queue, list) else [queue]
            for name, queue in payloads.items()
        }
        self.calls = []

    async def call_tool(self, name, arguments):
        self.calls.append((name, dict(arguments)))
        queue = self.payloads.get(name)
        if not queue:
            return _Result({"ok": True, "rev": 1})
        return _Result(queue.pop(0))


class _Host:
    """Test host: simulates the human returning to the main menu (S8 runbook)."""

    def __init__(self, fake):
        self.fake = fake

    def return_to_menu(self):
        self.fake.return_to_menu()


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class DemoAgentEndToEndTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeGame(recording_mode="armed", recording_slot=17)
        self.fake.start()
        self.addCleanup(self.fake.stop)
        handle, self.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=%d\ntoken=%s\n" % (self.fake.port, self.fake.token))
        self.addCleanup(lambda: os.path.exists(self.config_path) and os.unlink(self.config_path))
        self.params = StdioServerParameters(
            command=sys.executable,
            args=[MCP_SERVER, "--config", self.config_path],
        )

    def _run(self, scenario):
        async def runner():
            async with stdio_client(self.params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    return await scenario(session)

        return asyncio.run(runner())

    def test_scripted_agent_plays_the_first_week_over_real_mcp_stdio(self):
        async def scenario(session):
            tools = {tool.name for tool in (await session.list_tools()).tools}
            agent = demo_agent.ScriptedShopkeeper(session, host=_Host(self.fake))
            report = await agent.run()
            return tools, report

        tools, report = self._run(scenario)

        # The recorded tool surface must still exist (the demo may only use it).
        for name in demo_agent.REQUIRED_TOOLS:
            self.assertIn(name, tools, "missing MCP tool: " + name)

        self.assertEqual("completed", report["stop_reason"])
        self.assertEqual("scripted", report["policy"])
        self.assertIn("not an LLM", report["note"])

        used = [step["tool"] for step in report["steps"]]
        self.assertTrue(used)
        for name in used:
            self.assertIn(name, demo_agent.RECORDING_TOOLS, "tool outside the recording allowlist: " + name)
        for forbidden in ("act", "press", "new_game", "load_game", "save_game", "health"):
            self.assertNotIn(forbidden, used, "the demo must not use " + forbidden)

        # Negative branches really happened and were refused, not skipped.
        errors = [step.get("error") for step in report["steps"] if step.get("error")]
        for expected in ("menu_closed", "page_out_of_range", "option_locked",
                         "difficulty_start_forbidden", "ambiguous_target",
                         "cannot_afford", "choice_disabled", "no_dialogue"):
            self.assertIn(expected, errors, "the demo never exercised " + expected)

        summary = report["summary"]
        self.assertEqual(25, summary["start_cash"])
        self.assertEqual(5, summary["cash"])
        self.assertEqual(9, summary["day"])
        self.assertEqual(6, summary["rent_days_left"])
        self.assertEqual(100, summary["rent_paid_total"])
        self.assertEqual(1, summary["rent_weeks_paid"])
        self.assertEqual(8, summary["days_traded"])
        self.assertEqual(96, summary["buy_total"])
        self.assertEqual(176, summary["sell_total"])
        self.assertEqual(
            summary["start_cash"] - summary["buy_total"]
            + summary["sell_total"] - summary["rent_paid_total"],
            summary["cash"],
            "cash must reconcile against every recorded trade and the rent",
        )

        receipt = summary["rent_receipt"]
        self.assertEqual(100, receipt["amount"])
        self.assertEqual(105, receipt["cash_before"])
        self.assertEqual(5, receipt["cash_after"])
        self.assertEqual(-100, receipt["cash_delta"])
        self.assertEqual(0, receipt["rent_days_left_before"])
        self.assertEqual(7, receipt["rent_days_left_after"])
        self.assertEqual("dialogue_choice", receipt["source"])

        self.assertEqual(17, summary["bound_slot"])
        self.assertEqual("scavenger", summary["start_scenario_id"])
        self.assertEqual("apprentice", summary["start_type"])
        self.assertEqual(17, self.fake.snapshot()["save_slot"])
        self.assertEqual(100, self.fake.snapshot()["rent_paid_total"])
        self.assertIsNone(summary["rent_last_payment_after_load"],
                          "a controlled load resets the live receipt tracker")
        self.assertEqual(100, summary["rent_paid_total_after_load"])
        self.assertEqual(9, summary["day_after_load"])
        self.assertEqual(5, summary["cash_after_load"])
        self.assertEqual(summary["binding_before_save"], summary["binding_after_load"])

        # The fake only ever saw authenticated, allowlisted, single-step writes.
        actions = [row for row in self.fake.requests if row["path"] == "/action"]
        self.assertTrue(actions)
        seen_ids = set()
        for row in actions:
            self.assertEqual("Bearer " + self.fake.token, row["authorization"])
            body = row["body"]
            self.assertIn(body["op"], demo_agent.ALLOWED_OPS, "unexpected op: " + str(body["op"]))
            self.assertIn("action_id", body)
            if body["op"] in demo_agent.READ_ONLY_OPS:
                self.assertNotIn("rev", body, body["op"] + " is a read and must not carry rev")
                continue
            self.assertIsInstance(body["rev"], int, body["op"] + " lost rev")
            self.assertNotIn(body["action_id"], seen_ids, "action_id was reused")
            seen_ids.add(body["action_id"])

        dumped = json.dumps(report, ensure_ascii=False)
        self.assertNotIn(self.fake.token, dumped, "the report leaked the bearer token")

        health = json.loads(urllib.request.urlopen(self.fake.url + "/health", timeout=5).read())
        self.assertTrue(health["fake"])
        self.assertEqual("fake_game", health["mode"])

    def test_report_never_contains_the_bearer_token(self):
        async def scenario(session):
            agent = demo_agent.ScriptedShopkeeper(session, host=_Host(self.fake))
            return await agent.run()

        report = self._run(scenario)
        self.assertNotIn(self.fake.token, json.dumps(report, ensure_ascii=False))


class DemoAgentStopConditionTests(unittest.TestCase):
    """The policy stops with a stable reason instead of retrying or faking success."""

    def _run_until_abort(self, payloads):
        async def scenario():
            session = _StubSession(payloads)
            agent = demo_agent.ScriptedShopkeeper(session)
            try:
                await agent.run()
            except demo_agent.DemoAbort as error:
                return error, session
            raise AssertionError("the demo did not abort")

        return asyncio.run(scenario())

    def _base_payloads(self):
        return {
            "ping": {"ok": True, "pong": True, "rev": 3},
            "get_state": {"in_game": False, "rev": 3},
            "get_new_game_options": {
                "ok": True, "phase": "closed", "page": 0, "page_count": 0,
                "options": [], "selected_options": [], "has_selection": False,
                "continue": {}, "prompt_pending": False, "rev": 3,
            },
            "new_game_continue": {"ok": False, "error": "menu_closed", "rev": 3},
        }

    def test_stale_rev_stops_instead_of_retrying(self):
        payloads = self._base_payloads()
        payloads["open_new_game_menu"] = {"ok": False, "error": "stale_rev", "rev": 4}
        error, session = self._run_until_abort(payloads)
        self.assertEqual("stale_rev", error.reason)
        self.assertEqual(
            ["ping", "get_state", "get_new_game_options", "new_game_continue",
             "open_new_game_menu"],
            [name for name, _ in session.calls],
            "a stale rev must stop before any retry",
        )

    def test_unknown_outcome_stops_without_a_second_write(self):
        payloads = self._base_payloads()
        payloads["open_new_game_menu"] = {"ok": False, "error": "outcome_unknown",
                                           "rev": 4, "unknown": True}
        error, session = self._run_until_abort(payloads)
        self.assertEqual("outcome_unknown", error.reason)
        self.assertEqual(1, len([1 for name, _ in session.calls if name == "open_new_game_menu"]))

    def test_unexpected_error_stops_with_its_stable_code(self):
        payloads = self._base_payloads()
        payloads["open_new_game_menu"] = {"ok": False, "error": "recording_unconfigured",
                                           "rev": 3}
        error, _ = self._run_until_abort(payloads)
        self.assertEqual("unexpected_error", error.reason)
        self.assertEqual("recording_unconfigured", error.detail)

    def test_tools_outside_the_allowlist_are_refused_before_any_call(self):
        async def scenario():
            session = _StubSession({})
            box = demo_agent.ToolBox(session)
            with self.assertRaises(demo_agent.DemoAbort) as caught:
                await box.call("press", control_id="start")
            return caught.exception, session.calls

        error, calls = asyncio.run(scenario())
        self.assertEqual("tool_not_allowlisted", error.reason)
        self.assertEqual([], calls)

    def test_allowlist_keeps_the_dangerous_surface_out(self):
        for forbidden in ("act", "press", "new_game", "load_game", "save_game",
                          "debug_unlock_expedition", "wine_add_water"):
            self.assertNotIn(forbidden, demo_agent.ALLOWED_OPS)
        for required in demo_agent.REQUIRED_TOOLS:
            self.assertIn(required, demo_agent.RECORDING_TOOLS)


if __name__ == "__main__":
    unittest.main()
