# ADR 0023: The story, and quests that are made of logistics

Design, ready to implement. Written while the opening-route fix was in flight,
because the shape of this was already decided by data that has been sitting
unused since the beginning.

## The constraint that shaped it

> "Everything should be justified and supportive of logistics. If it isn't
> directly involved, there should be some indirect connection."

That rules out most of what "quests" usually means. No fetch quests that hand
you a trinket, no narrative beats that pause the factory, no rewards that are
not themselves a factory capability. If a quest does not make the player think
about throughput, it does not belong.

It also rules out the easy version of research — a menu where you click a node
and wait. That spends nothing and moves nothing.

## The story was already in the data

The end of the recipe graph is `von_neumann_seed`: a pressure hull, a
fabrication core, a life support loop, a cable harness and a propellant tank,
plus two Singularity machine hulls. That is a self-replicating probe, and it has
been the goal item since the progression was written.

So the premise is not invented, only stated:

> Your probe came apart on entry. The fabricator survived; nothing else did.
>
> A Von Neumann probe exists to make another Von Neumann probe. Yours cannot,
> yet — the machine that builds Seeds is itself a Seed's worth of industry. So
> you start with what a lander carries: a survey device, your hands, and enough
> stone to make a bench.
>
> Build the industry. Build the Seed. Send it on.

Three sentences, no characters, no dialogue, and it justifies every machine on
the map: you are not decorating a planet, you are bootstrapping a factory large
enough to reproduce a spacecraft.

## Quests are the tech tree, and the tech tree already exists

`data/techs.json` holds 32 techs — four lines (metallurgy, processing,
chemistry, fabrication) across eight tiers. Every one has `requires` (prior
techs) and `requires_item`. All 744 recipes carry `unlocked_by`. **Nothing has
ever read any of it.** Recipes are not gated; the ladder is a suggestion.

Give it a runtime and it is a questline already balanced against the recipe
graph:

| | |
|---|---|
| **Quest** | a tech |
| **Objective** | deliver `requires_item` — the tier's machine hull |
| **Prerequisites** | `requires` |
| **Reward** | every recipe whose `unlocked_by` is that tech |

The four Manual techs have no `requires_item`: they are unlocked from the start,
which is what keeps the opening playable.

## Delivery is the whole design

The objective must not be "have this in your inventory". It must be **deliver
it into a structure**, by hand at first and by belt or drone once the quantities
stop being carriable. That single decision is what makes research a logistics
sink rather than a menu:

- A tier costs four hulls (one per line), so the hull line is the first thing a
  player builds *for production* rather than for a single use.
- Hulls need plates, cable and a machine to assemble them — so the reward for
  automating hulls is the next tier, and the cost of the next tier is more
  hulls. The loop closes.
- Feeding it by belt beats feeding it by hand, and nothing forces that; the
  player works it out because carrying gets tedious. That is the correct way for
  a factory game to teach a belt.

The structure is the **Uplink** — where the finished Seed is eventually
assembled, and until then where research is delivered. One building, two
purposes, so the endgame is not a separate mechanic bolted on: the last quest is
delivering the Seed's five assemblies to the same place you have been feeding
all game.

## Rewards are capability, never trinkets

The reward is the recipe unlock. That is already the right shape — it is exactly
"you can now build more of the factory".

One addition, for the opening only: the first tech in each line also grants a
small item kit (a few belts, an inserter, a pole). Not as a prize, but because a
player who has just unlocked steam machines and owns no belts has to hand-craft
their way to their first automated line, and that is the point in every factory
game where people put it down. It is a smoothing of the ramp, and it should be
the only handout in the game.

## What this does not do

- **No timers, no waiting.** Research completes when the items arrive.
- **No side quests.** Every quest is on the critical path, because a quest off
  the critical path is by definition not supportive of logistics.
- **No narrative interruption.** The premise is shown once on a new game; after
  that the story is told by what the player can build.

## Implementation order

1. `Research` in `/sim`: which techs are unlocked, `IsUnlocked(recipe)`, and
   `Deliver(item, count)` against the current objectives. Save it — a factory
   that reloads with its research forgotten is worse than none.
2. Gate `BuildCatalogue.Offerable` and the recipe pickers on it. **The opening
   must stay playable**: the four Manual techs unlocked from tick zero, and a
   test that a new game can still reach its first miner.
3. The Uplink: a placeable structure that accepts items, shows what it wants,
   and is fed by hand, inserter or drone like any other machine.
4. A quest panel: current objectives, what each unlocks, and what is left for
   the Seed.
5. The intro text on new game, and the win state when the Seed is delivered.

Steps 1 and 2 are the ones with teeth — gating recipes touches everything the
build menu shows, and getting it wrong locks the player out of their own game.
Step 2 is where the opening-route tests earn their keep.
