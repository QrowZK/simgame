# 0040 -- Loading and taking by hand, as commands, and how far your arms go

Slice 3d of multiplayer, and the one that makes the *first hour* playable
together. ADR 0039 routed build, dig, remove, retask and hand delivery through
the command layer and listed what a shared world still refused. Top of that
list: **loading a machine by hand and emptying one**. There was no command kind
for either, so `MachinePanel` refused both with a sentence and pointed at the
belt and the inserter -- which do not exist yet at the point in the game where
those two buttons are the only way anything happens.

Two people could share a world and not play its opening. This closes that.

## The design objection, stated and then set aside

Like 3c, this adds no decision that was not already there: loading a furnace is
the same choice solo and shared. What is new is that the choice is now
*contested*. Two players standing at one bench with one cycle's worth of space
between them is a race, and ADR 0037's total order decides it -- by player id
and sequence, never by whose packet arrived first. That is a rule players feel
(one of them is told "it wants nothing right now"), and it is the reason the
refusal had to be its own sentence rather than a shrug.

## `CommandKind.Load` and `CommandKind.Take`

Both name a **tile**, not a machine index, for the reason `ChangeRecipe` does:
an index issued on the tick of a click can name a different machine by the tick
it is applied on, because removal is a swap-remove (ADR 0028).

**`Load` counts cycles, not items.** One cycle is the amount that makes the
progress bar move exactly once; it is defined for every machine that has a
recipe (`InputPerCycle`, batch included), and it is the same number on every
peer. An item count would have to be worked out from what the player happened to
be holding when they clicked, which is local knowledge, and local knowledge in a
command is a desync waiting for a slow connection. `Amount <= 0` is `BadAmount`:
zero cycles is a command that means nothing, not one that does nothing.

**`Take` has no amount.** The button is "empty it". A partial take would need an
item id as well as a count, which is one more thing for two peers to disagree
about and no decision gained.

The machine's `Recipe.Inputs` is a list in data order, so a load considers items
in one fixed order on every peer, and a player whose pockets cover only part of
a load covers the same part everywhere. A take walks the output buffer
**ordered by item id**, never in dictionary order, which `Dictionary` makes no
promise about: the order items land in an inventory is state, and state two
peers can compute differently is the whole failure mode.

### Five refusals, five sentences

| Outcome | When | What the player is told |
|---|---|---|
| `NoMachine` | bare ground, a belt, a pole | "there is no machine there to load / to empty" |
| `TooFar` | outside hand reach | "walk closer -- your hands go 6 tiles" |
| `OtherTeam` | a rival's machine | "that belongs to another team" |
| `WantsNothing` | full, or takes no inputs at all | "it already holds a full cycle's worth, or it takes no inputs at all" |
| `NoneCarried` | it wants twelve stone and you have none | "you are not carrying any of what it wants" |
| `NothingToTake` | nothing has finished in there yet | "nothing has finished in there yet" |

`WantsNothing` and `NothingToTake` are separate values on purpose: one says the
machine is full and the other says it is empty, and telling a player the wrong
one of those two sends them the wrong way across the base. Neither is `Ok` with
a detail of zero -- "it worked and moved nothing" reads as a dead button, and
that is exactly the mutant the tests kill.

Ownership is checked **before** reach, as removal does: walking closer never
makes a rival's bench yours.

A miner is not `NoMachine`. The player can see it perfectly well; it simply eats
nothing, so a load on one is `WantsNothing` and a take on one empties its
hopper -- which is how the first ore moves before an inserter exists.

## The reach decision, which was mine to make

**Both take hand reach (6 tiles), the same as digging and hand delivery.**

Loading and taking are moving items between your pockets and a machine's hopper
with your arms. That is what digging is and what handing a crate to the Uplink
is, and those are six tiles. Build reach is twelve because setting a hull down
is a throw, and the asymmetry is deliberate (ADR 0033): you can place at arm's
length plus a lob, and you must stand at the thing to work it.

The alternative -- build reach, so that anything you can place you can also
feed -- was rejected because it deletes the first hour's only spatial decision.
At twelve tiles a player never has to stand next to anything: they put the bench
down and feed it from where they were standing, and "walk to the furnace" stops
being part of the game. Standing at the furnace *is* the opening.

`LoadingAndTaking_TakeHandReach_NotBuildReach` pins this as an inequality and as
two exact distances, so widening either radius is a visible change rather than a
quiet one.

**`TryChangeRecipe` still has no reach check.** That question is open and is not
mine (ADR 0039 says the same). Retasking works from anywhere, solo and shared,
unchanged by this slice. Nothing here invents a rule for it.

## The contested case

Two peers load the same machine on the same tick. The batch is sorted by the
total order, so the lower player id applies first, fills the cycle, and the
second load finds a machine that wants nothing. Both peers compute both
outcomes, both fold both into the digest, and the state hash agrees.

This is proved twice: in `sim.tests` with the batch handed over in both orders
and the two worlds compared by hash, and in `--net-lockstep-test` with two real
`GameRoot`s and two real drivers. The second needed care -- the client runs a
few ticks behind the sequencer, so two clicks in one *frame* are stamped for two
different *ticks*, and a tick apart is not a contest at all (the first load's
cycle has already started and eaten the inputs). The run now issues the host's
load, waits for the frame on which the client's stamp lands on the same tick,
and **fails loudly if the two stamps differ** rather than quietly proving
nothing.

## Three numbers on a `CommandResult`, not one

ADR 0039 named a loss and left it: `RemovalReport` carries `Returned`,
`FluidVoided` and `Spilled`, `CommandResult` had one `Detail`, and removal spent
it on `Returned`. So pulling up a full tank stopped telling the player that the
fluid drained away.

`CommandResult` now carries `Detail`, `Voided` and `Spilled`. All three are
folded into the command digest, so a peer that computed a different spill is a
peer that diverged, and the removal sentence says all of it again: "12 item(s)
that were inside came back to you. 50 unit(s) of fluid drained away."

That is a wider change than "one more int" looks, and it was worth making
because the alternative -- a second report path for the solo case -- is the
two-games problem ADR 0039 exists to prevent.

**An honest limitation.** `Voided` is proved end to end (a quarter of a pipe run
removed voids a quarter of its water, through the command layer, exactly).
`Spilled` is wired, hashed and spoken, but **no removal I could construct
produces a nonzero one**: cutting a run hands back what stood on the cut tile
and the shortened run still holds the rest, and a whole tunnel pulled up returns
every item buried in it. The test asserts the zero and says why, rather than
asserting nothing and looking thorough.

## Save format and command format

The **save format did not change** and is not bumped. Nothing in the shape of a
save moved: input and output buffers, miner hoppers and inventories were all
already written and already hashed (checked, not assumed).

The **command format did**: `CommandCodec.Format` is 2. The byte layout is
untouched -- only the set of kind bytes that decode -- but a peer speaking
format 1 must refuse the whole batch at the door rather than only the first
batch that happens to carry a `Load`. Half a tick's commands is a desync with
extra steps.

## No prediction, still

A shared load is never applied locally. `MachinePanel` no longer touches
`HandOps` at all: both buttons call `PlayerActions`, which sends a command in a
shared world and applies the identical command through `World.ApplyCommands` in
the click when solo. `--menu-test` asserts the solo half moves the world *inside
the call* (`solo hand-feed  bench holds 12 in the click`), which is the property
a "one path" refactor most easily breaks.

## How it is checked

* `sim.harness --lockstep-test` -- the director now plays the real opening: each
  player walks to their patch, puts the starter kit's bench down where they
  stop, and loads and empties it for the rest of the run, plus loads of thin
  air, loads of zero cycles and hands in a rival's bench. It prints per-kind
  counts and **asserts every kind is issued at least once**, because a
  determinism harness that never issues a command cannot prove that command
  deterministic.
* `--net-lockstep-test` -- two `GameRoot`s: retask, take-too-early, the
  contested load on one tick, a load of the other peer's own bench, the cycle
  waited out, and both peers taking the output. One hash, one digest,
  byte-identical saves (`LocalPlayer` excepted).
* `--menu-test` -- the solo half, in the click.
* `sim.tests/HandCommandTests.cs` -- 17 tests on a *real* new game with the real
  bench and the real `build_man_furnace` recipe, ending in two peers hand-
  feeding one furnace through a shuffled stream.

Six mutants, six kills:

| Mutation | Caught by |
|---|---|
| A shared load applied locally instead of sent | `--net-lockstep-test`: the contest resolved to two winners, the peers never met on one tick, and the two hashes and saves parted |
| The loaded amount dropped from the state hash | `WhatALoadAndATakeChange_IsInTheStateHash` -- **after** it was rewritten: the first version moved the stone out of the player's pockets as well, so the hash parted on the inventory and the mutant survived. It now differs in the hopper alone |
| The output buffer dropped from the state hash | the same test, second half |
| Two peers loading one machine resolved by arrival order (`ordered.Sort()` removed) | `TwoPlayersLoadingOneMachineOnOneTick_AreSettledByTheTotalOrder` and the two-peer furnace run |
| `WantsNothing` reported as `Ok` | three tests, including the one that asserts the refusal *costs* nothing |
| A load taking build reach instead of hand reach | `LoadingAndTaking_TakeHandReach_NotBuildReach` |

## What playing it found, which no test did

The slice was played in a real window: host a world, build a bench, load it by
hand, take the furnace out. Two defects, both visible in one capture and neither
reachable from a test:

* **Every refusal in a shared world was cut off mid-word.** `NetStatusPanel`'s
  card was centred at the top on the argument that the middle of the screen is
  nobody's; the HUD's sentences run from the top-left across the middle, so a
  real capture read "Taking back what is at 3,3: nothi" and then a card. The
  card is now right-aligned. `--net-shot`'s `net-inflight.png` is the check, and
  it is a capture rather than an assertion because this is a thing you have to
  look at.
* **The machine panel titled itself with a data id**: "build_man_furnace
  [1x1]", a key out of `data/` on the screen of somebody who has never seen the
  file it is a key in. It now says "Manual Furnace" -- the buildable's display
  name, the same string the build menu offered it under. The third time this
  project has shown a raw id to a player, and the third time it was found by
  looking.
