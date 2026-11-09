#!/usr/bin/env python3
"""CP-SAT lower bound on CompactSort's bboxGap for the 50 benchmark instances.

Why this measures the right thing
---------------------------------
The mod's authoritative comparator (PackResult.IsTighterThan) is lexicographic:

    1. IsolatedHoles        asc
    2. EmptyComponents      asc
    3. BBoxGap              asc
    4. ItemClusters         asc
    5. SameItemContacts     desc
    6. CategoryContacts     desc
    7. MaxEmptyRect         desc
    8. Score                asc

The dumped baseline proves terms 1 and 2 are already at their floor on all 50
instances (holes == 0, components == 1).  Since a feasible layout with holes=0
and components=1 exists, the optimum must also have holes=0 and components=1.
So the first two terms pin, and the only open question is term 3: how small can
the bounding box get?

bboxGap = bboxArea - occupiedArea, and occupiedArea is a constant for an
instance, so minimizing bboxGap == minimizing bboxArea.  With no walls in the
synthetic benchmark the container is translation invariant, so a window at any
offset is equivalent to one anchored at (0,0): we only need to know whether all
pieces fit inside some w x h window with w <= W and h <= H.

For each instance we test every window area in [A, A + bestKnownGap - 1].  If
none is feasible, the heuristic's gap is proven optimal.  Otherwise the first
feasible area is a valid lower bound on the achievable gap.

Usage:
    python3 cpsat_bound.py [--limit 50] [--time 5] [--strict]
"""
import argparse
import json
import os
import sys

from ortools.sat.python import cp_model

HERE = os.path.dirname(os.path.abspath(__file__))


def solve_window(pieces, w, h, time_limit, want_solution=False):
    """Can all pieces fit in a w x h box?  Returns (feasible, solve, xsolver)."""
    model = cp_model.CpModel()
    x_ivs, y_ivs = [], []
    x_vars, y_vars, r_vars = [], [], []

    for i, (pw, ph) in enumerate(pieces):
        if pw == ph:
            sx, sy = pw, ph
            r_vars.append(None)
        else:
            r = model.NewBoolVar("r%d" % i)
            sx = model.NewIntVar(min(pw, ph), max(pw, ph), "sx%d" % i)
            sy = model.NewIntVar(min(pw, ph), max(pw, ph), "sy%d" % i)
            model.Add(sx == pw).OnlyEnforceIf(r.Not())
            model.Add(sx == ph).OnlyEnforceIf(r)
            model.Add(sy == ph).OnlyEnforceIf(r.Not())
            model.Add(sy == pw).OnlyEnforceIf(r)
            r_vars.append(r)

        x = model.NewIntVar(0, w, "x%d" % i)
        y = model.NewIntVar(0, h, "y%d" % i)
        xe = model.NewIntVar(0, w, "xe%d" % i)
        ye = model.NewIntVar(0, h, "ye%d" % i)
        x_ivs.append(model.NewIntervalVar(x, sx, xe, "ix%d" % i))
        y_ivs.append(model.NewIntervalVar(y, sy, ye, "iy%d" % i))
        x_vars.append(x)
        y_vars.append(y)

    model.AddNoOverlap2D(x_ivs, y_ivs)

    solver = cp_model.CpSolver()
    solver.parameters.max_time_in_seconds = time_limit
    solver.parameters.num_search_workers = 8
    status = solver.Solve(model)
    feasible = status in (cp_model.OPTIMAL, cp_model.FEASIBLE)
    if feasible and want_solution:
        sol = []
        for i, (pw, ph) in enumerate(pieces):
            rot = 0 if r_vars[i] is None else solver.Value(r_vars[i])
            bw, bh = (pw, ph) if rot == 0 else (ph, pw)
            sol.append({"id": i, "x": solver.Value(x_vars[i]),
                        "y": solver.Value(y_vars[i]), "w": bw, "h": bh, "rot": rot})
        return True, status, sol
    return feasible, status, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="only first N instances")
    ap.add_argument("--time", type=float, default=5.0, help="seconds per window solve")
    ap.add_argument("--json", default=os.path.join(HERE, "cpsat_bounds.json"))
    args = ap.parse_args()

    with open(os.path.join(HERE, "instances.json"), "r", encoding="utf-8") as f:
        data = json.load(f)

    instances = data["instances"]
    if args.limit:
        instances = instances[: args.limit]

    rows = []
    for inst in instances:
        W, H = inst["w"], inst["h"]
        pieces = [(p["w"], p["h"]) for p in inst["pieces"]]
        A = sum(p["area"] for p in inst["pieces"])

        runs = inst["runs"]
        mod = next(r for r in runs if r["name"] == "mod")
        best = next(r for r in runs if r["name"] == "bestOf")
        known_gap = min(r["bboxGap"] for r in runs)

        # candidate windows, cheapest (smallest area) first
        cands = []
        for w in range(1, W + 1):
            for h in range(1, H + 1):
                area = w * h
                if area >= A and area < A + known_gap:
                    cands.append((area, w, h))
        cands.sort()

        best_area = None
        best_sol = None
        for area, w, h in cands:
            ok, status, sol = solve_window(pieces, w, h, args.time, want_solution=True)
            if ok:
                best_area = area
                best_sol = (w, h, sol)
                break

        if best_area is None:
            lb_gap = known_gap          # proven: no smaller bbox exists
            proven = True
        else:
            lb_gap = best_area - A
            proven = False

        rows.append({
            "seed": inst["seed"], "w": W, "h": H, "n": len(pieces), "area": A,
            "modGap": mod["bboxGap"], "bestGap": best["bboxGap"],
            "knownGap": known_gap, "lbGap": lb_gap, "optimalByHeuristic": proven,
            "window": None if best_sol is None else [best_sol[0], best_sol[1]],
            "candidates": len(cands),
        })
        mark = "OPT" if proven else "gap!"
        print("seed %2d %2dx%-2d n=%-2d A=%-3d  mod=%-2d best=%-2d  LB=%-2d  %s (cands %d)" % (
            inst["seed"], W, H, len(pieces), A, mod["bboxGap"], best["bboxGap"],
            lb_gap, mark, len(cands)))

    n = len(rows)
    sum_mod = sum(r["modGap"] for r in rows)
    sum_best = sum(r["bestGap"] for r in rows)
    sum_lb = sum(r["lbGap"] for r in rows)
    proven = sum(1 for r in rows if r["optimalByHeuristic"])
    perfect = sum(1 for r in rows if r["lbGap"] == 0)

    print("\n=== summary over %d instances ===" % n)
    print("heuristic (mod)  total bboxGap : %4d   avg %.2f" % (sum_mod, sum_mod / n))
    print("best of 9 algos  total bboxGap : %4d   avg %.2f" % (sum_best, sum_best / n))
    print("CP-SAT lower bnd total bboxGap : %4d   avg %.2f" % (sum_lb, sum_lb / n))
    print("proven optimal on %d/%d instances" % (proven, n))
    print("instances where a zero-gap layout exists: %d" % perfect)
    if sum_mod:
        print("headroom vs mod: %.1f%% of the gap" % (100.0 * (sum_mod - sum_lb) / sum_mod))

    with open(args.json, "w", encoding="utf-8") as f:
        json.dump({"summary": {
            "n": n, "sumMod": sum_mod, "sumBest": sum_best, "sumLb": sum_lb,
            "proven": proven, "perfect": perfect}, "rows": rows}, f, indent=1)
    print("\nwrote " + args.json)


if __name__ == "__main__":
    main()
