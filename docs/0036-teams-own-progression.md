# ADR 0036: Teams own progression, and everything placed has an owner

Slice 1 of multiplayer, as far as `/sim` is concerned: the world can hold
several players on several teams, save them and load them. No networking — ADR
0035 is the transport, and it and this ADR do not touch each other yet.

## The design objection, stated and then set aside

On its own this slice adds no decision a player makes. A world with two teams
and no way to reach it from a lobby is a data model. The decisions it *enables*
are real and are the reason to take them in this order — whose factory is this,
what happens when two people want the same patch, is a second player a
teammate or a rival — but none of them ships today. The thing to evaluate today
is whether the model is honest, not whether it is fun.

What the model does commit to, and what is therefore already a design decision:

- **Progression is team-owned, carrying is not.** A team shares what it has
  researched and shares nothing it is holding. Two people on one team split the
  work of a tech ladder; they do not split a pocket.
- **There is no trade between teams.** Deliberately not built. The only thing
  two teams in one world compete for is the ground, which nobody owns.
- **A teammate's factory is yours.** Dismantling a teammate's smelter works.
  That is what makes a team a team, and it is also the sharpest thing about
  playing on one: there is no lock, so joining a team is trusting somebody.

## Progression is team-owned; carrying is per player

`Team` holds a name, its own `Research`, and its own `UnattendedDeliveries` —
the "your factory did that without you" milestone from docs/0030, which is
progress and therefore team-shaped. `Player` holds an id, a name, a team, a
position, a facing and an `Inventory`.

`World` holds `Teams` and `Players`, both index-equals-id, both append-only.
Neither a team nor a player is ever removed, because ownership on every building
and in every save is stored as that index: an id that shifted when a team was
added would hand one team's factory to another. An empty team costs a name and
an unlock list, which is cheaper than the alternative by an enormous margin.

`Player.TeamId` is a settable field precisely so that an invite (slice 4) is one
assignment. Nothing else about a player is team-shaped, so nothing else has to
move when they change sides.

**Rejected: a per-team object graph** — a `Team` owning lists of its machines,
belts and players. It reads better and it would have meant touching every
system in `/sim`, re-indexing the dense arrays the renderer streams from, and
changing what a save is. What it buys is "list this team's machines", which
nothing asks for.

## The aliases, and why they are not a hack

There were 293 references to `world.Player` and `world.PlayerInventory` across
24 files, and every one of them meant *the player at this keyboard*. So:

- `World.Players` is the roster and `World.LocalIndex` says which one is here.
- `World.Player` is `Players[LocalIndex]`.
- `World.PlayerInventory` is `Player.Inventory`.
- `World.Research` is `Teams[Player.TeamId].Research`, getter and setter both.

Every existing call site still compiles and still means what it meant. This is
not a deprecation shim: "the local player" is a real and permanent concept —
the UI always looks through somebody — and the alias is the name for it.

What changed instead is the *actions*. `TryBuild`, `TryRemove`,
`TryChangeRecipe`, `TryDigByHand`, `DeliverByHand`, `TryUplinkInHandReach`,
`InHandReach` and `InBuildReach` all take `Player? actor = null`, defaulting to
the local player. The default is honest here rather than a hiding place: an
existing single-player call site passing nothing is *correct*, because the local
player is the acting player when there is only one.

`TeamSession` and `TeamTests` pin the aliases as a property rather than trusting
them: moving `LocalIndex` moves the player, the inventory and the research
together. A world where `Player` followed the local index and `Research` did not
would hand somebody another team's unlocks and look entirely reasonable
doing it.

**No call site turned out to be a wrong reading.** Every `world.Player` in
`/sim` and in `/game/scripts` genuinely means the local player, because the game
has exactly one. The nearest thing to a trap is `BuildMenu.Bind`, which caches
`world.PlayerInventory` into a field; that is correct today and goes stale the
first time anything calls `SetLocalPlayer` after binding. It is in `game/`,
which this change did not touch, and it is written down here rather than fixed
quietly.

## Ownership is stored by anchor, next to what the building cost

`World` already kept `_builtFrom`: anchor tile → the item that paid for the
building, because "what did this cost you" cannot be read back off a belt tile
or a splitter. Ownership is a parallel map on the same key — anchor tile → team
id — and is written and dropped in the same two places.

The anchor is the one thing a 1×1 belt, a pipe and a 3×3 assembler all have, so
this is the only key that answers "who owns this" for every placeable with one
lookup. Adding a `TeamId` field to `Machine`, `Miner`, `Pole`, `Generator`,
`Accumulator`, the belt map and the fluid graph would have been seven fields,
seven save records and seven chances to forget one.

A tile with no entry is `Team.NoTeam`, and **nobody's is everybody's**. Machines
placed outside `TryBuild` — the demo world, headless throughput analysis — are
unowned, so ownership refuses nothing about them and those worlds behave exactly
as they did. That is a deliberate hole, and it is the same hole
`RemoveResult.UnknownBuilding` already names: anything placed outside `TryBuild`
is outside the rules that `TryBuild` enforces.

## Ownership is checked *before* reach, which reverses the house order

Everywhere else here, reach is checked first (ADR 0033): a player is never told
"something is already there" about a tile they cannot walk to, because only one
of the two true things is the problem they have.

Ownership inverts that. Walking closer to a rival's smelter never helps, so
`TooFar` first would send the player on a twelve-tile walk that ends in "that is
not yours". `RemoveResult.OtherTeam` and `RecipeChangeResult.OtherTeam` are
therefore answered before the radius, and `TeamTests.ARivalIsRefused_BeforeReach`
checks both distances so the order is a tested property rather than a comment.

A *teammate's* building is not refused at all. The two sentences a UI says are
"removed" and "that belongs to another team", and the removed one is the
teammate case. A refusal for a teammate would need a lock, a permission list and
a screen for managing it, none of which is worth what it buys.

A rival's Uplink is not "found and then refused" — it is skipped in the search
entirely, so `DeliverByHand` says the one useful thing: no Uplink of yours is in
reach.

Every refusal costs nothing, as everywhere else here. A refused retask evicts
no buffer, which matters more than it looks: an eviction on a refusal would be a
way to empty a factory you are not allowed to remove.

## Deliveries route by who built the Uplink

`DrainUplink` resolves the owning team from the Uplink's own anchor, not from
the local player. Two teams' belts running into two Uplinks four tiles apart
credit two different tech trees, and `TeamTests.AnUplinkCreditsTheTeamThatBuiltIt`
asserts that one team's belt moves neither the other's unlock list nor its
unattended count.

**The limitation, named rather than papered over.** A belt has no hands, so the
*reward* for an unattended delivery has nobody obvious to go to. It goes to the
owning team's **first player in roster order**. Roster order rather than
"nearest player", because nearest depends on where everybody is standing, and
under lockstep (slice 3) two peers must produce identical inventories from
identical commands — a tie-break on position is a divergence waiting for two
players to stand equidistant. The consequence is precise: a tech reward earned
by a belt lands with whoever joined the team first, until there is a team chest
to put it in. `AnUnattendedRewardLandsWithTheOwningTeamsFirstPlayer` tests it.

## Save format 15

Version 14's single player became a roster and its single research state became
one per team:

- `Teams[]` — name and `ResearchSave` each.
- `Players[]` — name, team index, position in milli-tiles, facing, inventory.
- `LocalPlayer` — which one the local view was looking through.
- `Built[].Team` — the owner, on the same record as the item that paid for it,
  because a removal hands the item back *to* the team and a file that kept one
  without the other would give a rival's factory away on the first reload.

Version 14 and below are refused. The direction that actually loses data is
writing a 15 and reading it as 14: a 14 loader takes one research state as the
world's, so a rival's unlocks would be handed to everybody on the map.

`SaveFile.Research` survives as a `[JsonIgnore]` **view** over
`Teams[localTeam].Research`, for readers written before teams existed
(`GameSession` reads it to summarise a save on the load screen). A view, not a
second copy: two places to write one fact is exactly how a field gets dropped
from one of them.

The round-trip test is deliberately untidy — three players, two teams, three
positions that are not tile centres, three facings, three different inventories,
two ladders at two different rungs, one reached by hand and one by machine.
A save test where everybody sits on the same tile on the same team with the same
pockets round-trips a dropped field without noticing; that is exactly what
happened to belt facing, and CLAUDE.md keeps the receipt.
`EveryPlayerDiffersFromEveryOther_SoASwapWouldShow` asserts the untidiness
itself, so a later edit cannot quietly tidy the world and weaken the test.

## Determinism

Everything added is integer. The roster ticks in index order, not in the order
anything happened to be enumerated; ownership is a lookup, never an iteration;
the unattended reward's recipient is chosen by roster order rather than by
distance. `TwoWorlds_WithTheSameCommands_StayByteIdentical` walks three players
on two teams for a thousand ticks and compares the whole save.

## The headless check, and where it lives

`sim.harness --teams-test` plays the model end to end: three players, two teams,
each finding its own ore, each building its own Uplink, a rival refused near and
far, a teammate's building taken down, two ladders advanced by two different
routes, then saved, reloaded, and checked again on the far side. Exit code 0 or
1, so CI needs no grep.

It lives in `/sim` and runs from a small console project rather than as a Godot
flag beside `--session-test`. Teams are simulation state, and a check that could
only be run by launching a renderer is a check that does not run on a machine
with no display. `TeamTests.TheHeadlessTeamsSession_Passes` drives the same
`TeamSession.Run`, so the headless run and the test suite cannot drift apart and
disagree about what passing means.
