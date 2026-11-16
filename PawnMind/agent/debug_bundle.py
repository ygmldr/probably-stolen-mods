"""Collect a read-only debugging bundle for the PawnMind bridge.

Usage (from the ``PawnMind`` directory)::

    python -m agent.debug_bundle --config <bridge.conf> --out <new-directory> [--limit 50]

The bundle is meant to be handed to a human (or a coding agent) together with
the question "why did this run go wrong?". It contains:

* ``bundle.json``   structured snapshot (health, recording status, state, recent
                    action history, screenshot index, collection notes)
* ``README.txt``    one-page human summary
* ``screenshots/``  copies of the evidence captures that the mod wrote into
                    ``ModData/pawnmind/screenshots/``

Guarantees:

* **Read-only.** Only ``/health``, ``/state`` and read-only MCP actions are
  issued; no gameplay write ever runs.
* **No secrets.** The bearer token is never written: the client keeps it in
  memory only, and every rendered string is scrubbed against it.
* **No saves.** Save files, previews and the bridge config itself are never
  copied or read.
* **No overwrite.** ``--out`` must be absent or an empty directory; anything
  else is refused before a single byte is written.

Exit codes: 0 = bundle written, 2 = fatal (bad arguments, unusable output
directory, bridge unreachable or unauthenticated).
"""

import argparse
import json
import os
import shutil
import sys
from datetime import datetime, timezone

try:  # python -m agent.debug_bundle
    from .pawnmind_client import BridgeClient, BridgeError
except ImportError:  # imported as a top-level module from agent/tests
    from pawnmind_client import BridgeClient, BridgeError

EXIT_OK = 0
EXIT_FATAL = 2

SCREENSHOT_SUBDIR = "screenshots"
BUNDLE_JSON = "bundle.json"
BUNDLE_README = "README.txt"


class BundleError(Exception):
    """Fatal, user-facing bundle problem."""


def _utc_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _redact(value, token):
    """Recursively scrub the bearer token out of any collected payload."""
    if not token:
        return value
    if isinstance(value, str):
        return value.replace(token, "[redacted]")
    if isinstance(value, dict):
        return {key: _redact(item, token) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_redact(item, token) for item in value]
    return value


def _collect(label, function, notes):
    """Run one collector; a failure becomes a note instead of killing the bundle."""
    try:
        return function()
    except (BridgeError, OSError, ValueError) as error:
        notes.append("%s unavailable: %s" % (label, error))
        return None


def _mod_data_dir(config_path):
    """ModData/pawnmind, derived from the bridge.conf location."""
    return os.path.dirname(os.path.abspath(config_path))


def _screenshot_dir(config_path):
    return os.path.join(_mod_data_dir(config_path), SCREENSHOT_SUBDIR)


def prepare_output(path):
    """Create the output directory, refusing anything that already has content."""
    resolved = os.path.realpath(os.path.abspath(os.path.expanduser(path)))
    if os.path.exists(resolved):
        if not os.path.isdir(resolved):
            raise BundleError("output path is not a directory: %s" % resolved)
        if os.listdir(resolved):
            raise BundleError("output directory is not empty: %s" % resolved)
    else:
        os.makedirs(resolved)
    return resolved


def collect(config_path, limit=50):
    """Gather every section. Returns (bundle dict, list of notes)."""
    client = BridgeClient(config_path)
    token = getattr(client, "token", None)
    notes = []

    health = _collect("health", lambda: client.request("GET", "/health", auth=False), notes)
    state = _collect("state", client.get_state, notes)
    recording = _collect(
        "recording status", lambda: client.action("get_recording_save_status"), notes)
    history = _collect(
        "action history", lambda: client.action("get_action_history", limit=limit), notes)
    shots = _collect(
        "screenshot index", lambda: client.action("list_screenshots", limit=limit), notes)

    bundle = {
        "collected_at": _utc_now(),
        "bridge": {
            "port": getattr(client, "port", None),
            "config_path": os.path.abspath(config_path),
        },
        "health": health,
        "recording": recording,
        "state_summary": _state_summary(state),
        "action_history": history,
        "screenshots": shots,
        "notes": notes,
    }
    return _redact(bundle, token), notes


def _state_summary(state):
    """Keep the bundle small: only the fields a debugger actually needs."""
    if not isinstance(state, dict):
        return state
    keep = (
        "rev", "in_game", "day", "cash", "shop_state", "rent_days_left",
        "rent_value", "inventory_count", "back_counter_count",
        "front_counter_count", "client_arrived", "can_end_day", "outside",
        "location",
    )
    summary = {key: state.get(key) for key in keep if key in state}
    rent = state.get("rent")
    if isinstance(rent, dict):
        summary["rent"] = {
            key: rent.get(key)
            for key in ("due", "days_left", "can_pay", "rent_paid_total",
                        "rent_weeks_paid", "rent_weeks_missed", "receipt_error")
            if key in rent
        }
    client = state.get("client")
    if isinstance(client, dict):
        summary["client"] = {
            key: client.get(key)
            # can_report was removed from the snapshot (it called
            # BargainUIManager.Refresh()); reported is the pure field.
            for key in ("name", "intent", "is_wanted", "reported")
            if key in client
        }
    return summary


def copy_screenshots(config_path, out_dir, shots, limit):
    """Copy evidence captures into the bundle. Returns (copied, missing, note)."""
    source = _screenshot_dir(config_path)
    target = os.path.join(out_dir, SCREENSHOT_SUBDIR)
    os.makedirs(target, exist_ok=True)

    wanted = []
    if isinstance(shots, dict):
        for row in shots.get("screenshots") or []:
            if isinstance(row, dict) and row.get("path"):
                wanted.append(os.path.basename(str(row["path"])))
    if not wanted and os.path.isdir(source):
        wanted = sorted(
            (name for name in os.listdir(source) if name.lower().endswith((".jpg", ".jpeg", ".png"))),
            reverse=True,
        )[:limit]

    copied, missing = 0, []
    for name in wanted[:limit]:
        origin = os.path.join(source, name)
        if not os.path.isfile(origin):
            missing.append(name)
            continue
        shutil.copy2(origin, os.path.join(target, name))
        copied += 1

    note = None
    if not os.path.isdir(source):
        note = "no screenshot directory at %s" % source
    elif not wanted:
        note = "screenshot directory is empty"
    elif missing:
        note = "listed but not found on disk: %s" % ", ".join(missing[:5])
    return copied, missing, note


def render_readme(bundle, copied, note):
    health = bundle.get("health") or {}
    recording = bundle.get("recording") or {}
    state = bundle.get("state_summary") or {}
    lines = [
        "PawnMind debug bundle",
        "=====================",
        "",
        "collected_at : %s" % bundle.get("collected_at"),
        "config       : %s" % (bundle.get("bridge") or {}).get("config_path"),
        "",
        "-- identity --",
        "game_version : %s" % health.get("game_version"),
        "assembly_mvid: %s" % health.get("game_assembly_mvid"),
        "write_gate   : %s (%s)" % (health.get("write_gate"), health.get("write_gate_reason")),
        "latest       : %s" % health.get("latest"),
        "",
        "-- recording --",
        "mode         : %s" % recording.get("recording_mode"),
        "configured   : %s" % recording.get("configured_slot"),
        "next_free    : %s" % recording.get("next_free_slot"),
        "bound        : %s" % recording.get("bound"),
        "error        : %s" % recording.get("error"),
        "",
        "-- game --",
        "in_game      : %s" % state.get("in_game"),
        "day / cash   : %s / %s" % (state.get("day"), state.get("cash")),
        "shop_state   : %s" % state.get("shop_state"),
        "",
        "-- screenshots --",
        "copied       : %d" % copied,
        "note         : %s" % (note or "ok"),
        "",
    ]
    notes = bundle.get("notes") or []
    if notes:
        lines.append("-- collection notes --")
        lines.extend(notes)
        lines.append("")
    lines.extend([
        "This bundle contains no bearer token, no save file and no bridge.conf.",
        "Check screenshots for other players' save names before publishing.",
    ])
    return "\n".join(lines) + "\n"


def write_bundle(config_path, out_path, limit=50):
    """Full pipeline. Returns the resolved output directory."""
    # Collect first: a failed collection must not leave an empty directory behind.
    bundle, _notes = collect(config_path, limit=limit)
    if bundle["health"] is None and bundle["state_summary"] is None:
        # Nothing answered: an empty bundle would only waste the user's time.
        raise BundleError("bridge did not answer on any endpoint; is the game running?")
    out_dir = prepare_output(out_path)

    copied, _missing, note = copy_screenshots(
        config_path, out_dir, bundle.get("screenshots"), limit)
    bundle["screenshot_copy"] = {"copied": copied, "note": note}

    with open(os.path.join(out_dir, BUNDLE_JSON), "w", encoding="utf-8") as handle:
        json.dump(bundle, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")
    with open(os.path.join(out_dir, BUNDLE_README), "w", encoding="utf-8") as handle:
        handle.write(render_readme(bundle, copied, note))
    return out_dir


def main(argv=None):
    parser = argparse.ArgumentParser(description="Collect a read-only PawnMind debug bundle.")
    parser.add_argument("--config", required=True, help="path to bridge.conf")
    parser.add_argument("--out", required=True, help="new (or empty) output directory")
    parser.add_argument("--limit", type=int, default=50,
                        help="max history / screenshot rows to collect (default 50)")
    args = parser.parse_args(argv)

    if args.limit < 1:
        print("debug_bundle: --limit must be >= 1", file=sys.stderr)
        return EXIT_FATAL

    try:
        out_dir = write_bundle(args.config, args.out, limit=args.limit)
    except BundleError as error:
        print("debug_bundle: %s" % error, file=sys.stderr)
        return EXIT_FATAL
    except (BridgeError, OSError) as error:
        print("debug_bundle: %s" % error, file=sys.stderr)
        return EXIT_FATAL

    print(out_dir)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
