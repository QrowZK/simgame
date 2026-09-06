# ADR 0007: Machine footprint is machine effect, and every machine has a panel

Two requirements settled together, because they interact: how big a machine is,
and how a player sees what it is doing. Sizing had to come first — footprint
decides what a click can hit.

## Footprint

**A machine's size is its throughput multiplier.** A machine `size` tiles per
side runs `size * size` batches of its recipe per cycle, in the same duration,
and costs `size * size` times as much to build.

The alternative was a separate speed or throughput number per machine, with
size chosen to look right beside it. That is two dials that mean the same thing,
and they drift: someone tunes throughput, nobody moves the model, and a player
learns that the big machine in the corner is not actually the powerful one. By
deriving parallelism from area in `tools/generate_data.py`, the correlation is
not a balance decision anyone has to keep honest — `EffectIsExactlyFootprint`
holds it, and there is no way to author a machine that is big and weak.

Build cost scales with area for the same reason in reverse: without it,
footprint is free throughput and every plant is 3x3.

Sizes follow real plant scale rather than a tier ladder, since scale is a
property of the process, not of the age:

| Size | What | Examples |
|------|------|----------|
| 1x1 | Bench-scale unit operations, all logistics | lathe, macerator, belt, inserter |
| 2x2 | Vessel-scale plant | electrolyzer, chemical reactor, boiler, centrifuge |
| 3x3 | Bulk continuous process units | vacuum distillation, steam cracker, FCC, arc furnace, fusion reactor |

A machine is always square. A rectangle doubles the placement rules — rotation,
which way it faces — for nothing the recipe graph can express.

`MachinePlacement` anchors on the footprint's **south-west corner**, not its
centre: a corner is what a cursor holds while placing, and it keeps occupancy
arithmetic in integers for even-sided machines. The sim keeps a tile ->
machine-index dictionary, so both "is this space free" and "what did I just
click" are O(1). At 100k machines a scan over placements is neither.

The renderer scales the plan by `size` but the height by only half that. A 3x3
raised to three times the height reads as a tower rather than as a bigger
machine, and buries its neighbours in shadow. The first render also showed the
attachment overhanging its hull, because it was scaled by plan in every axis;
it now lifts with the hull.

## Machine panels

Every machine has an inspection panel: recipe, per-cycle amounts, progress,
state, both buffers, and the player's own inventory. Clicking any tile of a 3x3
opens the one machine, not nine.

Three things made this more than a readout.

**The panel reports the machine, not the recipe card.** A 2x2 consumes four
times the listed amount. Showing the recipe's own numbers on a parallel machine
would make the panel lie about the machine it is attached to, so `Machine`
exposes `InputPerCycle`/`OutputPerCycle` with the batch already applied.

**The failure states say what to do.** "Starved" names the input it is short of
and by how much; "Blocked" says the output is full and nothing is taking it
away. Naming the state back at the player is the cheapest thing a panel can do
and the least useful.

**Hand operations are sim state, not UI state.** `Inventory` and `HandOps` live
in `/sim`, because loading a furnace by hand changes the world and every world
change belongs inside the deterministic tick's world. The panel only calls
them. `HandOps.Insert` refuses items the recipe cannot use — otherwise a
mistyped hand load buries an item where it can never be retrieved.

Buffer contents are exposed as **snapshots**, so a panel holding a reference can
never mutate the simulation.

Layout is `game/scenes/machine_panel.tscn` so it can be nudged without touching
code; `MachinePanel.cs` only fills it in.

Picking is ray-to-ground-plane, not physics. Machines are MultiMesh instances
with no colliders, and giving 100k of them collision shapes to support a click
would cost more than the entire sim tick.
