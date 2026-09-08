# ADR 0034: Drawing the player

The camera was the player. With a real position for the player in `/sim` there
has to be a body standing at it, and this records what that body is and why it
is built the way it is.

## Decision

**One `MeshInstance3D` holding one `ArrayMesh`, not a MultiMesh pool.**
`game/scripts/PlayerRenderer.cs` welds twenty-one boxes into a single
flat-shaded, vertex-coloured surface at `_Ready` and never rebuilds it. `Place`
writes a position and a rotation and nothing else.

The rest of the render layer is instanced because it draws thousands of things.
There is exactly one player, its parts share no mesh, and a pool per body part
is a draw batch per body part -- the same reasoning that made `LandingSite` one
mesh rather than eighteen. Measured with the harness (`--shot=player`, which
prints `draw batches`): total draw calls in a frame went 6 to 9 on open ground
and 42 to 50 in a frame of the demo factory, all of it the one instance and its
shadow-map passes. The number `--smoke` prints, `MachineRenderer.BatchCount`, is
a count of machine pools and is unchanged at 18.

**Not a model from `tools/generate_models.py`.** The kit generator emits parts
that compose -- hulls by tier, attachments by category -- and a figure composes
with nothing. Putting it in the kit would add a `.glb` that exactly one call
site loads, and cost the whole kit a regeneration to move an arm.

## Colour: what was left

The five machine states own green, amber, red, blue and violet, and reading
those on a person would be a lie -- a player standing still is not a starved
machine. Ground is greens and sand, machines are greys, browns and tier colours.
What the palette had not spent was **value** and **magenta**.

So: a bone-white suit, which is the brightest thing on the field and the only
white mass on it, with near-black at the visor, pack and boots so the figure
still has a silhouette when it is fifty pixels tall. Magenta appears three
times and nowhere else in the game. The waist webbing and the pack lid are the
landing site's copper, because the figure came off that probe and should look
salvaged rather than issued.

A first pass flashed both shoulders magenta as well and the figure read as a
pink toy: the accent stopped pointing at anything because it was everywhere.
A first pass also had grey legs the same value as the machine kit, and at the
opening zoom the figure read as a small machine on legs.

## Facing: three cues, because one is not enough at this size

A dark chest panel on the front and a dark pack on the back, so the torso is
light on one face and dark on the other from any camera yaw. A magenta band
across the **front half of the crown**, which is the cue that survives when the
camera looks nearly straight down and the top faces are most of what is drawn.
And a magenta chevron lying on the ground ahead of the feet, which reads at any
zoom the camera allows.

The chevron was a solid arrowhead first and read as a painted patch, the same
mistake the belt arrows made before they became wedges. Two bars with a gap
between them stay a chevron.

## `Place(float x, float z, float facingDegrees)`

World units, one tile to 1.0, matching `MachineRenderer.TileSize`.
`facingDegrees` is clockwise from north, and **north is -Z**, which is the
sim's -Y, matching `Sim.Directions.Delta`. The figure is modelled facing -Z, so
the node's Y rotation is the negation of the angle.

Callers converting from the sim's integer facing want
`Atan2(FacingX, -FacingY)`: the sim's +Y is south, so dropping the negation
draws every player exactly backwards.

Safe before the player has moved, and safe every frame: two property writes and
no allocation.

## The one animation, and why it is not a timer

The figure bobs vertically. The bob is a function of `DistanceWalked`, which is
integrated inside `Place` from the positions the sim actually reported -- a
frame where the player did not move adds nothing and the bob is exactly zero,
so a stopped player is flat on the ground and never frozen mid-hop. A step of
more than a tile in one call is a teleport (a load, a debug jump) and does not
advance the stride.

The rejected alternative is the obvious one: `_Process(delta)` driving a phase.
It costs nothing and it lies -- it walks while the sim holds the player still,
which is the exact failure the whole render layer is built to avoid.

## Seeing it

`scenes/art_preview.tscn` gained a `player` shot, because `GameRoot` and `Boot`
are usually being edited by whoever is wiring gameplay and an art change should
not have to wait on them:

```
xvfb-run -a godot --path game --rendering-driver opengl3 \
    res://scenes/art_preview.tscn -- --shot=player --out=/tmp/p.png \
    [--zoom=30] [--facing=135] [--machines] [--no-player]
```

`--machines` puts a corner of the demo factory in the frame, which is the only
way to judge the size relationship; `--no-player` is the control for the draw
call measurement above.
