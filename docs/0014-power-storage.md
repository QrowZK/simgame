# ADR 0014: Power storage — accumulators

The grid built in ADR 0011 has no memory. Supply is whatever the generators
burn this tick, demand is whatever the machines want, and the difference is
either wasted or a brownout. That makes every grid a peak-load grid: to run a
factory whose demand spikes for twenty ticks in every hundred, you must build
generation for the spike and idle it the rest of the time.

Accumulators give the grid a memory: a tick's surplus can pay for a later
tick's shortfall.

## What an accumulator is

Three numbers, and nothing else:

| Field | Decision it represents |
|---|---|
| **Capacity** | how long a gap it can cover |
| **RatePerTick** | how deep a gap it can cover |
| **Charge** | what it is holding now |

Capacity and rate are deliberately separate, as they are in GregTech: a large
slow bank rides out a long night, a small fast one absorbs a spike. One
"storage" number could not express the difference, and the difference is the
whole decision.

`State` reports Blocked when full and Starved when empty, so an accumulator
reads through exactly the same tint and panel as every other machine rather
than needing its own vocabulary.

## Where it sits in the tick

Storage brackets the consumer allocation:

1. `DischargeStorage` — top the supply up towards demand
2. allocate to machines, miners and extractors
3. `ChargeStorage` — put what is genuinely left over into storage

This ordering is the whole safety property, and it is tested
(`ChargingNeverCompetesWithProduction`): a factory must never brown out because
its batteries were filling. Charging sees only the surplus that survived
production, so a grid that exactly meets its demand stores nothing and runs
everything — rather than storing a little, browning out, and discharging it
back next tick.

Discharge is capped at demand for the same reason in reverse: energy pulled out
of a bank and not used would simply vanish.

## Sharing a bank out

Both directions distribute in proportion to what each accumulator can take or
give, using the same exact-integer running carry as the consumer allocator. No
floats, so no drift and no divergence between machines.

Two things this got wrong on the way, both caught by tests rather than by
reading:

- **The amount must be settled before the loop.** Reading the shortfall inside
  the loop meant each `Release` raised the supply, so the shortfall shrank as
  the loop ran and the first accumulators carried the whole load. The bank
  ended at `2000, 2900, 3200, 3800` when it should have been level.
- **The remainder must rotate.** The carry hands the division's remainder to
  whoever is processed last, so a fixed order gave the same accumulator the
  extra unit every single tick: three accumulators splitting 100 settled at
  `2970, 2970, 3060`. The distribution order is rotated by `TickCount`, which
  keeps it deterministic and keeps the bank level.

The invariant that matters is that a bank reads as one number. Accumulators at
unrelated levels would make "how much buffer do I have" unanswerable without
clicking each one.

## Rendering

Accumulators go through the machine renderer's existing hull and attachment
pools — they are machines as far as the renderer is concerned, and their own
pool would cost a draw batch for one more silhouette.

Their attachment is tinted by charge rather than by machine state: dead slate
at empty, bright green at full, linear in between. State would only say
"charging, full, empty", and the question a player has standing in front of a
bank is how much is left in it.

## Saves

The save format goes to version 6. Capacity, rate and charge are all stored;
charge especially, since dropping it would either hand the player a free full
bank or wipe one they spent a night's surplus filling. The busy world the save
tests round-trip now contains a part-full accumulator, and `AssertMidFlight`
fails if storage is ever at an extreme when the fingerprint is taken — an empty
bank round-trips through a save that drops the charge entirely.

## Data

`accumulator` is a machine in `machines.json` at every tier, with an item and a
build recipe per tier mirroring the generator's, spending the generator's
motors on cable instead: a battery moves charge, it does not turn anything.

The starter kit deliberately does not include one. Storage is a thing you build
once you have a grid worth buffering.
