#!/usr/bin/env python3
"""Prints a human-readable walkthrough of the progression in data/.

Balance/design review aid: shows what each tier unlocks and traces the shortest
crafting path to every tier's gate component. Run from the repo root.
"""

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
D = os.path.join(ROOT, "data")


def load(name):
    return json.load(open(os.path.join(D, name)))


def main():
    tiers = sorted(load("tiers.json"), key=lambda t: t["index"])
    items = {i["id"]: i for i in load("items.json")}
    recipes = load("recipes.json")
    index = {t["id"]: t["index"] for t in tiers}

    by_tier = {t["id"]: [] for t in tiers}
    for r in recipes:
        by_tier[r["tier"]].append(r)

    # shortest producer depth for each item, for the critical-path trace
    producer = {}
    for r in recipes:
        for o in r["outputs"]:
            producer.setdefault(o["item"], []).append(r)

    print("=" * 74)
    print("PROGRESSION LADDER")
    print("=" * 74)
    for t in tiers:
        rs = by_tier[t["id"]]
        made = {o["item"] for r in rs for o in r["outputs"]}
        cats = {}
        for i in made:
            cats[items[i]["category"]] = cats.get(items[i]["category"], 0) + 1
        metal = t["metal"] or "-"
        print("\n%-4s %-9s power=%-6d metal=%-16s recipes=%d"
              % (t["id"], t["name"], t["power"], metal, len(rs)))
        print("     unlocks: " + ", ".join("%s x%d" % (k, v) for k, v in sorted(cats.items())))

    print("\n" + "=" * 74)
    print("ORE MULTIPLIER LADDER (chalcopyrite -> copper)")
    print("=" * 74)
    for r in recipes:
        if "chalcopyrite" in r["id"] or r["id"] == "smelt_copper_dust":
            ins = ", ".join("%dx %s" % (i["count"], i["item"]) for i in r["inputs"])
            outs = ", ".join("%dx %s" % (o["count"], o["item"]) for o in r["outputs"])
            print("  [%s] %-26s %-42s -> %s" % (r["tier"], r["machine"], ins, outs))

    print("\n" + "=" * 74)
    print("CRITICAL PATH TO EACH TIER GATE")
    print("=" * 74)

    def trace(item_id, depth=0, seen=None, out=None):
        seen = seen if seen is not None else set()
        out = out if out is not None else []
        if item_id in seen or depth > 5:
            return out
        seen.add(item_id)
        if items.get(item_id, {}).get("raw"):
            out.append("  " * depth + "%s (raw)" % item_id)
            return out
        # Prefer producers that don't feed on something already on this path --
        # ingot and dust recycle into each other, and following that loop hides
        # the real chain. Fall back to the loop producer rather than claiming none.
        all_cands = producer.get(item_id, [])
        cands = [c for c in all_cands if not any(i["item"] in seen for i in c["inputs"])]
        note = ""
        if not cands:
            if not all_cands:
                out.append("  " * depth + "%s (NO PRODUCER)" % item_id)
                return out
            cands, note = all_cands, "  [recycles from this chain]"
        # Among equal-tier options prefer the one that doesn't rebuild the item
        # from another form of itself, so alloying beats re-smelting its own dust.
        stem = item_id.rsplit("_", 1)[0]
        r = min(cands, key=lambda x: (index[x["tier"]],
                                      sum(1 for i in x["inputs"] if i["item"].startswith(stem))))
        out.append("  " * depth + "%s  <- [%s %s] %s%s" % (item_id, r["tier"], r["machine"], r["id"], note))
        if note:
            return out
        for i in r["inputs"]:
            trace(i["item"], depth + 1, seen, out)
        return out

    for t in tiers[1:]:
        hull = "%s_machine_hull" % t["id"].lower()
        print("\n%s (%s):" % (t["id"], t["name"]))
        for line in trace(hull)[:14]:
            print("  " + line)

    print("\n" + "=" * 74)
    print("TOTALS: %d tiers, %d items, %d recipes" % (len(tiers), len(items), len(recipes)))
    print("=" * 74)
    return 0


if __name__ == "__main__":
    sys.exit(main())
