#!/usr/bin/env python3
"""Expands data/spec/progression.json into the generated data/*.json files.

Run from the repo root:  python3 tools/generate_data.py
CI regenerates and diffs the result, so the committed output must always match.

The one rule that shapes most of this: a tier's own materials, components and
machines are produced at the tier BELOW it. You build Voltaic machines with
Steam-tier tools. Without that, every tier's gate metal would require a machine
made of that same metal.
"""

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPEC = os.path.join(ROOT, "data", "spec", "progression.json")
OUT = os.path.join(ROOT, "data")

# form -> (display suffix, machine, input form, in count, out count, ticks)
FORM_RULES = {
    "plate":     ("Plate",     "bender",    "ingot", 1, 1, 60),
    "rod":       ("Rod",       "lathe",     "ingot", 1, 2, 60),
    "wire":      ("Wire",      "wire_mill", "ingot", 1, 2, 60),
    "fine_wire": ("Fine Wire", "wire_mill", "wire",  1, 4, 80),
    "foil":      ("Foil",      "bender",    "plate", 1, 4, 80),
}
DUST_NAME = "Dust"
INGOT_NAME = "Ingot"
GEAR_TICKS = 120


def main():
    spec = json.load(open(SPEC))
    tiers = spec["tiers"]
    by_index = {t["id"]: t["index"] for t in tiers}
    tier_at = {t["index"]: t for t in tiers}
    mats = {m["id"]: m for m in spec["materials"]}

    items, recipes, machines, techs = [], [], [], []
    seen_items = set()

    # Which metals actually exist at the Manual tier, where there are no shaping
    # machines yet and plates must be hammered out by hand. Directly-smeltable
    # ores seed the set; alloys join it once all their inputs are in it. Anything
    # else (byproduct-only metals like silver) is shaped by machine instead.
    man_metals = {m["id"] for m in spec["materials"]
                  if m.get("ore") and m.get("direct_smelt", True)
                  and by_index[m["tier"]] <= 1}
    for _ in range(4):
        for m in spec["materials"]:
            if m.get("alloy") and by_index[m["tier"]] <= 1 and m["id"] not in man_metals:
                if all(i["item"].rsplit("_", 1)[0] in man_metals for i in m["alloy"]["inputs"]):
                    man_metals.add(m["id"])

    def add_item(iid, name, category, tier, raw=False, form="solid", tags=None):
        if iid in seen_items:
            return
        seen_items.add(iid)
        items.append({"id": iid, "name": name, "category": category,
                      "tier": tier, "form": form, "raw": raw,
                      "tags": tags or []})

    def prod_tier(tier_id):
        """The tier that must be able to produce tier_id's own materials."""
        return tier_at[max(0, by_index[tier_id] - 1)]

    def add_recipe(rid, machine, tier_id, ticks, ins, outs, tech_line, power_mult=1):
        # tier is provisional here; retier() below moves each recipe to the
        # earliest tier that can actually run it. power_draw and unlocked_by are
        # derived from the final tier in finalize().
        recipes.append({
            "id": rid,
            "machine": machine,
            "tier": tier_id,
            "duration_ticks": ticks,
            "inputs": ins,
            "outputs": outs,
            "_tech_line": tech_line,
            "_power_mult": power_mult,
        })

    # ---- tech nodes: four lines running the length of the ladder -------------
    for line in spec["tech_lines"]:
        for t in tiers:
            prev = tier_at.get(t["index"] - 1)
            requires = []
            if prev:
                requires.append("tech_%s_%s" % (prev["id"].lower(), line["id"]))
            if line["id"] != "fabrication" and prev:
                requires.append("tech_%s_fabrication" % prev["id"].lower())
            techs.append({
                "id": "tech_%s_%s" % (t["id"].lower(), line["id"]),
                "name": "%s %s" % (t["name"], line["name"]),
                "tier": t["id"],
                "line": line["id"],
                "requires": requires,
                # Component-gated, not points-gated: you must physically hold the
                # tier's hull before its tech line opens.
                "requires_item": ("%s_machine_hull" % t["id"].lower()) if t["index"] >= 1 else None,
            })

    # ---- fluids and hand-written extra items --------------------------------
    for f in spec["fluids"]:
        add_item(f["id"], f["name"], "fluid", f["tier"], raw=f.get("raw", False), form="fluid",
                 tags=["petrochem"] if f.get("petrochem") else [])
    for e in spec["extra_items"]:
        add_item(e["id"], e["name"], e.get("category", "intermediate"), e["tier"])

    # ---- materials: ore chain, forms, alloys --------------------------------
    for m in spec["materials"]:
        mid, mname, mtier = m["id"], m["name"], m["tier"]
        forms = m["forms"]
        pt = prod_tier(mtier)["id"]
        direct = m.get("direct_smelt", True)

        labels = m.get("form_labels", {})
        tags = ["polymer"] if m.get("polymer") else []

        def form_item(form, default_label):
            add_item("%s_%s" % (mid, form), "%s %s" % (mname, labels.get(form, default_label)),
                     form, mtier, tags=tags)

        for form in forms:
            if form == "dust":
                form_item("dust", DUST_NAME)
            elif form == "ingot":
                form_item("ingot", INGOT_NAME)
            elif form == "gear":
                form_item("gear", "Gear")
            else:
                form_item(form, FORM_RULES[form][0])

        ore = m.get("ore")
        if ore:
            oid = ore["id"]
            add_item(oid, ore["name"], "raw_ore", mtier, raw=True)
            add_item("%s_crushed" % oid, "Crushed %s" % ore["name"], "crushed_ore", mtier)
            add_item("%s_purified" % oid, "Purified %s" % ore["name"], "purified_ore", mtier)
            bp = ore["byproducts"]

            # 1x — smelt the raw ore directly (Manual tier)
            if direct:
                add_recipe("smelt_%s" % oid, "furnace", pt, m.get("smelt_ticks", 192),
                           [{"item": oid, "count": 1}],
                           [{"item": "%s_ingot" % mid, "count": 1}], "metallurgy")
            # 2x — crush, then macerate each half to dust
            add_recipe("crush_%s" % oid, "crusher", "STM", 100,
                       [{"item": oid, "count": 1}],
                       [{"item": "%s_crushed" % oid, "count": 2}], "processing")
            add_recipe("macerate_%s" % oid, "macerator", "STM", 80,
                       [{"item": "%s_crushed" % oid, "count": 1}],
                       [{"item": "%s_dust" % mid, "count": 1}], "processing")
            # 2.5x — wash for the first byproduct
            wash_out = [{"item": "%s_purified" % oid, "count": 1},
                        {"item": "stone_dust", "count": 1}]
            if len(bp) > 0:
                wash_out.append({"item": "%s_dust" % bp[0], "count": 1})
            add_recipe("wash_%s" % oid, "washer", "VLT", 120,
                       [{"item": "%s_crushed" % oid, "count": 1}, {"item": "water", "count": 1}],
                       wash_out, "processing")
            # 3x — centrifuge for the second byproduct
            cent_out = [{"item": "%s_dust" % mid, "count": 3}]
            if len(bp) > 1:
                cent_out.append({"item": "%s_dust" % bp[1], "count": 1})
            add_recipe("centrifuge_%s" % oid, "centrifuge", "ARC", 200,
                       [{"item": "%s_purified" % oid, "count": 2}],
                       cent_out, "processing")
            # 4x — acid bath for the third byproduct
            bath_out = [{"item": "%s_dust" % mid, "count": 4}]
            if len(bp) > 2:
                bath_out.append({"item": "%s_dust" % bp[2], "count": 1})
            add_recipe("chembath_%s" % oid, "chemical_bath", "PLS", 240,
                       [{"item": oid, "count": 1}, {"item": "sulfuric_acid", "count": 1}],
                       bath_out, "processing")
            # 6x — endgame replication, every byproduct at once. This is what the
            # Singular tier is for; without it the top of the ladder gates nothing.
            rep_out = [{"item": "%s_dust" % mid, "count": 6}]
            for b in bp:
                rep_out.append({"item": "%s_dust" % b, "count": 1})
            add_recipe("replicate_%s" % oid, "replicator", "SNG", 300,
                       [{"item": oid, "count": 1}, {"item": "helium3", "count": 1}],
                       rep_out, "processing")

        # dust -> ingot
        if direct and "dust" in forms and "ingot" in forms:
            add_recipe("smelt_%s_dust" % mid, "furnace", pt, m.get("smelt_ticks", 192),
                       [{"item": "%s_dust" % mid, "count": 1}],
                       [{"item": "%s_ingot" % mid, "count": 1}], "metallurgy")

        # ingot -> dust. Recycling, and the only source of dust for alloys and
        # synthetics, which have no ore to macerate.
        if "dust" in forms and "ingot" in forms:
            add_recipe("macerate_%s_ingot" % mid, "macerator", "STM", 80,
                       [{"item": "%s_ingot" % mid, "count": 1}],
                       [{"item": "%s_dust" % mid, "count": 1}], "processing")

        # alloys
        alloy = m.get("alloy")
        if alloy:
            add_recipe("alloy_%s" % mid, "alloy_smelter", pt, alloy["ticks"],
                       alloy["inputs"],
                       [{"item": "%s_ingot" % mid, "count": alloy["count"]}], "metallurgy")

        # shaping. At the Manual tier there are no machines yet, so these are
        # hand recipes at half yield -- enough to bootstrap the first bronze.
        for form in forms:
            if form in FORM_RULES:
                label, mach, src, ic, oc, ticks = FORM_RULES[form]
                if src not in forms:
                    continue
                if pt == "MAN" and mid in man_metals:
                    mach, oc, ticks = "manual_crafting", max(1, oc // 2), ticks * 4
                add_recipe("form_%s_%s" % (mid, form), mach, pt, ticks,
                           [{"item": "%s_%s" % (mid, src), "count": ic}],
                           [{"item": "%s_%s" % (mid, form), "count": oc}], "metallurgy")
        if "gear" in forms and "plate" in forms and "rod" in forms:
            mach = "manual_crafting" if (pt == "MAN" and mid in man_metals) else "assembler"
            add_recipe("form_%s_gear" % mid, mach, pt, GEAR_TICKS,
                       [{"item": "%s_plate" % mid, "count": 4}, {"item": "%s_rod" % mid, "count": 1}],
                       [{"item": "%s_gear" % mid, "count": 1}], "metallurgy")

    # ---- per-tier components ------------------------------------------------
    def resolve(pattern, t):
        """{metal}_plate -> steel_plate, or None if the tier has no such metal."""
        for key in ("metal", "insulator", "wire_metal", "magnet_metal"):
            token = "{%s}" % key
            if token in pattern:
                if not t[key]:
                    return None
                return pattern.replace(token, t[key])
        return pattern

    for t in tiers:
        if t["index"] == 0:
            continue
        lo = t["id"].lower()
        pt = prod_tier(t["id"])["id"]
        mach = "manual_crafting" if pt == "MAN" else "assembler"
        for c in spec["components"]:
            if c["electronics"] and not t["electronics"]:
                continue
            cid = "%s_%s" % (lo, c["id"])
            add_item(cid, "%s %s" % (t["name"], c["name"]), "component", t["id"])
            ins = []
            for spec_in in c["inputs"]:
                if "component" in spec_in:
                    ins.append({"item": "%s_%s" % (lo, spec_in["component"]), "count": spec_in["count"]})
                elif "item" in spec_in:
                    ins.append({"item": spec_in["item"], "count": spec_in["count"]})
                else:
                    resolved = resolve(spec_in["pattern"], t)
                    if resolved:
                        ins.append({"item": resolved, "count": spec_in["count"]})
            add_recipe("comp_%s" % cid, mach, pt, c["ticks"], ins,
                       [{"item": cid, "count": c["count"]}], "fabrication")

    # ---- machines -----------------------------------------------------------
    for mach_spec in spec["machines"]:
        machines.append({
            "id": mach_spec["id"],
            "name": mach_spec["name"],
            "tiers": mach_spec["tiers"],
            "min_tier": mach_spec["tiers"][0],
        })
        for tier_id in mach_spec["tiers"]:
            t = tier_at[by_index[tier_id]]
            lo = tier_id.lower()
            item_id = "%s_%s" % (lo, mach_spec["id"])
            starting = bool(mach_spec.get("starting"))
            add_item(item_id, "%s %s" % (t["name"], mach_spec["name"]), "machine", tier_id,
                     raw=starting)
            if starting:
                continue  # the player's own hands; granted, never crafted
            pt = prod_tier(tier_id)["id"]
            builder = "manual_crafting" if pt == "MAN" else "assembler"
            if tier_id == "MAN":
                # Bootstrap: the first furnace and crucible are piled-up stone.
                ins = [{"item": "stone_deposit", "count": 12}]
            else:
                ins = [{"item": "%s_machine_hull" % lo, "count": 1}]
                if t["electronics"]:
                    ins.append({"item": "%s_circuit" % lo, "count": 2})
                ins.append({"item": "%s_cable" % lo, "count": 4})
                for part in mach_spec["parts"]:
                    ins.append({"item": "%s_%s" % (lo, part["component"]), "count": part["count"]})
            add_recipe("build_%s" % item_id, builder, pt, 400, ins,
                       [{"item": item_id, "count": 1}], "fabrication")

    # ---- the goal ----------------------------------------------------------
    goal = spec.get("goal")
    if goal:
        for a in goal["assemblies"]:
            add_item(a["id"], a["name"], "assembly", a["tier"], tags=["goal"])
            add_recipe("build_%s" % a["id"], "assembler", a["tier"], a["ticks"],
                       [{"item": i, "count": c} for i, c in a["inputs"]],
                       [{"item": a["id"], "count": 1}], "fabrication")
        add_item(goal["id"], goal["name"], "goal", goal["tier"], tags=["goal"])
        add_recipe("build_%s" % goal["id"], goal["machine"], goal["tier"], goal["ticks"],
                   [{"item": i, "count": c} for i, c in goal["inputs"]],
                   [{"item": goal["id"], "count": 1}], "fabrication",
                   goal.get("power_multiplier", 1))

    # ---- hand-written chemistry --------------------------------------------
    for c in spec["chemistry"]:
        add_recipe(c["id"], c["machine"], c["tier"], c["ticks"],
                   c["inputs"], c["outputs"], c["tech"], c.get("power_multiplier", 1))

    # ---- retier: move each recipe to the earliest tier that can run it ------
    # A recipe is provisionally placed at the tier its *material* belongs to, but
    # its inputs may not exist that early (an alloy dust needs a macerator; a
    # byproduct-only metal needs an ore washer). Sitting at an unreachable tier
    # would make the recipe silently dead, so walk the graph to a fixed point and
    # push each recipe up to where it genuinely becomes runnable.
    machine_tiers = {m["id"]: set(m["tiers"]) for m in spec["machines"]}
    order = [t["id"] for t in sorted(tiers, key=lambda x: x["index"])]
    raw_items = {i["id"] for i in items if i["raw"]}

    def snapshots():
        reach = set(raw_items)
        out = {}
        for ti, tid in enumerate(order):
            usable = [r for r in recipes if by_index[r["tier"]] <= ti]
            while True:
                grew = False
                for r in usable:
                    if all(i["item"] in reach for i in r["inputs"]):
                        for o in r["outputs"]:
                            if o["item"] not in reach:
                                reach.add(o["item"])
                                grew = True
                if not grew:
                    break
            out[tid] = set(reach)
        return out

    for _ in range(len(order) + 2):
        snap = snapshots()
        bumped = False
        for r in recipes:
            cur = by_index[r["tier"]]
            for ti in range(cur, len(order)):
                tid = order[ti]
                if tid not in machine_tiers[r["machine"]]:
                    continue
                if all(i["item"] in snap[tid] for i in r["inputs"]):
                    if ti != cur:
                        r["tier"] = tid
                        bumped = True
                    break
        if not bumped:
            break

    # ---- finalize: derive power and tech gating from the settled tier -------
    for r in recipes:
        tier_id = r["tier"]
        r["power_draw"] = tier_at[by_index[tier_id]]["power"] * r.pop("_power_mult")
        r["unlocked_by"] = "tech_%s_%s" % (tier_id.lower(), r.pop("_tech_line"))

    key_order = ["id", "machine", "tier", "duration_ticks", "power_draw",
                 "inputs", "outputs", "unlocked_by"]
    recipes[:] = [{k: r[k] for k in key_order} for r in recipes]

    out_tiers = [{k: v for k, v in t.items()} for t in tiers]

    def write(name, payload):
        path = os.path.join(OUT, name)
        with open(path, "w") as fh:
            json.dump(payload, fh, indent=2)
            fh.write("\n")
        return path

    write("tiers.json", out_tiers)
    write("items.json", items)
    write("machines.json", machines)
    write("recipes.json", recipes)
    write("techs.json", techs)

    print("tiers=%d items=%d machines=%d recipes=%d techs=%d"
          % (len(out_tiers), len(items), len(machines), len(recipes), len(techs)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
