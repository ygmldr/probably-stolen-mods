"""Policy source tests: deny by default, evidence-driven, sets stay consistent."""

import json
import os
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MOD_DIR = os.path.dirname(AGENT_DIR)
sys.path.insert(0, AGENT_DIR)

from tool_policy import (  # noqa: E402
    allowlist, decision_for, live_ready, load_policy, validate,
)
import mcp_server  # noqa: E402


def _inventory():
    with open(os.path.join(MOD_DIR, "docs", "audit", "tools-inventory.json"),
              encoding="utf-8") as handle:
        return json.load(handle)


class ToolPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy = load_policy()
        cls.inv = _inventory()
        cls.full = {t["name"] for t in cls.inv["full"]}
        cls.recording = set(cls.inv["recording"])

    def test_policy_is_structurally_valid(self):
        self.assertEqual([], validate(self.policy))

    def test_every_exported_tool_has_an_entry(self):
        missing = self.full - set(self.policy["tools"])
        self.assertEqual(set(), missing, "a new tool must be registered before use")

    def test_unknown_tool_is_denied(self):
        self.assertEqual("deny", decision_for(self.policy, "definitely_not_a_tool"))
        self.assertEqual([], [n for n in allowlist(self.policy, "play")
                              if n == "definitely_not_a_tool"])

    def test_unknown_mode_exposes_nothing(self):
        self.assertEqual([], allowlist(self.policy, "yolo"))

    def test_reads_are_not_labelled_writes(self):
        for name in ("list_goods", "get_item", "list_inventory", "read_book"):
            self.assertEqual("pure", self.policy["tools"][name]["purity"], name)
        self.assertEqual("write", self.policy["tools"]["confirm_trade"]["purity"])

    def test_get_state_is_blocked_until_d0a(self):
        entry = self.policy["tools"]["get_state"]
        self.assertFalse(entry["modes"]["play"], "still refreshes the bargain UI")
        self.assertEqual("needs_evidence", entry["decision"])
        self.assertEqual([], [n for n in allowlist(self.policy, "play") if n == "get_state"])

    def test_privileged_tools_are_denied(self):
        for name in ("save_game", "new_game", "act", "press"):
            self.assertEqual("deny", decision_for(self.policy, name), name)

    def test_mutating_read_is_denied(self):
        self.assertEqual("deny", decision_for(self.policy, "bargain_preview"))
        self.assertEqual("needs_evidence", decision_for(self.policy, "get_clientele"))

    def test_play_allowlist_is_inside_the_recording_surface(self):
        self.assertTrue(set(allowlist(self.policy, "play")) <= self.recording,
                        "play must never expose a tool the server does not record")

    def test_recording_names_are_known_to_the_policy(self):
        missing = set(mcp_server.RECORDING_TOOL_NAMES) - set(self.policy["tools"])
        self.assertEqual(set(), missing)

    def test_live_ready_is_empty_until_real_machine_runs(self):
        self.assertEqual([], live_ready(self.policy),
                         "no live evidence recorded for this build yet")

    def test_allowed_writes_carry_evidence(self):
        for name, entry in self.policy["tools"].items():
            if entry["decision"] == "allow" and entry["purity"] == "write":
                self.assertTrue(entry.get("evidence") or entry.get("status") == "fixed",
                                "%s allowed without evidence" % name)
