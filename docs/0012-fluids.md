# ADR 0012: Fluids — pipes, tanks, pumps and sources

The previous fluid model was a placeholder: networks were *declared* with a tile
count, existed nowhere on the map, and nothing consumed them. 154 of 668 recipes
touch a fluid, and none of them could run. This makes fluids a placed,
connected, consumable system.

## Networks come from what you build

A network is a connected run of orthogonally adjacent nodes — the same
component-finding the power grid does, for the same reason: the player builds a
shape and the shape decides what is connected, rather than the player declaring
a network.

Three node kinds, because they do three different jobs:

| Node | Volume | Flow | Decision it represents |
|---|---|---|---|
| **Pipe** | a little | its bore | route, and how wide |
| **Tank** | a lot | none | buffer a line against a spike |
| **Pump** | none | its rate | get past the narrowest pipe |

Collapsing them into one "pipe with properties" would hide that a tank adds no
throughput and a pump adds no volume.

**A run's rate is its narrowest pipe**, raised by the best pump on it. That is
the thing players already say about these systems — one bronze segment in a
steel line throttles the whole run — and it makes pumps a real answer to a real
problem rather than a strictly-better pipe.

Volume is everything summed. So more pipe buys volume, wider pipe or a pump buys
flow, and a tank buys only buffer: three distinguishable decisions rather than
one number that goes up.

## Contents live on nodes, not networks

Rebuilding components has to move the fluid across. Each node carries its share
of its old network by capacity, so **splitting a run divides the fluid where the
cut was** and joining two runs adds them together. Storing contents per network
instead would have to guess how to divide a split.

Joining two runs carrying **different** fluids keeps the larger and destroys the
rest — and counts it in `VoidedByMixing`, which the HUD can show. Silently
keeping one would leave a player unable to work out where their oil went. A
network still refuses a second fluid outright while it holds one; the void only
happens when geometry forces the merge.

## Machines are plumbed, not belted

`RecipeInput`/`RecipeOutput` now carry `IsFluid`, set from the `form` field the
data files already had. The world fills a machine's fluid inputs from the
network it is standing beside, before the machines run, and drains its fluid
outputs into that network afterwards — exactly as it hands out power.

`Machine` itself needed **no change at all**: fluids live in the same buffers as
solids, and the world does the moving. One transport concept per direction
rather than two parallel systems inside every machine.

Connection is to pipe running *alongside* the footprint, not underneath it,
which is what lets a 3×3 sit on bare ground and still be plumbed.

## Fluids need a source

`FluidExtractor` is the fluid counterpart of `Miner`, deliberately the same
shape: it fills a small buffer on a cycle and the world moves that into whatever
it is plumbed to.

An oil derrick on a crude patch **depletes it** exactly as a miner depletes ore
— so `Catalogue.RawSolids` now includes raw fluids that come out of the ground,
and worldgen buries oil alongside the ores. A water pump standing in a lake does
not deplete anything, because a lake is not a resource you can exhaust at this
scale; `AmbientFluids` names the two (water, air) that work that way.

Placing one anywhere else is refused, rather than producing a building that
never runs.

## Two bugs the tests caught, and two the mutations did

Writing the tests found:

- **Fluid created out of nothing.** The node→network array is over-allocated,
  and an unused slot defaults to 0 — which reads as "network 0" on the next
  rebuild, so every newly placed pipe inherited a share of an existing
  network's contents. Adding ten pipes 20 tiles away raised the world's total
  fluid from 200 to 320.
- An index-out-of-range from ticking extractors before the network cache was
  refreshed.

Then eight mutations, one behaviour at a time. Six were caught; the two that
survived were both **conservation at the input boundary**, and both would have
been invisible in play until a factory quietly stopped adding up:

| Survivor | What it would do |
|---|---|
| Give the machine what it asked for, not what the pipe had | Machines run on fluid that was never pumped |
| Empty the extractor regardless of what the pipe took | A full line destroys everything the pump raises |

Both now have tests: a network stocked with exactly two and a half cycles' worth
must buy exactly two cycles, and a derrick against a one-tile pipe must back up
with its buffer intact rather than lose it.

## Verification

17 fluid tests plus the extended save coverage; all ten mutations caught after
the two fixes above. Save format is version 4 — networks are no longer stored at
all, since they are a consequence of where the nodes sit, and rebuilding them on
load is what keeps the file from disagreeing with the layout it also stores.

`--session-test` plays the loop headlessly and CI asserts it: find open water,
stand a pump in it, run pipe inland to an ore washer, and check the washer runs
on what the pump pulled.
