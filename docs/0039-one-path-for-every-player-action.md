# 0039 -- One path for every player action, and drawing the 100 ms

Slice 3c of multiplayer, and the one that makes a hosted world playable rather
than walkable. Slice 3b (ADR 0038) routed *walking* through the command layer
and refused building, digging and removal in a shared world with a sentence.
This routes all of it: build, dig, remove, retask and hand delivery. Nothing in
`/sim` changed.

## The design objection, stated and then set aside

On its own this slice adds no new decision: nothing here gives a player a
choice they did not have solo. What it does is make the choices they already
had *available to two people at once*, and one of those choices changes shape
in the doing. A build now costs 100 ms of commitment. Under lockstep you cannot
take a click back, and two players clicking the same tile in the same frame is
settled by ADR 0037's total order rather than by whose hand was faster. That is
a rule players will feel, and it is the reason the in-flight state has to be
drawn rather than hidden: a commitment the player cannot see they have made is
not a decision, it is a surprise.

## One path, not two

The obvious way to finish 3b is a network branch beside each existing call:

```csharp
if (Net is not null) Net.Issue(...); else _world.TryBuild(...);
```

That is two implementations of the rules. Two lists of refusal sentences that
drift apart, two answers to "may I build here", and eventually two games -- one
solo and one shared -- with one changelog between them. The refusal a player
reads is a rule of the world (ADR 0037 makes it part of the state hash), so a
refusal that exists in one mode and not the other is a rule that exists in one
mode and not the other.

So there is one path, `game/scripts/PlayerActions.cs`. Every mutating action a
player can take becomes a `PlayerCommand`, and the *only* difference between
solo and shared is **when it is applied and by whom**:

| | shared | solo |
|---|---|---|
| stamped for | `now + 6` by `LockstepDriver.Issue` | the current tick |
| applied by | every peer, from the sequencer's batch | `World.ApplyCommands`, in the click |
| answered by | `LockstepDriver.LastResults`, 100 ms later | the same call, immediately |

Both end in `World.ApplyCommands` -> `World.Apply`, which is where the rules
are. Solo has no queue, no wait and no network: a build takes effect inside the
click exactly as it did before, and `--menu-test` asserts that -- the machine
count moves *in the call*, not on some later tick. The obvious way to get one
path wrong is to make the solo path wait for something, and that assertion is
what catches it.

Rejected: **routing solo through the driver too**, with a null transport and a
delay of zero. It sounds tidier and it puts a sequencer, an outbox and a
per-tick seal in the path of a single-player click for no benefit; the failure
mode is a solo game that stalls because a loopback of nothing did not speak for
a tick.

Rejected: **keeping `TryBuild` for the local peer and sending a command as
well.** That is prediction, and it is the classic lockstep desync (ADR 0038):
perfect on a loopback, coming apart as latency rises. The mutation test for it
is in the run -- applying locally *and* sending makes the machine count move in
the frame of the click, and the two peers' hashes part four ticks later.

## What the sentences cost, and where they live

`ActionVoice` holds one refusal sentence per outcome, used by both paths. It is
richer than `Sim.CommandOutcomes.Say` -- it knows what was clicked and how far
the player's arms go -- and falls back to the sim's sentence for anything it
does not name, so no refusal is ever wordless.

Two numbers were lost and are named rather than papered over. `RemovalReport`
carries `FluidVoided` and `Spilled`; `CommandResult` carries one `Detail`, and
removal spends it on `Returned`. So a removal now says how many items came back
and no longer says how much fluid drained or how many items fell off the belt.
That information does not survive the command layer, and inventing a second
report path for the solo case would be the two-games problem again. If it turns
out to matter, the fix is in `/sim` -- more than one number on a
`CommandResult` -- not here.

## Drawing the 100 ms

Three things, because a click that appears to do nothing gets clicked again:

* **A marker on the tile**, in amber at 55% alpha (`PendingActionsView`), using
  the real hull mesh for a build so the footprint is honest. Deliberately *not*
  the build ghost's green/red: those colours claim an outcome, and the outcome
  is the one thing nobody knows yet. Amber says *asked for*.
* **A line on the shared-session card**: "Asked for, not yet done: Manual
  Crafting Bench at 6,-5". It went on that card after a capture showed the two
  alternatives failing -- a fourth HUD line lands on top of the guide card, and
  a caption in either bottom corner is behind the build menu or the machine
  panel exactly when the player is building or inspecting.
* **The answer, when it comes.** Every queued action is matched back to its
  result by `(tick, sequence)` and spoken with the subject it was clicked for:
  "Manual Uplink at 2,0: something is already there." A refusal arrives 100 ms
  after the click, by which time the player has moved the mouse, so the
  sentence has to name what it is about. And an action that is *never* answered
  -- a stopped session, a tick that ran without it -- is counted as `Lost` and
  says so, because silently vanishing is the exact failure this reconciliation
  exists to prevent.

`LockstepDriver.LastResults` had to change: it was cleared per tick, and a
frame covers up to eight ticks, so the results of every tick but the last were
dropped. A build refused on the first of five ticks in one frame told the
player nothing. It is now cleared once per `Advance`.

## What a shared world still refuses, with a reason

* **Loading a machine by hand, and emptying one.** There is no command kind for
  either (ADR 0037 has six and these are not among them), and doing them
  locally would desync. `MachinePanel` refuses both with a sentence naming the
  belt and the inserter as the way round it. This is the honest half of an
  unfinished feature and it is the next thing to fix -- it is also the *first
  hour* of the game, so a shared world is not yet a shared opening.
* **Quick load.** It replaces this peer's world with one nobody else has.
  Saving is fine and still works.

## The reach question this does not answer

`TryChangeRecipe` has no reach check (ADR 0021 predates the player having a
position), so retasking works from anywhere on the map -- solo and now shared,
identically. The command is routed and the rule is untouched. Adding a radius
here would give the sim two answers for one action, and the question of what
that radius should be is with the people who decide such things.

## How it is checked

`--net-lockstep-test` gained a section that plays **two `GameRoot`s**, one per
peer, through the methods a mouse click reaches -- `PlaceHeld`, `DigOrClose`,
`RemoveAt`, `Actions.Retask`, `Actions.Deliver` -- not through the driver. Ten
actions each, five kinds, and it ends on one hash, one digest and byte-identical
saves (`LocalPlayer` excepted, as before). It prints what each peer issued, what
was applied, what was refused and why.

`--menu-test` gained the solo half: a real new game, real clicks, and the
assertion that the world changes inside the call.

`--net-shot` gained `net-inflight.png`, which photographs the amber marker, the
in-flight line and a real refusal together. It is held open by *stalling* the
session -- the second peer is simply not advanced -- because a 100 ms window is
not something a camera can be pointed at and hoped over.

Seven mutants, seven kills:

| Mutation | Caught by |
|---|---|
| Apply a shared build locally as well as sending it | machine count moved in the frame of the click; hashes, digests and saves all parted |
| Send the build but never queue it | 0 in flight after a click; 1 action never answered |
| Drop a queued command's refusal instead of showing it | 10 issued and 5 heard about; Blocked, NothingThere and NoMachine all missing; 5 sentences instead of 10 |
| One peer's builds bypass the driver entirely | 1 machine from two builds; hashes, digests and saves parted |
| Clear `LastResults` per tick instead of per `Advance` | 1 of the host's actions never answered |
| Draw every in-flight marker and leave it hidden | markers visible = 0 (`Drawn` counts what is visible, after the first version of it counted intent and survived this) |
| Stamp a solo command for `TickCount + 1` | solo build took effect on no tick: 3 issued, 0 applied, 3 `WrongTick` |
