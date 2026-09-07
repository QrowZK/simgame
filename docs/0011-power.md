# ADR 0011: Power

Generators, poles, and a per-machine draw. The draw itself was already in
`/data` — every recipe has carried a `power_draw` since the progression was
generated — so this is mostly about making the sim honour a number that already
existed.

## Zero is not a special case

Manual-tier recipes have `power_draw` 0, and a machine with no draw runs with no
grid at all. That is not a convenience switch: the Manual tier genuinely is hand
tools, and it is what keeps a new game playable before the first generator
exists. It also means every test written before power still passes unchanged,
because they were all describing unpowered machines and still are.

## A brownout, not a cull

When a network cannot meet its demand, **everything on it runs proportionally
slower**. The alternative — run some machines at full speed and stop the rest —
needs a rule for which ones lose, and every such rule is arbitrary. Index order
would mean the same machines always lose, for a reason invisible on screen.

The split is exact integer arithmetic, no floats. Each consumer takes the floor
of its running share and the remainder carries to the next, so the shares sum to
exactly the supply — no drift, and no dependence on floating-point ordering that
would break determinism.

Machines hold an energy buffer and spend one tick's draw per tick of work. A
machine on a half-fed network therefore fills its buffer over two ticks and runs
every other one, which is a 50% slowdown that arrives without any special
"slow mode" in the machine at all.

**The buffer is deliberately uncapped.** Capping it at one tick's draw looks
obviously safe and quietly destroys power: a machine drawing 7 that receives 6 a
tick can never reach 7 if the buffer is clipped to 7 every tick, so it runs at
half speed on six-sevenths of the energy and the rest vanishes. That bug was
real, and the conservation test — total work done against total energy
generated — is what caught it. The buffer cannot run away either: it only grows
while the machine cannot afford a tick, and the allocator never hands out more
than one tick's draw, so it stays below twice the draw by construction.

## Holding a cycle

A machine that loses power mid-cycle **keeps its progress and its inputs**.

This forced a real change. `Machine.Tick` used to decide whether to start a
cycle from its *state*; an unpowered machine is not `Working`, so it would have
re-entered the start path every tick and consumed the recipe's inputs again and
again. The cycle is now carried by the timer, not the state, and the start path
runs only when no cycle is in progress. `Restore` had to learn the same rule:
an `Unpowered` machine is mid-cycle too, and zeroing its timer on load would
have silently restarted its work and eaten its inputs twice.

## Two radii

A pole has a `SupplyRadius` (what it energises) and a larger `WireRadius` (how
far it reaches another pole). Keeping them separate is what makes running a line
across the map a different activity from covering a factory floor — and it is
why a distant mine can be fed at all.

Connection is **mutual**: the smaller of two reaches decides. Otherwise one big
pylon could drag a small pole into a network it could never have joined, and a
player could merge two grids by accident.

Networks are connected components of poles, recomputed when something is built
rather than maintained incrementally. Building is rare and ticking is not, so
the cost belongs on the rare side — and a rebuild cannot drift out of step with
the truth the way an incremental union can. It is O(poles²), which is the right
complexity for tens of poles and a rebuild that does not run per tick.

## Unpowered is its own state

Like `Depleted` before it, `Unpowered` is deliberately not `Starved`: the fix is
a generator or a pole, not a belt. It has its own status colour — blue, nothing
like the red of blocked or the amber of starved — so a browning-out factory is
recognisable without opening a panel, and the panel distinguishes "not connected
to anything" from "connected, but the grid has nothing spare" by whether the
machine has any charge at all.

The HUD shows supply/demand rather than a percentage, because a player fixing a
brownout needs to know how much more generation to build and "68%" does not say
that.

## Generators are not machines

A generator consumes an item and produces energy, which is not a recipe.
Modelling it as one would mean inventing an "energy" item that belts could carry
and machines could stockpile. Fuel is consumed when a burn *starts*, the same
rule the miner uses about ore halfway out of the hole, so a generator picked up
mid-burn has already paid for the coal in it.

A generator no pole reaches still burns its fuel. It is running; it is just
wired to nothing, which is a mistake the player should see costing them coal.

## The lookup had to be cached

Resolving which network a machine sits on is a scan over the poles. Doing that
live, twice per machine per tick, made the sim tick **0.77 ms for 1024 machines**
— worse than 0.34 ms for *2048* machines before power existed. At the 100k
target it would have cost more than the entire rest of the simulation.

`PowerGrid` now carries a version that bumps whenever a pole or generator is
added, and `World` caches each machine's network, rebuilding when the version or
the entity count changes. Both are rare; the tick is not.

That took the same run from 0.770 ms to **0.094 ms** per tick — an eight-fold
improvement, and now cheaper than the pre-power baseline was per machine.

## Verification

Thirteen tests, then deliberate breakage: eleven mutations across the allocator,
the poles, the draw scaling and the save format. Ten were caught immediately.
The one that survived — swapping the mutual wire reach for the larger of the two
— slipped through because every pole in the tests had the same reach, so
min and max were indistinguishable. A test with mismatched reaches now covers it.

The save format went to version 3. An older file has no poles, so every powered
machine in it would load dark; refusing it is the honest outcome.

Getting the save's coverage honest took two passes of the same kind. Machine
energy buffers were all zero at the save point, so a save that dropped them
round-tripped perfectly — the fix was a machine on the grid drawing far more
than the grid can spare, which spends most ticks part-way to affording one.
`AssertMidFlight` now requires exactly that, so the coverage cannot decay back.

`--session-test` plays the power loop headlessly and CI asserts it: a machine
that needs electricity sits dark, then a pole and a fuelled generator bring it
to life.
