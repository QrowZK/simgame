# ADR 0005: Logistics, drones, and programmable automation

Decision on how player-directed automation (drones, movers, robot logistics)
works. **Not yet built** — this records the shape it must take, so it isn't
relitigated when it is.

## Decision

Three layers, built in this order:

1. **Declarative logistics (WMS).** Storage zones and classes, task queues,
   min/max replenishment, slotting policy, dispatch, congestion rules.
   Configured, not coded. This is the substrate.
2. **Rule tables.** A structured editor for custom dispatch and routing
   priorities — more expressive than settings, still data.
3. **Lua controllers.** An escape hatch, driving the *same task API* as
   layers 1 and 2.

## Why this order

The scripting layer is only worth having if it commands meaningful verbs. Built
on top of a task system, a controller says "replenish slot A7" or "reprioritise
this wave". Built directly on entities, the standard library is `moveTo(x, y)`
and `grab()`, and any factory large enough to be interesting becomes unwritable.
Ordering the layers this way is what decides whether the scripting is expressive
or merely tedious.

It also means the game is fully playable, and every logistics problem solvable,
without anyone writing a line of code.

## Constraints that shape it

**Determinism is the binding constraint.** The sim must be byte-identical after
10,000 ticks (ADR 0001 / the Phase 0 contract), which rules out wall-clock
timeslicing and any ambient randomness. Layers 1 and 2 are deterministic by
construction because they are data. Layer 3 needs an explicit
**instruction-budget scheduler** — each controller gets a fixed quota of
interpreter steps per tick, and the scheduler visits controllers in a fixed
order.

**One interpreter per controller, never per drone.** At the 100k-entity target
a VM per unit is not viable. Controllers command many units. This is also how
real warehouse automation is organised, so the performance constraint and the
realism point the same direction.

**Programs must be event-driven, not long-running.** A coroutine parked mid-task
on a sleep is very hard to serialise, and saves have to work. Programs that run
to completion in response to an event (task available, unit idle, inventory
below threshold) serialise trivially, because the only state to persist is the
data, not an execution stack. This is an API decision that is expensive to
reverse, so it is settled now.

**MoonSharp** for the interpreter when layer 3 arrives: pure managed C#, no
native dependencies, so `/sim` stays engine-free and testable at `dotnet test`
speed. Native LuaJIT bindings would be faster and would cost that property.

## Sequencing

This sits above belts, inserters and fluids in the build order and does not
start until they exist. Drone logistics on top of a world with no belts would be
building the roof before the walls.
