# ADR 0002: Tier ladder, resource taxonomy, and tech tree

Settles three of the open questions from the project handoff and scopes the
progression that the rest of the game is built around.

## Decisions

**Eight tiers, named by power era, not voltage.** GregTech's best structural
idea is that a tier *is* a power grade: one number gates machines and recipes
together. We keep that and drop the LV/MV/HV naming, which is both derivative
and wrong for the pre-electric tiers.

| # | Code | Name | Power | Structural metal | Character |
|---|------|------|-------|------------------|-----------|
| 0 | MAN | Manual | 0 | — | Hand tools, burner furnace, copper/tin/iron |
| 1 | STM | Steam | 8 | bronze | Boilers, mechanical machines, no electronics |
| 2 | VLT | Voltaic | 32 | steel | First electricity, circuits, basic chemistry |
| 3 | ARC | Arc | 128 | aluminum | Electrolysis, plastics, ore centrifuging |
| 4 | PLS | Plasma | 512 | titanium | Kroll process, acid baths, platinum group |
| 5 | FUS | Fusion | 2048 | tungsten steel | Fusion fuel, PTFE, rare earths |
| 6 | QNT | Quantum | 8192 | iridium | Exotic matter, iridium-limited |
| 7 | SNG | Singular | 32768 | collapsium | Matter replication |

Power scales 4x per tier. Electronics begin at Voltaic — steam machines are
purely mechanical, which is why the Steam tier has hulls and pistons but no
circuits.

**Fluids are in the design from the start**, even though the fluid sim is step
5 of the build order. Ore washing and industrial chemistry are where a game in
this genre gets its mid-game substance; designing the tree without them would
mean restructuring it later. `/data` is allowed to be ahead of `/sim`. Fluids
live in `items.json` with `"form": "fluid"` rather than a separate file, so the
recipe graph stays a single namespace that the invariant tests can validate as
one graph. The sim can still store them in a separate system — `form` is just
the attribute telling it which storage layout applies.

## The ore multiplier ladder

The reason to climb the ladder is not new items, it is more output from the
same finite ore. Every ore supports the same five-step chain, and each step is
gated to a higher tier:

| Yield | Tier | Process |
|-------|------|---------|
| 1x | MAN | Smelt the raw ore |
| 2x | STM | Crush, then macerate each half |
| 2x + byproduct | VLT | Wash the crushed ore (needs water) |
| 3x + byproduct | ARC | Centrifuge the purified ore |
| 4x + byproduct | PLS | Sulfuric acid bath |
| 6x + all byproducts | SNG | Matter replication |

## Ores are deliberately non-uniform

Each ore carries up to three byproducts, released at the washing, centrifuging
and acid-bath steps respectively. This is what makes ore processing worth
revisiting, and it is how several metals are obtained at all — silver, cobalt,
gallium, palladium, cerium, osmium and **iridium** have no ore of their own.

Iridium is the intentional wall: it appears only as the second byproduct of
sperrylite, so reaching the Quantum tier means running platinum ore at volume
rather than finding a new deposit.

## Tech tree shape

Four tech lines — Metallurgy, Ore Processing, Chemistry, Fabrication — each run
the full length of the ladder, giving 32 nodes. Progression is
**component-gated, not points-gated**: a tier's nodes require physically
holding that tier's machine hull (`requires_item`), not an accumulated research
score. There is no research currency anywhere in the design.

## The invariant that makes a deep ladder survivable

> **A tier's own materials, components and machines must be producible using
> only the tier below it.**

Without this, tier N's gate metal ends up requiring a machine made of tier N's
gate metal, and the ladder silently becomes unclimbable somewhere in the middle
where nobody looks. This bit repeatedly while writing the spec: titanium was
only makeable in a titanium-hulled reactor, plastic was needed to build the
machine that makes plastic, and collapsium required a Singular reactor built
from collapsium.

It is enforced by `EveryTierGateComponent_IsBuildableWithThePreviousTiersMachines`
and `EveryRecipe_HasInputsReachableAtItsOwnTier` in `/data.tests`, which walk
the graph tier by tier from raw resources. These run on every commit. At
1,000+ recipes this class of bug is not findable by reading.

## Generated, not hand-written

`/data/spec/progression.json` is the hand-authored source: ~43 materials, 17
fluids, 8 component patterns, 26 machine categories and 22 hand-written
chemistry recipes. `tools/generate_data.py` expands it into the five
`data/*.json` files — currently **420 items and 502 recipes**.

Most of the recipe count is combinatorial (every metal x every form, every
component x every tier), so authoring it by hand would be both enormous and
impossible to rebalance. Machine, component and shaping recipes follow fixed
patterns; only genuine chemistry is written out.

The generator includes a retier pass: recipes are placed provisionally at their
material's tier, then pushed up to the earliest tier that can actually run them.
This is what stops a recipe from sitting at a tier where its inputs don't exist
yet — a silent dead recipe.

CI regenerates and diffs, so committed data can never drift from the spec.
**Edit the spec, never `data/*.json`.**

## Deliberately not decided

Recipe counts are a scoping skeleton, not final balance. Durations and power
draws are placeholder values chosen for shape, not tuned. Ore world-generation
frequency, byproduct percentages, and per-tier machine speed multipliers are
all still open, and none of them require schema changes.
