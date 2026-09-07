# simgame

A factory-automation game (Factorio-lineage core loop, GregTech-style multi-tier
progression). Phase 0 (sim core) and Phase 1 (Godot render layer) complete.

## Layout

- `/sim` — plain C# class library, the simulation core. No Godot reference.
- `/sim.tests` — xUnit tests for `/sim`.
- `/data` — generated game data. `spec/progression.json` is the hand-authored source.
- `/data.tests` — xUnit tests validating `/data` (dangling refs, reachability, tier gating).
- `/game` — Godot 4 project. Camera rig + MultiMesh renderer. Thin: reads sim state, owns none.
- `/tools` — one-off scripts (e.g. the no-Godot-reference CI check).
- `/docs` — architecture decision records.

## Building and testing

Requires .NET 8 SDK. In a container where the toolchain is not on `PATH`, run
`. tools/env.sh` first.

```
dotnet build      # must be zero warnings
dotnet test       # sim + data invariant tests
./tools/check-no-godot-reference.sh
```

## Game data

`data/*.json` is **generated**. Edit `data/spec/progression.json`, then:

```
python3 tools/generate_data.py     # regenerate data/*.json
python3 tools/dump_progression.py  # human-readable ladder + critical paths
python3 tools/factory_plan.py      # machines needed to build any target
```

CI regenerates and diffs, so committed data cannot drift from the spec.
The progression design is documented in `docs/0002-progression-design.md`.

## Running the game

Requires Godot 4.3 (.NET/Mono build).

```
dotnet build game/Game.csproj
godot --path game                              # play
godot --path game --headless -- --smoke        # headless verification
godot --path game --headless -- --machines=100000   # stress the renderer
```

Controls: WASD pan, Q/E rotate yaw, mouse wheel zoom, B build, R rotate,
F5 save, F9 load, F1 script editor, Esc menu.

```
xvfb-run -a godot --path game --rendering-driver opengl3 -- --screenshot
```

Headless flags, all of which live in `game/scripts/Boot.cs` and
`game/scripts/GameRoot.cs`:

| Flag | What it does |
|---|---|
| `--smoke` | tick, render and report counts, timings and invariants |
| `--session-test` | play every loop end to end: save, build, belts, power, fluids |
| `--screenshot` | capture the world with a machine panel open |
| `--belt-shot` | capture framed on the belt line |
| `--build-shot` | capture with build mode open and a ghost placed |
| `--menu-shot`, `--start-shot`, `--editor-shot` | title screen, a new game, the script editor |
| `--machines=N`, `--seed=N` | size and seed the placeholder factory |

Godot ignores an unrecognised flag silently, so a capture that looks like the
default usually means the flag name is wrong.

## Art

Machine models are generated, not hand-modelled:

```
blender --background --python tools/generate_models.py
```

This writes the whole kit — 8 tier hulls, 10 function attachments, a belt and a
pipe — into `game/models/` as `.glb`. One tile is 1.0 unit and every part is
grid-aligned, so machines snap to integer tile coordinates.
Rendering and art-pipeline decisions are in `docs/0003-3d-rendering-and-art-pipeline.md`.
