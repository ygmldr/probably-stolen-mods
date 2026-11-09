#!/usr/bin/env python3
"""Independent re-computation of the CompactSort objective metrics from dumped
placements, used to validate that the Python understanding matches the C# code.

Usage: python3 evaluate.py            # verify dumped metrics
"""
import json
import sys
from collections import deque
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def load(path=None):
    path = path or os.path.join(HERE, "instances.json")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def occupancy(inst, run):
    """Return W x H boolean grid for a run's placements."""
    W, H = inst["w"], inst["h"]
    occ = [[False] * W for _ in range(H)]
    for p in run["placements"]:
        x0, y0, w, h = p["x"], p["y"], p["w"], p["h"]
        for dy in range(h):
            for dx in range(w):
                occ[y0 + dy][x0 + dx] = True
    return occ


def solid(occ, W, H, x, y):
    # out of bounds counts as solid, exactly like PackGrid.IsWall
    if x < 0 or y < 0 or x >= W or y >= H:
        return True
    return occ[y][x]


def metrics(inst, run):
    W, H = inst["w"], inst["h"]
    occ = occupancy(inst, run)
    area = sum(1 for y in range(H) for x in range(W) if occ[y][x])

    # bbox gap
    xs = [x for y in range(H) for x in range(W) if occ[y][x]]
    ys = [y for y in range(H) for x in range(W) if occ[y][x]]
    if xs:
        bbox = (max(xs) - min(xs) + 1) * (max(ys) - min(ys) + 1)
        gap = max(0, bbox - area)
    else:
        gap = 0

    # isolated 1-cell holes
    holes = 0
    for y in range(H):
        for x in range(W):
            if occ[y][x]:
                continue
            if (solid(occ, W, H, x - 1, y) and solid(occ, W, H, x + 1, y)
                    and solid(occ, W, H, x, y - 1) and solid(occ, W, H, x, y + 1)):
                holes += 1

    # 4-connected empty components
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
    return {"area": area, "bboxGap": gap, "holes": holes, "components": comps}


def verify(data):
    bad = 0
    total = 0
    for inst in data["instances"]:
        for run in inst["runs"]:
            if not run["placements"]:
                continue
            total += 1
            m = metrics(inst, run)
            for k in ("bboxGap", "holes", "components"):
                if m[k] != run[k]:
                    bad += 1
                    print("MISMATCH seed=%d run=%s %s: py=%d cs=%d" %
                          (inst["seed"], run["name"], k, m[k], run[k]))
                    break
    print("verified %d runs, %d mismatches" % (total, bad))
    return bad


def summary(data):
    ins = data["instances"]
    def runs(name):
        return [(i, r) for i in ins for r in i["runs"] if r["name"] == name]
    print("\n%-10s %8s %8s %8s %8s %8s" % ("run", "avgGap", "sumGap", "holes", "comps", "avgMs"))
    for name in ["mod", "bestOf"]:
        rs = runs(name)
        if not rs:
            continue
        n = len(rs)
        print("%-10s %8.2f %8d %8d %8d %8.1f" % (
            name,
            sum(r["bboxGap"] for _, r in rs) / n,
            sum(r["bboxGap"] for _, r in rs),
            sum(r["holes"] for _, r in rs),
            sum(r["components"] for _, r in rs),
            sum(r["ms"] for _, r in rs) / n))

    # how often is the mod's layout already the best of the whole zoo?
    same = 0
    deltas = []
    for i in ins:
        mod = next((r for r in i["runs"] if r["name"] == "mod"), None)
        best = next((r for r in i["runs"] if r["name"] == "bestOf"), None)
        if mod and best:
            if mod["bboxGap"] == best["bboxGap"]:
                same += 1
            deltas.append(best["bboxGap"] - mod["bboxGap"])
    print("\nmod gap == bestOf gap on %d/%d instances" % (same, len(ins)))
    print("bestOf - mod bboxGap total delta = %d cells across 50 containers"
          % sum(deltas))


if __name__ == "__main__":
    d = load()
    verify(d)
    summary(d)
