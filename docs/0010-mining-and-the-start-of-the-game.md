# ADR 0010: Mining, and starting a game with nothing

Worldgen described ore that nothing could consume, and a "new game" handed the
player a finished factory. Both are fixed here, and they are one change: ore has
to enter the item graph before starting with nothing can be playable.

## Ore leaves the ground in exactly one place

`Ground` wraps `WorldGen` with a **sparse depletion overlay**: patch centre ->
units removed. `WorldGen` stays a pure function of the seed and can never
change, so the map is still four bytes in a save; what has been dug is
simulation state and lives beside it. An untouched world stores nothing.

Two consumers, and no third:

- **`HandOps.Mine`** — the whole game before the first miner. It takes no space,
  needs no power, and is the only way to bootstrap a world where you own
  nothing.
- **`Miner`** — a building that extracts on a cycle.

Extraction happens when a miner's cycle **completes**, not when it starts. A
miner removed mid-cycle has therefore taken nothing, and the ground and the
buffers can never disagree about ore that is halfway out of the hole.

A miner mines **what it was built on**, resolved once at build time. That makes
`TryPlaceMiner` fail loudly on bare rock rather than producing a building that
silently never runs, and it means a miner on a patch that later runs out reports
`Depleted` instead of quietly changing what it produces.

`Depleted` is a new `MachineState`, appended last so existing saves keep their
numbering. It is deliberately not `Starved`: no belt or inserter will ever fix
it, so both the panel and the status colour have to say something different — a
blocked machine is a problem to solve on the spot, a worked-out one is a reason
to go and find more ore.

Miner yield is `BaseYield * footprint area`, the same size-is-effect rule ADR
0007 gave machines. One rule across the game, learned once.

## Connected to the machine graph, not beside it

`EndpointKind.Miner` lets belts and inserters pull from a miner exactly as they
pull from a machine, so the real path — ground, miner, inserter, furnace — works
with no hand in the loop. That path is what `OreReachesAMachineOverABelt`
asserts, and it is the whole meaning of "connected".

The game now also reads `/data` at runtime. `Sim.Data.Catalogue` turns the JSON
definitions into one `ItemDatabase` and a `Recipe` per definition; `GameData`
and the loader moved from `data.tests` into `/sim` so the game and the tests use
the same code rather than two copies that can drift. Saves resolve their recipes
against this, which closes the placeholder noted in ADR 0008.

Ore specs are derived from the data files by `NewGame.OreSpecs` — every raw
solid becomes something worldgen deals, gated by its tier. Adding a raw material
to `/data` now puts it in the world with no further wiring, and the data test
calls that same function rather than rebuilding its own specs, so the thing
under test is the thing that ships.

## Starting with nothing

A new game is a landing site: a prospector, your hands, and 24 stone. No
machines, no miners, no belts.

The stone is the smallest thing that makes the first recipe openable — the
Manual-tier furnace and crucible are piled-up stone. A miner in the starter kit
would skip the part of the game this change exists to create; an empty kit would
mean nothing can be built at all.

The first loop is therefore: prospect, walk, dig by hand, hand-craft a furnace,
and only then build a miner. `TheFirstHour_CanBePlayedByHand` asserts the
bootstrap end to end, including that whatever you can dig is actually consumed
by some recipe — a minable ore nothing eats would be a dead start.

`EveryStart_HasMineableOreWithinAShortWalk` runs 20 seeds, because a seed that
can strand the player is not a start.

## Verification

Unit tests, then deliberate breakage, then the real engine.

Mining and the extended save format were each mutation-tested — ten mutations,
one field or behaviour at a time, all ten caught. The save-format ones matter
most: dropping a miner's buffer, its cycle length, its item, or the depletion
overlay all now fail the 10,000-tick round-trip.

The save version went to **2**. A version-1 save has no record of what was dug,
so loading one would silently refill every patch the player had emptied —
refusing it is the honest outcome.

`--session-test` gained a start-flow run that plays the opening headlessly:
new game, assert no factory, prospect, hand-mine, build a miner, tick, and check
the ground actually went down. CI asserts its result lines, so "you can start a
game" is a build failure when it stops being true.

The dense placeholder factory still exists as `GameSession.DemoFactory`, used by
`--smoke` and `--machines=N` to measure the renderer. It is no longer what a
player gets -- and it now builds from real `/data` recipes rather than invented
ones. That was not tidying: its own saves could not be loaded back, because the
recipe ids it made up were not in the catalogue the loader resolves against.
The session test found that, not the unit tests.

## Terrain had to be drawn

The renderer only ever drew machines. That was survivable while a new game
started with a factory and fatal the moment it started with nothing: the player
landed in an empty void with no way to see where anything was.

`TerrainRenderer` instances one flat tile mesh per visible tile, coloured by
terrain type and tinted where there is ore -- the same MultiMesh approach as the
machines, for the same reason. Ore is a tint on the ground rather than a marker,
because a patch is a region you stand on and the decision it drives ("is this
worth building on") is about its shape. A worked-out patch keeps a faint tint,
so the player can see where they have already been.

The field is rebuilt only when the camera moves a whole tile or the ground
changes, since re-evaluating noise for 16,600 tiles sixty times a second is not
free. Tiles are drawn slightly under a tile wide, so the seams read as a grid:
the game is played on tile coordinates and hiding that helps nobody.
