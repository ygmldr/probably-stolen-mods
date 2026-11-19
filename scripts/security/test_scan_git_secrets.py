"""Tests for the repository secret scanner (R5.1/F0).

The scanner is the only thing standing between a committed credential and a
public repository, so two properties matter as much as the detection itself:

* an allowlisted *fixture* must not turn into a blanket exception for the same
  pattern, and
* a finding must never print the matched value — CI logs are public too.

Independent review 2026-11-19 (F0) found the token patterns bypassed the
allowlist entirely and printed the raw match.
"""

import hashlib
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import scan_git_secrets as scanner  # noqa: E402

# The fixture is an exact allowlist entry; it must stay a literal so the scan of
# this very file stays clean.
FIXTURE = "sk-live-SECRET-token-0123456789abcdef"

# Built from parts on purpose: the scanner scans this repository, and a literal
# here would be exactly the kind of hit it exists to catch.
OTHER_TOKEN = "sk-" + "live-REAL-looking-token-" + "fedcba9876543210"


def scan(text, allowlist=()):
    findings = []
    scanner.scan_text("some/file.py", text, set(allowlist), findings)
    return findings


class TokenAllowlistTests(unittest.TestCase):
    def test_allowlisted_fixture_is_not_a_finding(self):
        self.assertEqual([], scan(FIXTURE, allowlist=[FIXTURE]))

    def test_a_different_token_of_the_same_shape_still_fails(self):
        findings = scan(OTHER_TOKEN, allowlist=[FIXTURE])
        self.assertEqual(1, len(findings),
                         "the allowlist is an exact value, not a pattern exception")

    def test_unknown_token_fails_without_an_allowlist(self):
        self.assertEqual(1, len(scan(OTHER_TOKEN)))


class RedactionTests(unittest.TestCase):
    def _assert_redacted(self, findings, secret):
        self.assertTrue(findings, "the token must still be reported")
        for _, _, excerpt in findings:
            self.assertNotIn(secret, excerpt, "a finding must not print the value")
            self.assertIn("sha256:", excerpt, "it keeps a stable, irreversible digest")

    def test_token_finding_is_redacted(self):
        self._assert_redacted(scan(OTHER_TOKEN), OTHER_TOKEN)

    def test_assignment_finding_is_redacted(self):
        line = 'api_key = "%s"' % OTHER_TOKEN
        self._assert_redacted(scan(line), OTHER_TOKEN)

    def test_redaction_is_stable_for_dedup(self):
        first = scan(OTHER_TOKEN)
        second = scan(OTHER_TOKEN)
        self.assertEqual(first, second, "the same value redacts the same way")


class RedactHelperTests(unittest.TestCase):
    def test_redact_keeps_length_and_digest(self):
        text = scanner.redact(OTHER_TOKEN)
        self.assertIn("len=%d" % len(OTHER_TOKEN), text)
        self.assertIn(hashlib.sha256(OTHER_TOKEN.encode()).hexdigest()[:12], text)
        self.assertNotIn(OTHER_TOKEN, text)

    def test_redact_handles_empty(self):
        self.assertEqual("len=0", scanner.redact(""))


if __name__ == "__main__":
    unittest.main()
