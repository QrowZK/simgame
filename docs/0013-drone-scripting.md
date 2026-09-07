# ADR 0013: Drone scripting in Lua

Implements layer 3 of ADR 0005, and **revises two of its decisions**. Both
revisions are recorded here rather than made quietly.

## What was confirmed

**MoonSharp.** Pure managed C#, no native dependency, so `/sim` stays
engine-free and testable at `dotnet test` speed. Probed before committing to it:
version 2.0.0 restores cleanly and behaves as needed.

**The instruction-budget scheduler.** MoonSharp's `AutoYieldCounter` force-
suspends a coroutine after a fixed number of instructions, which is exactly the
mechanism ADR 0005 required. Every controller gets `StepsPerTick` steps, and the
scheduler visits controllers in index order, so a program's progress is a
function of the tick number and nothing else. `while true do end` — which a
player will write on their first day — costs one tick's budget and no more.

**One interpreter per controller, never per drone.** At the 100k-entity target a
VM per unit is not viable, and a controller commanding many units is how real
warehouse automation is organised.

## Revision 1: programs may be long-running

ADR 0005 said programs must be event-driven, because "a coroutine parked
mid-task on a sleep is very hard to serialise". That reasoning was sound and its
conclusion was too strong.

The serialisation problem is real: MoonSharp cannot save a suspended
coroutine's stack. But the answer does not have to be giving up imperative
programs — which is what ComputerCraft-style scripting *is*, and what was asked
for. Instead:

- A program **restarts from the top on load**.
- Anything it needs to remember lives in `state`, a string table that **is**
  saved.
- The drones' physical state — position, cargo, current task — is world state
  and saves normally, so the factory does not miss a beat; only the program
  counter is lost.

So `while true do ... world.sleep(30) end` is a legal and normal program.

The cost is honest and worth naming: a program that keeps important progress in
a local variable rather than in `state` will redo work after a load. That is a
documented property of the API, not a bug to be discovered.

## Revision 2: task-shaped API, without waiting for the full WMS

ADR 0005 sequenced declarative logistics → rule tables → Lua, on the grounds
that scripting is only worth having if it commands meaningful verbs — `moveTo(x,
y)` and `grab()` make any factory large enough to be interesting unwritable.

That insight is kept; the sequencing is not. The API is **task-shaped from the
start**:

```lua
queue.haul('magnetite', 200, mineX, mineY, smelterX, smelterY)
```

A controller says what needs to happen and the dispatcher decides who does it.
The full WMS (zones, classes, slotting, replenishment policy) can be built later
*underneath* this call without the scripting API changing, which is the property
the original sequencing was protecting.

## Dispatch is deliberately dull

The lowest-numbered idle drone takes the oldest pending task. Nearest-drone
dispatch would be smarter and would make assignment depend on distance
comparisons and tie-breaks — the exact thing that turns a reproducible factory
into a nearly-reproducible one. Smarter routing belongs in the controller
script, where the player can see it and it stays data.

## Determinism, specifically

- `math.random` and `math.randomseed` are **removed**, not seeded. A seeded
  generator is deterministic but would still make two identical factories
  diverge the moment one controller called it a different number of times.
- `os` and `io` are absent (hard sandbox), verified by test rather than assumed.
- `pairs` iteration order is insertion order and stable across runs — probed
  before relying on it.
- Controllers run **last in the tick**, on a world that has finished moving, so
  a program reading a machine's output sees a settled number rather than one
  that depends on where in the tick it asked.

## A design bug the tests caught

The first dispatcher treated a source with nothing in it as permanently dry and
finished the task immediately. But a source that has not produced *yet* is the
normal case — a smelter fills over its cycle — so every task queued against a
working machine was abandoned on arrival.

Drones now wait `Patience` ticks (ten seconds) at a source before giving up.
Long enough to cover any machine's cycle; short enough that sending a drone to a
machine that will never produce is visible rather than a silent hang.

## The one thing that does not round-trip

Because a program restarts from the top, a world with controllers is **not**
byte-identical across save and load: the reloaded copy has executed its opening
lines a second time. Everything else — drones, tasks, machines, the grid, the
pipes — still matches exactly.

Rather than quietly excluding controllers from the save test, this is asserted
directly: memory survives the trip untouched, and the program then starts again
from its first line. Both halves are tested, so neither can change without
saying so.

## Verification

Nineteen tests across drones, controllers and saves. The properties held to are:
an infinite loop cannot hang the sim, a syntax error is a message rather than a
crash, one controller's runtime error does not stop another, the sandbox is
closed, nothing is created or destroyed in the carrying, distance costs time,
and **the same program produces the same factory twice**.

The instruction budget proved itself the hard way. Removing `AutoYieldCounter`
as a mutation did not merely fail a test — it **hung the test runner outright**,
because `while true do end` then runs forever inside a tick. That is the clearest
possible demonstration that the budget is load-bearing, and it is why the
mutation sweep for this ADR treats a timeout as a caught mutation rather than as
an inconclusive run.
