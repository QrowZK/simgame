# simgame — house rules

Read `README.md` for the layout and `docs/` for why things are the way they are.
Everything below is non-negotiable and applies to everyone working in this
repository, human or agent.

## Before anything else: the toolchain

Neither `dotnet` nor `godot` is on `PATH` in the container this is usually
worked on in, so every command below fails with "not found" until you have run:

```
. tools/env.sh
```

Source it, do not execute it — it exports `PATH`, `DOTNET_ROOT` and `GODOT`
into the current shell, and defines `godot` so the commands read as the README
writes them. Do this once at the start of a session. If it reports that it
found nothing, say so rather than working around it with a hardcoded path: the
Godot download lands in a temporary directory that does not survive a new
machine.

## The rules that CI enforces

- **`/sim` must never reference Godot.** It is a plain C# class library.
  `tools/check-no-godot-reference.sh` fails the build if it does.
- **Zero warnings.** `dotnet build -warnaserror` must succeed.
- **`/data/*.json` is generated.** Edit `data/spec/progression.json`, then run
  `python3 tools/generate_data.py`. CI regenerates and diffs, so hand-editing
  the output is reverted with a failing build.
- **The sim is deterministic.** Fixed 60 UPS, integer arithmetic, no floats in
  anything that affects state. Two worlds from the same seed must stay
  byte-identical for 10,000 ticks.

## The rule that nothing enforces

**Never report a task as done on the basis of code that has not been compiled
and run.** Not "this should work". Run it:

```
dotnet build -warnaserror && dotnet test
./tools/check-no-godot-reference.sh
python3 tools/generate_data.py && git diff --exit-code -- data/
godot --headless --path game -- --smoke --machines=4096
godot --headless --path game -- --session-test
```

Those last two are the ones people skip and the ones that catch the most: the
session test plays every loop end to end, and the smoke run reports numbers a
screenshot cannot show you.

Rendering changes are not verified until you have *looked* at a screenshot:

```
xvfb-run -a godot --path game --rendering-driver opengl3 -- --screenshot
```

Every visual defect this project has had was found by looking, not by reading.

## How we know the tests are worth anything

**Mutation testing is standard practice here, not an occasional luxury.** After
writing a test, break the code it covers — one field, one condition, one
constant at a time — and confirm the test fails. Restore, and repeat.

Every survivor so far has been a hole in the *test*, not the code:

- A conservation test that read as though it counted eight items was counting
  one, because `TryInsertBack` fills the back of a lane and every later call
  fails silently.
- A save test laid every belt facing east at one speed, so dropping facing
  *and* dropping speed both round-tripped unnoticed.
- A bank of accumulators all at the same charge hid a distribution bug.

When a mutation survives, the test world is usually too tidy. Make it messy —
mid-cycle, part-full, mixed — and assert exact numbers rather than "greater
than zero".

## Design rules worth knowing before you propose anything

- **Footprint is the only throughput dial.** A machine runs `size²` batches and
  costs `size²` to build. There is no speed table. A new machine is only
  interesting if it offers a better input ratio, earlier access, or a product
  nothing else makes.
- **Refusals carry reasons, not bools.** "You have none", "something is there"
  and "no ore under it" need different sentences in front of a player.
- **Save format changes bump the version** and refuse older files rather than
  loading them wrong.
- **Write down why, not what.** The code says what it does. Comments and ADRs
  exist for the decision behind it and the alternative that was rejected.

## The team

Three specialists work in this repository. `docs/team/README.md` says who owns
what and how work passes between them; `docs/team/board.md` is where open
handoffs live.
