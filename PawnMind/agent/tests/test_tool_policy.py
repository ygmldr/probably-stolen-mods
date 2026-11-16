"""Policy source tests: deny by default, evidence-driven, sets stay consistent."""

import json
import os
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MOD_DIR = os.path.dirname(AGENT_DIR)
sys.path.insert(0, AGENT_DIR)

from tool_policy import (  # noqa: E402
    SNAPSHOT_READERS, allowlist, candidate_allowlist, decision_for, live_ready,
    load_policy, play_allowlist, validate,
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

    def test_get_state_is_candidate_but_not_live_ready(self):
        entry = self.policy["tools"]["get_state"]
        self.assertEqual("d0a_fixed", entry["status"], "snapshot no longer refreshes")
        self.assertEqual("allow", entry["decision"], "statically allowed")
        self.assertIn("get_state", candidate_allowlist(self.policy))
        self.assertNotIn("get_state", play_allowlist(self.policy),
                         "live PPT-READ evidence is still missing")

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

    def test_bootstrap_never_reads_the_snapshot(self):
        # R3-01: the text used to say "no get_state" while the table said yes.
        bootstrap = set(allowlist(self.policy, "bootstrap"))
        self.assertEqual(set(), bootstrap & SNAPSHOT_READERS)
        self.assertFalse(self.policy["tools"]["get_state"]["modes"]["bootstrap"])

    def test_snapshot_reader_aliases_match_the_server_source(self):
        # R3-02 / PP-READ-04: get_run_summary, get_legal_actions, wait_until and
        # the long polls all go through GET /state; a new alias must be registered.
        import re
        lines = open(os.path.join(AGENT_DIR, "mcp_server.py"), encoding="utf-8").read().splitlines()
        found, current, decorated = set(), None, False
        for line in lines:
            if line.strip().startswith("@tool("):
                decorated = True
                continue
            match = re.match(r"    def (\w+)\(", line)
            if match:
                current = match.group(1) if decorated else None
                decorated = False
                continue
            if current and ("client.get_state(" in line or "client.wait_events(" in line):
                found.add(current)
        self.assertEqual(SNAPSHOT_READERS, found,
                         "snapshot readers drifted from the policy constant")

    def test_live_ready_is_empty_until_real_machine_runs(self):
        self.assertEqual([], play_allowlist(self.policy),
                         "play requires live evidence, not just a static allow")
        self.assertTrue(candidate_allowlist(self.policy), "candidates still reported")
        self.assertEqual([], live_ready(self.policy),
                         "no live evidence recorded for this build yet")

    def test_allowed_writes_carry_evidence(self):
        for name, entry in self.policy["tools"].items():
            if entry["decision"] == "allow" and entry["purity"] == "write":
                self.assertTrue(entry.get("evidence") or entry.get("status") == "fixed",
                                "%s allowed without evidence" % name)

    def test_generated_csharp_catalog_matches_the_policy(self):
        """The C# runtime gate must read the same policy, not a second copy."""
        import importlib.util
        import os

        here = os.path.dirname(os.path.abspath(__file__))
        mod_dir = os.path.dirname(os.path.dirname(here))
        script = os.path.join(mod_dir, "tools", "gen_policy_catalog.py")
        spec = importlib.util.spec_from_file_location("gen_policy_catalog", script)
        generator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(generator)

        generated = os.path.join(mod_dir, "src", "Core", "PolicyCatalog.g.cs")
        with open(generated, encoding="utf-8") as handle:
            current = handle.read()
        self.assertEqual(generator.render(self.policy), current,
                         "PolicyCatalog.g.cs is stale; run tools/gen_policy_catalog.py")

        self.assertEqual("2", str(self.policy["schema"]))
        self.assertIn(generator.canonical_sha256(self.policy), current,
                      "the generated file must carry the canonical policy hash")
