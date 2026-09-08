# ADR 0037: Commands, the total order, and the state hash

Slice 3a of multiplayer, entirely inside `/sim`: a command type, deterministic
application, a hash that catches divergence, and a harness that proves the
three of them together. Nothing here knows a network exists. The driver that
pumps commands across the wire is slice 3b.

## The design objection, stated and then set aside

On its own this slice adds no decision a player makes. It is a data type, a
sort, and a checksum. But it is not neutral either, and two of the choices
below are already gameplay:

- **A refusal is part of the simulation, not a local UI decision.** Every peer
  computes the same reason for the same command, and the reason is folded into
  the world's hash. That is what makes "that belongs to another team" a rule of
  the world rather than a message one client decided to show.
- **The total order is by player id, so ties on the ground go to the lower
  numbered player**, deterministically, forever. Two people clicking the same
  tile in the same 16 ms is not a rare case in multiplayer; it is the first
  thing two players do. There is no race — the earlier joiner wins — and that
  is a rule players will notice.

## The command

One closed value, `PlayerCommand`, covering move intent, dig by hand, build,
remove, retask and hand delivery. It carries `(Tick, PlayerId, Sequence)`, a
kind, `X`, `Y`, `Amount`, `Facing`, and an item and a recipe **by name**.

**Names, not `ItemId`s.** An `ItemId` is an index into the running
`ItemDatabase`, stable only while registration order is; the save file already
writes the name table for exactly this reason. Ten bytes on a command that
occurs a handful of times a second is cheaper than one class of silent
divergence where a build places the wrong machine on one peer.

**A tile, not a machine index, for `ChangeRecipe`.** Removing a machine moves
the last machine into its slot (swap-remove, ADR 0028), so an index issued on
the tick a player clicked can name a different machine by the tick it is
applied. A tile cannot move.

**Rejected: a class per action.** Polymorphic commands mean a polymorphic
decoder, which is one more place for two builds of the game to disagree about
what an unknown byte means. One flat struct wastes a few bytes on a Move that
carries no item and is the same shape on every peer.

## The encoding

`CommandCodec`: a two-byte magic, a one-byte format version, a count, then a
fixed 30 bytes per command plus two length-prefixed ASCII names. Explicit
little-endian, written by hand.

**Rejected: JSON, and any reflective serialiser.** Both make the wire format a
consequence of a type's field order, so a refactor that reorders two fields is
a silent protocol change between two builds that both claim the same version.
The format byte here changes when the layout does, and is deliberately separate
from the save version and from the network protocol string: reading a save and
reading a command are different capabilities.

Every read is bounds-checked and every enum is range-checked. A corrupt or
hostile packet raises `CommandFormatException` at the door rather than becoming
a command with a nonsense kind halfway through a tick — under lockstep there is
nothing useful a peer can do with half a command, and guessing is how a desync
becomes untraceable.

## The total order

`World.ApplyCommands` **sorts, always**, on `PlayerCommand.CompareTo`, which
compares `(tick, player, sequence)` and then *every remaining field*. A caller
cannot opt out by pre-ordering its list: the sort happens regardless and is
idempotent on a sorted one.

Comparing the whole command rather than only the key is the point. A comparison
on the key alone can tie, and a tie leaves the sequence to the sort's
stability — which preserves **arrival order**, the one input two peers do not
share. With every field compared, two commands can only tie when they are
byte-identical, and applying two identical commands in either order is the same
world. The second is refused as `Duplicate` anyway, detected by a comparison
with the previous element rather than by a set, because a set's enumeration
order would be one more thing to pin down.

The harness proves the property empirically over 10,000 ticks with one peer's
batches shuffled; `ApplyingATicksCommandsInAnyOrder_LandsOnOneWorld` proves it
exhaustively over all six permutations of three players wanting one tile.

## Refusals are simulation

Every command, applied or refused, is folded into `World.CommandDigest` along
with its outcome and the number it produced. This exists because **a refusal
costs nothing** — that is the house rule, and it means a peer that never
received a refused command is otherwise indistinguishable from one that
received and refused it. With the digest, a dropped refusal is a divergence
caught at the next comparison. So is a peer that refused the same command for a
*different reason*, which is the subtler and worse case, since two peers that
disagree about why are two peers about to disagree about what.

`CommandOutcome` is one flat enum of reasons with a sentence each, mapping the
existing `BuildResult` / `RemoveResult` / `DigResult` / `RecipeChangeResult` /
`DeliveryRefusal` values. The command layer **dispatches to the existing
rules and invents none**: bare ground is still answered before reach for a dig,
ownership is still answered before reach for a removal.

**A limitation this exposes rather than fixes.** `TryChangeRecipe` has no reach
check (ADR 0021 never added one), so a command can retask a teammate's machine
from anywhere on the map. That is today's single-player rule reaching
multiplayer unchanged. Adding a radius here would give the sim two answers for
one action, so it is named here and left for whoever decides the rule.

## The state hash

`World.StateHash()` is FNV-1a over the same state the save walks, in the same
fixed orders — orders already pinned by the byte-identical-save property, so
the hash inherits a guarantee instead of inventing a second one. FNV by hand
rather than a library hash because the hash is part of the determinism
contract: `string.GetHashCode` is randomised per process in .NET, which would
put the desync detector inside the class of bug it exists to find. Strings are
length-prefixed, so "ab"+"c" and "a"+"bc" are different hashes.

Measured at **≈90 µs** on the harness world, computed every 60 ticks — a
hundredth of a percent of a second of game time.

**Covered:** tick, seed, the command digest and counts, every team (name,
research, unattended deliveries), every player (name, team, position, facing,
inventory), every anchor's item and owning team, machines, miners, extractors,
ground depletion, poles, generators, accumulators, belt tiles, every item on
every lane with its gap, splitters, inserters, fluid nodes and networks, drones,
haul tasks, and controller programs with their variables.

**Excluded, deliberately:**

- **`LocalIndex`** — which player this peer looks through. Every peer has a
  different one *by definition*; hashing it would make two correct peers
  disagree on the first comparison.
- **`Player.Intent`** — what the keyboard is holding. Not saved (a loaded world
  stands still, ADR 0033) and not needed: the nine possible intents produce nine
  distinct displacements, so an intent that differs shows up as a position that
  differs on the very next tick, and position is hashed.
  `TwoDistinctIntents_AlwaysMoveAPlayerToTwoDistinctPlaces` holds that claim up.
- **The item name table** — built from `data/`, which the version handshake
  (ADR 0035) already pins at the door.
- **Renderer state** — caches, dirty flags, belt map versions. Nothing in the
  tick reads them.

Everything excluded is a thing two peers may silently disagree about forever,
so the list is short and each entry has a reason above.

## Save format 16

The command log — digest and the two counts — is saved, and 15 and below are
refused. The digest does not affect future simulation, so this is not strictly
state; it is saved because a hash that changed across a save would read as a
desync on the first comparison after a load, and a host that saves and clients
that load is where this feature is going. The digest is written as a *string*:
`ulong` in JSON is a number, and a reader with 53 bits of mantissa rounds it.

## The harness

`sim.harness --lockstep-test` runs two worlds from one seed, feeds them one
command stream, shuffles one peer's batch every tick, and compares hashes every
60 ticks for 10,000 ticks, then compares saves byte for byte and reloads one.
`LockstepTests.TenThousandTicks_OfAShuffledCommandStream_StayHashIdentical`
drives the same entry point, so the headless run and `dotnet test` cannot
disagree about what passing means.

The stream is untidy on purpose: four players on two teams, two teams' walks
crossing one ore patch, jittered movement that has to be corrected, digging
until the patch is worked out, building, removing and rebuilding, a teammate's
tile contested, a rival's building attacked from far away and up close,
retasking, hand deliveries, and — mixed in — a command for the wrong tick, a
command twice, a player who does not exist, an item this world has never heard
of, and a recipe this game does not contain. It prints counts per outcome and
**asserts each of thirteen outcomes occurred at least once**, because a stream
that quietly stopped producing refusals would pass forever while proving
nothing.

Last run: 13,461 commands issued, 2,494 applied, 10,967 refused across 16
distinct outcomes, 332 hashes at ~90 µs, 10,000 ticks, no divergence, identical
saves.

## What is not here

No sockets, no scheduling, no input delay, no buffering by tick, no UI. The
seam for slice 3b is exactly two things: `CommandCodec.Encode/Decode` for the
transport's opaque `byte[]`, and `World.ApplyCommands(builds, recipes, batch)`
called **before** `World.Tick()` for the tick it names. Deciding *which* tick a
command is addressed to — the input delay, the lock-step wait, what to do about
a peer that is late — is scheduling policy and belongs with the driver that can
see the network.
