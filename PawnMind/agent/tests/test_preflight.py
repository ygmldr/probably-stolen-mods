"""S8 preflight contract tests: read-only, explicit, no write ops.

Every test starts a temporary `FakeGame` on a random loopback port and writes a
throwaway bridge.conf for it. The tests never touch a real save, the game
process, Steam, or the fixed production port. They assert:
  * the three read-only checks (/health, /state, get_recording_save_status);
  * the exit-code contract (0 ready / 1 not-ready / 2 fatal);
  * the stable --json shape and the mandatory UNVERIFIED warning;
  * that the preflight never sends a write op and never prints the token.
"""

import contextlib
import io
import json
import os
import socket
import subprocess
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PAWNMIND_ROOT = os.path.abspath(os.path.join(AGENT_DIR, ".."))
sys.path.insert(0, AGENT_DIR)

import preflight  # noqa: E402  (Red: the module does not exist yet)
from fake_game import FakeGame, READ_ONLY_OPS, WRITE_OPS  # noqa: E402

TOP_LEVEL_KEYS = {
    "ok",
    "exit_code",
    "checks",
    "health",
    "state",
    "recording",
    "warnings",
    "next_actions",
}
CHECK_KEYS = {"id", "ok", "code", "summary", "details", "next_action"}
HEALTH_KEYS = {
    "game_version",
    "game_assembly_mvid",
    "expected_game_version",
    "expected_assembly_mvid",
    "write_gate",
    "write_gate_reason",
    "latest",
}
STATE_KEYS = {"in_game", "day", "cash", "rent"}
RECORDING_KEYS = {
    "recording_mode",
    "configured_slot",
    "next_free_slot",
    "next_free_known",
    "configured_slot_occupied",
    "bound",
    "binding",
    "slot_matches",
    "run_matches",
    "error",
}
LATEST_MARKERS = ("latest=UNVERIFIED", "需人工核对")


class PreflightTestCase(unittest.TestCase):
    def start_game(self, **kwargs):
        game = FakeGame(**kwargs)
        game.start()
        self.addCleanup(game.stop)
        return game

    def write_config(self, port, token):
        handle, path = tempfile.mkstemp(prefix="pawnmind-preflight-", suffix=".conf")
        with os.fdopen(handle, "w", encoding="utf-8") as config:
            config.write("port=%d\ntoken=%s\n" % (port, token))
        self.addCleanup(lambda p=path: os.path.exists(p) and os.unlink(p))
        return path

    def config_for(self, game, token=None):
        return self.write_config(game.port, token or game.token)

    def invoke(self, config_path, *extra):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = preflight.main(["--config", config_path] + list(extra))
        return code, stdout.getvalue(), stderr.getvalue()

    def invoke_json(self, config_path, *extra):
        code, text, error = self.invoke(config_path, "--json", *extra)
        self.assertEqual("", error, "preflight --json must not write stderr")
        return code, json.loads(text), text

    def assert_latest_warning(self, text, payload):
        for marker in LATEST_MARKERS:
            self.assertIn(marker, text)
        self.assertTrue(
            any("latest=UNVERIFIED" in warning for warning in payload["warnings"]),
            payload["warnings"],
        )
        self.assertTrue(
            any("需人工核对" in warning for warning in payload["warnings"]),
            payload["warnings"],
        )

    def assert_checks_are_stable(self, payload):
        self.assertEqual(TOP_LEVEL_KEYS, set(payload))
        self.assertIsInstance(payload["checks"], list)
        self.assertTrue(payload["checks"], "a preflight must report its checks")
        for check in payload["checks"]:
            self.assertEqual(CHECK_KEYS, set(check), check)
            self.assertIsInstance(check["ok"], bool)
            self.assertIsInstance(check["summary"], str)
            self.assertIsInstance(check["details"], dict)
            if not check["ok"]:
                self.assertIsInstance(check["next_action"], str)
                self.assertTrue(check["next_action"].strip())


class ReadyPreflightTests(PreflightTestCase):
    def test_armed_with_matching_next_free_slot_exits_zero(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(0, code, text + error)
        for marker in (
            "game_version: VERSION 100-Rev13",
            "game_assembly_mvid: ",
            "expected_game_version: VERSION 100-Rev13",
            "expected_assembly_mvid: ",
            "write_gate: open",
            "write_gate_reason: null",
            "latest: UNVERIFIED",
            "in_game: false",
            "recording_mode: armed",
            "configured_slot: 17",
            "next_free_slot: 17",
            "next_free_known: true",
            "configured_slot_occupied: false",
            "bound: false",
            "slot_matches: false",
            "run_matches: false",
            "error: recording_not_bound",
        ):
            self.assertIn(marker, text)
        self.assert_latest_warning(text, self.invoke_json(config)[1])
        self.assertNotIn(game.token, text + error)

        _code, payload, _raw = self.invoke_json(config)
        self.assertTrue(payload["ok"])
        self.assertEqual(0, payload["exit_code"])
        self.assertEqual(HEALTH_KEYS, set(payload["health"]))
        self.assertEqual(STATE_KEYS, set(payload["state"]))
        self.assertEqual(RECORDING_KEYS, set(payload["recording"]))
        self.assert_checks_are_stable(payload)

    def test_bound_with_slot_and_run_matches_exits_zero_and_prints_state(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        game.bound = True
        game.binding = {
            "slot": 17,
            "run_id": "run-0001",
            "run_number": 1,
            "start_type": "apprentice",
            "start_scenario_id": "fence",
        }
        game.in_game = True
        game.day = 3
        game.cash = 42
        game.shop_state = "OPEN"
        game.rent_days_left = 4
        game.can_end_day = True
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(0, code, text + error)
        for marker in (
            "in_game: true",
            "day: 3",
            "cash: 42",
            "rent: due=100",
            "recording_mode: bound",
            "configured_slot: 17",
            "bound: true",
            "slot_matches: true",
            "run_matches: true",
            'binding: {"run_id": "run-0001"',
        ):
            self.assertIn(marker, text)
        self.assert_latest_warning(text, self.invoke_json(config)[1])


class NotReadyPreflightTests(PreflightTestCase):
    def test_default_fake_without_recording_is_not_ready(self):
        game = self.start_game(recording_mode="off")
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("recording_unconfigured", text)
        self.assertIn("recording_mode: disabled", text)
        self.assertIn("configured_slot: null", text)
        self.assertIn("NEXT:", text)
        self.assertNotIn(game.token, text + error)

        _code, payload, raw = self.invoke_json(config)
        self.assertFalse(payload["ok"])
        self.assertEqual(1, payload["exit_code"])
        self.assert_checks_are_stable(payload)
        self.assertTrue(any(
            check["code"] == "recording_unconfigured" and not check["ok"]
            for check in payload["checks"]
        ), payload["checks"])
        self.assert_latest_warning(raw, payload)

    def test_next_free_and_configured_slot_mismatch_reports_the_difference(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=18)
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("configured_slot: 17", text)
        self.assertIn("next_free_slot: 18", text)
        self.assertIn("+1", text)
        self.assertIn("recording_slot", text)

        _code, payload, raw = self.invoke_json(config)
        self.assertEqual(1, payload["exit_code"])
        self.assert_checks_are_stable(payload)
        mismatch = [check for check in payload["checks"]
                    if check["code"] == "recording_slot_mismatch"]
        self.assertEqual(1, len(mismatch), payload["checks"])
        self.assertFalse(mismatch[0]["ok"])
        self.assertEqual(17, mismatch[0]["details"]["configured_slot"])
        self.assertEqual(18, mismatch[0]["details"]["next_free_slot"])
        self.assertEqual(1, mismatch[0]["details"]["difference"])
        self.assertIn("recording_slot", mismatch[0]["next_action"])
        self.assertTrue(any("recording_slot" in action
                            for action in payload["next_actions"]))
        self.assert_latest_warning(raw, payload)

    def test_locked_write_gate_is_not_ready_and_prints_the_reason(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        game.health_payload["write_gate"] = "locked"
        game.health_payload["write_gate_reason"] = "game_assembly_mvid_mismatch"
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("write_gate: locked", text)
        self.assertIn("game_assembly_mvid_mismatch", text)
        self.assertIn("expected_assembly_mvid", text)
        self.assertIn("NEXT:", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assertEqual(1, payload["exit_code"])
        locked = [check for check in payload["checks"]
                  if check["code"] == "write_gate_locked"]
        self.assertEqual(1, len(locked), payload["checks"])
        self.assertFalse(locked[0]["ok"])
        self.assertEqual("game_assembly_mvid_mismatch",
                         locked[0]["details"]["write_gate_reason"])

    def test_bound_but_slot_mismatch_is_not_ready(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        game.bound = True
        game.binding = {
            "slot": 18,
            "run_id": "run-0001",
            "run_number": 1,
            "start_type": "apprentice",
            "start_scenario_id": "fence",
        }
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("slot_matches: false", text)
        self.assertIn("recording_binding_mismatch", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assert_checks_are_stable(payload)
        mismatch = [check for check in payload["checks"]
                    if check["code"] == "recording_binding_mismatch"]
        self.assertEqual(1, len(mismatch), payload["checks"])
        self.assertFalse(mismatch[0]["ok"])
        self.assertIn("binding", mismatch[0]["next_action"])

    def test_locked_recording_state_is_not_ready(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)

        def locked_status(op):
            return {
                "ok": True,
                "op": op,
                "recording_mode": "locked",
                "configured_slot": 17,
                "next_free_slot": 18,
                "next_free_known": True,
                "configured_slot_occupied": False,
                "bound": False,
                "slot_matches": False,
                "run_matches": False,
                "last_save_time": None,
                "error": "recording_slot_mismatch",
            }

        game._status_payload = locked_status
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("recording_mode: locked", text)
        self.assertIn("recording_locked", text)
        self.assertIn("recording_slot_mismatch", text)
        self.assertIn("NEXT:", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assert_checks_are_stable(payload)
        locked = [check for check in payload["checks"]
                  if check["code"] == "recording_locked"]
        self.assertEqual(1, len(locked), payload["checks"])
        self.assertFalse(locked[0]["ok"])
        self.assertIn("不得", locked[0]["next_action"])

    def test_unknown_next_free_slot_is_not_ready(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=None)
        game.next_free_slot = None
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(1, code, text + error)
        self.assertIn("next_free_known: false", text)
        self.assertIn("next_free_slot: null", text)
        self.assertIn("recording_next_free_unknown", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assert_checks_are_stable(payload)
        self.assertTrue(any(
            check["code"] == "recording_next_free_unknown"
            for check in payload["checks"]
        ), payload["checks"])

    def test_missing_next_free_known_does_not_block_a_numeric_match(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)

        def status_without_prediction_flag(op):
            return {
                "ok": True,
                "op": op,
                "recording_mode": "armed",
                "configured_slot": 17,
                "next_free_slot": 17,
                "configured_slot_occupied": False,
                "bound": False,
                "slot_matches": False,
                "run_matches": False,
                "last_save_time": None,
                "error": "recording_not_bound",
            }

        game._status_payload = status_without_prediction_flag
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(0, code, text + error)
        self.assertIn("next_free_known: null", text)
        self.assertIn("configured_slot: 17", text)
        self.assertIn("next_free_slot: 17", text)


class _MalformedHealthGame(FakeGame):
    """Test double that answers /health with a non-JSON body."""

    def _handle_http(self, handler, method):
        if handler.path == "/health":
            data = b"not-json"
            handler.send_response(200)
            handler.send_header("Content-Type", "application/json")
            handler.send_header("Content-Length", str(len(data)))
            handler.end_headers()
            handler.wfile.write(data)
            return
        return super()._handle_http(handler, method)


class FatalPreflightTests(PreflightTestCase):
    def test_unreachable_port_exits_two(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        config = self.write_config(port, "0" * 32)

        code, text, error = self.invoke(config)

        self.assertEqual(2, code, text + error)
        self.assertIn("无法连接", text)
        self.assertIn("GET /health", text)
        self.assertIn("port", text)

        _code, payload, raw = self.invoke_json(config)
        self.assertFalse(payload["ok"])
        self.assertEqual(2, payload["exit_code"])
        self.assert_checks_are_stable(payload)
        fatal = [check for check in payload["checks"]
                 if check["code"] == "connect_failed"]
        self.assertEqual(1, len(fatal), payload["checks"])
        self.assertFalse(fatal[0]["ok"])
        self.assert_latest_warning(raw, payload)

    def test_missing_health_field_exits_two(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        game.health_payload.pop("game_assembly_mvid")
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(2, code, text + error)
        self.assertIn("game_assembly_mvid", text)
        self.assertIn("health_missing_fields", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assertEqual(2, payload["exit_code"])
        self.assert_checks_are_stable(payload)
        missing = [check for check in payload["checks"]
                   if check["code"] == "health_missing_fields"]
        self.assertEqual(1, len(missing), payload["checks"])
        self.assertIn("game_assembly_mvid", missing[0]["details"]["missing"])

    def test_malformed_health_json_exits_two(self):
        game = _MalformedHealthGame(recording_mode="armed", recording_slot=17,
                                    next_free_slot=17)
        game.start()
        self.addCleanup(game.stop)
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(2, code, text + error)
        self.assertIn("GET /health", text)
        self.assertIn("connect_failed", text)

        _code, payload, _raw = self.invoke_json(config)
        self.assertEqual(2, payload["exit_code"])
        self.assertTrue(any(
            check["code"] == "connect_failed" and not check["ok"]
            for check in payload["checks"]
        ), payload["checks"])

    def test_bad_token_is_a_fatal_authentication_failure(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        config = self.config_for(game, token="f" * 32)

        code, text, error = self.invoke(config)

        self.assertEqual(2, code, text + error)
        self.assertIn("认证失败", text)
        self.assertNotIn(game.token, text + error)
        self.assertNotIn("f" * 32, text + error)

        _code, payload, _raw = self.invoke_json(config)
        self.assertEqual(2, payload["exit_code"])
        fatal = [check for check in payload["checks"]
                 if check["code"] == "auth_failed"]
        self.assertEqual(1, len(fatal), payload["checks"])


class ReadOnlyAndOutputTests(PreflightTestCase):
    def test_preflight_sends_only_the_three_read_operations(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        config = self.config_for(game)

        code, text, error = self.invoke(config)

        self.assertEqual(0, code, text + error)
        requests = list(game.requests)
        self.assertEqual(["/health", "/state", "/action"],
                         [request["path"] for request in requests])
        self.assertEqual(["GET", "GET", "POST"],
                         [request["method"] for request in requests])
        self.assertIsNone(requests[0]["authorization"],
                          "/health must stay unauthenticated")
        self.assertEqual("Bearer " + game.token,
                         requests[1]["authorization"])
        self.assertEqual("Bearer " + game.token,
                         requests[2]["authorization"])

        actions = [request for request in requests
                   if request["path"] == "/action"]
        self.assertEqual(1, len(actions))
        body = actions[0]["body"]
        self.assertEqual("get_recording_save_status", body["op"])
        self.assertIn(body["op"], READ_ONLY_OPS)
        self.assertNotIn(body["op"], WRITE_OPS)
        self.assertNotIn("rev", body)
        self.assertNotIn(game.token, text + error)

    def test_json_is_stable_and_contains_no_token(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        config = self.config_for(game)

        first_code, first, first_raw = self.invoke_json(config)
        second_code, second, second_raw = self.invoke_json(config)

        self.assertEqual(0, first_code)
        self.assertEqual(0, second_code)
        self.assertEqual(first, second, "the preflight JSON must be stable")
        self.assert_checks_are_stable(first)
        self.assertNotIn(game.token, first_raw)
        self.assertNotIn(game.token, second_raw)
        self.assertEqual(0, first["exit_code"])
        self.assertTrue(first["ok"])
        self.assert_latest_warning(first_raw, first)

    def test_module_entrypoint_runs_the_read_only_preflight(self):
        game = self.start_game(recording_mode="armed", recording_slot=17,
                               next_free_slot=17)
        config = self.config_for(game)

        completed = subprocess.run(
            [sys.executable, "-m", "agent.preflight",
             "--config", config, "--json"],
            cwd=PAWNMIND_ROOT,
            capture_output=True,
            text=True,
            timeout=60,
        )

        self.assertEqual(0, completed.returncode, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertTrue(payload["ok"])
        self.assertEqual(0, payload["exit_code"])
        self.assertNotIn(game.token, completed.stdout + completed.stderr)


if __name__ == "__main__":
    unittest.main()
