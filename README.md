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

Some headless checks belong to the sim rather than to the renderer, and live in
a small console project so they run on a machine with no display driver:

```
dotnet run --project sim.harness -- --teams-test     # teams, roster, ownership, save
dotnet run --project sim.harness -- --lockstep-test  # two peers, one shuffled command stream
```

`--lockstep-test` takes `--ticks=N` (default 10,000). It runs two worlds from
one seed, feeds them one command stream with one peer's batches shuffled, and
compares state hashes every 60 ticks and saves at the end. See
`docs/0037-commands-the-total-order-and-the-state-hash.md`.

It exits 0 on success and 1 on any failed check, so CI needs no grep. The
scenario is `Sim.TeamSession.Run`, which `sim.tests` drives as well, so the
headless run and the test suite cannot disagree about what passing means.
See `docs/0036-teams-own-progression.md`.

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

Controls: WASD walk (the camera follows), Q/E rotate yaw, mouse wheel zoom, B build, X remove,
R rotate, F5 save, F9 load, F1 script editor, Esc menu.

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
| `--shore-shot` | capture framed on the nearest coastline -- spawn is deliberately inland, so no other capture contains water |
| `--build-shot` | capture with build mode open and a ghost placed |
| `--menu-shot`, `--start-shot`, `--editor-shot` | title screen, a new game, the script editor |
| `--net-test` | a host and a client in one process: roster, messages both ways, clean parting, and every refusal with its reason (ADR 0035) |
| `--net-lockstep-test` | a host and a client in one process playing **one world** for 3,000 ticks: hashes compared throughout, saves compared at the end, then two `GameRoot`s building, digging, removing, retasking and delivering through the real input path (ADR 0039), then one peer corrupted by a milli-tile to prove the desync detector fires (ADR 0038) |
| `--net-shot` | capture the host, join and lobby screens, both states of the in-game net status card, and a real shared world with an action in flight (ADR 0039) |
| `--machines=N`, `--seed=N` | size and seed the placeholder factory |
| `--net-port=N` | the port `--net-test` uses (it also uses N+1 and N+2) |
| `--net-ticks=N` | how many ticks `--net-lockstep-test` plays (default 3,000) |

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
