"""P2 retrieval: the 30 annotated scenarios, state gating, eval metrics, MCP wiring.

The fixture is a throwaway pair of databases seeded from
``agent/memory/seeds/verified_rules.json``.  It is synthetic test data, never a
recording.  Nothing here opens the live ``PawnMind/play/`` databases, contacts
the bridge or touches a save.
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
AGENT_DIR = os.path.join(ROOT, "agent")
if AGENT_DIR not in sys.path:
    sys.path.insert(0, AGENT_DIR)

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError:  # pragma: no cover - the mcp package is optional
    ClientSession = None
    StdioServerParameters = None
    stdio_client = None

from agent.memory.classification import (  # noqa: E402
    MCP_STATE_READS,
    READ_ONLY_ACTION_OPS,
    READ_ONLY_OPS,
)
from agent.memory.memories import MemoryStore  # noqa: E402
from agent.memory.retrieval_eval import (  # noqa: E402
    DEFAULT_CASES_PATH,
    evaluate,
    load_cases,
)
from agent.memory.seed import seed_database  # noqa: E402
from agent.memory.store import EventStore  # noqa: E402

MCP_SERVER = os.path.join(AGENT_DIR, "mcp_server.py")
VERSION = "VERSION 100-Rev13"
GAME_ID = "probably_stolen"
TOKEN = "0123456789abcdef0123456789abcdef"
MEMORY_TOOL_NAMES = frozenset({
    "memory_bootstrap",
    "memory_retrieve",
    "memory_get",
    "memory_status",
})


class SeededRetrievalCaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.events_path = os.path.join(cls.tmp.name, "events.db")
        cls.memories_path = os.path.join(cls.tmp.name, "memories.db")
        cls.seed_summary = seed_database(cls.memories_path, cls.events_path)
        cls.store = MemoryStore(cls.memories_path, events_db=cls.events_path)
        cls.data = load_cases()

    @classmethod
    def tearDownClass(cls):
        try:
            cls.store.close()
        finally:
            cls.tmp.cleanup()

    def _run_case(self, case):
        scope = self.data["scope_presets"][case["scope"]]
        return self.store.retrieve(
            case["query"], limit=5, scope=scope,
            state=case.get("state"),
            token_budget=case.get("token_budget"))

    def test_seed_has_the_documented_evaluation_fixtures(self):
        self.assertEqual(30, len(self.data["cases"]))
        self.assertEqual(15, self.seed_summary["memories_added"])
        self.assertEqual(30, len({case["case_id"]
                                  for case in self.data["cases"]}))

    def test_all_thirty_annotated_scenarios(self):
        for case in self.data["cases"]:
            with self.subTest(case_id=case["case_id"]):
                result = self._run_case(case)
                got = [item["memory_id"] for item in result["items"]]
                self.assertEqual(sorted(case["expected"]), sorted(got),
                                 "case %s: expected %r, got %r"
                                 % (case["case_id"], case["expected"], got))
                for item in result["items"]:
                    self.assertIn(item["recall_reason"],
                                  {"fulltext", "keyword", "entity"},
                                  "unexpected recall_reason")
                    self.assertGreaterEqual(item["evidence_count"], 1)
                    self.assertIn("contradict_count", item)
                    self.assertIn("status", item)
                    self.assertIn("text", item)
                    self.assertIn("kind", item)

    def test_state_conditions_are_hard_filters(self):
        both = [case for case in self.data["cases"]
                if case["case_id"] == "zh_inventory_both_rules"][0]
        without_state = [case for case in self.data["cases"]
                         if case["case_id"] == "zh_inventory_without_state"][0]
        low_state = [case for case in self.data["cases"]
                     if case["case_id"] == "zh_inventory_condition_low"][0]

        with_state = {item["memory_id"]
                      for item in self._run_case(both)["items"]}
        self.assertIn("mem_seed_inventory_condition", with_state)
        for case in (without_state, low_state):
            ids = {item["memory_id"]
                   for item in self._run_case(case)["items"]}
            self.assertNotIn("mem_seed_inventory_condition", ids,
                             "a rule whose state condition is unknown or false"
                             " must not be returned")

    def test_budget_keeps_the_highest_priority_warning(self):
        case = [case for case in self.data["cases"]
                if case["case_id"] == "budget_keeps_top_warning_only"][0]
        result = self._run_case(case)
        self.assertEqual(["mem_seed_inv_capacity"],
                         [item["memory_id"] for item in result["items"]])
        self.assertTrue(result["truncated"])
        self.assertEqual(1, result["dropped"])
        self.assertLessEqual(result["token_estimate"], 30)

    def test_hidden_statuses_and_foreign_scopes_never_leak(self):
        hidden = {
            "mem_seed_conflict_high", "mem_seed_conflict_low",
            "mem_seed_deprecated_clear_shelf", "mem_seed_hypothesis",
            "mem_seed_superseded_old",
        }
        for case in self.data["cases"]:
            with self.subTest(case_id=case["case_id"]):
                ids = {item["memory_id"]
                       for item in self._run_case(case)["items"]}
                self.assertEqual(set(), ids & hidden)
                if case["scope"] != "save40_runA":
                    self.assertNotIn("mem_seed_landlord", ids)
                    self.assertNotIn("mem_seed_run_private", ids)

    def test_retrieval_is_replayable_on_the_same_database_snapshot(self):
        case = [case for case in self.data["cases"]
                if case["case_id"] == "zh_inventory_both_rules"][0]
        scope = self.data["scope_presets"][case["scope"]]
        state = case["state"]
        first = self.store.search(case["query"], limit=5, scope=scope,
                                  state=state)
        second = self.store.search(case["query"], limit=5, scope=scope,
                                   state=state)
        self.assertEqual(
            ["mem_seed_inv_capacity", "mem_seed_inventory_condition"],
            [item["memory_id"] for item in first])
        self.assertEqual([item["memory_id"] for item in first],
                         [item["memory_id"] for item in second])


class StateConditionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.events_path = os.path.join(self.tmp.name, "events.db")
        self.memories_path = os.path.join(self.tmp.name, "memories.db")
        self.events = EventStore(self.events_path)
        self.timeline = self.events.start_session(
            agent_session_id="sess-state", game_version=VERSION,
            game_mvid="mvid", instance_id="local-01").timeline_id
        self.events.attach_identity(self.timeline, run_id="run-state",
                                    slot=40, run_number=1)
        self.store = MemoryStore(self.memories_path,
                                 events_db=self.events_path)
        event = self.events.append_event(
            self.timeline, event_type="observation", tool_name="get_state",
            result_status="success", result={"ok": True},
            capture_quality="complete")
        self.store.add_candidate(
            kind="warning", text="state gated rule",
            evidence=[event], game_version=VERSION, save_id="slot:40",
            run_id="run-state", timeline_id=self.timeline,
            memory_id="mem_state",
            scope_extra={"conditions": {"state": {
                "in_game": True,
                "rent.days_left": {"max": 1},
                "inventory_occupancy_min": 0.9,
            }}})
        verified = self.store.verify("mem_state", actor="user:test")
        self.assertTrue(verified["ok"], verified)

    def tearDown(self):
        try:
            self.store.close()
            self.events.close()
        finally:
            self.tmp.cleanup()

    def scope(self):
        return {"game_id": GAME_ID, "game_version": VERSION,
                "save_id": "slot:40", "run_id": "run-state",
                "timeline_id": self.timeline}

    def ids(self, state):
        return [item["memory_id"] for item in self.store.search(
            "state gated rule", limit=5, scope=self.scope(), state=state)]

    def test_matching_nested_and_flat_state_returns_the_rule(self):
        matching = {"in_game": True, "rent": {"days_left": 1},
                    "inventory_occupancy": 0.95}
        self.assertEqual(["mem_state"], self.ids(matching))
        flat = {"in_game": True, "rent_days_left": 1,
                "inventory_occupancy": 0.95}
        self.assertEqual(["mem_state"], self.ids(flat))

    def test_missing_state_never_returns_a_conditional_rule(self):
        self.assertEqual([], self.ids(None))
        self.assertEqual([], self.ids({"in_game": True,
                                       "inventory_occupancy": 0.95}))

    def test_false_or_out_of_range_state_filters_the_rule(self):
        self.assertEqual([], self.ids(
            {"in_game": False, "rent": {"days_left": 1},
             "inventory_occupancy": 0.95}))
        self.assertEqual([], self.ids(
            {"in_game": True, "rent": {"days_left": 2},
             "inventory_occupancy": 0.95}))
        self.assertEqual([], self.ids(
            {"in_game": True, "rent": {"days_left": 1},
             "inventory_occupancy": 0.3}))

    def test_type_mismatch_fails_closed(self):
        self.assertEqual([], self.ids(
            {"in_game": "yes", "rent": {"days_left": 1},
             "inventory_occupancy": 0.95}))

    def test_state_operators_are_supported_in_the_key_form(self):
        event = self.events.append_event(
            self.timeline, event_type="observation", tool_name="get_state",
            result_status="success", result={"ok": True},
            capture_quality="complete")
        self.store.add_candidate(
            kind="warning", text="rent due rule", evidence=[event],
            game_version=VERSION, save_id="slot:40", run_id="run-state",
            timeline_id=self.timeline, memory_id="mem_rent_state",
            scope_extra={"conditions": {"state": {
                "rent_days_left_max": 1}}})
        self.assertTrue(self.store.verify("mem_rent_state",
                                          actor="user:test")["ok"])

        def hits(days):
            return [item["memory_id"] for item in self.store.search(
                "rent due rule", limit=5, scope=self.scope(),
                state={"rent": {"days_left": days}})]

        self.assertEqual(["mem_rent_state"], hits(1))
        self.assertEqual([], hits(2))


class _StubStore(object):
    def __init__(self, results, kinds=None):
        self.results = results
        self.kinds = kinds or {}
        self.calls = []

    def search(self, query, limit=5, scope=None, state=None,
               token_budget=None):
        self.calls.append((query, limit, scope, state, token_budget))
        return [{"memory_id": memory_id}
                for memory_id in self.results.get(query, [])][:limit]

    def get_memory(self, memory_id):
        return {"memory_id": memory_id, "kind": self.kinds.get(
            memory_id, "fact")}


class EvaluationMetricTests(unittest.TestCase):
    def _cases(self):
        return [
            {"case_id": "gold_two", "query": "q1", "scope": "s",
             "expected": ["m1", "m2"]},
            {"case_id": "empty_ok", "query": "q2", "scope": "s",
             "expected": []},
            {"case_id": "empty_bad", "query": "q3", "scope": "s",
             "expected": []},
        ]

    def _evaluate(self, results, kinds=None):
        store = _StubStore(results, kinds)
        return evaluate(store, self._cases(), k=5,
                        scope_presets={"s": {"game_id": GAME_ID}})

    def test_recall_and_precision_math(self):
        report = self._evaluate({
            "q1": ["m1", "mX"],
            "q2": [],
            "q3": ["mZ"],
        })
        by_id = {case["case_id"]: case for case in report["cases"]}
        self.assertAlmostEqual(0.5, by_id["gold_two"]["recall"])
        self.assertAlmostEqual(0.5, by_id["gold_two"]["precision"])
        self.assertAlmostEqual(1.0, by_id["empty_ok"]["recall"])
        self.assertAlmostEqual(1.0, by_id["empty_ok"]["precision"])
        self.assertAlmostEqual(0.0, by_id["empty_bad"]["precision"])
        self.assertAlmostEqual(0.5, report["summary"]["recall_at_k"])
        self.assertAlmostEqual(0.5, report["summary"]["precision_at_k"])
        self.assertEqual(1, report["summary"]["passed"])
        self.assertEqual(2, report["summary"]["negative_cases"])
        self.assertEqual(1, report["summary"]["negative_passed"])

    def test_warning_miss_is_reported_from_gold_kinds(self):
        report = self._evaluate(
            {"q1": ["m1"], "q2": [], "q3": []},
            kinds={"m1": "fact", "m2": "warning"})
        self.assertEqual(1, report["summary"]["warning_gold"])
        self.assertEqual(1, report["summary"]["warning_missed"])
        self.assertAlmostEqual(1.0, report["summary"]["warning_miss_rate"])

    def test_empty_dataset_is_rejected(self):
        with self.assertRaises(ValueError):
            evaluate(_StubStore({}), [], k=5,
                     scope_presets={"s": {"game_id": GAME_ID}})


class ReadOnlyClassificationTests(unittest.TestCase):
    def test_memory_tools_are_read_only_mcp_tools(self):
        self.assertTrue(MEMORY_TOOL_NAMES <= MCP_STATE_READS)
        self.assertTrue(MEMORY_TOOL_NAMES <= READ_ONLY_OPS)
        self.assertEqual(set(), MEMORY_TOOL_NAMES & READ_ONLY_ACTION_OPS,
                         "memory tools are not C# actions")

    def test_default_cases_path_exists_and_is_marked_test_data(self):
        self.assertTrue(os.path.isfile(DEFAULT_CASES_PATH))
        with open(DEFAULT_CASES_PATH, encoding="utf-8") as handle:
            data = json.load(handle)
        self.assertTrue(data["test_data"])


@unittest.skipIf(ClientSession is None, "mcp package not installed")
class MemoryToolWiringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.memories_path = os.path.join(cls.tmp.name, "memories.db")
        cls.events_path = os.path.join(cls.tmp.name, "events.db")
        seed_database(cls.memories_path, cls.events_path)
        handle, cls.config_path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as config:
            config.write("port=15528\ntoken=%s\n" % TOKEN)

    @classmethod
    def tearDownClass(cls):
        os.unlink(cls.config_path)
        cls.tmp.cleanup()

    def _run(self, calls, with_events=True):
        args = [MCP_SERVER, "--config", self.config_path,
                "--tools", "recording",
                "--memories-db", self.memories_path]
        if with_events:
            args.extend(["--memory-db", self.events_path])
        params = StdioServerParameters(command=sys.executable, args=args)

        async def scenario():
            out = {}
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    names = {tool.name for tool in
                             (await session.list_tools()).tools}
                    out["_tools"] = names
                    for name, arguments in calls:
                        result = await session.call_tool(name, arguments)
                        out[name] = json.loads(result.content[0].text)
            return out

        return asyncio.run(scenario())

    def test_four_read_only_tools_work_without_a_bridge(self):
        scope = {
            "game_id": GAME_ID,
            "game_version": VERSION,
            "save_id": "slot:40",
            "run_id": "run-seed-a",
            "timeline_id": "tl_seed_main",
        }
        out = self._run([
            ("memory_status", {}),
            ("memory_bootstrap", {"limit": 8, "scope": scope}),
            ("memory_retrieve", {
                "query": "库存快满了，还要继续进货吗？", "limit": 5,
                "scope": scope,
                "state": {"inventory_occupancy": 0.95}}),
            ("memory_get", {"memory_id": "mem_seed_cash_rent"}),
        ])

        self.assertTrue(MEMORY_TOOL_NAMES <= out["_tools"])
        for name in MEMORY_TOOL_NAMES:
            self.assertIn(name, out["_tools"])

        status = out["memory_status"]
        self.assertTrue(status["ok"])
        self.assertEqual(3, status["memories"]["schema_version"])
        self.assertEqual(15, status["memories"]["memories"])
        self.assertTrue(status["events"]["available"])
        self.assertEqual(1, status["events"]["schema_version"])

        boot = out["memory_bootstrap"]
        self.assertTrue(boot["ok"])
        self.assertTrue(boot["memories"])
        for item in boot["memories"]:
            self.assertEqual("verified", item["status"])
            for field in ("memory_id", "kind", "text", "confidence", "why",
                          "evidence_count"):
                self.assertIn(field, item)

        retrieved = out["memory_retrieve"]
        self.assertTrue(retrieved["ok"])
        self.assertEqual(["mem_seed_inv_capacity",
                          "mem_seed_inventory_condition"],
                         [item["memory_id"]
                          for item in retrieved["memories"]])
        self.assertEqual(
            {"inventory_occupancy_min": 0.9},
            retrieved["memories"][1]["conditions"]["state"])
        hit = retrieved["memories"][0]
        self.assertIn(hit["recall_reason"],
                      {"fulltext", "keyword", "entity"})
        self.assertGreaterEqual(hit["evidence_count"], 1)
        self.assertIn("contradict_count", hit)

        detail = out["memory_get"]
        self.assertTrue(detail["ok"])
        self.assertEqual("mem_seed_cash_rent",
                         detail["memory"]["memory_id"])
        self.assertTrue(detail["memory"]["evidence"])
        evidence = detail["memory"]["evidence"][0]
        self.assertIn("evidence_role", evidence)
        self.assertIn("added_at", evidence)
        self.assertIn("event", evidence)
        self.assertEqual("intent", evidence["event"]["event_type"])
        self.assertTrue(detail["revisions"])

    def test_memory_error_paths_are_stable(self):
        out = self._run([
            ("memory_get", {"memory_id": "mem_missing"}),
            ("memory_retrieve", {"query": ""}),
            ("memory_bootstrap", {"limit": 0}),
        ])
        self.assertEqual("not_found", out["memory_get"]["error"])
        self.assertEqual("invalid_argument",
                         out["memory_retrieve"]["error"])
        self.assertEqual("query", out["memory_retrieve"]["field"])
        self.assertEqual("invalid_argument", out["memory_bootstrap"]["error"])
        self.assertEqual("limit", out["memory_bootstrap"]["field"])

    def test_memory_status_degrades_when_events_db_is_not_configured(self):
        out = self._run([("memory_status", {})], with_events=False)
        status = out["memory_status"]
        self.assertTrue(status["ok"])
        self.assertFalse(status["events"]["configured"])
        self.assertEqual("not_configured", status["events"]["reason"])


if __name__ == "__main__":
    unittest.main()
