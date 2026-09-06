#!/usr/bin/env python3
"""Prints the factory needed to build a target at a given rate.

    python3 tools/factory_plan.py                       # the goal, 1 per 20000 ticks
    python3 tools/factory_plan.py steel_ingot 600       # a steel line, 1 per 600 ticks

Producers are chosen the way the game unlocks them: run every recipe to a fixed
point from raw resources and take whichever one first made each item obtainable.
That is non-circular by construction, so it never picks the ingot -> dust -> ingot
recycling loop as the way to make an ingot.
"""

import collections
import json
import math
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
D = os.path.join(ROOT, "data")


def load(name):
    return json.load(open(os.path.join(D, name)))


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else "von_neumann_seed"
    period = int(sys.argv[2]) if len(sys.argv) > 2 else 20000

    items = {i["id"]: i for i in load("items.json")}
    recipes = load("recipes.json")
    tiers = {t["id"]: t["index"] for t in load("tiers.json")}
    by_id = {r["id"]: r for r in recipes}

    if target not in items:
        sys.exit("unknown item: %s" % target)

    ordered = sorted(recipes, key=lambda r: (tiers[r["tier"]], r["id"]))
    raw = {i["id"] for i in items.values() if i["raw"]}

    reach, first, order, step = set(raw), {}, {}, 0
    grew = True
    while grew:
        grew = False
        for r in ordered:
            if all(i["item"] in reach for i in r["inputs"]):
                for o in r["outputs"]:
                    if o["item"] not in reach:
                        reach.add(o["item"])
                        first[o["item"]] = r
                        order[r["id"]] = step
                        step += 1
                        grew = True

    if target not in reach:
        sys.exit("%s is not reachable from raw resources" % target)

    needed, queue = set(), [target]
    while queue:
        item = queue.pop(0)
        if item in raw or item not in first:
            continue
        recipe = first[item]
        if recipe["id"] in needed:
            continue
        needed.add(recipe["id"])
        queue.extend(i["item"] for i in recipe["inputs"])

    plan = sorted((by_id[i] for i in needed), key=lambda r: order.get(r["id"], 1 << 30))

    # Propagate demand in cycles per tick -- never in machine counts, or every
    # recipe's one-machine minimum compounds a full duty cycle up the chain.
    demand = {target: 1.0 / period}
    counts, raw_draw = {}, collections.Counter()
    for r in reversed(plan):
        duration = max(1, r["duration_ticks"])
        cycles = max((demand.get(o["item"], 0) / o["count"] for o in r["outputs"]), default=0)
        counts[r["id"]] = max(1, math.ceil(cycles * duration))
        for i in r["inputs"]:
            demand[i["item"]] = demand.get(i["item"], 0) + cycles * i["count"]
            if i["item"] in raw:
                raw_draw[i["item"]] += cycles * i["count"]

    name = lambda i: items[i]["name"]
    print("=" * 76)
    print("FACTORY FOR %s  -- one per %d ticks (%.1f s)" % (name(target).upper(), period, period / 60))
    print("=" * 76)
    print("%d recipe steps, %d machines total\n" % (len(plan), sum(counts.values())))

    by_tier = collections.defaultdict(list)
    for r in plan:
        by_tier[r["tier"]].append(r)

    for tier in sorted(by_tier, key=lambda t: tiers[t]):
        rows = sorted(by_tier[tier], key=lambda r: -counts[r["id"]])
        print("[%s] %d steps, %d machines" % (tier, len(rows), sum(counts[r["id"]] for r in rows)))
        for r in rows[:8]:
            print("     %4d x %-26s (%s)" % (counts[r["id"]], r["id"], r["machine"]))
        if len(rows) > 8:
            print("     ... and %d more" % (len(rows) - 8))
        print()

    print("-" * 76)
    print("RAW RESOURCE DRAW (units/second)")
    print("-" * 76)
    for item, rate in raw_draw.most_common():
        print("  %-26s %9.3f /s" % (name(item), rate * 60))

    print()
    print("Heaviest steps overall:")
    for rid, n in sorted(counts.items(), key=lambda kv: -kv[1])[:8]:
        print("  %4d x %-30s %s" % (n, rid, by_id[rid]["machine"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
