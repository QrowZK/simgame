# ADR 0008: Save format

## Decision

Saves are **JSON, keyed on stable strings, holding state only** — never
positions derived from the seed, never recipes copied out of the data files.

`SaveGame.Capture(World)` produces a `SaveFile`; `SaveGame.Restore(save,
recipes)` rebuilds a `World` from one. Both live in `/sim`, so a save is a sim
concept and the engine layer only chooses when to call them.

## Nothing is keyed on a runtime integer id

`ItemId`s are assigned in registration order. A save that stored them would
silently turn iron into tin the first time the data files gained an item above
it in the order. So the file carries its own item-name table, and every item
reference in it is an index into that table which is remapped on load.

Recipes are stored **by id, and looked up in the running game's recipe set**
rather than copied into the file. That is the deliberate trade:

- A balance change reaches existing saves, which is what a factory game wants
  from a patch. Freezing recipes into the save would mean an old factory keeps
  running the old numbers forever, beside new machines running the new ones.
- The cost is that a *removed* recipe id makes old saves unloadable. The loader
  names the missing recipe rather than dropping the machine — losing a machine
  quietly is worse than failing to load, because the player finds the hole in
  their factory hours later.

## The world is not in the file

Terrain and ore are pure functions of `(seed, tile)`, so the save stores the
seed. A 100k-tile map costs four bytes.

## Determinism makes the save testable

The interesting property is not "the file parses" but "the factory that comes
back is the factory that was saved". Because the sim is deterministic, that is
an assertion rather than an inspection: reload a save, run both copies 10,000
ticks, and require byte-identical state. Any field the save dropped shows up as
divergence.

That test found nothing on its own — every field was already covered. What
found the real holes was **deliberately breaking the save** and checking the
test failed. Eighteen mutations, one field at a time. Four passed:

| Mutation | Why it slipped through | Fix |
|---|---|---|
| Drop the inserter's cooldown | Its source belt had drained by the save point, so the arm sat idle at cooldown 0 forever | Save while it is mid-swing, and assert that it is |
| Drop the splitter's buffer | A connected splitter empties within a few ticks | Add a second splitter wired to nothing, which stays backed up |
| Zero every belt gap | Items were packed compressed, so every gap was already 0 | Feed the lane item by item with the belt running, so items are genuinely spread out |
| Reset belt speed and machine capacity to their defaults | The test world used the defaults, so the mutation was a no-op | Give everything distinctive non-default values |

The lesson generalises: a round-trip test proves only as much as the state in
the world it round-trips. `AssertMidFlight` now guards each of these — the
suite fails if the world it saves is not genuinely mid-cycle, mid-swing,
mid-rotation and carrying spread-out items — so the coverage cannot decay back
without saying so.

## Consequences

`World.Rng` was removed. Nothing read it, and an unused `System.Random` is a
save hazard: its internal state is not serialisable, so the first code to use
it would have broken save/load determinism silently. Anything needing
randomness should hash the seed and the tick, like `Noise` already does.

Several sim types gained a narrow save surface — `Lane.CopyTo`/`Restore`,
`Splitter.Restore`, `Inserter.Restore`, `Machine.Restore`,
`FluidNetwork.Restore`, `World.RestoreTick`, `World.IsPlaced`. These are
deliberately small and documented as save-only, rather than opening the types
up generally or reaching into them by reflection.

The format is versioned. A file whose version is not the current one is
refused, naming the mismatch: a half-understood save is worse than no save.
