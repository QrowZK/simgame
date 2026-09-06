# simgame

A factory-automation game (Factorio-lineage core loop, GregTech-style multi-tier
progression). Greenfield; Phase 0 complete.

## Layout

- `/sim` — plain C# class library, the simulation core. No Godot reference.
- `/sim.tests` — xUnit tests for `/sim`.
- `/data` — `items.json`, `recipes.json`, `machines.json`, `tiers.json`.
- `/data.tests` — xUnit tests validating `/data` (dangling refs, reachability, cycles).
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
