# 0038 -- The lockstep driver: host as sequencer, six ticks of delay, and stopping on desync

Slice 3b of multiplayer. Slices 2 and 3a built two proven halves -- a transport
that carries an opaque `byte[]` on one reliable ordered channel (ADR 0035) and
a command layer that turns a player's action into bytes and applies a tick's
worth of them in a total order (ADR 0037). This is the piece between them:
`game/scripts/LockstepDriver.cs`, which makes two machines play one world.

The seam is exactly what 3a left: `CommandCodec.Encode`/`Decode` for the bytes,
and `World.ApplyCommands(...)` called **before** `World.Tick()` for the tick it
names. Nothing in `/sim` changed for this slice.

## Host as sequencer

Clients send their own commands to the host. The host merges each tick's
commands from everyone and broadcasts **one authoritative batch per tick**.
Every peer -- the host included -- then applies that batch and ticks.

This is not host authority over *state*. The host's world is an ordinary world.
It runs the identical simulation from the identical batch, it is not consulted
about what happened, and when it disagrees with a client the session **stops**
rather than the client being corrected. The host's only authority is over
**which commands belong to which tick**, and that is the one question two peers
provably cannot answer identically on their own: each of them sees a different
arrival order, and arrival order is the one input a network refuses to share.

Why the host and not a vote or a token ring: the ENet topology is already a
star (ADR 0035), so the host is already in the middle of every message. A
peer-to-peer sequencer over a star transport is a mesh emulated on a hub, with
every peer's commands crossing the host twice.

The cost is honest and worth naming: the host has half a round trip less
latency than everyone else, and if the host leaves, the session ends. Both are
consequences of the star, not of this decision.

## Every peer speaks every tick, even to say nothing

Each peer sends an input message for every tick, empty or not. That is the
whole reason the sequencer works: "I have nothing to do this tick" and "I have
not got here yet" are different facts, and a sequencer that cannot tell them
apart either stalls forever or drops somebody's click. At 60 ticks a second
with a handful of peers that is a few hundred small reliable packets a second,
which is what lockstep costs and why it scales with players rather than with
factory size.

The alternative considered and rejected: seal a tick on a deadline and
re-address anything late to the next open tick. That never stalls, which sounds
better, and it means a laggy player's actions land at a moment nobody chose --
including, sometimes, after the thing they were aimed at has moved. A visible
stall is a worse second and a better game.

## Input delay: six ticks

A local action is never applied when the key is pressed. It is stamped
`now + 6` and applied on that tick by everyone, **including the peer that
issued it**. Applying a local command early because it is ours is the classic
lockstep desync: it works perfectly on a loopback socket and comes apart as
latency rises, which is the worst possible failure schedule.

Six ticks is 100 ms at 60 UPS. Above a loopback or LAN round trip; below the
~130 ms at which a keypress stops feeling connected to its result; and an even
100 ms, so the number in a bug report is a number a person can hold. It is a
constant and not an adaptive window on purpose -- an adaptive delay is a number
two peers can disagree about, and every number two peers can disagree about is
a desync waiting for a bad afternoon.

The one place it is visible in play is building: a click places a machine a
tenth of a second later. The renderer can hide that later with a ghost that
appears instantly and is confirmed or withdrawn six ticks on; that is a
rendering decision and is not made here.

## No peer runs a tick it has not got the batch for

`Advance` runs ticks while batches are available and stops when one is missing.
A stall is not a freeze: `NetStatus` carries who is being waited for and for how
long, and `NetStatusPanel` puts it on the screen in amber. A silent freeze is
indistinguishable from a crash, and this project's rule about refusals carrying
reasons applies just as much when the refusal is "not yet".

The host knows exactly who it is waiting for -- whichever peer has not spoken
for the tick it is trying to seal. A client only knows it has no batch, so it
says "the host" until the host tells it otherwise; naming a peer it cannot see
would be inventing information. The host broadcasts the name once a stall
passes six ticks.

## Stop on desync, and do not attempt recovery

Every peer computes `World.StateHash()` every 60 ticks and exchanges it. On a
mismatch the session **stops**, on both sides, with a sentence carrying the
tick, both hashes and both peers' names. There is no resynchronisation, no
rollback and no "probably fine": a wrong recovery is worse than an honest stop,
and an honest stop is what makes the bug reportable. That sentence is the most
important thing this feature will ever show a player, and it is the whole bug
report.

The detector has been seen to fire. `--net-lockstep-test` perturbs one peer's
player position by one milli-tile -- a thousandth of a tile, invisible on a
screen, and exactly the size of divergence a float would produce -- and asserts
that both peers stop and that the reason names the right tick. A detector that
has never fired is not a detector.

## Starting a world, and late join

The host's seed goes to every peer and each one generates the same world from
it. No world transfer, no save on the wire. The Start message carries the seed,
the peer ids in order and their names, so every peer maps peer to player index
identically rather than trusting its own roster ordering.

Everyone lands on **team 0**. Teams own progression (ADR 0036) and the lobby has
no way to choose a side, so inventing one here would put a rule in the game that
no screen explains. Two teams arrives with the screen that chooses between them.

**Late join is refused with a reason.** A peer that missed tick 0 cannot catch
up without either a state transfer or a replay of every batch, and this slice
has neither. Refusing is the honest option; half-working would mean it desyncs
on its first hash comparison instead of being told no.

## What is deliberately not done yet

* **Only walking goes through the command layer in the running game.** Building,
  digging and removal still call `World.TryBuild`/`TryDig`/`TryRemove` directly
  from `GameRoot`, which in a shared world would change one peer's world and
  nobody else's. Rather than let that desync, `GameRoot` refuses those clicks in
  a shared world with a sentence saying so. The driver applies all six command
  kinds and the headless run drives builds, digs and removals through it, so
  what is missing is the input routing and the "your click happens in 100 ms"
  feedback in the panels -- slice 3c.
* **Byte-identical saves, with one named exception.** The two peers' saves are
  byte-identical apart from `LocalPlayer`, which records *which* player is the
  local one and is genuinely different on each peer. `--net-lockstep-test`
  asserts both halves: identical once that field is equalised, and *different*
  as written -- so the day somebody makes it shared, the run says so rather than
  quietly passing.
* **No timeout on a stall.** A peer that goes away stalls everyone until
  somebody leaves the session by hand. ENet's disconnect removes them from the
  roster and the sequencer stops waiting, so a crash resolves itself; a peer
  that is merely hanging does not.

## How it is checked

```
godot --headless --path game -- --net-lockstep-test
```

A host and a client in one process, one seed, two players, real commands from
both sides -- walks, digs, builds and removals, with roughly 40% of them refused
for reasons the digest folds in -- for 3,000 ticks. It asserts: the two worlds
start on one hash; a command issued during tick T takes effect on tick T+6 and
not before; 50 of 50 hash comparisons agree; every command issued by either peer
reached the simulation; the digests match; and the saves match. Then it corrupts
one peer and asserts the stop.

Six mutants, six kills:

| Mutation | Caught by |
|---|---|
| Stamp a local command for the current tick instead of `now + 6` | input-delay check, and 0 of 51 commands reached the world |
| Run a tick without the batch for it | desync detector fired at tick 180; 1 of 3 hash comparisons disagreed |
| Never exchange hashes | 0 comparisons over the wire; the corrupted peer went unnoticed |
| Sequencer drops one peer's commands | 51 issued, 27 reached the world |
| Apply the batch after `Tick()` instead of before | the command took effect on no tick; 0 applied, 51 refused |
| Seal a tick before every peer has spoken for it | 51 issued, 27 reached the world |
