# ADR 0006: Belts and pipes

Build-order step 3, plus the fluid transport that step 5 will build on.

## Belts: relative gaps make ticking O(1) per lane

A lane stores items back-to-front with the gap **ahead** of each item. Because
gaps are relative, sliding the entire lane forward is one subtraction on the
front item's gap — everything behind keeps its gap unchanged and does not need
touching. A ring buffer makes removal from the front O(1) too.

The result is that **a saturated hundred-tile belt costs the same to tick as an
empty one**. Measured, with every lane packed solid:

| Segments | Items on belts | Tick cost |
|----------|----------------|-----------|
| 1,000 × 10 tiles | 80,000 | 0.053 ms |
| 10,000 × 10 tiles | 800,000 | 0.602 ms |
| 10,000 × 50 tiles | 4,000,000 | 0.777 ms |

Five times the items for 1.3× the time: cost tracks **segments**, not items.
Four million items tick in 0.78 ms against a 16.67 ms budget.

Everything is integer, in units of 1/256 of a tile with items a quarter tile
apart. Floating point would drift over 10,000 ticks and break determinism.

## Two ways to put an item on a belt

`TryInsertBack` is the physical one: an item appears at the lane's entry, and
only fits once the previous item has travelled a full spacing away. This is what
produces backpressure, and it is what the network uses.

`TryPack` appends hard against the item ahead. Normal simulation never does this
— items become compressed by *moving*, not by being placed that way — but a
backed-up belt is a legitimate state to construct directly for setup and tests,
where feeding one item per spacing of travel would take thousands of ticks.

Keeping these separate caught a genuine subtlety: **taking the front item off a
solid belt frees space at the front, not the back.** The entry stays blocked
until the line has actually advanced into the gap. There is a test for it.

## The tick is two-phase

Every lane advances, then every hand-off happens, both in fixed order. Movement
is therefore independent of the order segments were built in, and hand-offs are
deterministic.

One knock-on effect: a freed slot propagates upstream one segment per tick. This
is invisible in play — at basic belt speed an item takes eight ticks to cross a
single item spacing — and it buys order-independence, which is worth more.

## Splitters and inserters

Splitters are **round-robin, not random**: the balance is exact and the sim stays
reproducible. A blocked output never starves the open side, because the splitter
tries each side in rotation rather than insisting on one.

Inserters have a swing cadence and a stack size, so throughput is a property of
the inserter tier rather than of how fast the belt underneath happens to run.
They bridge belt↔machine in both directions.

## Pipes are not "belts, but liquid"

The handoff already warned against modelling fluids as belts; the design goes
further and makes pipes *deliberately better* for fluids.

A connected run of pipe is **one reservoir with a throughput cap**, not a series
of per-tile volumes. Moving fluid across a hundred tiles costs exactly what
moving it across one costs. That is both the cheaper simulation — O(networks)
per tick rather than O(fluid units) — and the more truthful one, since a real
pipeline's capacity is set by bore and pumping, not by how far away the far end
is. More pipe adds volume; only pumps add flow rate.

Even the basic pipe tier moves 200 units/tick against a belt lane's 0.125
items/tick, so once a fluid is involved the answer is always to pipe it. That is
the intended consequence.

**Inflow and outflow are budgeted separately.** Sharing one budget let a
network's own inflow starve its outflow, which made the delivered rate depend on
how much pipe had been laid — the exact coupling this model exists to avoid. A
test asserts a 500-tile run delivers precisely what a 5-tile run does.

Networks carry one fluid at a time; mixing is refused rather than silently
averaged.

## Conservation is the test that matters

Item duplication and loss are the bug class that actually kills automation
games, and they are invisible until a factory has run for hours. So the suite
runs a feed belt into a splitter into two machines for 20,000 ticks and asserts
that every ore injected is accounted for — in transit, waiting in a machine,
consumed mid-cycle, or turned into exactly one plate. Fluids get the same
treatment.

## Not yet built

- **Corners.** Segments can be linked at right angles, but a real curve
  compresses the inside lane relative to the outside; this models both lanes as
  equal length.
- **Mid-belt insertion.** Inserters currently feed a lane's entry. Dropping onto
  the middle of a long belt needs a positional insert, which is a linear scan —
  acceptable given inserters are swing-limited, but not written yet.
- **Underground belts** exist as a craftable item but have no distinct
  behaviour; they are currently just linked segments.
- Belt tiers are defined (`SpeedBasic` through `SpeedTurbo`) but not yet wired to
  the tier ladder's belt recipes.
