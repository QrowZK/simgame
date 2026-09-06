# ADR 0004: Petrochemicals, polymers, and the goal item

Replaces the placeholder `plastic` / `rubber` / `silicone` / `ptfe` materials with
a real petrochemical network, and makes finishing the game require all of it.

## Polymers follow their own history

The order in which polymers were actually invented maps almost exactly onto a
tier ladder, so realism buys progression for free:

| Tier | Insulator | Real date | Why it lands there |
|------|-----------|-----------|--------------------|
| VLT | Bakelite | 1907 | The first synthetic plastic, and an electrical insulator by trade. Reached from **coal**, not oil — gasify coal, take the tar, distil phenol, react with formaldehyde from methanol. Entirely pre-petroleum, which is what makes it available before the refinery exists. |
| ARC | PVC | 1930s | The classic cable insulation. First polymer that needs cracked ethylene. |
| PLS | Polyethylene | 1950s | Ziegler-Natta era. |
| FUS | PTFE | 1938 | High-temperature. Fluorspar → HF → chloroform → R-22 → TFE. |
| QNT | PEEK | 1980s | Extreme service. Hydroquinone + difluorobenzophenone. |

The full set is 16 polymers, each by its real route: bakelite, PVC, polyethylene,
polypropylene, polystyrene, SBR, PET, ABS, nylon 6,6, polycarbonate, PTFE, epoxy,
polyurethane, silicone, aramid, PEEK.

## The refinery is a real refinery

```
crude oil ──atmospheric distillation──▶ refinery gas · naphtha · kerosene · diesel · residue
                                        │
  residue ──vacuum distillation──▶ VGO · vacuum residue
  naphtha ──hydrotreater──▶ treated naphtha + H₂S ──Claus──▶ sulfur
  treated naphtha ──steam cracker──▶ ethylene · propylene · butadiene · pygas
  treated naphtha ──cat reformer──▶ benzene · toluene · xylene
  VGO ──FCC──▶ propylene · butylene · slurry
  vacuum residue ──coker──▶ petroleum coke ──▶ synthetic graphite
```

Hydrotreating produces H₂S, which the Claus unit turns into elemental sulfur —
so sulfur has a second, industrial source alongside its ore, exactly as in life.

**Every stream has a sink**, enforced by a test. Diesel and FCC slurry become
carbon black (which SBR needs for compounding); butylene is alkylated back into
kerosene; ethane is cracked for more ethylene; petroleum coke is graphitised.
A refinery cut nothing consumes is dead weight in the network, and CI now says so.

## Air is an ambient resource

Bakelite needs formaldehyde, which needs oxygen, which previously arrived only
with the Voltaic electrolyzer — one tier too late for the Voltaic insulator.
Air separation is an 1895 process, so `air` is now a free raw fluid and the
Steam tier separates it into oxygen and nitrogen. This is what unblocks the
whole coal-chemistry branch.

## The goal item

**Von Neumann Seed** — a self-replicating factory probe. The endgame of an
automation game is a machine that builds factories.

It is assembled from five sub-assemblies, and the split exists to force
coverage rather than to add steps:

| Assembly | Pulls in |
|----------|----------|
| Pressure Hull | aramid, epoxy, collapsium, synthetic graphite |
| Fabrication Core | PEEK, polycarbonate, silicone, Singular circuits |
| Life Support Loop | PET, nylon, polyurethane, PTFE, polypropylene |
| Cable Harness | PVC, polyethylene, bakelite, SBR |
| Propellant Tank | kerosene (RP-1), ABS, polystyrene, titanium |

Between them the five assemblies consume **all 16 polymer families**, and the
goal's transitive closure covers **all 47 petrochemical streams** — 184 of 548
items, eight levels deep.

That is the requirement stated as a graph property, so it is tested rather than
trusted. `GoalInvariantTests` asserts the goal requires every polymer family and
every petrochem-tagged item; adding a polymer without wiring it into an assembly
fails CI. Items now carry `tags`, which is how the test finds them.

## What this changed elsewhere

Recipes went from 502 to 621 and items from 420 to 548. The Arc and Plasma tiers
gained real chemistry (2 and 3 recipes before, 16 and 24 now), which was the
thinnest part of the ladder. Quantum and Singular chemistry are still light —
that gap is real and not yet addressed.

Balance values remain placeholders.
