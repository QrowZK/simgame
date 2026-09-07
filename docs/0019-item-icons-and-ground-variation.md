# ADR 0019: procedural item icons, and ground that reads as ground

Three visual gaps closed at once, and the two of them that needed a decision
rather than just code.

## 1. Underground belts and splitters are drawn

ADR 0018 made both placeable and the sim moves items through them; nothing on
screen distinguished either from bare ground. What is drawn now, and why:

- **A housing** on each tunnel end, in the same speed shade a belt deck uses,
  darkened. Same colour language as belts so a mixed-tier line still reads as
  one line; standing proud of the deck because a tunnel end is a building, not
  a piece of floor.
- **A mouth**, offset to the side the items actually use — behind, for an
  entrance that swallows them; ahead, for an exit that hands them out. Dark for
  a hole, pale for a spout.
- **Red on both**, when `PartnerOf` returns -1. An unpaired end behaves as a
  plain one-tile belt, which is a live state and not an error, but it is also
  the belt bug that is hardest to see. It gets the loudest colour on the map.
- **Studs on the buried tiles**, drawn once per pair from the entrance. This is
  the only thing on screen that says two holes are the same tunnel.
- **Two chevrons on a splitter**, one straight on and one on the branch, the
  branch dimmer. Which side the branch leaves by is the whole decision the
  player made when they rotated it, so it is drawn rather than implied — and
  drawn with the same chevron a belt uses, so nobody has to learn a second
  notation.

**Cost: one draw batch.** Housings, mouths, splitter bodies and buried studs
are all rectangles, and four pools for four rectangles would have cost four
batches. They share one unit-box `MultiMesh` with a per-instance non-uniform
scale instead. The chevrons reuse the existing arrow pool and cost none.

The defect the same handoff named is fixed in one line: `BuildItems` walked
`TilesOfSegment`, and an entrance's segment includes the tiles the tunnel runs
under, so ore in transit was drawn on the surface — and on top of anything
built over the tunnel. `map.IsBuried(x, y)` answers exactly that question.

## 2. Ground varies, from the worldgen's own numbers

`TerrainRenderer.Colour` returned one flat colour per `TerrainType`, so at play
distance the map read as five blocks of colour. Variation now comes from three
scales at once, all of them pure functions of the seed and the tile:

| Scale | Source | What it buys |
|---|---|---|
| Whole-map | `WorldGen.HeightAt` | High ground pale and dry, low ground dark. Shading follows the same contours that decide where the water is. |
| ~9 tiles | `Noise.Value(cell: 9)` | A field has somewhere to be greener. A single global ramp leaves large flats as smooth as they were. |
| One tile | `Noise.Hash` | No two neighbours match exactly, which is what stops a zoomed-in floor looking painted. |

**Derived from the seed, never from renderer-side randomness.** That is not
tidiness. Screenshots are how rendering is verified in this repository, and a
renderer that rolled its own dice would make every capture a different picture
and every visual regression unprovable.

Water is varied at about a third of the strength, and neither ore patches nor
the shoreline were touched: they were not what read as flat.

**The rebuild is now cached by tile.** Shading needs `HeightAt` as well as
`TerrainAt`, which evaluates the fractal twice per tile, and a rebuild is
triggered by the camera crossing a *single* tile boundary and then re-evaluates
all 37,000 tiles — of which all but two strips were on screen the frame before.
Measured headless: 24.4 ms before this change, 39 ms with shading and no cache,
**25 ms with the cache**. So the shading is free and the pre-existing hitch is
smaller. Caching is sound because the inputs are pure; the only thing that
changes a tile's colour is mining it out, and that already forces a rebuild by
its own counter, where the cache is dropped.

The remaining ~24 ms is filling and uploading the 37,000-instance buffer, which
predates all of this and is untouched.

## 3. Every item has an icon, and all 617 are generated

The player-facing UI was text-only: the build menu listed `"{DisplayName}
x{count}"` for 229 near-identically-worded machines.

**Decision: generate all 617, from what the data already says.** The same
argument ADR 0003 makes for meshes. An authored set at this size would be
incoherent before it was finished and permanently one item behind
`data/spec/progression.json`; deriving each icon from its data row means adding
an item gives it an icon for free and nothing can ship iconless.

### What an icon carries, and what it drops

Category, tier and material are three dimensions and an icon is 32 pixels. An
icon that encodes all three equally reads as noise, so they are ranked:

- **Shape is the category.** Seventeen silhouettes — droplet, ingot, slab,
  coil, gear, lump, crystal, chip, hull-plus-attachment. It is the coarse
  question a player asks of a list, and shape is the only channel that survives
  being small.
- **Colour is the material**, hashed from the item id by *exactly* the fold
  `BeltRenderer.ItemColour` already uses, so a copper plate is the same colour
  in the build menu as it is riding past on a belt. The generator reproduces
  C#'s 32-bit wrap and truncated remainder for that reason.
- **Tier is a segmented bar** along the bottom, one segment per tier. Countable
  up close; at menu size the segments blur into a bar whose *length* is still
  the tier, which is the read that survives downscaling.
- **Form is dropped.** `fluid` is already a category, so form would only add a
  second, quieter copy of the same fact.

The machine icon is a hull with an attachment on top, because that is how
`MeshKit` builds the machine on the map and how `/data` crafts it. Same
structure at three sizes.

### One atlas, not 617 files

617 icon files would be 617 resource loads at startup and 617 texture bindings
in one list. `tools/generate_icons.py` writes a single 936x864 PNG —
`game/icons/items.png`, 59 KB — and the runtime slices `AtlasTexture` regions
out of it. **One load, one texture binding**, and the `AtlasTexture` objects are
made on demand and kept, so a menu of forty buildables allocates forty small
resources rather than six hundred at startup.

Each icon is 32x32 art inside a 36x36 cell. The two-pixel transparent gutter is
not decoration: Godot samples an atlas region with filtering, and without it the
icon next door bleeds into this one's edge. Mipmaps are off in the import for
the same reason.

### Committed, but not CI-diffed

`game/icons/items.png` is committed and regenerated by hand, following
`game/models/*.glb`: the artefact is the deliverable and the script is how it is
reproduced.

It is deliberately **not** given `data/`'s regenerate-and-diff treatment. The
output is a zlib stream, and byte-identical output across zlib versions is not
something to stake a build on — a diff gate there is a flake waiting to fire in
a file nobody edited. The guarantee that actually matters is not "these bytes";
it is "every item has an icon", and that is a number:

```
item icons      known=617 of 617
```

in the `--smoke` report, alongside `belt parts` and `terrain rebuild`. The
runtime matches icons to items **by id**, read from `items.index.json`, rather
than trusting slot order: an atlas generated before an item was added would
otherwise resolve every later item to its neighbour's icon, silently. Matching
on id makes a stale atlas lose icons instead of scrambling them, and the count
above says how many.

Those three lines are new and nothing greps them yet. They are there for QA to
grep if it wants them; no existing line's text changed.

### The one thing icons cost

An `ItemList` grows its rows to fit its icons. Left alone that pushed two
entries off the bottom of the build menu and clipped the `x3` off the end of
every name — an icon that hides the count has taken more than it gave. The icon
is pinned to roughly a line's height and the column widened to pay for the
space, rather than taking it out of the name.

## Alternatives rejected

**Sourcing an icon pack.** Same answer as ADR 0003 gave for meshes, for the
same reason: no pack covers eight industrial tiers coherently, and combining
packs produces exactly the mismatch a list of 617 makes obvious.

**One icon per item file, generated at build time.** Removes the atlas but
keeps 617 loads, and gains nothing — the icons are still generated either way.

**Encoding tier in the hue.** Hue was already spoken for by material identity,
and splitting it between two meanings makes it carry neither.

**Renderer-side noise for terrain.** Cheaper than a second `HeightAt` call, and
it would have made every screenshot a different picture. The cache bought the
same speed without giving that up.
