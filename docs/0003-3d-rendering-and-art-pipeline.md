# ADR 0003: 3D meshes, angled camera, and the art pipeline

Settles the last open question from the handoff: 2D sprites with a faked angle,
or real 3D meshes under an angled camera.

## Decision

**Real 3D meshes under an angled, freely-rotatable camera.** Fixed pitch (50°
default), free yaw, zoom, pan. Orthographic by default for the classic factory
read; switching to a long-focal perspective is a one-line change on `CameraRig`.

Yaw rotates freely rather than snapping to 90°. Snapping only exists to hide
that sprites have a fixed facing; with real meshes there is nothing to hide.

## Consequence: parts must be generated, not sourced

The scoped progression has 26 machine categories across 8 tiers (~130 machine
variants) and 420 items. Two things follow.

**Sourcing from asset packs is the wrong route.** No pack covers eight
industrial tiers coherently, and combining packs to fill the gaps produces
exactly the mismatch a rotatable camera makes obvious. Coherence has to come
from construction, not from curation.

**Bespoke meshes are also a rendering problem, not just an art-budget one.**
MultiMesh batches per unique mesh. One mesh per machine variant means ~130
batches; the modular kit below means 18, with per-instance colour carrying tier
and status. At the 100k-entity target that difference is the whole ballgame.

So a machine is drawn as a shared **hull** (chosen by tier) plus a function
**attachment** (chosen by category) — 8 + 10 meshes composing into every
variant. This deliberately mirrors how machines are crafted in `/data`: hull
plus function part. The art structure and the recipe structure are the same
structure.

Items get the same treatment: the form ladder means an iron plate and a copper
plate are one mesh with different per-instance colour, so ~10 form meshes cover
all 420 items.

**Authoring is parametric** — `tools/generate_models.py` runs Blender headless
and emits the whole kit from parameters, so it stays coherent by construction
and the entire look can be regenerated when the art direction changes:

```
blender --background --python tools/generate_models.py
```

It writes 8 hulls, 10 attachments, a belt and a pipe as `.glb` into
`game/models/`, with a generated plate-and-rivet texture (deterministic, so
regenerating produces no spurious diff). One tile is 1.0 unit; every part is
centred on X/Z, sits on Y=0 and stays inside a 1x1 footprint, so placing at
integer tile coordinates lines up exactly.

Blender is not in CI, so the committed `.glb` files are the artefact and the
script is how they are reproduced.

Two details the first render exposed. Corner posts standing proud of the body
read as spikes rather than structure, so they are now flush. And attachments
were seated at a fixed height, which left them floating above the short tiers
and buried in the tall ones — `MeshKit.DeckHeight` now mirrors `hull_height()`
from the generator, which prints the expected values on every run.

`MeshKit` loads `res://models/hull_{tier}` and `res://models/attach_{category}`
(`.tres`, `.res`, `.glb` or `.gltf`) when present and falls back to procedural
placeholders otherwise, so parts can land one at a time with no renderer change.

An authored mesh keeps its own material — that is where the texture lives — so
the renderer only applies its shared material override to placeholders. The
authored material has `VertexColorUseAsAlbedo` switched on at load, so
per-instance tier and status colour still multiplies through the texture rather
than being lost.

## Measured: why instances are uploaded as buffers

The obvious implementation — `SetInstanceTransform` and `SetInstanceColor` per
entity — costs two C#/engine boundary crossings per entity per frame. Measured
headless at 4096 machines that was **3.30 ms/frame**, most of a 60 UPS budget
spent on marshalling alone.

Filling a managed `float[]` and assigning `MultiMesh.Buffer` once per pool
turns that into plain memory writes plus 18 interop calls per frame:

| Machines | Sim tick | Render sync (setters) | Render sync (buffer) |
|----------|----------|-----------------------|----------------------|
| 4,096 | 0.25 ms | 3.30 ms | **0.34 ms** |
| 100,000 | 4.10 ms | — | **7.81 ms** |

At 100k machines the frame costs 11.9 ms of the 16.67 ms budget.

**What that number does not prove:** these runs are headless, so no
rasterisation happened — the GPU cost of 200k instanced draws is still
untested. The game *has* now been seen: `--screenshot` under `xvfb-run` with the
OpenGL driver renders and saves a frame, which is how the two issues above were
found. That path is software-rasterised, so its frame rate means nothing. The sim tick is also still Phase 0's dictionary-backed machine
storage, and chunked updates (skipping idle chunks) are not implemented yet.
Both are known headroom.

## Structure

`/game` stays thin and owns no state. `GameRoot` ticks the `World` at a fixed
60 UPS with a bounded catch-up, then hands the renderer a read-only view;
`MachineRenderer` only reads. The headless smoke test asserts this by rotating
the camera and checking the tick count is unchanged.

Machine positions live in `/sim` as `MachinePlacement` in a dense array, exposed
as `ReadOnlySpan`. World layout is game state, so the sim owns it — but it is
stored for bulk reads, not as objects to walk.

`/sim` still has no Godot reference; the CI grep still enforces it.
