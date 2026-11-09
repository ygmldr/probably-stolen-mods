#!/usr/bin/env python3
"""Witness check for the instances where CP-SAT found a smaller bounding box.

The lower bound is geometric (it ignores the holes / connectivity terms of the
lexicographic objective).  For every flagged instance we re-solve the smallest
window, then check the witness directly on the full W x H grid:

    holes      == 0   (no 1-cell dead pocket, borders count as solid)
    components == 1   (the free cells are one 4-connected region)

If the witness passes, the improvement is real and legal under the objective.
If it fails we report it as unverified rather than claiming a win.
"""
import json
import os
from collections import deque

from cpsat_bound import solve_window

HERE = os.path.dirname(os.path.abspath(__file__))


def check(inst, window, sol):
    W, H = inst["w"], inst["h"]
    w, h = window
    occ = [[False] * W for _ in range(H)]
    for p in sol:
        for dy in range(p["h"]):
            for dx in range(p["w"]):
                occ[p["y"] + dy][p["x"] + dx] = True

    solid = lambda x, y: (x < 0 or y < 0 or x >= W or y >= H) or occ[y][x]
    holes = 0
    for y in range(H):
        for x in range(W):
            if occ[y][x]:
                continue
            if solid(x - 1, y) and solid(x + 1, y) and solid(x, y - 1) and solid(x, y + 1):
                holes += 1

    seen = [[False] * W for _ in range(H)]
    comps = 0
    for y in range(H):
        for x in range(W):
            if seen[y][x] or occ[y][x]:
                continue
            comps += 1
            q = deque([(x, y)])
            seen[y][x] = True
            while q:
                cx, cy = q.popleft()
                for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                    if 0 <= nx < W and 0 <= ny < H and not seen[ny][nx] and not occ[ny][nx]:
                        seen[ny][nx] = True
                        q.append((nx, ny))

    area = sum(1 for y in range(H) for x in range(W) if occ[y][x])
    xs = [x for y in range(H) for x in range(W) if occ[y][x]]
    ys = [y for y in range(H) for x in range(W) if occ[y][x]]
    bbox = (max(xs) - min(xs) + 1) * (max(ys) - min(ys) + 1)
    return {"gap": bbox - area, "holes": holes, "components": comps, "area": area}


def main():
    with open(os.path.join(HERE, "cpsat_bounds.json"), "r", encoding="utf-8") as f:
        bound = json.load(f)
    with open(os.path.join(HERE, "instances.json"), "r", encoding="utf-8") as f:
        data = json.load(f)
    by_seed = {i["seed"]: i for i in data["instances"]}

    flagged = [r for r in bound["rows"] if not r["optimalByHeuristic"]]
    print("checking %d flagged instances\n" % len(flagged))
    ok = 0
    for r in flagged:
        inst = by_seed[r["seed"]]
        pieces = [(p["w"], p["h"]) for p in inst["pieces"]]
        W, H = inst["w"], inst["h"]
        A = r["area"]
        window = tuple(r["window"])
        feasible, status, sol = solve_window(pieces, window[0], window[1], 20.0, want_solution=True)
        if not feasible:
            print("seed %2d: no witness (unexpected)" % r["seed"])
            continue
        m = check(inst, window, sol)
        legal = m["holes"] == 0 and m["components"] == 1 and m["gap"] == r["lbGap"]
        ok += legal
        print("seed %2d %2dx%-2d  window %2dx%-2d  modGap=%d  witnessGap=%d  holes=%d comps=%d  %s"
              % (r["seed"], W, H, window[0], window[1], r["modGap"], m["gap"],
                 m["holes"], m["components"], "LEGAL" if legal else "not legal"))

    print("\n%d/%d improvements verified legal under the real objective" % (ok, len(flagged)))


if __name__ == "__main__":
    main()
