# 0033 — The player has a position, and it costs something

## The problem

There was no player. `World` had a `PlayerInventory` — a pocket with nobody
attached to it — and WASD panned a camera. Digging, building and delivering all
happened anywhere on the map at any distance, because nothing in the game knew
where anybody was.

The game's own text said otherwise, in three places:

- the guide: *"Walk to the nearest usable patch and mine it by hand."*
- the Uplink step: *"put it down somewhere you will not mind walking to."*
- the premise: you start with *"a survey device, your hands, and enough stone
  to make a bench."*

None of those sentences was true. The survey device surveyed from the camera, so
a player could scroll two hundred tiles and prospect there without moving. The
Uplink's siting was not a decision, because every siting was equally convenient.
And a pair of hands that reaches the whole map is not a pair of hands.

This ADR makes them true.

## Position is simulation state, in integers

`Sim.Player` lives in `World`, ticks with it, and is saved. Position is
**milli-tiles**: integer thousandths of a tile, `X` and `Y` as `int`.

Floats were rejected for the reason the house rules give: two worlds from the
same seed must stay byte-identical for 10,000 ticks, and position now decides
whether a build succeeds. A float that drifts by one ulp across two machines
decides a boundary build differently on each, and the divergence then shows up
as a machine that exists in one world and not the other — a determinism bug that
looks like a gameplay bug.

A thousand steps per tile is not arbitrary. Walk speed is **6 tiles a second**,
which at 60 UPS is exactly **100 milli-tiles a tick**, so a walk of any length is
plain integer addition: no accumulator, no remainder, no carry. A diagonal is
**71** per axis — `100 / sqrt(2)` rounded — a constant rather than a
normalisation, because normalising means a square root and a square root means a
float. The rounding makes a diagonal 0.4% faster than a cardinal, which is far
below anything a player can feel and is identical on every machine, which the
float would not be.

Movement comes from a **`MoveIntent`** the engine layer sets once a frame and the
sim consumes once a tick. Never from frame delta time: a frame covers zero ticks
or eight, so a layer applying its own delta would make how far you walked depend
on your frame rate.

**Rejected: keeping position in Godot and passing it into the sim for the reach
checks.** It would have been less code today. It also puts the one number that
decides whether a build succeeds outside the thing that has to be deterministic
and outside the thing that is saved, so a reload would put the player wherever
the camera happened to be — which is exactly the bug this ADR closes.

## The camera follows, and trails

`CameraRig` no longer pans. It keeps yaw (Q/E), zoom (wheel) and the
ground-picking ray — the three things that are genuinely about *looking* — and
`GameRoot` calls `Follow(playerPosition, delta)` once a frame.

It **trails** rather than recentring instantly, closing 8/second of the remaining
gap, about an eighth of a second of lag. A rig locked to the player makes the
*world* the thing that slides while the figure stays nailed to the middle of the
screen, which reads as the ground moving under a static man. Trailing lets the
player pull ahead of centre in the direction they are walking, so the screen
shows more of where they are going than where they have been.

Past 40 tiles it cuts instead. A load, or a capture path framing something across
the map, is not a walk, and smoothly flying two hundred tiles is a long look at
nothing.

## The two radii, argued from this game's numbers

**Hand reach: 6 tiles.** The number is the ore patch. Starter patches are radius
6 (`NewGame.OreSpecs`), so standing in the middle of one puts every tile of it in
reach: a patch is one place you stand, not a field you shuffle across. Six tiles
is also just over a machine and a half, so hand-feeding a 3×3 smelter from beside
it works and hand-feeding one across a bus does not.

**Build reach: 12 tiles.** Twice the hands, and deliberately not more:

- Twelve tiles is a belt run you can lay from one standing spot, so building a
  line is walk-lay-walk rather than a click for every tile.
- Twelve is the **near** edge of the guaranteed starter patch's 12–40 tile band
  (ADR 0026). That is the point. From the landing site you can put the Uplink
  down and reach the ground around it, and you cannot reach the ore. The first
  thing the game asks you to do is walk. `PlayerTests.FromTheLandingSite_...`
  asserts exactly this.
- It is larger than hand reach because the fiction is different — you set a
  machine down at arm's length plus a shove, you do not dig at range — and
  because equal radii would make the build ghost and the dig cursor the same
  circle, which teaches the player nothing.

**Is the opening a walk or a chore?** At 6 tiles a second the far edge of the
guaranteed band is 6.7 seconds each way; `--session-test` measures the real thing
and prints it (`walked 408 ticks (6.8 s) to stand on it` on seed 20260907). A
manual furnace wants 24 stone at 5 per click, which the starter kit already
carries, so the first walk buys ore rather than being the price of the first
recipe. The opening ladder's nine steps are all within a few tens of tiles of
the landing site.

The radii are **not tuned to make tests pass.** Several harnesses broke the
moment reach existed — `--session-test`'s belt flow, its tunnel band, the
opening route, and twenty-six unit tests — and every one of them was fixed by
moving the player, mostly through `Sim.Walk.To`, which drives the same intents a
keyboard drives.

## Looking is not touching

Opening a machine's panel is free at any distance, and always will be. Nothing
about inspection is physical, a factory you cannot read from across the map is a
factory you cannot debug, and the click that opens a panel is the same click that
would otherwise have to be a different click near and far.

Only the *actions* ask. What is gated, and with what:

| Action | Radius | Refusal |
|---|---|---|
| Build | 12 | `BuildResult.TooFar` |
| Remove | 12 | `RemoveResult.TooFar` |
| Dig a resource tile | 6 | `DigResult.TooFar` |
| Hand-load a machine, take its output | 6 | panel message |
| Deliver into the Uplink by hand | 6 | `DeliveryRefusal.NoUplinkInReach` |
| Survey (P) | — | answers about where you are standing |
| Open a panel, read a machine | — | never refused |

Removal uses the **build** radius rather than the hand radius. A rule where you
can place at twelve tiles and only take back at six means a mistake you have to
walk to before you can undo it.

Every refusal is its own reason, as everywhere else here, and reach is checked
**before** the footprint: a player is never told "something is already there"
about a tile they cannot walk to. Both refusals are true, but only one of them is
the problem they have.

Hand delivery now requires an Uplink you can actually touch. Before this,
`DeliverByHand` credited research on a map with no Uplink standing on it
anywhere — the one thing the premise says is impossible.

## Save format 14

An older file has no position in it, so loading one as 14 would put the player
back at the origin. After twenty hours that is the middle of a factory, standing
inside whatever has been built there since, and a hundred tiles from the patch
they saved beside. With reach, that is not a cosmetic wrong place: it is a save
that reloads unable to touch the machine it was saved working on. Version 13 and
below are refused.

Position is stored as raw milli-tiles, not as a tile. A tile is lossy — saving
mid-stride and reloading would snap to the tile centre, so every save would move
the player half a tile, and over a long game that adds up.

## The limitation, named rather than papered over

**The movement intent is not saved.** A world written while walking reloads
standing still, whatever was held down when it was written.

This is a choice, not an oversight. Resuming a walk nobody is asking for is
worse than starting stopped, and a save should describe the world rather than the
keyboard. It is tested rather than hidden:
`PlayerTests.AWalkInProgress_IsNotSaved_AndTheLoadedWorldStandsStill`. The
consequence is precise and worth stating: byte-identical round-tripping holds for
the position, and does not hold for the walk.

## The avatar

`GameRoot` looks the renderer up by name and calls it through `Call`, guarded, so
the game compiles, runs headlessly and passes its tests whether or not a figure
has been drawn. Facing is two integers in the sim (`FacingX`, `FacingY`) and
becomes an angle only on the Godot side — the sim's `+Y` is *south*, so the
conversion negates Y. Getting that wrong is 180° out, which looks right walking
east or west and is backwards walking north or south.
