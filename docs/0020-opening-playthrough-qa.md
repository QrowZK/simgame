# 0020: QA — the opening playthrough

Not an architecture decision. This is a **test plan and its findings**: a manual
playthrough of the route a new player actually takes, from the landing site to a
working factory, written down so it can be re-run.

Everything below was run, not reasoned about. Where I did not check something, it
says so.

- **Build under test:** `005ea1e` (`art: draw tunnels and splitters, vary the
  ground, give every item an icon`), branch
  `claude/automation-game-handoff-e5w3q1`.
- **Environment:** Linux x86-64, .NET 8.0.424, Godot 4.3-stable mono, headless.
  Save format v8, data regenerated from `data/spec/progression.json` and clean.
- **Method:** adapted from
  <https://oceanviewgames.co.uk/blog/posts/game-qa-testing-best-practices>.

## How the article was adapted, and what was dropped

The article is written for mobile. Honestly, most of it does not apply here and I
am not going to pretend otherwise:

| Section | Verdict |
|---|---|
| Functional testing incl. edge cases | **Used.** Its "rapid input and interruption" becomes *unusual orderings and boundary states* — building the bench on the wrong recipe, mining the wrong ore first, spending the last stone. |
| Bug report format | **Used verbatim**: specific title, numbered repro, intermittency, expected vs actual, environment, evidence. |
| Severity S1–S4 | **Used verbatim.** Every finding is ranked. |
| Regression / "two days becomes minutes" | **Used.** Every finding below is either an automated test in `sim.tests/OpeningRouteTests.cs` or an assertion in `ci.yml`. |
| Automated vs manual split | **Used.** Smoke and build verification are automated; the route walk itself is manual and is the section below. |
| Device/OS compatibility matrix | **Dropped.** One desktop target. |
| IAP / ATT sandbox / store compliance | **Dropped.** No store, no purchases, no tracking. |
| Crashlytics / crash SDK from day one | **Dropped.** The nearest equivalent is `--smoke` and `--session-test` already failing the build on a throw. |
| Multiplayer / network conditions | **Dropped.** Single player, no network. |
| "Profile on real hardware, not the editor" | **Partly used.** All timings here are headless Godot, not the editor — but there is no second machine to compare against, so no performance claim is made. |

## The route as designed, and where it actually stops

The route in `docs/0010` and `sim/NewGame.cs`:

> land with a starter kit → prospect → walk → mine by hand → place the crafting
> bench → craft → build a furnace → build a miner → power it → belt it →
> automate.

Walked step by step in the real sim, from `NewGame.Create`:

| # | Step | Result |
|---|---|---|
| 1 | Land with a prospector, hands, 24 stone | works |
| 2 | Prospect (`Prospector(400).Scan`) — 15–19 resources found on every seed | works |
| 3 | Walk to the nearest hit | works, but the nearest hit is usually useless — **F2** |
| 4 | Hand-mine it (`HandOps.Mine`) | works, including on liquid — **F3** |
| 5 | Place the bench (`TryBuild`) | works, and consumes the only bench you will ever have |
| 6 | Craft on the bench | works **once**, for one recipe, forever — **F1** |
| 7 | Build a furnace | reachable only if step 6 spent the bench on `build_man_furnace` |
| 8 | Build a miner | **unreachable** — F1 |
| 9–11 | Power, belt, automate | unreachable — all of it is downstream of a miner |

**The route stops at step 6.** Steps 7–11 are individually correct and
individually tested; nothing joins them to step 6, because every test that
exercises them hands itself the items it needs.

---

## Findings

### F1 — S1 — A new game can craft exactly one recipe, ever, and the route to the first miner needs ten

**Severity: S1 (progression blocker).**
**Intermittency: 10/10 — every seed, every time. Not a race.**

Three facts, each fine alone, combine into a dead end:

1. `Machine.Recipe` is `readonly` and is chosen at build time. `MachinePanel`
   has no recipe picker, and there is no `SetRecipe` anywhere in `sim/` or
   `game/scripts/`. A placed machine runs one recipe for its whole life.
2. Nothing removes a placed machine. `docs/0018` already notes removal does not
   exist for belts; it does not exist for machines either.
3. **No recipe in the data produces `man_manual_crafting`.** The starter kit is
   the only source, and it holds one.

And `manual_crafting` is the sole producer of all 49 steam-tier recipes,
including its own prerequisites. Walking `build_stm_miner` back through its
inputs gives **ten distinct bench recipes**:

```
build_man_furnace, build_stm_miner, comp_stm_cable, comp_stm_conveyor,
comp_stm_machine_hull, comp_stm_motor, form_bronze_plate, form_bronze_rod,
form_copper_wire, form_iron_rod
```

Ten recipes, one bench, one recipe per bench, no second bench, no way to remove
the first.

**Steps to reproduce**

1. `. tools/env.sh`
2. `dotnet test sim.tests --filter TheRouteToTheFirstMiner` — remove the `Skip`
   argument on the `[Fact]` first.
3. Or by hand, in any harness: `NewGame.Create(seed, Catalogue.Instance)`;
   `world.TryBuild(catalogue, item("man_manual_crafting"), x, y, recipe("build_man_furnace"))`;
   observe `PlayerInventory.Count(bench) == 0`; observe no recipe in
   `Catalogue.Data.Recipes` outputs `man_manual_crafting`.

**Expected:** the player can run every bench recipe on the route to their first
miner — by placing more benches, by re-tasking the one they have, or by picking
it back up.

**Actual:** one craft, then nothing. The game does not refuse anything or say
anything — it simply has no next move. This is the worst shape a blocker can
take, because it looks like the player has missed something.

**Evidence:** `--session-test` prints it and passes anyway:

```
can build       1 kinds from the starter kit
holding         Crafting Bench (49 recipes)
built           Ok at 0,0
--- building ok ---
```

`RunBuildFlow` places the bench on `RecipesFor(bench).FirstOrDefault()`, which is
`form_copper_plate` — a recipe needing a copper ingot, which needs a furnace,
which needs the bench. So CI's opening-loop assertion currently passes on a bench
that can never complete a single cycle, and calls it `ok`.

**Owner: `gameplay`.** Which of the three facts to change is a design decision,
not a QA one. Cheapest is a `build_man_manual_crafting` recipe taking stone —
but note that only converts the blocker into a stone treadmill (see F2), so
re-tasking or removal is likely the better answer.

**Regression:** `OpeningRouteTests.TheRouteToTheFirstMiner_FitsInTheBenchesAPlayerCanEverHave`,
currently `Skip`ped with a pointer here. It derives the ten recipes from the data
rather than listing them, so it tracks the recipe graph. Unskip it with the fix.

---

### F2 — S2 — The nearest resource cannot be used at manual tier on 18 of 20 seeds

**Severity: S2 (major, with a workaround: walk further).**
**Intermittency: 18/20 seeds. Measured, not sampled.**

The manual furnace smelts exactly five ores — `chalcopyrite`, `cassiterite`,
`magnetite`, `galena`, `sphalerite`. The prospector sorts hits by distance and
the player walks to the top of the list. On 18 of the first 20 seeds that is
halite, dolomite, coal, quartz, limestone, garnierite or crude oil — none of
which anything the player can build will touch.

Measured, seeds 1–20 (`nearest` → `nearest smeltable`):

```
seed  1  halite@33            magnetite@64
seed  4  crude_oil@5          cassiterite@65
seed  6  coal_deposit@8       sphalerite@128
seed 10  crude_oil@6          chalcopyrite@157
seed 17  coal_deposit@4       cassiterite@56
seed 20  limestone_deposit@89 magnetite@184
```

`--session-test` on the shipped seed does exactly this and reports success:

```
nearest ore     halite at 44,-51 (67 tiles), 6805 units
hand mined      25
miner built     True
--- start flow ok ---
```

25 units of halite, and a miner built on it that will fill with more halite.
Nothing at the manual tier consumes any of it.

**Expected:** the first resource a new player is pointed at is one they can
actually do something with, or the prospector says which ones they can use.

**Actual:** the prospector is a ranked list with no indication of usability, and
the shortest walk is the wrong one four times out of five.

**Why the existing tests miss it:** `EveryStart_HasMineableOreWithinAShortWalk`
asserts only that *some* patch exists within 400 tiles, and
`TheFirstHour_CanBePlayedByHand` asserts only that *some recipe somewhere in the
graph* consumes what was dug — including recipes on machines eight tiers away.
Both pass on a seed where the player's first two hours are spent on halite.

**Owner: `gameplay`** — the fix is a design call (mark usable hits in the
prospector, widen the manual furnace, or bias early rings toward the five).

**Closed by ADR 0026**, with two of those three: worldgen deals a guaranteed
starter patch 12–40 tiles from spawn, derived from the recipe graph rather than
listed, and the survey device — which had no UI at all until now — marks every
hit with whether research can consume it. The shipped seed opens with sphalerite
at 37 tiles, ranked first. `EveryStart_PutsSomethingSmeltableUnderTheProspector`
pins it against the prospector's own radius, and CI greps the *rank* in the
session log, not the existence of ore.

**Regression:** `OpeningRouteTests.EveryStart_HasAnOreTheManualFurnaceCanSmeltWithinAWalk`
— passes today, and pins the property the old test only gestured at: a
*smeltable* ore within a 300-tile budget on all 20 seeds.

---

### F3 — S3 — Crude oil is a fluid deposit and can be dug out with bare hands

**Severity: S3.**
**Intermittency: 10/10 on any seed with an oil patch in range; seeds 4 and 10 put one 5–6 tiles from spawn.**

`NewGame.OreSpecs` iterates `Catalogue.RawSolids`, which is *every* raw
non-machine item except water and air — crude oil included, by design, so an oil
derrick has something to stand on. But `HandOps.Mine` never asks what form the
deposit is.

**Steps to reproduce**

1. `NewGame.Create(seed: 4, Catalogue.Instance)`
2. Prospect from spawn; crude oil is the nearest hit, 5 tiles away.
3. `HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory, 10)`

**Expected:** 0. Oil is gated behind the oil derrick — a 4-hull steam building
at the far end of the chain in F1.
**Actual:** `10`. Ten units of crude oil in the player's pockets at minute zero.

The immediate harm is small (nothing at manual tier refines it), but it is the
*most misleading* thing on the map on those seeds: the prospector points a new
player at oil six tiles away, digging it appears to work, and it is a dead end
they cannot see the bottom of.

**Owner: `gameplay`.**
**Regression:** `OpeningRouteTests.HandMining_RefusesAFluidDeposit`, `Skip`ped
with a pointer here. Unskip with the fix.

---

### F4 — S3 — An unpaired tunnel entrance permanently poisons twice its reach, and cannot be removed

This is the `TooFarToTunnel` question `docs/0018` flagged. **Verdict: the
refusal itself is right; its permanence is not.**

`BeltMap.PlaceUnderground` scans `reach * 2` tiles back and refuses anything it
finds an unpaired same-facing entrance behind, at any distance in that window.
Measured exactly (`AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach`), for
`reach = 4`:

| Distance ahead of the entrance | Result |
|---|---|
| 1–4 | pairs, `TunnelRefusal.None` |
| 5–8 | refused, `TunnelRefusal.TooFar` |
| 9+ | placed as a new entrance |

Inside `reach` the refusal is exactly right and the message is good — *"Too far:
a Steam Underground Belt tunnels 2 tiles"* teaches the number, which is what the
ADR argued for. The band from `reach+1` to `reach*2` is the cost the ADR names,
and on its own it would be S4: it is narrow, and the workaround (face another
way, or finish the first tunnel) is one keypress.

**What makes it S3 is the second thing `docs/0018` flagged: removal does not
exist.** An entrance placed by mistake cannot be picked up, so the poisoned band
is permanent for that save. A player who places an entrance, changes their mind,
and walks on has silently made 4 tiles of their bus unbuildable in that
direction, forever, with a message that talks about tunnelling distance and
never mentions the thing actually in the way.

**Owner: `gameplay`.** The narrower fix is not to change the span rule but to
build removal — which F1 also wants, and which is the single change that would
close the most of this report.

**Regression:** `AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach` pins the band
exactly, so narrowing it becomes a visible change rather than a silent one.

---

### F5 — S3 — A tunnel arrives 24 ticks early, and the gain is farmable

The third thing `docs/0018` flagged. **Verdict: the number is honest, the ADR's
framing of it is too generous.**

`ATunnelIsAsLongAsTheSurfaceItReplaces` asserts the gain exactly, as
`breaks * ItemSpacing / SpeedBasic` = `3 * 64 / 8` = 24 ticks. That is good
testing — it pins the mechanism rather than the outcome.

But the ADR calls it "the same small gain every corner in this game has always
given", and 24 ticks is **27% of an 11-tile crossing** (88 ticks at basic
speed). The gain is linear in segment breaks and free: a straight tunnel buys
three breaks with no layout cost, where farming the same three breaks with
corners costs a zigzag. So 0018 did not create the exploit, but it made it
cheap, and the mitigation the ADR reasoned about (make the span real belt) does
not address this at all — it addresses teleporting, which was a different bug.

**Not a blocker and not urgent** — nobody reaches belts before F1 is fixed. Named
here so it is a decision rather than an oversight.

**Owner: `gameplay`.** No new test: the existing assertion already pins the
constant, and adding a second one that asserts the same arithmetic would be
theatre.

---

### F6 — S3 — Three `--smoke` lines were ungrepped, including the icon count

Raised by `art` in `docs/0019`, and correct: `belt parts`, `terrain rebuild` and
`item icons known=N of N` were printed and checked by nothing. The icon line is
the one that matters — the atlas is regenerated by hand and deliberately not
diffed, so that line was the only thing between a stale atlas and a build menu
of blank rows.

**Fixed in `ci.yml` (my file), this change.** Three assertions added:

- `belt parts` must report non-zero undergrounds, splitters, solids and arrows.
- `terrain rebuild` must be present and under 60 ms — a ceiling over the 25 ms
  the cache in 0019 measured, not a baseline. There is no stored performance
  baseline in this repository and one run on one contended machine is not one, so
  this is a guard against the cache being removed, not a performance assertion.
- `item icons` must have `known == of`, and non-zero. Compared as **numbers, not
  as the literal 617**, so adding an item does not turn CI red in a file its
  author does not own.

Each was verified to fail on a mutated log (`known=610 of 617` → error;
`in 99.0 ms` → error; line deleted → grep fails) and to pass on the real one.

---

### F7 — S4 — Every `BuildResult` has its own sentence today, and nothing keeps it that way

`GameRoot.PlaceHeld` names eight of the nine `BuildResult` values explicitly and
they are all distinct. The ninth, `NotBuildable`, falls to `_` and is unreachable
from a click — the UI can only offer what the build catalogue knows. So the
house rule holds right now.

What does not hold is that it will keep holding: the `_` arm means the *next*
`BuildResult` someone adds compiles, ships, and tells the player "Cannot build a
Steam Furnace" where it needed to say why. `TooFarToTunnel` got its sentence in
0018 only because its author remembered.

**No production defect. Fixed on my side** with two tests that read
`game/scripts/GameRoot.cs` and assert every value except `NotBuildable` has an
explicit arm, and that no two sentences are the same string.

---

## Boundary states probed

Deliberate misuse, in the article's "reasonable-but-unintended order" sense:

| What a player might do | What happens | Verdict |
|---|---|---|
| Place the bench on a recipe it can never run (`form_copper_plate`) | Placed, `Starved` forever, no warning | F1's real shape |
| Place the bench somewhere useless | Same as anywhere — no removal, so it is stranded | F1/F4 |
| Spend all 24 stone | 2 manual machines exactly, then a 33–245 tile walk for more | works as designed; pinned |
| Mine the wrong ore first | Nothing says it is wrong | F2 |
| Hand-mine a liquid | Succeeds | F3 |
| Build on the same tile twice | `Blocked`, costs nothing, distinct sentence | correct |
| Build a machine with no recipe | `NeedsRecipe`, distinct sentence | correct |
| Overshoot a tunnel | `TooFarToTunnel`, names the span | correct |
| Place a miner on bare rock | `NoResource`, distinct sentence | correct |
| Place a pump away from water | `NoFluid`, distinct sentence | correct |

The refusal path is genuinely good. Every refusal a player can trigger reaches
them as its own sentence, and none of them charges for the item. The problem is
not that the game goes quiet when it refuses — it is that it goes quiet when
there is nothing left to do (F1).

## Mutation sweep

Every new assertion was verified by breaking the thing it covers and watching it
go red.

| # | Mutation | Test | Result |
|---|---|---|---|
| M1 | `StarterKit` stone `24 → 23` | `TheStarterKitStone_IsExactlyTwoManualMachines` | caught |
| M2 | `PlaceUnderground` scan `reach * 2 → reach` | `AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach` | caught |
| M3 | delete the `BuildResult.NoFluid` arm | `EveryBuildResult_HasItsOwnSentenceInTheBuildUI` | caught |
| M4 | make `NoResource` reuse the `Blocked` sentence | `TheBuildRefusalSentences_AreAllDifferent` | caught |
| M5 | `OreSpecs` ring `index - 1 → index + 2` | `EveryStart_HasAnOre…` / `…HasStoneWithinReach` | **survived at first**, caught after |
| M6 | `RecipesFor` `Tier <= → Tier <` | `EveryStart_HasAnOreTheManualFurnaceCanSmelt…` | caught |
| M7 | atlas count `known=610 of 617` in the log | `ci.yml` icon assertion | caught |
| M8 | `terrain rebuild … in 99.0 ms` in the log | `ci.yml` terrain assertion | caught |
| M9 | delete the `belt parts` line from the log | `ci.yml` belt-parts grep | caught |

**M5 is the one worth reading.** Both reachability tests originally asserted
"within 400 tiles" — the prospector's own range — and passed with every ore
pushed *three rings further out*. The test world was too tidy in the way
`CLAUDE.md` describes: the bound was the tool's, not the player's. Re-pinned to a
300-tile walking budget against a measured worst case of 245, after which M5 is
caught.

**Accepted survivor, named rather than hidden:** ring `index - 1 → index + 1`
still passes. One ring out keeps every seed inside 300 tiles. Tightening past
that would make the test track worldgen noise, so I left it — the sweep catches
a two-ring regression and not a one-ring one, and that is a limit of this test,
not a property of the code.

## What was run

```
. tools/env.sh
dotnet build -warnaserror                       # 0 warnings, 0 errors
dotnet test                                     # all green
./tools/check-no-godot-reference.sh
python3 tools/generate_data.py && git diff --exit-code -- data/
godot --headless --path game -- --smoke --machines=4096
godot --headless --path game -- --session-test
```

## What I did NOT check

Stated plainly, because a partial check rounded up to a pass is worse than no
check:

- **No screenshot.** `art`'s tunnel housings, splitter chevrons, buried studs,
  ground variation and the 617 icons were verified only as *counts* in the smoke
  report. Nobody has looked at them, and `CLAUDE.md` is explicit that rendering
  is not verified until someone has. `xvfb-run … --screenshot` needs running by
  whoever owns the look.
- **No performance claim.** The 60 ms terrain ceiling is a guard, not a
  baseline. I did not run a before/after pair on this machine and so make no
  statement about whether 0018 or 0019 cost anything.
- **No save round-trip of a half-built opening.** The session test round-trips
  the dense demo factory; a world containing one starved bench and nothing else
  was not saved and reloaded. Worth doing once F1 makes such a world reachable
  past step 6.
- **Only 20 seeds.** F2's 18/20 is 20 seeds, not a distribution.
- **Splitter lane behaviour was not probed at all.** `docs/0018` says a splitter
  does not lane-balance; I took that as read rather than measuring it.
- **The GUI was not driven.** Everything above is the sim and the headless
  session runner. No mouse ever clicked a build menu.
