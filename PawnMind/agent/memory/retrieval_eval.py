"""Reproducible retrieval evaluation over the 30 annotated P2 scenarios.

The dataset is **synthetic test data** (``agent/memory/seeds/retrieval_cases.json``
and ``verified_rules.json``), not a recording.  It measures retrieval mechanics
only: scope isolation, status filtering, state gating, CJK/entity recall and
token-budget truncation.

Usage (from ``PawnMind/``)::

    python -m agent.memory.seed --memories-db /tmp/pm/memories.db \
        --db /tmp/pm/events.db
    python -m agent.memory.retrieval_eval --memories-db /tmp/pm/memories.db \
        --events-db /tmp/pm/events.db --k 5
"""

import argparse
import json
import os
import sys

from .memories import MemoryStore
from .store import MemoryUnavailable

DEFAULT_CASES_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "seeds", "retrieval_cases.json")

METRIC_SCHEMA = 2


def load_cases(path=None):
    """Load and validate the annotation file (test data only)."""
    path = path or DEFAULT_CASES_PATH
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    if not data.get("test_data"):
        raise ValueError("retrieval cases must be marked test_data=true")
    cases = data.get("cases") or []
    if not cases:
        raise ValueError("retrieval cases are empty")
    presets = data.get("scope_presets") or {}
    seen = set()
    for case in cases:
        case_id = case.get("case_id")
        if not case_id or case_id in seen:
            raise ValueError("retrieval cases need unique case_id values")
        seen.add(case_id)
        if case.get("scope") not in presets:
            raise ValueError("case %s uses an unknown scope preset %r"
                             % (case_id, case.get("scope")))
        if not isinstance(case.get("expected", []), list):
            raise ValueError("case %s expected must be a list" % case_id)
    data.setdefault("scope_presets", presets)
    data["_path"] = os.path.abspath(path)
    return data


def _case_metrics(gold, got):
    gold_set = set(gold)
    got_set = set(got)
    correct = len(gold_set & got_set)
    if gold_set:
        recall = float(correct) / len(gold_set)
    else:
        recall = 1.0 if not got else 0.0
    if got:
        precision = float(correct) / len(got)
    else:
        precision = 1.0 if not gold else 0.0
    return recall, precision


def evaluate(store, cases, k=5, scope_presets=None):
    """Run every case and return per-case rows plus macro metrics."""
    if not cases:
        raise ValueError("no retrieval cases to evaluate")
    scope_presets = scope_presets or {}
    rows = []
    warning_gold = 0
    warning_missed = 0
    for case in cases:
        scope = scope_presets.get(case.get("scope"))
        hits = store.search(
            case["query"], limit=k, scope=scope,
            state=case.get("state"),
            token_budget=case.get("token_budget"))
        got = [item["memory_id"] for item in hits]
        gold = list(case.get("expected") or [])
        recall, precision = _case_metrics(gold, got)
        passed = set(gold) == set(got)
        for memory_id in gold:
            try:
                kind = store.get_memory(memory_id).get("kind")
            except (MemoryUnavailable, KeyError):
                kind = None
            if kind == "warning":
                warning_gold += 1
                if memory_id not in got:
                    warning_missed += 1
        rows.append({
            "case_id": case.get("case_id"),
            "category": case.get("category"),
            "query": case["query"],
            "scope": case.get("scope"),
            "state": case.get("state"),
            "token_budget": case.get("token_budget"),
            "expected": gold,
            "returned": got,
            "recall": recall,
            "precision": precision,
            "passed": passed,
        })

    with_gold = [row for row in rows if row["expected"]]
    negatives = [row for row in rows if not row["expected"]]
    recall_at_k = (sum(row["recall"] for row in with_gold) / len(with_gold)
                   if with_gold else 1.0)
    precision_at_k = sum(row["precision"] for row in rows) / len(rows)
    return {
        "ok": True,
        "test_data": True,
        "metrics": METRIC_SCHEMA,
        "k": int(k),
        "cases": rows,
        "summary": {
            "total": len(rows),
            "passed": sum(1 for row in rows if row["passed"]),
            "with_gold": len(with_gold),
            "negative_cases": len(negatives),
            "negative_passed": sum(1 for row in negatives if row["passed"]),
            "recall_at_k": recall_at_k,
            "precision_at_k": precision_at_k,
            "total_gold": sum(len(row["expected"]) for row in rows),
            "total_returned": sum(len(row["returned"]) for row in rows),
            "mean_returned": (sum(len(row["returned"]) for row in rows)
                              / float(len(rows))),
            "warning_gold": warning_gold,
            "warning_missed": warning_missed,
            "warning_miss_rate": ((float(warning_missed) / warning_gold)
                                  if warning_gold else 0.0),
        },
    }


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Evaluate P2 memory retrieval on the annotated test cases")
    parser.add_argument("--memories-db", required=True,
                        help="path to the separate memories.db")
    parser.add_argument("--events-db", default=None,
                        help="path to the events memory.db (optional)")
    parser.add_argument("--cases", default=None,
                        help="override the retrieval_cases.json path")
    parser.add_argument("--k", type=int, default=5)
    parser.add_argument("--format", choices=("json", "table"), default="json")
    args = parser.parse_args(argv)

    if args.k <= 0:
        parser.error("--k must be positive")
    try:
        data = load_cases(args.cases)
    except (OSError, ValueError) as exc:
        print("cannot load cases: %s" % exc, file=sys.stderr)
        return 2
    store = None
    try:
        store = MemoryStore(args.memories_db, events_db=args.events_db)
        report = evaluate(store, data["cases"], k=args.k,
                          scope_presets=data["scope_presets"])
    except (MemoryUnavailable, ValueError) as exc:
        print("cannot evaluate: %s" % exc, file=sys.stderr)
        return 3
    finally:
        if store is not None:
            store.close()

    if args.format == "json":
        json.dump(report, sys.stdout, ensure_ascii=False, indent=2,
                  sort_keys=True)
        sys.stdout.write("\n")
        return 0
    summary = report["summary"]
    for row in report["cases"]:
        print("%s %-34s recall=%.2f precision=%.2f expected=%s got=%s"
              % ("PASS" if row["passed"] else "FAIL", row["case_id"],
                 row["recall"], row["precision"],
                 ",".join(row["expected"]) or "-",
                 ",".join(row["returned"]) or "-"))
    print("")
    print("passed=%d/%d recall@%d=%.4f precision@%d=%.4f"
          " negative=%d/%d warning_miss=%d/%d"
          % (summary["passed"], summary["total"], report["k"],
             summary["recall_at_k"], report["k"],
             summary["precision_at_k"], summary["negative_passed"],
             summary["negative_cases"], summary["warning_missed"],
             summary["warning_gold"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
