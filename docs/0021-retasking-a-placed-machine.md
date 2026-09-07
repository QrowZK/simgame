# ADR 0021: Retasking a placed machine

**Supersedes the "Recipes are chosen before placing, not after" section of
ADR 0015.** That section said a machine's recipe is fixed at build time, named
the price of changing that — "giving `Machine` a recipe change with an eviction
rule, and a save version" — and left it. This is that bill, paid.

## What changed my mind

Not an argument. A playthrough.

`docs/0020` walked the opening route in the real sim and found the three facts
that combine into a dead end: `Machine.Recipe` was readonly, nothing removes a
placed machine, and **no recipe in the data produces another crafting bench**.
The starter kit holds exactly one bench, the bench is the sole source of all 49
steam-tier recipes, and the route to the first miner needs ten of them. So a new
game could craft exactly one thing, ever, and then had no next move — no
refusal, no message, nothing to try. The game did not break; it simply stopped.

ADR 0015 was not wrong about the cost. It was wrong about the alternative. It
weighed "choose before placing" against "choose after" as two interface idioms
and picked the one with less work in it. What it did not weigh is that the same
decision, applied to a game whose first machine is also its only machine, is a
progression blocker — and one that CI reported as `--- building ok ---` for
weeks, because the session test placed the bench on `form_copper_plate` (a
recipe needing a copper ingot, needing a furnace, needing the bench) and
asserted only that the placement succeeded.

Three fixes were available: a recipe that crafts a second bench, removal of
placed machines, or retasking. A craftable bench is the cheapest and the worst —
it turns the blocker into a stone treadmill and asks the player to place ten
benches for ten crafts, which is bookkeeping wearing the costume of a decision.
Removal is worth building (it also answers `docs/0018`'s unpaired-entrance
problem) but it is a bigger change and the wrong shape here: picking a bench up
and putting it down again to change what it makes is a chore, not a choice.

Retasking is the one that adds a decision rather than a step. A machine's recipe
becomes a thing you manage — this bench is making rods today and plates
tomorrow — which is the actual early-game rhythm, and it is what makes the
question "how many benches do I want" interesting later instead of forced now.

## The eviction rule: conservation

The question ADR 0015 deferred, and the only genuinely hard part: what happens
to the inputs already inside.

**Everything the machine is holding goes back to the player.** All three of:

1. **The input buffer.** Those items are the player's and were never consumed.
2. **The output buffer.** Finished goods, already paid for.
3. **The batch of a cycle in flight.** This is the one that is easy to miss and
   impossible for a player to notice. A machine takes its inputs out of the
   buffer when a cycle *starts* and writes outputs when it *ends*, so mid-cycle
   those items exist nowhere that any UI displays. Refunding them is exact, not
   generous: no output has been produced, so handing the inputs back creates
   nothing.

The alternatives were considered and rejected:

- **Void them.** A machine that silently eats a stack of ore on a recipe change
  is the kind of thing that loses a save's worth of trust. It also makes the
  picker a hazard: the safe move becomes "never click this list".
- **Refuse while non-empty.** Honest, and unhelpful exactly when it matters. The
  bench that blocks the opening route is *always* non-empty — it is mid-craft —
  so the player's move becomes "empty it by hand first", which is the same
  eviction done manually and worse.
- **Keep the inputs in place.** Tempting for the common case of switching
  between two recipes that share an ore, and wrong in general: a buffer holding
  something the new recipe cannot use is a machine that reports itself starved
  while visibly full. `HandOps.Insert` already refuses items a recipe has no use
  for, for the same reason.

Conservation is also the property the tests are written against, rather than the
arithmetic: across a retask the number of units in the world does not change.
The exact counts (`Retasking_HandsBackTheInputsTheOutputsAndTheCycleInFlight`)
are there to make it legible; the conservation test is what pins it.

Two smaller rules fall out:

- **Re-picking the current recipe is a no-op**, evicting nothing. A picker that
  dumps a running machine when the player clicks the already-highlighted row is
  a trap, and mid-cycle it would eat the batch in flight.
- **A refusal costs nothing.** A machine asked for a recipe it cannot run keeps
  its recipe and its contents — the same rule `TryBuild` has had since 0015.

The energy buffer is deliberately *not* returned. It is at most one tick's draw,
it is not an item, and there is nothing to hand it back to.

## The player is told

`RecipeChangeResult` is an enum, not a bool, for the reason `BuildResult` is
one: "it already makes that", "this machine cannot make that" and "this machine
cannot be retasked at all" need different sentences. `World.TryChangeRecipe`
also reports **how many units came back**, and the panel says so —
*"Retasked. 14 item(s) came back to you."* A silent eviction and a silent void
look identical from the player's chair.

## Where it lives

In `MachinePanel`, because that is where a player already inspects a machine.
The list is built from `BuildCatalogue.RecipesFor` — the same call, the same
ordering and the same row format the build menu uses before placing — so the two
lists cannot disagree about what a machine is allowed to run. A machine still
runs its own tier and every tier below it, checked at retask exactly as it is
checked at build.

## What it cost

- **A save version, 8 to 9.** Not for the recipe, which was always stored, but
  for a new field: a machine now remembers **the item it was placed from**. That
  is the only way to ask the build catalogue what it may run — a placement's
  tier and category select meshes and do not identify a machine. A version 8
  file loaded as 9 would give every machine an unknown source and refuse to
  retask any of them: a factory that silently cannot be reconfigured, which is
  precisely the quiet wrongness a version number exists to prevent.

- **A named limit.** A machine built without a source item — headless analysis
  and the demo world do this — cannot be retasked, and says so in the panel
  rather than guessing. Following the house rule about exposing limits rather
  than papering over them. The demo factory now tags its machines with a
  plausible source, because "the panel says every machine is un-retaskable" was
  true of that code and false of the game it stands in for.

- **`Machine.Recipe` is no longer `readonly`.** It is a property with a private
  setter and `SetRecipe` is the only writer, so the eviction cannot be bypassed
  by assignment.

## The test that lied, replaced

`RunBuildFlow` asserted that a bench could be placed and printed
`--- building ok ---`. True, and useless: it was placed on a recipe it could
never complete.

The session test now **plays the opening route**: from a starter kit and bare
ground it prospects, hand-mines, crafts a furnace and an alloy smelter on the
one bench, retasks that bench through every step of the chain, smelts, alloys,
forms, assembles, and ends with a steam miner in the player's hands and placed
on ore. Eleven distinct recipes on one bench, 36 retasks, three machines. It
uses only the sim's player-facing operations — `TryBuild`, `TryChangeRecipe`,
`HandOps` — so it fails if any link breaks, and it names the item it got stuck
on rather than reporting a bool.

Verified by breaking it: with `TryChangeRecipe` forced to refuse, the run prints
`stuck on        build_man_alloy_smelter (CannotRun)` and
`opening route   FAILED`, which is the pre-fix game exactly.

`sim.tests/OpeningRouteTests` walks the same route through a second,
independently written runner. Two implementations of the same walk over the same
data: if the recipe graph ever grows a shape only one of them handles, the
disagreement is the signal.

## Also here: hands cannot scoop a liquid

`docs/0020` F3. `NewGame.OreSpecs` buries every raw material, crude oil
included — by design, since the derrick has to stand on something — and
`HandOps.Mine` never asked what form the deposit was. On seeds 4 and 10 oil is
five tiles from spawn, so it is the first thing a new player walks to, digging it
appears to work, and it is a dead end they cannot see the bottom of.

The deposit now knows its own form (`OreSpec.IsFluid`, carried into `OrePatch`)
and `HandOps.Mine` refuses one. The gate is in `HandOps`, not in
`Ground.Extract`, because the derrick goes through `Extract` too and must keep
working — tested both ways round, since a refusal that refuses everything passes
the half of the test that only checks the refusal.

## Still open, deliberately

- **Removal** (`docs/0020` F4). Retasking closes the progression blocker; it
  does not let a player undo a misplacement, and an unpaired tunnel entrance is
  still permanent. Its own decision.
- **F2**, the nearest resource being unusable at manual tier on 18 of 20 seeds.
  The route now works; it may still start with a long walk.
