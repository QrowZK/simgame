# 0028 — Taking back what you built

Status: accepted
Date: 2026-09-07

## The problem

Nothing could be removed. Everything a player put down was there for the life
of the save, and the case QA filed (F4 in `docs/0020-opening-playthrough-qa.md`)
is the one that makes that unacceptable rather than merely annoying: an
underground belt entrance refuses every same-facing end from `reach + 1` to
`reach * 2` tiles ahead of it (`AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach`).
The refusal is right — it teaches the span — but with no removal, one
misplaced entrance made four to twelve tiles of the player's bus permanently
unbuildable, and the only way out was to route around it forever.

ADR 0021 closed the other half of this (a placed machine can be retasked), and
in doing so it set the precedent that decides most of what follows.

## Is there a decision in it?

Removal on its own adds no decision — undoing a mistake is not a choice, it is
the absence of a punishment. What it adds is decisions *elsewhere*: with
removal, laying a bus is something you can be wrong about cheaply, so a player
will try the tunnel, the splitter and the awkward pole placement instead of
placing nothing until they are sure. The cost of a bad guess is what was
stopping experimentation, and that is worth the code.

## What comes back

**The building, plus everything inside it.** This is ADR 0021's eviction rule
applied unchanged, and it is applied by calling the same code: `TryRemove` on a
machine calls `Machine.SetRecipe(machine.Recipe, PlayerInventory)`, which
already refunds the input buffer, the output buffer, and the batch of a cycle
in flight. One rule, one implementation, one place to be wrong.

Three things deliberately do **not** come back, and the line between them and
the machine's in-flight batch is the same line in every case: *has the thing
already been paid out as something else?*

- **A generator's burning fuel unit.** `Generator.Tick` takes a unit out of
  stock at the *start* of a burn, and it has been producing power ever since.
  Refunding it would mint energy from nothing. The whole units still in the
  hopper do come back. (A machine's in-flight batch is the opposite case: its
  inputs are gone from the buffer and no output has been written yet, so
  refunding them is exact rather than generous.)
- **An accumulator's charge, and a machine's energy buffer.** Energy is not an
  item and there is nothing to hand it back to. Same call as ADR 0021 made.
- **Fluid in a pipe, tank or pump.** Also not an item — the network measures it
  in units of capacity, not in stacks — so it drains into the ground. It is
  *counted*, not hidden: `RemovalReport.FluidVoided` and
  `FluidSystem.VoidedByRemoval`, alongside the existing `VoidedByMixing`, and
  the GUI says the number out loud. A player who cannot see the loss will never
  work out where their oil went.

## What happens to items on a removed belt

`BeltMap.SpilledOnRemoval` already existed for the items a *rebuild* strands
when a run shrinks under them. Removal does not reuse it as a disposal route,
because destroying a player's ore because they cut a belt in the wrong place is
the same betrayal as a machine eating its input buffer.

Instead, `BeltMap.TakeItemsOn` lifts every item standing on the tiles being
removed and hands them back, and everything else stays exactly where it was
standing. It is built on the snapshot/restore pass the rebuild already uses
rather than reaching into a lane directly: lane gaps are relative to the item
ahead, so plucking one item out of the middle means recomputing the gap of the
one behind it, which is precisely what `Restore` does.

`SpilledOnRemoval` still exists and is still reported — the difference between
its value before and after a removal is `RemovalReport.Spilled` — because a
long belt shortened by a cut genuinely can have nowhere to put everything that
was on it, and that is worth saying rather than hiding.

The case that is easy to get wrong is the **tunnel**. Items inside an
underground belt are standing on tiles the player cannot see or click, so a
removal that only emptied the clicked tile would silently destroy everything in
flight underground. `TilesEmptiedByRemoving` returns the clicked tile plus the
buried span, and `RemovingATunnelEnd_HandsBackWhatWasStillUnderground` pins it
at exact counts.

## Refusals

Two, and they are genuinely different sentences:

- `NothingThere` — bare ground, ore, water, or a tile a tunnel merely passes
  under. The player's aim was off.
- `UnknownBuilding` — something is there, but nothing recorded which item paid
  for it. This is the game's limitation, not the player's mistake, and it says
  so.

There is deliberately **no "busy" refusal.** A machine mid-cycle comes up and
hands the batch back; a pole holding the whole factory's power together comes
up as readily as it went down. The entire reason this exists is that
misplacement was permanent, and a removal that refuses whenever the factory is
running is a removal you cannot use. The knock-on is the player's problem to
see, which is what the power and fluid rebuilds already make visible.

## How a building knows what it cost

Removal hands back the item that was spent, so it has to know which item that
was — and for most placed things that cannot be read back off the map. A
splitter tile stores a facing and nothing else. Two tiers of transport belt run
at the same speed. A pole knows its radii, not its tier.

So `World` keeps `_builtFrom`: anchor tile → item, written by `TryBuild` and
dropped by `TryRemove`. Keyed by the anchor (the south-west corner) because
that is the one tile every kind of building has, from a 1x1 belt to a 3x3
assembler.

The honest limitation this creates, named rather than papered over: **a
building that was not placed through `TryBuild` cannot be removed.** Scenarios,
the demo world and the analysis worlds place machines directly and never spend
an item, so there is nothing to give back; they get `UnknownBuilding`. A played
game routes every placement through `TryBuild`, and a new game starts with
nothing on the map at all (ADR 0026), so no real save contains one.

**Save format 12** carries the record. A version 11 file loaded as 12 would
come back with an empty map of it, so every belt, pole and machine in a
twenty-hour factory would refuse to be picked up — the exact permanence this
change exists to end, reintroduced silently by a file that looked like it
loaded correctly. Older files are refused, as always.

While adding it, the save's pole restore turned out to put poles straight into
the power grid without reserving their tiles, so a *loaded* pole could be built
over. It is restored through `World.AddSavedPole` now, which claims the tiles
that are free and leaves the ones that are not — an old hand-built world can
have a pole standing on a generator, and refusing to load it would delete a
working grid without a word.

## Compaction, not tombstones

Removing a machine moves the last machine into its slot rather than leaving a
hole. The dense arrays (`Placements`, `MachineStates`) are what the renderer
streams into instance buffers in one pass, precisely so that it never tests
anything per machine; a tombstone would either draw a hull that is not there or
force every consumer to learn to skip holes. Swap-remove costs one occupancy
repaint for one moved index.

Three things had to move with the machine and each is a tested property:
its occupancy tiles, the Uplink registration (`World._uplinks` is keyed by
index — get this wrong and an ordinary assembler is drained into research every
tick), and the belt endpoints, which hold machine *indices* and are re-resolved
by forcing a recompile at the end of every removal.

Poles, generators, accumulators, miners and extractors compact the same way.
Doing so meant giving them real indices in the occupancy grid: generators,
accumulators and poles used to share one index-less marker because nothing ever
clicked through to them. Removal does — a click has to resolve to *which* pole
— so each kind now carries its index the way machines and miners always have.

## Knock-on effects use the existing rebuilds

Nothing here repairs a network by hand. Removing a pole marks the power grid
dirty and the components are recomputed from the poles that are left; removing
a pipe marks the fluid system dirty and the flood fill re-splits it, carrying
contents per node by capacity as it already did; removing a belt tile marks the
map dirty and every run is recompiled and every tunnel re-paired. The map is
left in the state never having built the thing would have left it in, which is
the property the tests assert rather than the code paths.

## In the game

`X` arms removal, and it turns build mode off rather than layering under it:
both modes own the left mouse button, and a click that could either place or
destroy depending on state nobody can see is how a player loses a machine they
did not mean to touch. Escape and right-click back out, as they do from build
mode.

The preview is the existing build ghost, unchanged. It already draws a
footprint in the mesh of the thing it represents, and the question before a
removal click is the one it already answers: which building is under my cursor.
Green still means the click works, red still means it will be refused, so
nothing has to be relearned for the second mode.

## Alternatives rejected

- **Refusing removal of a running machine.** Safe-looking and useless: the
  misplacements you want to undo are in a factory that is running.
- **Destroying belt contents on removal** (reusing `SpilledOnRemoval` as the
  route). Simpler by about thirty lines, and directly contrary to ADR 0021's
  conservation rule.
- **Deriving the item from the placed thing** instead of recording it. Works
  for machines (tier and category are in the placement) and fails outright for
  splitters, belts and poles.
- **Tombstoning removed machines** to keep indices stable. Pushes a per-entity
  test into the renderer's bulk copy, which is the one place this codebase has
  decided never to have one.
