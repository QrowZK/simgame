# 0030 — The first five minutes

Status: accepted

## The problem

A new player landed, read three sentences of premise, and was shown four
objectives — Steam Metallurgy, Ore Processing, Chemistry, Fabrication — each
wanting one Steam Machine Hull. A hull is eight bronze plates and two cables:
copper and tin ore, two smelts, an alloy smelter, a bench, and twenty minutes of
work that nothing in the game names. All four Manual techs were open at tick
zero (`RequiresItem is null`), so **nothing was deliverable in the first five
minutes and the objectives panel opened on four impossible tasks.** Nothing said
to press P, nothing said what to build, and the first achievable thing — a
furnace out of the 24 stone in the starter kit — was written down nowhere.

## What this changes

Two things, and they are separate on purpose.

**1. Four rungs ahead of the tier ladder** (`data/spec/progression.json`,
`intro_techs`). They are techs like any other, so they cost nothing new in
`Research`, the quest panel and the tech tree draw them, and they are saved and
reachability-checked by the tests that already existed:

| Rung | Wants | Gives |
|---|---|---|
| Uplink Online | 1 × any raw ore | 1 Manual Furnace |
| First Metal | 3 × any metal ingot | 2 Steam Inserters, and the recipe |
| Mechanised Haulage | 6 × any metal ingot | 8 Steam Transport Belts, and the recipe |
| The Line Runs | 12 × any metal ingot | the four Steam tech lines open |

One, then three, then six, then twelve: the delivery that is a novelty, the one
that is a walk, and the one that is a chore you now have the parts to stop
doing. The four Steam techs gained `tech_start_line` as a prerequisite, so a new
game's objective list is **one rung the player can do** rather than four they
cannot.

**2. `sim/Guide.cs`** — nine steps, each derived from world state, wrapping the
rungs in the things a rung cannot express: press P, put the Uplink down, place
the furnace, build the belt.

## Why these rungs and not others

- **The reward is always the next rung's tool**, never a cosmetic and never
  points. The furnace is what makes the ingots the second rung wants; the
  inserters and belts are what make the fourth rung bearable. The only material
  grant that skips a chore rather than a lesson is the furnace itself, and the
  player keeps their 24 stone for the alloy smelter — bronze is a lesson, so it
  is never handed over.
- **The pain is the argument.** The first delivery is one item carried by hand
  because carrying one item is fine. The fourth is twelve because carrying
  twelve is not, and the two rungs before it paid for the machinery that makes
  the trip stop being yours.
- **The first rung accepts any smelted ore**, not a named one. Which ore is near
  spawn is a property of the seed (ADR 0026), and a rung naming chalcopyrite
  would be uncompletable on every seed that buried it. The accepted set is
  derived in `tools/generate_data.py` from what a Manual furnace smelts, which
  is exactly the set `NewGame.StarterOres` guarantees a patch of near spawn —
  `GuideTests.TheFirstRung_AcceptsExactlyWhatTheStartIsGuaranteedToHave` fails if
  those two ever drift apart. Stone falls out of the set by construction:
  nothing smelts it, and a first objective satisfied by the kit's own 24 stone
  would be a rung stepped over without mining anything.

## Every step is satisfied by world state

The best-evidenced negative finding in the research on this genre is Wube's own
conclusion about the Factorio tutorial they deleted: *"Player actions are so
heavily constrained that the player learns just how to solve the tutorial rather
than learning the concepts."* So `Guide` has no event hooks, no stored progress,
and no "complete step" call. It asks the world nine questions. A player may
place the Uplink first, belt before they hand-feed, or work the whole thing out
with the panel closed, and it still counts — `GuideTests.PlayingItOutOfOrder_StillCounts`
is that property.

Two consequences were designed rather than tolerated:

- **Steps are monotone, but only across steps that prove something.** "You are
  holding ore" stops being true the moment the ore is smelted, so a step is also
  done when a *later* step is done — but only when that later step implies it. A
  delivery does: you cannot deliver an ore into an Uplink you never placed.
  Placing a building does not, and a guide that ticked "find ore and mine it"
  because a building went down would be reporting work the player has not done,
  which is worse than having no guide.
- **The one thing the sim has to remember.** Everything else is inferable, but
  "an item reached the Uplink that the player did not carry" is not: it is the
  same delivery either way. `World.UnattendedDeliveries` counts what arrives
  through a machine's input buffer, which only a belt, an inserter or a drone
  fills — a hand delivery goes straight to `Research` and never touches a
  machine. That counter is the opening's climax, is saved, and is why the save
  format is 13.

## What was rejected

- **A hand-cranked Manual miner as the last reward,** so the beat sheet's "miner
  on ore and the loop runs without you" could land inside five minutes. A Steam
  miner needs a grid, which is a boiler and a generator away. But a Manual miner
  would be a 1×1 miner at the same 60-tick cycle as the Steam one and free of
  power, i.e. strictly better than a machine already in the game — and footprint
  is the only throughput dial, so there is no honest way to make it worse. The
  opening therefore ends at the belt, and the last step hands over to the tech
  tree by naming the hull.
- **Gating the furnace, the bench or `build_stm_miner`.** Only the belt and the
  inserter moved behind a rung. Both are Manual-tier recipes that need a Steam
  hull, so nothing that was buildable at tick zero stopped being so — the gate
  costs the player nothing and buys a reward that can be *named*. Gating the
  furnace would have locked a player who spent their kit; gating the miner would
  have broken the route `OpeningRouteTests` walks for a reason that was only
  presentational.
- **Points-based rungs** ("deliver 20 of anything"). Component-gating is the
  whole design (ADR 0023) and the opening is not the place to teach a rule the
  rest of the game does not follow.
- **A scripted tutorial with constrained input.** See above; it is the one thing
  the research is unambiguous about.
- **Naming the reward as "unlocks 26 recipes",** which is what the panel said
  before this. `ResearchObjective.RewardSummary` now says "Gives you 2 Steam
  Inserters", from `TechDef.rewards` in the data. The four Steam starter kits in
  `Research.Kits` still exist and are read through the same accessor, so a UI has
  one place to ask what a tech gives.

## Honest limits

- **A need's `Item` is not always an item id.** For a group it is the label the
  progress is filed under — `"any metal ingot"` — and `ResearchNeed.Accepts` is
  the list of real ids. Anything resolving an `ItemId` must read `Accepts`;
  `MachinePanel`'s deliver-everything button did not, and delivered nothing on
  the first four objectives of the game until it was fixed.
- **Old saves are refused, not migrated.** A version 12 file cannot mention
  rungs that did not exist, so loading one would quietly take the belt and the
  inserter back off a player who had earned them.
- **Two tests changed shape rather than being weakened.**
  `AtTickZero_EveryManualRecipeIsOpenAndNothingAboveIt` now names the two
  re-gated recipes explicitly and asserts they are locked, and the reachability
  closure delivers the first item a need *accepts* rather than the need's key.
  Both are still exact counts.
