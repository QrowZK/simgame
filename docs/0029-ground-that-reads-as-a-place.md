# ADR 0029: ground that reads as a place

The map was a flat sheet of green plates. ADR 0019 broke one colour per
`TerrainType` into three scales of noise, which fixed the flat *colour* and left
two things it did not touch and said so: the shoreline, and the fact that the
whole map is geometrically one plane at `y = -0.04`. This is those two.

## The constraint, first, because it decides everything else

**Land must stay flat.** The sim is a plane. Machines are seated at `y = 0`,
belts and inserters and the build ghost all assume a flat surface, and
`TryBuild` does not know what height is. Lifting land tiles to make hills makes
machines float or sink, which is breaking the game to decorate it.

So every land tile's *top face* is at exactly `y = 0`, without exception, and
the renderer only ever varies how far a tile extends *downwards* — which nothing
can stand on and nothing has to agree with.

**Water is the opposite case.** `TryBuild` refuses water outright, so nothing
will ever stand on a water tile. That makes the water surface free to be
recessed by its real depth, and it is the only place in this renderer where
relief is honest. All of the geometry below is there.

## What changed

### The tile mesh is a unit box, scaled per instance

It was `BoxMesh { Size = (0.94, 0.08, 0.94) }` — the dimensions lived in the
mesh, which was fine while every tile was the same shape. Land and water are now
different shapes, so the mesh is a unit box and the size is in the per-instance
transform. **This costs no draw batch**: land and water are still one MultiMesh
and one upload. Measured before and after with `--smoke`: `draw batches 18` both
ways.

### Water is recessed, and the rim of a pool is shallower than its middle

Depth comes from `HeightAt` — the same number that decided the tile was water at
all — so a pool is deep where the worldgen says the ground is low. Nothing is
invented for the picture.

One thing is derived rather than sampled: a water tile with land on any of its
four sides is drawn a step shallower and paler than the rest. This is what makes
a bay read as a bowl instead of a lake of constant depth, and it is honest
because a tile adjacent to land *is* the shallow edge. Worldgen's height field
is too smooth at tile scale to say so on its own; the adjacency test says it
exactly.

### Banks are the land tile's own side wall

A land tile with water next to it is drawn a full unit thick instead of 0.12.
Its top face does not move — it cannot — but its side is now the wall between
the land plane and the recessed water, lit by the sun at a different angle from
the top. That is the bank, and it costs no extra instance: it is the same box
the tile was always drawn as, extended downwards.

The tile *behind* a bank tile is thin again. The seam between them is 0.015
wide, so the step is invisible from anywhere except underneath, which is not a
place the camera goes.

### The seam is a hairline, not a grid line

Tiles were drawn at 0.94 of a tile, so six percent of every tile was gap. At the
zoom the game is actually played at that is a ruled grid, and "the map reads as
graph paper" was the complaint that opened this work. Land is now 0.985 wide and
0.12 thick away from water, so the seam reads as grout between plates rather
than as ruled paper — and a player counting squares to place a 3x3 can still
count them.

**Rejected: dropping the seam entirely.** ADR 0019's reasoning still holds. The
game is played on tile coordinates and hiding them helps nobody.

### Biomes blend instead of stepping

`TerrainAt` sorts height into five buckets and the renderer painted one flat
colour per bucket, so every tile in a sixty-unit band of height was the same
green and the boundary between two bands was a hard contour line that no ground
has. Colour now comes off a ten-stop ramp read with a lerp, so a field runs from
damp low green through dry olive into scree without a seam anywhere.

The height used for the *lookup* is warped by two mid-scale noises first, so the
biome boundaries wander instead of tracing the height contours exactly — a beach
that is a smooth curve everywhere reads as a diagram.

**The warp is clamped so it can never push a land tile into the water part of
the ramp.** The shoreline the player sees is the shoreline `TryBuild` enforces,
and those two must not disagree by a single tile. Water/land is always decided
by the unwarped height; the warp only moves colour within land.

### A field-sized variation term, for the opening view

Worldgen deliberately flattens the home region so a new game is playable, which
means the height ramp has nothing to say at spawn and the first thing a player
sees was a single sheet of green whatever the ramp did. A variation at ~26 tiles
gives it meadows and dry ground. Still a pure function of the seed: screenshots
are how rendering is verified here, and a renderer that rolled its own dice
would make every capture a different picture and every visual regression
unprovable.

## Heights are cached separately from colours, and never invalidated

A tile now needs its four neighbours' heights as well as its own, to know
whether it is a bank or a shallow rim. `HeightAt` is a five-octave fractal;
five of them per tile across a 37,000-tile field is a stutter.

The height cache is safe to keep forever where the tile cache is not, and the
distinction is the point: **mining changes what a tile is worth and therefore
its colour, which is why the tile cache is dropped on `DepletionCount`. It does
not move the ground.** Sharing one cache would have made every ore extraction
re-run the worldgen for the whole visible field.

## Cost

Measured with `godot --headless --path game -- --smoke --machines=4096`, three
runs each, on the same machine:

| | before | after |
|---|---|---|
| draw batches | 18 | 18 |
| render sync | 0.70 / 0.76 / 0.67 ms/frame | 0.67 / 0.66 / 0.70 ms/frame |
| terrain rebuild | 25.9 / 24.5 / 24.1 ms | 30.2 / 33.2 / 30.7 ms |

Zero extra batches and no measurable per-frame cost. The rebuild — which happens
when the camera crosses a tile boundary, not every frame — is about 6 ms worse,
which is the larger per-tile struct and the extra buffer arithmetic. The tile
cache is read by reference rather than copied out, which recovered part of it.
CI's ceiling is 60 ms.

## Proving it, since a screenshot cannot

A dark blue plate and a hole in the ground look identical from above, so
`--smoke` reports what the renderer actually drew:

```
terrain water   tiles=15331 deepest=0.87 below land
```

Measured over the coast, not over spawn: worldgen puts the home region a long
way inland on purpose, so the default field contains no water at all and would
have reported zero either way. `TerrainRenderer.NearestWater` walks out to the
first water tile and the field is rebuilt there. `ci.yml` asserts both the count
and that `deepest` exceeds 0.1, so a change that flattens the sea back into
painted tiles fails the build rather than being noticed months later.

`--shore-shot` exists for the same reason: every other capture in this
repository is inland, so the shoreline — the one place this renderer puts relief
— appeared in none of them.


## Addendum — what the rebuild number was measuring

The ceiling this change first tripped turned out to be measuring the wrong
thing, and finding that out cost more than the terrain work itself.

`--smoke` timed a single `Sync` after a one-tile camera move. That is mostly
cache hits, and it was also the first rebuild in the process, so it carried the
JIT cost of the whole describe-and-colour chain. The same binary printed 95 ms
and then 31 ms on consecutive runs, and the same commit went green on one CI
runner and red on another.

The run now warms the path, drops the tile cache, and times a whole field
described from scratch. Measured that way, on one machine and one method:

| | full field, warm code, cold cache |
|---|---|
| before this change | ~83 ms |
| after this change | ~175 ms |

So the terrain work does roughly double the cost of describing a field, and the
old code was already well past the 60 ms the workflow claimed to enforce. The
ceiling is now 300 ms against the honest number.

## Addendum — mining no longer redraws the world

Chasing that measurement surfaced something worse, and older than this change.
`Sync` dropped its entire tile cache whenever `Ground.DepletionCount` moved:

```csharp
if (dug != _builtDug || _tiles.Count > MaxCachedTiles) _tiles.Clear();
```

Every ore extraction therefore threw away 37,000 described tiles and rebuilt
them from the worldgen — and a running factory extracts constantly. The full
describe is the expensive thing in this renderer, and the game was paying it
over and over.

Describing the ground again cannot change it: the ground is a pure function of
seed and tile. What mining changes is how strongly the ore tints — 0.65 for a
live patch, 0.18 for a worked-out one. So the tile now stores the ground colour
and the ore colour separately along with the patch it belongs to, and the tint
is resolved at write time, once per *patch* per rebuild rather than once per
tile. The cache survives.

This is not covered by a test, and it cannot be covered by the current smoke
run: the demo world is built with no `WorldGen`, so it has no ore patches, and
an attempt to time a depletion redraw there printed `mined=0` and `0.0 ms`. A
metric that reports 0 ms for work that never ran is worse than no metric, so it
was removed rather than committed, and the gap is on the board for qa — along
with the fact that the smoke world contains no ore at all.
