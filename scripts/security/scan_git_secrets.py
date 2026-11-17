#!/usr/bin/env python3
"""Fail the build when a credential, key file or private-key block is present.

The scanner looks at three places, because a secret can hide in any of them:

1. the worktree (what a clone would check out),
2. every blob in every commit (what `git log -p` would reveal),
3. every commit message.

It only reports things that are actionable: known token prefixes, private-key
blocks, credential-like assignments and key/env file names. It deliberately does
not guess from bare high-entropy strings, because hashes and test fixtures would
be flagged constantly; that trade-off is why the fixture allowlist exists.

Usage:
    python3 -B scripts/security/scan_git_secrets.py            # scan HEAD + history
    python3 -B scripts/security/scan_git_secrets.py --staged   # scan the index only
Exit code 0 = clean, 1 = findings, 2 = the scan could not run.
"""

import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ALLOWLIST_PATH = os.path.join(ROOT, "scripts", "security", "secret_allowlist.txt")

#: Values that are obviously placeholders rather than credentials.
PLACEHOLDER = re.compile(
    r"^(|null|none|true|false|yes|no|0|1|-|\*{3,}|x{3,}|\.{3,}|<[^>]*>|\$\{[^}]*\}|"
    r"%[A-Za-z_]+%|\[redacted\]|redacted|changeme|placeholder|example|test|dummy)$",
    re.IGNORECASE,
)

#: Known credential formats. A match anywhere is fatal.
TOKEN_PATTERNS = [
    ("github-token", re.compile(r"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}\b")),
    ("github-pat", re.compile(r"\bgithub_pat_[A-Za-z0-9_]{20,}\b")),
    ("aws-access-key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("google-api-key", re.compile(r"\bAIza[0-9A-Za-z_\-]{35}\b")),
    ("slack-token", re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}\b")),
    ("openai-key", re.compile(r"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}\b")),
    ("private-key-block", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
]

#: key = value / "key": "value" style assignments of credential-ish names.
ASSIGNMENT = re.compile(
    r"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|"
    r"private[_-]?key|client[_-]?secret|auth[_-]?token)\b\s*[:=]\s*['\"]?([^\s'\";,]{6,})"
)

#: Paths that must never be committed.
KEY_FILE = re.compile(
    r"(?i)(^|/)(id_rsa|id_dsa|id_ecdsa|id_ed25519)(\.pub)?$|"
    r"\.(pem|pfx|p12|keystore|jks|key)$|(^|/)\.env($|\.)|(^|/)bridge\.conf$"
)

SKIP_DIRS = {".git", "node_modules", "__pycache__", ".venv", "bin", "obj", "dist",
             "_decomp", "_internal", "test-artifacts"}
SKIP_SUFFIX = (".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".7z", ".dll",
               ".pdb", ".pdf", ".xlsx", ".zstd", ".bundle")


def load_allowlist():
    values = set()
    if not os.path.exists(ALLOWLIST_PATH):
        return values
    with open(ALLOWLIST_PATH, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line and not line.startswith("#"):
                values.add(line)
    return values


def looks_like_secret(value):
    """A credential is a long, dense, opaque token - not an expression."""
    if len(value) < 16 or len(value) > 200:
        return False
    if not re.fullmatch(r"[A-Za-z0-9_\-+/=]+", value):
        return False
    if not re.search(r"[0-9]", value) or not re.search(r"[A-Za-z]", value):
        return False
    counts = {}
    for char in value:
        counts[char] = counts.get(char, 0) + 1
    entropy = -sum((n / float(len(value))) * __import__("math").log(n / float(len(value)), 2)
                   for n in counts.values())
    return entropy >= 3.2


def is_fixture(value, allowlist):
    if value in allowlist:
        return True
    if PLACEHOLDER.match(value):
        return True
    # Repeated / sequential test values: "0123456789abcdef" * n, "deadbeef" * n.
    for unit in ("0123456789abcdef", "deadbeef", "cafebabe", "0", "a", "f"):
        if value and value == (unit * (len(value) // len(unit) + 1))[:len(value)]:
            return True
    return False


def scan_text(label, text, allowlist, findings):
    if not text:
        return
    if "\x00" in text:
        return
    for name, pattern in TOKEN_PATTERNS:
        for match in pattern.finditer(text):
            findings.append((label, name, match.group(0)[:80]))

    for match in ASSIGNMENT.finditer(text):
        name, value = match.group(1), match.group(2)
        if is_fixture(value, allowlist):
            continue
        # A bare interpolation, a path or an expression is not a credential.
        if value.startswith(("$", "%", "<")) or "/" in value:
            continue
        if not looks_like_secret(value):
            continue
        findings.append((label, "credential-assignment:" + name.lower(), value[:80]))


def ignored_paths(candidates):
    """Paths git would never commit, so a local .env is not a finding."""
    if not candidates:
        return set()
    proc = subprocess.run(["git", "-C", ROOT, "check-ignore", "--stdin"],
                          input="\n".join(candidates) + "\n",
                          check=False, capture_output=True, text=True)
    return set(line.strip() for line in proc.stdout.splitlines() if line.strip())


def scan_worktree(allowlist, findings):
    candidates = []
    for root, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
            candidates.append(rel)
    ignored = ignored_paths(candidates)

    for rel in candidates:
        if rel in ignored:
            continue
        path = os.path.join(ROOT, rel)
        if True:
            if KEY_FILE.search(rel):
                findings.append((rel, "key-or-env-file", rel))
                continue
            if rel.endswith(SKIP_SUFFIX):
                continue
            try:
                if os.path.getsize(path) > 2 * 1024 * 1024:
                    continue
                with open(path, encoding="utf-8", errors="replace") as handle:
                    scan_text(rel, handle.read(), allowlist, findings)
            except OSError:
                continue


def git(*args, **kwargs):
    return subprocess.run(["git", "-C", ROOT] + list(args), check=False,
                          capture_output=True, text=True, **kwargs)


def scan_history(allowlist, findings):
    objects = git("cat-file", "--batch-all-objects",
                  "--batch-check=%(objectname) %(objecttype)").stdout.splitlines()
    blobs = [line.split()[0] for line in objects if line.endswith(" blob")]
    if blobs:
        proc = subprocess.run(["git", "-C", ROOT, "cat-file", "--batch"],
                              input=("\n".join(blobs) + "\n").encode("utf-8"),
                              check=False, capture_output=True)
        data = proc.stdout
        pos = 0
        while True:
            header_end = data.find(b"\n", pos)
            if header_end < 0:
                break
            header = data[pos:header_end].split()
            if len(header) < 3:
                break
            size = int(header[2])
            body = data[header_end + 1:header_end + 1 + size]
            pos = header_end + 1 + size + 1  # skip the trailing newline
            try:
                text = body.decode("utf-8")
            except UnicodeDecodeError:
                continue
            scan_text("blob " + header[0].decode()[:12], text, allowlist, findings)

    for line in git("log", "--all", "--format=%H%x09%B").stdout.split("\n"):
        if "\t" in line:
            scan_text("commit-msg " + line.split("\t", 1)[0][:12], line, allowlist, findings)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--staged", action="store_true",
                        help="scan only the index (files added to the next commit)")
    args = parser.parse_args(argv)

    try:
        git("rev-parse", "--git-dir")
    except OSError:
        print("secret-scan: not a git repository", file=sys.stderr)
        return 2

    allowlist = load_allowlist()
    findings = []

    if args.staged:
        for rel in git("diff", "--cached", "--name-only", "--diff-filter=ACMR").stdout.splitlines():
            if KEY_FILE.search(rel):
                findings.append((rel, "key-or-env-file", rel))
                continue
            text = git("show", ":" + rel).stdout
            scan_text(rel, text, allowlist, findings)
    else:
        scan_worktree(allowlist, findings)
        scan_history(allowlist, findings)

    # De-duplicate; the same file can appear in many commits.
    unique = sorted(set(findings))
    for label, kind, excerpt in unique[:200]:
        print("FINDING %-40s %-32s %s" % (label, kind, excerpt))
    if unique:
        print("\nsecret-scan: %d finding(s)" % len(unique), file=sys.stderr)
        return 1

    print("secret-scan: clean (%s)" % ("index" if args.staged else "worktree + history"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
