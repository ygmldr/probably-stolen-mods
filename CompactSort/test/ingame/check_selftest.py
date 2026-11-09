#!/usr/bin/env python3
"""Parse the in-game CompactSort self-test lines from Player.log."""

import argparse
import glob
import os
import re
import sys
from pathlib import Path
from typing import Optional

DEFAULT_GLOBS = [
    "/mnt/c/Users/*/AppData/LocalLow/Questing Goose Studio/Probably Stolen/Player.log",
    os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log"),
]

START_RE = re.compile(r"\[CompactSort\]\[TEST\] start")
RESULT_RE = re.compile(r"\[CompactSort\]\[TEST\] (PASS|FAIL) case=(\S+) detail=(.*)")
SUMMARY_RE = re.compile(r"\[CompactSort\]\[TEST\] SUMMARY pass=(\d+) fail=(\d+)")


def find_log(explicit: Optional[str]) -> Path:
    if explicit:
        return Path(explicit)
    for pattern in DEFAULT_GLOBS:
        for candidate in glob.glob(pattern):
            path = Path(candidate)
            if path.is_file():
                return path
    raise FileNotFoundError("Player.log not found; pass --log <path>")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--log", help="Path to Player.log")
    args = parser.parse_args()

    log_path = find_log(args.log)
    text = log_path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()

    start = -1
    for i, line in enumerate(lines):
        if START_RE.search(line):
            start = i
    run = lines[start:] if start >= 0 else lines

    failures = []
    summary = None
    for line in run:
        match = RESULT_RE.search(line)
        if match and match.group(1) == "FAIL":
            failures.append(line.strip())
        match = SUMMARY_RE.search(line)
        if match:
            summary = (int(match.group(1)), int(match.group(2)))

    print(f"log: {log_path}")
    if summary is None:
        print("FAIL: no [CompactSort][TEST] SUMMARY line found")
        print("Hint: load a save, enter the store, then press F8.")
        return 2

    passed, failed = summary
    print(f"SUMMARY: pass={passed} fail={failed}")
    for line in failures:
        print(line)
    if failed or failures:
        print("SELFTEST FAILED")
        return 1
    print("SELFTEST PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
