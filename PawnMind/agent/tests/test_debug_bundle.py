"""Tests for agent.debug_bundle (read-only debugging evidence collection)."""

import json
import os
import shutil
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

import debug_bundle  # noqa: E402
from fake_game import FakeGame  # noqa: E402


class DebugBundleTests(unittest.TestCase):
    def setUp(self):
        self.game = FakeGame(recording_mode="armed", next_free_slot=40)
        self.game.start()
        self.addCleanup(self.game.stop)

        self.root = tempfile.mkdtemp(prefix="pawnmind-bundle-")
        self.addCleanup(shutil.rmtree, self.root, True)

        self.mod_data = os.path.join(self.root, "ModData", "pawnmind")
        os.makedirs(os.path.join(self.mod_data, "screenshots"), exist_ok=True)
        self.config = os.path.join(self.mod_data, "bridge.conf")
        self.game.write_config(self.config)

        for index, name in enumerate((
                "20261114-220001_r10_d1_open_shop_ok.jpg",
                "20261114-220002_r12_d1_choose_dialogue_ok.jpg")):
            with open(os.path.join(self.mod_data, "screenshots", name), "wb") as handle:
                handle.write(b"\xff\xd8\xff\xe0fake-jpeg-%d" % index)

    def _bundle(self, limit=50):
        out = os.path.join(self.root, "out")
        debug_bundle.write_bundle(self.config, out, limit=limit)
        with open(os.path.join(out, "bundle.json"), encoding="utf-8") as handle:
            return out, json.load(handle)

    # ------------------------------------------------------------- happy path

    def test_bundle_contains_health_recording_state_and_screenshots(self):
        out, bundle = self._bundle()

        self.assertTrue(os.path.isfile(os.path.join(out, "README.txt")))
        self.assertEqual(debug_bundle.EXIT_OK, debug_bundle.main(
            ["--config", self.config, "--out", os.path.join(self.root, "out2")]))

        self.assertEqual("open", bundle["health"]["write_gate"])
        self.assertEqual("armed", bundle["recording"]["recording_mode"])
        self.assertEqual(40, bundle["recording"]["next_free_slot"])
        self.assertFalse(bundle["state_summary"]["in_game"])
        self.assertIn("collected_at", bundle)
        self.assertEqual(2, bundle["screenshot_copy"]["copied"])

        copied = sorted(os.listdir(os.path.join(out, "screenshots")))
        self.assertEqual(2, len(copied))
        self.assertTrue(all(name.endswith(".jpg") for name in copied))

    def test_bundle_never_contains_the_bearer_token(self):
        out, _bundle = self._bundle()
        token = self.game.token

        for name in ("bundle.json", "README.txt"):
            with open(os.path.join(out, name), encoding="utf-8") as handle:
                self.assertNotIn(token, handle.read(), name)
        for name in os.listdir(os.path.join(out, "screenshots")):
            with open(os.path.join(out, "screenshots", name), "rb") as handle:
                self.assertNotIn(token.encode(), handle.read(), name)

    def test_state_summary_is_trimmed(self):
        _out, bundle = self._bundle()
        summary = bundle["state_summary"]
        self.assertNotIn("inventory", summary, "full inventory must not be bundled")
        self.assertNotIn("legal_actions", summary)

    # ---------------------------------------------------------- output safety

    def test_refuses_non_empty_output_directory(self):
        out = os.path.join(self.root, "busy")
        os.makedirs(out)
        with open(os.path.join(out, "keep.txt"), "w", encoding="utf-8") as handle:
            handle.write("do not delete me")

        with self.assertRaises(debug_bundle.BundleError):
            debug_bundle.write_bundle(self.config, out)

        self.assertTrue(os.path.isfile(os.path.join(out, "keep.txt")))

    def test_refuses_output_path_that_is_a_file(self):
        out = os.path.join(self.root, "a-file")
        with open(out, "w", encoding="utf-8") as handle:
            handle.write("x")

        with self.assertRaises(debug_bundle.BundleError):
            debug_bundle.write_bundle(self.config, out)

    def test_fatal_exit_code_for_busy_output(self):
        out = os.path.join(self.root, "busy2")
        os.makedirs(out)
        with open(os.path.join(out, "keep.txt"), "w", encoding="utf-8") as handle:
            handle.write("x")
        self.assertEqual(debug_bundle.EXIT_FATAL, debug_bundle.main(
            ["--config", self.config, "--out", out]))

    # ------------------------------------------------------------ degradation

    def test_missing_screenshot_directory_is_noted_not_fatal(self):
        shutil.rmtree(os.path.join(self.mod_data, "screenshots"))
        out, bundle = self._bundle()

        self.assertEqual(0, bundle["screenshot_copy"]["copied"])
        self.assertIn("no screenshot directory", bundle["screenshot_copy"]["note"])
        self.assertTrue(os.path.isfile(os.path.join(out, "bundle.json")))

    def test_unreachable_bridge_is_fatal(self):
        self.game.stop()
        out = os.path.join(self.root, "dead")
        self.assertEqual(debug_bundle.EXIT_FATAL, debug_bundle.main(
            ["--config", self.config, "--out", out]))
        self.assertFalse(os.path.exists(out), "no directory left behind on failure")

    def test_limit_is_applied(self):
        out = os.path.join(self.root, "limited")
        debug_bundle.write_bundle(self.config, out, limit=1)
        self.assertEqual(1, len(os.listdir(os.path.join(out, "screenshots"))))


class RedactionTests(unittest.TestCase):
    def test_redact_walks_nested_structures(self):
        scrubbed = debug_bundle._redact(
            {"a": "prefix-secret-suffix", "b": ["secret"], "c": 1}, "secret")
        self.assertEqual("prefix-[redacted]-suffix", scrubbed["a"])
        self.assertEqual(["[redacted]"], scrubbed["b"])
        self.assertEqual(1, scrubbed["c"])

    def test_empty_token_is_a_no_op(self):
        payload = {"a": "secret"}
        self.assertEqual(payload, debug_bundle._redact(payload, None))


if __name__ == "__main__":
    unittest.main()
