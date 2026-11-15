"""A dead long poll must come back as data, not as a transport exception."""

import os
import sys
import tempfile
import unittest

AGENT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, AGENT_DIR)

from pawnmind_client import BridgeClient  # noqa: E402


class WaitEventsErrorShapeTests(unittest.TestCase):
    def _client(self, port):
        root = tempfile.mkdtemp(prefix="pawnmind-wait-")
        path = os.path.join(root, "bridge.conf")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("port=%d\ntoken=%s\n" % (port, "t" * 64))
        return BridgeClient(path)

    def test_unreachable_bridge_returns_a_structured_error(self):
        client = self._client(15999)  # nothing listens here

        result = client.wait_events(since_rev=0, timeout_ms=1000)

        self.assertIsInstance(result, dict, "never raises")
        self.assertFalse(result.get("ok"), "reports failure")
        self.assertEqual("wait_failed", result.get("error"), "stable code")
        self.assertIn("detail", result, "keeps the transport reason")


if __name__ == "__main__":
    unittest.main()
