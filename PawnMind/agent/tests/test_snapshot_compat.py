"""PP-READ-06 / PP-READ-05 (Python half): snapshot consumer compatibility.

The C# snapshot stopped emitting ``client.can_report`` (it called
``StoreClient.CanReport()`` -> ``BargainUIManager.Refresh()``) and split
``legal_actions`` from the new ``candidate_actions`` list. Older snapshots -
including ones cached in an agent's memory - do not have those keys, and the
Python layer must degrade instead of raising ``KeyError`` or silently claiming a
candidate is legal.
"""

import os
import sys
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

import debug_bundle  # noqa: E402
import mcp_server  # noqa: E402
from fake_game import FakeGame  # noqa: E402


class LegalActionCompatibilityTests(unittest.TestCase):
    def test_legacy_snapshot_without_candidate_actions_degrades(self):
        state = {
            "rev": 7,
            "legal_actions": [
                {"action": "open_shop", "args": {}},
                {"action": "sleep", "args": {}},
            ],
        }
        payload = mcp_server._legal_action_payload(state)
        self.assertEqual(7, payload["rev"])
        self.assertEqual(2, len(payload["legal_actions"]))
        self.assertEqual([], payload["candidate_actions"],
                         "a pre-B snapshot has no candidate section")

    def test_new_snapshot_keeps_candidates_separate(self):
        state = {
            "rev": 3,
            "legal_actions": [{"action": "open_shop", "args": {}}],
            "candidate_actions": [
                {"action": "bargain", "args": {"direction": "sell_discount"},
                 "availability": "unknown", "requires": "native_guard"},
            ],
        }
        payload = mcp_server._legal_action_payload(state)
        self.assertEqual(1, len(payload["legal_actions"]))
        self.assertEqual(1, len(payload["candidate_actions"]))
        candidates = [row["action"] for row in payload["candidate_actions"]]
        self.assertEqual(["bargain"], candidates,
                         "candidates must not leak into legal_actions")
        legal = [row["action"] for row in payload["legal_actions"]]
        self.assertNotIn("bargain", legal)

    def test_missing_or_malformed_state_degrades_to_empty_lists(self):
        for state in (None, {}, {"legal_actions": None},
                      {"legal_actions": "oops", "candidate_actions": 5}):
            payload = mcp_server._legal_action_payload(state)
            self.assertIsInstance(payload["legal_actions"], list)
            self.assertIsInstance(payload["candidate_actions"], list)
            self.assertEqual([], payload["legal_actions"])
            self.assertEqual([], payload["candidate_actions"])

    def test_get_legal_actions_docstring_mentions_candidates(self):
        source = open(os.path.join(AGENT_DIR, "mcp_server.py"),
                      encoding="utf-8").read()
        index = source.index("def get_legal_actions(")
        doc_end = source.index('"""', source.index('"""', index) + 3)
        doc = source[index:doc_end]
        self.assertIn("candidate", doc.lower(),
                      "the tool must tell the model candidates are not legal")


class DebugBundleCompatibilityTests(unittest.TestCase):
    def test_legacy_client_without_can_report_does_not_raise(self):
        summary = debug_bundle._state_summary({
            "rev": 1,
            "client": {"name": "Old", "intent": "BUY", "is_wanted": False},
        })
        self.assertEqual("Old", summary["client"]["name"])
        self.assertNotIn("can_report", summary["client"])
        self.assertFalse(summary["client"].get("reported", False))

    def test_new_client_reports_pure_reported_field(self):
        summary = debug_bundle._state_summary({
            "rev": 2,
            "client": {"name": "New", "intent": "SELL", "is_wanted": True,
                       "reported": False},
        })
        self.assertEqual("New", summary["client"]["name"])
        self.assertIn("reported", summary["client"])
        self.assertNotIn("can_report", summary["client"])

    def test_non_dict_state_is_passed_through(self):
        self.assertEqual("raw", debug_bundle._state_summary("raw"))


class FakeGameSchemaTests(unittest.TestCase):
    """The offline fake must expose the same snapshot shape the bridge does."""

    def test_fake_state_carries_candidate_actions_and_no_can_report(self):
        game = FakeGame()
        game.in_game = True
        state = game._build_state()
        self.assertIn("candidate_actions", state)
        self.assertIsInstance(state["candidate_actions"], list)
        self.assertNotIn("can_report", state.get("client") or {})

    def test_fake_legal_rows_carry_availability_available(self):
        # The C# snapshot states availability on every legal-action row; the
        # offline fake must mirror that contract (E module low finding).
        game = FakeGame()
        game.in_game = True
        for row in game._legal_actions():
            self.assertEqual("available", row.get("availability"),
                             "legal rows must be explicitly available")


if __name__ == "__main__":
    unittest.main()
