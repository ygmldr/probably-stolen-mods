"""Read-only CLI over the P1 event store (``memory_history`` in CLI form).

Usage (from ``PawnMind/``)::

    python -m agent.memory.cli --db play/memory/memory.db status
    python -m agent.memory.cli --db play/memory/memory.db timelines
    python -m agent.memory.cli --db play/memory/memory.db history --limit 20
    python -m agent.memory.cli --db play/memory/memory.db history --type intent
    python -m agent.memory.cli --db play/memory/memory.db reconcile

P1 deliberately exposes no MCP tool: the memory MCP surface arrives with P2.
The same functions back that tool later.
"""

import argparse
import json
import sys

from .store import EventStore, MemoryUnavailable

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_UNAVAILABLE = 3


SENSITIVE_KEYS = ("token", "authorization", "password", "secret", "api_key",
                  "apikey", "access_token", "refresh_token")


def _redact(value):
    """Strip bridge credentials from anything about to be printed."""
    if isinstance(value, dict):
        return {key: ("[redacted]" if str(key).lower() in SENSITIVE_KEYS
                      else _redact(item))
                for key, item in value.items()}
    if isinstance(value, list):
        return [_redact(item) for item in value]
    return value


def _row_for_output(row):
    out = dict(row)
    for key in ("args_json", "result_json", "delta_json"):
        raw = out.get(key)
        if raw:
            try:
                out[key[:-5]] = json.loads(raw)
            except ValueError:
                out[key[:-5]] = raw
        out.pop(key, None)
    for key in ("args", "result", "delta"):
        if key in out:
            out[key] = _redact(out[key])
    return out


def cmd_timelines(store, args):
    conn = store._require()
    rows = conn.execute(
        "SELECT * FROM timelines ORDER BY created_at ASC").fetchall()
    payload = []
    for row in rows:
        item = dict(row)
        item["events"] = store.count_events(item["timeline_id"])
        item["snapshots"] = store.count_snapshots(item["timeline_id"])
        payload.append(item)
    _emit(payload, args)
    return EXIT_OK


def cmd_history(store, args):
    rows = store.history(
        args.timeline,
        limit=args.limit,
        event_types=[args.type] if args.type else None,
        since_seq=args.since_seq)
    _emit([_row_for_output(row) for row in rows], args)
    return EXIT_OK


def cmd_status(store, args):
    pending = store.pending_events(args.timeline)
    payload = {
        "db": store.path,
        "schema_version": store.schema_version(),
        "timelines": len(store._require().execute(
            "SELECT timeline_id FROM timelines").fetchall()),
        "events": store.count_events(args.timeline),
        "pending": len(pending),
        "pending_actions": [
            {"tool": row["tool_name"], "action_id": row["action_id"],
             "seq": row["seq"]}
            for row in pending
        ],
    }
    _emit(payload, args)
    return EXIT_OK


def cmd_reconcile(store, args):
    recovered = store.reconcile_pending(args.timeline)
    _emit({"reconciled": recovered}, args)
    return EXIT_OK


def _emit(payload, args):
    if args.format == "json":
        json.dump(payload, sys.stdout, ensure_ascii=False, indent=2)
        sys.stdout.write("\n")
        return
    if isinstance(payload, list):
        if not payload:
            print("(no rows)")
            return
        for row in payload:
            print(" ".join("%s=%s" % (k, row[k]) for k in sorted(row)
                           if row[k] is not None))
        return
    for key in sorted(payload):
        print("%s: %s" % (key, payload[key]))


def build_parser():
    parser = argparse.ArgumentParser(description="PawnMind interaction history")
    parser.add_argument("--db", required=True, help="path to memory.db")
    parser.add_argument("--format", choices=("table", "json"), default="table")

    sub = parser.add_subparsers(dest="command", required=True)

    timelines = sub.add_parser("timelines", help="list history branches")
    timelines.add_argument("--format", choices=("table", "json"),
                           default=argparse.SUPPRESS)
    timelines.set_defaults(func=cmd_timelines)

    history = sub.add_parser("history", help="print the event timeline")
    history.add_argument("--timeline", default=None)
    history.add_argument("--limit", type=int, default=50)
    history.add_argument("--since-seq", type=int, default=0)
    history.add_argument("--type", default=None,
                         choices=("intent", "result", "observation",
                                  "external_state_change", "sleep_completed",
                                  "sleep_unresolved"))
    history.add_argument("--format", choices=("table", "json"),
                         default=argparse.SUPPRESS)
    history.set_defaults(func=cmd_history)

    status = sub.add_parser("status", help="counts and pending actions")
    status.add_argument("--timeline", default=None)
    status.add_argument("--format", choices=("table", "json"),
                        default=argparse.SUPPRESS)
    status.set_defaults(func=cmd_status)

    reconcile = sub.add_parser(
        "reconcile", help="mark pending actions as unknown (never re-dispatches)")
    reconcile.add_argument("--timeline", default=None)
    reconcile.add_argument("--format", choices=("table", "json"),
                           default=argparse.SUPPRESS)
    reconcile.set_defaults(func=cmd_reconcile)

    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if getattr(args, "format", None) is None:
        args.format = "table"
    try:
        store = EventStore(args.db)
    except MemoryUnavailable as exc:
        print("memory unavailable: %s" % exc, file=sys.stderr)
        return EXIT_UNAVAILABLE
    try:
        return args.func(store, args)
    except MemoryUnavailable as exc:
        print("memory unavailable: %s" % exc, file=sys.stderr)
        return EXIT_UNAVAILABLE
    finally:
        store.close()


if __name__ == "__main__":
    sys.exit(main())
