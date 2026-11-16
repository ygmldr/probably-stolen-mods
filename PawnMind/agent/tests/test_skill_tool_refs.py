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

Two D-contract tools (``get_operation`` / ``wait_operation``) are allowed to be
referenced while the Lead is still landing them; once they are registered the
exception set is simply unused. See the Skill's "schema 依赖待核对" section.
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

#: Frozen 2026-11-17 D contract tools that may be referenced before the Lead's
#: parallel change registers them. Keep this set tiny and delete the entries
#: once the tools exist in ``mcp_server.py``.
PENDING_CONTRACT_TOOLS = frozenset({"get_operation", "wait_operation"})

#: Tool calls the Skill must teach for the first-week loop.
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
        cls.known = cls.registered | PENDING_CONTRACT_TOOLS

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
        # The pending D tools are exempt until the Lead lands them.
        missing = sorted(self.references - self.policy - PENDING_CONTRACT_TOOLS)
        self.assertEqual([], missing,
                         "SKILL.md references tools absent from"
                         " tool_policy.json: %r" % (missing,))

    def test_pending_contract_tools_are_documented(self):
        # This must hold both before and after the Lead registers the D tools:
        # the Skill keeps referencing them, and the schema-dependency section
        # stays the place that records their registration state.
        referenced_pending = self.references & PENDING_CONTRACT_TOOLS
        if not referenced_pending:
            return
        self.assertIn("schema 依赖待核对", self.text,
                      "the D-contract tools must be listed in the"
                      " 'schema 依赖待核对' section")

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
