"""P1 CLI tests: history ordering, filtering, reconcile, secret hygiene."""

import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from agent.memory.cli import main
from agent.memory.store import EventStore


class CliTestBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "memory.db")
        store = EventStore(self.db)
        self.session = store.start_session(
            agent_session_id="sess-cli", game_version="VERSION 100-Rev13",
            game_mvid="mvid", instance_id="local-01")
        self.tl = self.session.timeline_id
        store.attach_identity(self.tl, run_id="run-cli", slot=40, run_number=1)
        store.append_event(self.tl, event_type="observation", tool_name="get_state",
                           result_status="success")
        intent = store.append_intent(self.tl, tool_name="confirm_trade",
                                     args={"item_id": 1, "side": "buy"},
                                     action_id="act-cli", rev=10, game_day=1)
        store.append_result(intent, status="success", result={"ok": True, "rev": 11})
        store.append_event(self.tl, event_type="external_state_change",
                           tool_name="state_observation", result_status="success",
                           delta={"cash": {"before": 100, "after": 90}})
        store.close()

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *argv):
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = main(list(argv))
        return code, buffer.getvalue()


class HistoryTests(CliTestBase):
    def test_history_prints_events_in_sequence_order(self):
        code, output = self.run_cli("--db", self.db, "--format", "json", "history")
        self.assertEqual(0, code)
        rows = json.loads(output)
        self.assertEqual([1, 2, 3], [row["seq"] for row in rows])
        self.assertEqual("observation", rows[0]["event_type"])
        self.assertEqual("intent", rows[1]["event_type"])
        self.assertEqual("success", rows[1]["result_status"],
                         "the result updates the intent row instead of adding a row")

    def test_history_filters_by_type(self):
        code, output = self.run_cli("--db", self.db, "--format", "json",
                                    "history", "--type", "intent")
        self.assertEqual(0, code)
        rows = json.loads(output)
        self.assertEqual(1, len(rows))
        self.assertEqual("confirm_trade", rows[0]["tool_name"])
        self.assertEqual({"item_id": 1, "side": "buy"}, rows[0]["args"])

    def test_history_respects_limit_and_since_seq(self):
        code, output = self.run_cli("--db", self.db, "--format", "json",
                                    "history", "--limit", "1", "--since-seq", "2")
        self.assertEqual(0, code)
        rows = json.loads(output)
        self.assertEqual(1, len(rows))
        self.assertEqual(3, rows[0]["seq"])

    def test_history_never_prints_the_bridge_token(self):
        secret = "0123456789abcdef" * 2
        store = EventStore(self.db)
        store.append_event(self.tl, event_type="observation",
                           tool_name="get_state", result_status="success",
                           args={"token": secret})
        store.close()
        code, output = self.run_cli("--db", self.db, "history", "--limit", "50")
        self.assertEqual(0, code)
        self.assertNotIn(secret, output)


class StatusAndReconcileTests(CliTestBase):
    def test_status_counts_events_and_pending(self):
        store = EventStore(self.db)
        store.append_intent(self.tl, tool_name="sleep", args={},
                            action_id="act-pending", rev=12, game_day=1)
        store.close()
        code, output = self.run_cli("--db", self.db, "--format", "json", "status")
        self.assertEqual(0, code)
        payload = json.loads(output)
        self.assertEqual(1, payload["pending"])
        self.assertEqual("sleep", payload["pending_actions"][0]["tool"])
        self.assertEqual(4, payload["events"])

    def test_reconcile_marks_pending_unknown_and_is_idempotent(self):
        store = EventStore(self.db)
        store.append_intent(self.tl, tool_name="sleep", args={},
                            action_id="act-pending", rev=12, game_day=1)
        store.close()

        code, output = self.run_cli("--db", self.db, "--format", "json", "reconcile")
        self.assertEqual(0, code)
        self.assertEqual(1, json.loads(output)["reconciled"])

        code, output = self.run_cli("--db", self.db, "--format", "json", "reconcile")
        self.assertEqual(0, json.loads(output)["reconciled"])

        code, output = self.run_cli("--db", self.db, "--format", "json", "status")
        self.assertEqual(0, json.loads(output)["pending"])

    def test_timelines_lists_branches_with_counts(self):
        code, output = self.run_cli("--db", self.db, "--format", "json", "timelines")
        self.assertEqual(0, code)
        rows = json.loads(output)
        self.assertEqual(1, len(rows))
        self.assertEqual("run-cli", rows[0]["run_id"])
        self.assertEqual("slot:40", rows[0]["save_id"])
        self.assertEqual(3, rows[0]["events"])


class ErrorTests(unittest.TestCase):
    def test_missing_db_path_is_a_clean_failure(self):
        tmp = tempfile.TemporaryDirectory()
        try:
            blocker = os.path.join(tmp.name, "file")
            with open(blocker, "w", encoding="utf-8") as handle:
                handle.write("x")
            buffer = io.StringIO()
            with redirect_stdout(buffer):
                code = main(["--db", os.path.join(blocker, "memory.db"), "status"])
            self.assertEqual(3, code)
        finally:
            tmp.cleanup()


if __name__ == "__main__":
    unittest.main()
