"""The mode allowlist is generated from one registry; drift fails the build."""

import json
import os
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MOD_DIR = os.path.dirname(AGENT_DIR)
sys.path.insert(0, AGENT_DIR)

from tool_policy import (  # noqa: E402
    STRICT_DENY, allowed_in, purity, strict_allowlist,
)
import mcp_server  # noqa: E402


def _inventory():
    path = os.path.join(MOD_DIR, "docs", "audit", "tools-inventory.json")
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


class ToolPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inv = _inventory()
        cls.full = {t["name"]: t for t in cls.inv["full"]}
        cls.recording = set(cls.inv["recording"])

    def test_recording_is_a_subset_of_full(self):
        self.assertTrue(self.recording <= set(self.full),
                        "every recording tool must exist in the full inventory")

    def test_every_recording_tool_is_in_the_full_registry(self):
        missing = set(mcp_server.RECORDING_TOOL_NAMES) - set(self.full)
        self.assertEqual(set(), missing, "server exports tools absent from the inventory")

    def test_every_tool_has_a_purity(self):
        for name, tool in self.full.items():
            self.assertIn(purity(name, tool.get("readOnlyHint")),
                          {"pure", "cached_impure", "mutating", "write"},
                          "%s has no purity" % name)

    def test_strict_list_drops_unproven_reads_and_privileged_tools(self):
        strict = set(strict_allowlist(self.inv["full"]))
        for name in ("bargain_preview", "get_clientele", "save_game", "new_game", "act", "press"):
            self.assertNotIn(name, strict, "%s must not enter strict_player" % name)

    def test_strict_list_is_inside_the_recording_set(self):
        strict = set(strict_allowlist(self.inv["full"]))
        self.assertTrue(strict <= self.recording,
                        "strict must be generated from the recording surface")

    def test_reads_are_classified_from_the_recorder_allowlist(self):
        from tool_policy import READ_ONLY_TOOLS
        self.assertGreater(len(READ_ONLY_TOOLS), 40, "recorder read-only set")
        self.assertEqual("pure", purity("list_goods"), "a read must not count as a write")
        self.assertEqual("write", purity("confirm_trade"), "a write stays a write")

    def test_get_state_stays_out_until_d0a_has_evidence(self):
        from tool_policy import PENDING_PURITY
        self.assertIn("get_state", PENDING_PURITY, "documented blocker, not silent")
        self.assertNotIn("get_state", strict_allowlist(self.inv["full"]),
                         "the snapshot still refreshes the bargain UI")

    def test_deny_list_beats_every_mode_except_test(self):
        for name in STRICT_DENY:
            self.assertFalse(allowed_in("strict_player", name, True), name)
            self.assertFalse(allowed_in("assisted", name, True), name)
            self.assertTrue(allowed_in("test", name, True), name)
