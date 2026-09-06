# simgame

A factory-automation game (Factorio-lineage core loop, GregTech-style multi-tier
progression). Greenfield; Phase 0 complete.

## Layout

- `/sim` — plain C# class library, the simulation core. No Godot reference.
- `/sim.tests` — xUnit tests for `/sim`.
- `/data` — generated game data. `spec/progression.json` is the hand-authored source.
- `/data.tests` — xUnit tests validating `/data` (dangling refs, reachability, tier gating).
- `/game` — Godot 4 project (not yet scaffolded; Phase 1).
- `/tools` — one-off scripts (e.g. the no-Godot-reference CI check).
- `/docs` — architecture decision records.

## Building and testing

Requires .NET 8 SDK.

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
```

CI regenerates and diffs, so committed data cannot drift from the spec.
The progression design is documented in `docs/0002-progression-design.md`.
