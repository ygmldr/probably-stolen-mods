"""Offline lint for the first-week Skill (module E, Nightly Batch A).

The Skill is prompt material, so a typo in a tool name would silently teach the
model a tool that does not exist. This test parses the Skill as text and checks
every ``tool()`` reference against the *real* MCP server registry, without
importing the stdio server (no ``mcp`` package, no subprocess, no network).

It also pins the protocol vocabulary the Skill must teach: ``legal_actions`` vs
``candidate_actions``, the operation two-axis status, "never retry an uncertain
operation with a new action_id", and externally verifiable rent evidence.

Registry source: ``agent/mcp_server.py`` parsed with :mod:`ast`; only
``@tool()``-decorated top-level/nested functions count.

D+C landed (116 tools / 106 recording): ``get_operation`` / ``wait_operation``
are registered and covered by ``tool_policy.json``, so there is no pending
exception set anymore. The final frozen response fields are documented in the
Skill's "schema 已核对" section and pinned by the assertions below.
"""

import ast
import json
import os
import re
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MOD_DIR = os.path.dirname(AGENT_DIR)
sys.path.insert(0, AGENT_DIR)

MCP_SERVER = os.path.join(AGENT_DIR, "mcp_server.py")
POLICY_PATH = os.path.join(AGENT_DIR, "policy", "tool_policy.json")
SKILL_PATH = os.path.join(MOD_DIR, "skills", "pawnmind-first-week", "SKILL.md")

#: Tool calls the Skill must teach for the first-week loop.
#: (D landed: get_operation/wait_operation are registered, 116 tools / 106
#: recording, so there is no pending exception set anymore.)
REQUIRED_SKILL_TOOLS = frozenset({
    "ping",
    "get_state",
    "get_run_summary",
    "get_legal_actions",
    "list_goods",
    "list_inventory",
    "confirm_trade",
    "choose_dialogue",
    "advance_dialogue",
    "dismiss_current_client",
    "get_pending_prompt",
    "answer_prompt",
    "open_shop",
    "call_next_customer",
    "end_day",
    "sleep",
    "get_day_report",
    "get_calendar",
    "wait_for_events",
    "get_new_game_options",
    "open_new_game_menu",
    "select_new_game_option",
    "new_game_continue",
    "confirm_new_game",
    "get_recording_save_status",
    "get_operation",
    "wait_operation",
})

#: Markdown section titles the handoff requires in the Skill.
REQUIRED_SECTIONS = (
    "启动",
    "状态观察",
    "普通交易",
    "对话",
    "关店",
    "睡觉",
    "租金",
    "错误分类",
    "停止条件",
)

TOOL_CALL_RE = re.compile(r"`([a-z][a-z0-9_]*)\(\)`")


def registered_tools():
    """Parse ``mcp_server.py`` with ast and return the @tool() names."""
    with open(MCP_SERVER, encoding="utf-8") as handle:
        tree = ast.parse(handle.read())
    names = set()
    for node in ast.walk(tree):
        if not isinstance(node, ast.FunctionDef):
            continue
        for decorator in node.decorator_list:
            target = decorator.func if isinstance(decorator, ast.Call) else decorator
            if isinstance(target, ast.Name) and target.id == "tool":
                names.add(node.name)
    return names


def policy_tools():
    with open(POLICY_PATH, encoding="utf-8") as handle:
        policy = json.load(handle)
    return set(policy.get("tools", {}))


def read_skill():
    with open(SKILL_PATH, encoding="utf-8") as handle:
        return handle.read()


def skill_tool_references(text):
    return set(TOOL_CALL_RE.findall(text))


class SkillToolReferenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.registered = registered_tools()
        cls.policy = policy_tools()
        cls.text = read_skill()
        cls.references = skill_tool_references(cls.text)
        cls.known = cls.registered

    def test_server_registry_is_not_empty(self):
        self.assertIn("get_state", self.registered)
        self.assertGreater(len(self.registered), 100)

    def test_skill_references_at_least_one_tool(self):
        self.assertTrue(self.references,
                        "SKILL.md must reference real tools as `tool()`")

    def test_every_referenced_tool_exists_in_the_mcp_registry(self):
        missing = sorted(self.references - self.known)
        self.assertEqual([], missing,
                         "SKILL.md references tools that do not exist in the"
                         " MCP registry: %r" % (missing,))

    def test_every_referenced_tool_is_covered_by_the_policy(self):
        missing = sorted(self.references - self.policy)
        self.assertEqual([], missing,
                         "SKILL.md references tools absent from"
                         " tool_policy.json: %r" % (missing,))

    def test_operation_tools_are_registered(self):
        for name in ("get_operation", "wait_operation"):
            self.assertIn(name, self.registered,
                          "%s must be registered (D landed)" % name)
            self.assertIn(name, self.policy,
                          "%s must have a tool_policy entry" % name)

    def test_write_response_fields_are_documented(self):
        for token in ("operation_id", "execution_status", "delivery_status",
                      "dispatched", "redelivered", "undelivered",
                      "process_restarted"):
            self.assertIn(token, self.text,
                          "SKILL.md must document the final write/operation"
                          " response field %r" % token)

    def test_trade_evidence_fields_are_documented(self):
        for token in ("business_complete", "client_unchanged",
                      "items_transferred", "cash_before", "cash_after",
                      "cash_changed", "staged_item_id", "client_intent",
                      "outcome_unverified"):
            self.assertIn(token, self.text,
                          "SKILL.md must document the frozen confirm_trade"
                          " evidence field %r" % token)

    def test_end_day_and_sleep_semantics_are_documented(self):
        for token in ("shop_closed_after", "shop_still_open", "day_changed",
                      "save_evidence", "save_completed", "save_unknown",
                      "last_save_time"):
            self.assertIn(token, self.text,
                          "SKILL.md must document the frozen end_day/sleep"
                          " field %r" % token)

    def test_schema_is_marked_as_verified_not_pending(self):
        self.assertIn("schema 已核对", self.text,
                      "the D/C schema is frozen; the old '待核对' section must"
                      " become a verified summary")
        self.assertNotIn("schema 依赖待核对", self.text,
                         "stale schema-dependency section must be removed")

    def test_required_first_week_tools_are_taught(self):
        missing = sorted(REQUIRED_SKILL_TOOLS - self.references)
        self.assertEqual([], missing,
                         "SKILL.md does not teach these first-week tools: %r"
                         % (missing,))

    def test_required_sections_exist(self):
        for title in REQUIRED_SECTIONS:
            self.assertIn(title, self.text,
                          "SKILL.md is missing the %r section" % (title,))

    def test_legal_and_candidate_actions_are_distinguished(self):
        self.assertIn("legal_actions", self.text)
        self.assertIn("candidate_actions", self.text)
        self.assertIn("native_guard", self.text)
        self.assertIn('availability="unknown"', self.text,
                      "candidate_actions availability must be spelled out")

    def test_pending_or_uncertain_requires_operation_query_not_blind_retry(self):
        self.assertIn("get_operation()", self.text)
        self.assertIn("wait_operation()", self.text)
        self.assertIn("action_id", self.text)
        self.assertIn("盲重试", self.text)

    def test_rent_needs_external_settlement_evidence(self):
        self.assertIn("last_rent_payment", self.text)
        self.assertIn("模型自述", self.text,
                      "the Skill must say model self-report is not rent"
                      " evidence")

    def test_skill_does_not_teach_the_removed_can_report_field(self):
        # `client.can_report` was removed by the B contract; the pure fields
        # are `reported` / `is_wanted`.
        self.assertNotIn("can_report", self.text)
        self.assertIn("is_wanted", self.text)

    def test_skill_is_marked_offline_draft(self):
        self.assertIn("OFFLINE_DRAFT", self.text)
        self.assertIn("NOT_RUN", self.text)


if __name__ == "__main__":
    unittest.main()
