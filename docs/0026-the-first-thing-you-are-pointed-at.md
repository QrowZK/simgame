# 0026 — The first resource you are pointed at is one you can use

## Context

QA's opening playthrough (`docs/0020-opening-playthrough-qa.md`, F2) measured
what a new player walks to. On 18 of the first 20 seeds the nearest resource to
spawn was halite, dolomite, coal, quartz, limestone, garnierite or crude oil —
none of which anything at the manual tier will touch. The nearest ore the
starting furnace could actually smelt was 56 to 184 tiles away, often outside the
prospector's 96-tile range entirely.

The shipped seed did exactly this and `--session-test` reported success:

```
nearest ore     halite at 44,-51 (67 tiles), 6805 units
hand mined      25
miner built     True
--- start flow ok ---
```

Twenty-five units of halite and a miner built on it that will fill with more
halite. The existing tests all passed, because each asserted something weaker
than the thing that mattered: that *some* patch existed within 400 tiles, and
that *some recipe somewhere in the graph* consumed what was dug — including
recipes on machines eight tiers away.

Two smaller facts turned out to matter as much:

- **The prospector had no UI at all.** It has existed since worldgen landed and
  nothing in the game ever showed it. The player carried a survey device whose
  entire job is answering "what is near me and which way", and had no way to ask.
- **A ranked list is not enough on its own.** Even with the right ore nearby,
  nothing on the list distinguished the copper the player needs from the halite
  they cannot use.

## Decision

**Worldgen deals a guaranteed starter patch into the home region.** One resource
that some recipe unlocked at tick zero consumes, on land, between 12 and 40 tiles
from spawn — a real distance, not a bounding box, so the corner of a square
cannot make "40" mean 55. It is dealt before the region's other patches, so every
later patch keeps its distance from it rather than the other way round.

Dealt, not rolled, like the rest of worldgen: a threshold that usually works
still strands the occasional player, and a stranded player cannot tell an unlucky
seed from a broken game.

**Which ores count is derived, never listed.** `NewGame.StarterOres` asks the
recipe and tech graph: a raw solid qualifies when a recipe unlocked at tick zero
consumes it. Widening the manual furnace in `progression.json` widens the
guarantee with no code change. What the starter kit already grants is excluded —
stone qualifies on the letter of the rule, and a guaranteed patch of the one
resource the player lands holding 24 of would satisfy the guarantee while fixing
nothing.

**The survey device gets a face, and its rows say whether you can use the thing.**
`P` opens it, surveying from where the player is looking rather than from the
origin — the device is carried, so walking somewhere and asking again is the
whole interaction. `Research.ConsumableNow` is what the marks are computed from,
so the list widens as research lands.

Unusable hits are **marked, not hidden**. Knowing there is bauxite 60 tiles east
long before anything can smelt it is exactly what a player plans around, and a
list that quietly dropped it would be lying by omission. The mark is a word as
well as a colour, so it survives a player who cannot separate the two greens.

## Alternatives rejected

- **Widen the manual furnace** so more of what is nearby is smeltable. This makes
  the tier ladder's first rung meaningless — the manual furnace smelting halite
  is not a fix, it is a different game.
- **Bias early rings toward the five ores** by weighting the deal. Softer, and
  therefore untestable in the way that matters: "usually near" cannot be asserted,
  and the seeds where it failed would be the ones a player met.
- **Filter the survey list to usable resources only.** Hides the map from the
  player to make the opening tidier, and destroys the one decision the prospector
  exists to create: how far are you willing to go, and for what.

## Consequences

Save format 10 → 11. The file layout is untouched, but the same seed now
generates a different world, and a version 10 file holds mined amounts keyed to
patches at coordinates that no longer hold those patches. A miner standing on ore
that has moved out from under it is precisely the silent wrongness the version
number exists to refuse.

The shipped seed now opens with sphalerite at 37 tiles, ranked first of three,
marked usable. CI greps that rank rather than the existence of ore, because
existence was true on every seed while the opening was still a lottery.

Four mutations were run against the guarantee — removing it, ignoring the starter
flag when choosing the ore, widening the range, and dropping the minimum distance
— and three against the marking, including one that marks everything usable.
All seven were caught.
