#!/usr/bin/env python3
"""Validate CompactSortSnapshots JSON files and basic schema."""
import argparse
import glob
import json
import os
import sys
from pathlib import Path

DEFAULT = "/mnt/c/Users/*/AppData/LocalLow/Questing Goose Studio/Probably Stolen/CompactSortSnapshots"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", nargs="?", help="snapshot directory or .json file")
    args = parser.parse_args()

    if args.path:
        roots = [Path(args.path)]
    else:
        roots = [Path(p) for p in glob.glob(DEFAULT)]
    files = []
    for root in roots:
        if root.is_dir():
            files.extend(sorted(root.glob("*.json")))
        elif root.is_file():
            files.append(root)
    if not files:
        print("no snapshots found", file=sys.stderr)
        return 2

    failures = 0
    for path in files:
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            containers = data.get("containers")
            if not isinstance(containers, list):
                raise ValueError("containers is not a list")
            for c in containers:
                w, h = int(c["width"]), int(c["height"])
                wall = c.get("wall", "")
                if len(wall) != w * h:
                    raise ValueError("wall length mismatch in " + str(c.get("kind")))
                for item in c.get("items", []):
                    if len(item.get("mask", "")) != item["width"] * item["height"]:
                        raise ValueError("item mask mismatch " + str(item.get("identifier")))
        except Exception as e:
            failures += 1
            print("FAIL", path.name, e)
        else:
            print("PASS", path.name, "containers=" + str(len(containers)))

    print("SUMMARY files=" + str(len(files)) + " failures=" + str(failures))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
