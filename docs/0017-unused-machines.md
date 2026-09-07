# ADR 0017: Giving the unused machines a job

Five machines were in the data, in the tech tree, craftable, placeable — and
inert. The sifter, mixer, cutter and vacuum freezer had items and build recipes
with nothing in `recipes.json` naming them as its machine, and the spinneret was
sold from Plasma with its only recipe at Quantum. Building any of them produced
a machine that could never run.

Building (ADR 0015) exposed this rather than caused it: `Offerable` had to hide
them from the build menu so the player was not handed a trap. This gives them
jobs instead.

## The rule each one had to satisfy

Footprint is the only throughput dial (ADR 0002): a machine runs `size²`
batches and costs `size²` to build, so "faster" is not a lever. A new machine is
therefore only interesting if it offers a **better input ratio**, **earlier
access**, or **a product nothing else makes**. Anything else is a second way to
do the same thing at the same price, which is not a decision.

| Machine | Tier | Job | The decision it offers |
|---|---|---|---|
| **Sifter** | STM | 3 crushed ore → 2 purified + stone dust | purified ore without water, a tier before the washer |
| **Mixer** | VLT | alloy dusts → alloy dust | one smelt per alloy instead of one per component |
| **Cutter** | VLT | 1 metal ingot → 2 plates | the plate line is worth rebuilding |
| **Spinneret** | PLS | 1 polymer ingot → 2 plates | the same, for the engineering polymers |
| **Vacuum freezer** | PLS | 12 hydrogen → 1 deuterium | the fusion gate, cheaper and a tier earlier |

## Why these jobs and not others

**The cutter and the spinneret fill a hole that was already visible in the form
ladder.** Every shaping step doubles or quadruples — an ingot lathes into 2
rods, mills into 2 wire, a plate benders into 4 foil — and only the bender's
ingot-to-plate is one for one. Plates are the backbone of every component, so
that one-for-one was the ladder's flattest rung. A metal is cut; a polymer is
spun; both give 2.

This also fixes the spinneret honestly. Its old recipe made *aramid* from
monomers, which is a polycondensation and belongs on the polymerizer with every
other polymer. Moving it there leaves the spinneret as what its name says: the
machine that shapes a polymer, from Plasma, where the engineering polymers
start. Thermosets below that tier are not spun and are left on the bender.

**The mixer is the dust route to an alloy.** Same ratio as the alloy smelter,
but the inputs come straight off the macerator rather than being smelted to
ingots first — so bronze costs one smelt instead of three. That is the whole
reason to build one, and it is why a mixer is worth having in GregTech too.

**The vacuum freezer took the heaviest ratio in the tree.** Deuterium was 16
water in a Fusion-tier centrifuge, gating everything past fusion. Cryogenic
distillation of hydrogen is both the real industrial route and cheaper here, and
it pays for the electrolysis chain the player already has. The factory planner
now puts 13 vacuum freezers on the critical path to the goal, which is the test
that it is genuinely used rather than merely present.

**The sifter is the one the planner will not choose, and that is expected.** It
is strictly worse per crushed ore than washing and gives no byproduct. Its value
is that it needs no water: it is the only way to reach the Arc centrifuge's 3x
yield without ever laying a pipe. `factory_plan.py` optimises recipe steps and
does not model what infrastructure costs to build, so it prefers the washer.
That is a limit of the planner, not a fault in the machine — but it is worth
being explicit that one of the five is justified by a cost the tooling cannot
see.

## Tiers settle themselves

Recipes are authored at a sensible starting tier and the existing retier pass
walks them to the earliest tier where the machine exists *and* the inputs are
reachable. So the cutter's recipes spread from Voltaic to Quantum on their own,
following their materials, and the only manual change needed was widening the
cutter's tier range to Singular — metals run to the top of the ladder, so the
machine that cuts them has to as well.

Polymers are identified by what the polymerizer makes rather than by a
hand-kept list, so adding a polymer to the chemistry section does not also
require remembering to add it somewhere else.

## Two invariants, so this cannot come back

- `EveryProcessingMachine_IsUsedByAtLeastOneRecipe` — every machine that runs
  recipes is named by one. Structures are exempt: a power pole with a recipe
  would be the strange thing.
- `NoMachine_IsSoldBeforeItsFirstRecipe` — the subtler version of the same bug,
  and the one the spinneret had.

On the sim side, `EveryPlaceableMachineHasSomethingItCanMake` now asserts what
it previously could not: that `Offerable` excludes nothing. The filter stays,
because "every machine has a job" is a property of the data rather than of the
code, and it is cheaper to keep the guard than to find the next unused machine
by watching someone build one.
