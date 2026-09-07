# 0027 — A shipped game carries its own data

## Context

v0.1.0 was published, and New Game did not work. The player report was
immediate; the reproduction was one click:

```
System.IO.DirectoryNotFoundException: Could not locate a 'data' directory
above /tmp/relcheck/lin/data_Game_linuxbsd_x86_64/
```

`GameData` read its five JSON files by walking up from the binary until it found
a directory called `data` containing `items.json`. In a checkout that finds the
repository's own `data/`. An exported build ships no such directory, so the walk
ran off the top of the filesystem and threw — on the first thing a player does.

**Every check passed.** 298 unit tests, `--smoke`, `--session-test`, and the
release workflow's "the exported build must actually run" step. The reason is
worth writing down, because it is the general lesson rather than a detail about
JSON:

- The tests run inside the checkout, so the walk always succeeded.
- The **exported binary** was run from `dist/` — which is *inside the
  checkout*. Walking up from `dist/data_Game_linuxbsd_x86_64/` reaches the
  repository root and finds `data/`. The artifact was tested in the one
  location where its bug is invisible.

Verifying an artifact where it was built is not verifying it. The whole class of
defect this belongs to — "works because of something in the build environment" —
is exactly what an artifact test is supposed to catch, and placing that test
inside the build tree defeats it completely.

A second bug surfaced in the same investigation. The menu's blank-seed path:

```csharp
seed = (int)(Time.GetUnixTimeFromSystem() * 1000) & 0x7FFFFFFF;
```

The milliseconds are about 1.7e12. Casting that double straight to `int`
saturates to `int.MinValue`, whose low 31 bits are zero — so **every "surprise
me" world was seed 0**: the same world, every time, for every player. Nothing
caught it because every test names its own seed.

## Decision

**The data travels inside the assembly.** `Sim.csproj` embeds `data/*.json` as
resources and `GameData.Instance` reads those. The filesystem walk stays, but
only for tools and tests: the generator writes the files, and a test compares
the embedded copy against them so the shipped recipes cannot drift from the
repository's.

`/sim` still references no Godot — an embedded resource is plain .NET, so the
data loads identically in the game, the tests and the generator.

**The artifact is tested away from the source tree.** The release workflow
copies the exported build into `$RUNNER_TEMP/play` and runs it there. Nothing in
that directory can rescue a binary that depends on its build environment.

**`--menu-test` presses the button a player presses.** It shows the menu, emits
`NewGameRequested` with the seed the menu itself invents, and runs the world
that comes back. It runs in CI and again against the exported artifact. It would
have caught both bugs: the missing data on the first, and the constant seed on
the second, because CI asserts the seed is not zero.

## Consequences

`data/*.json` is now compiled into `Sim.dll`, so a build carries about 400 KB it
did not before, and changing the data means rebuilding rather than editing files
beside the binary. Editing them beside a shipped binary was never supported —
it only looked supported to whoever was standing in a checkout.

The rule this project already had — *never report a task as done on the basis of
code that has not been compiled and run* — was followed here, and still shipped a
broken build. It needs the addition that this ADR exists to record: **run it
where a user would run it**. A binary tested only where it was built has not been
tested.
