# ADR 0015: Building — placing machines from the inventory

Every entity in the game was placed by code or restored from a save. The player
could mine by hand, carry a starter kit and read a recipe tree, and could not
put a single thing on the ground. This adds building.

## The sim owns the decision, the UI owns the gesture

`World.TryBuild` is the whole interaction in one call: it resolves the item to
a buildable, checks the tile, places the thing, and charges for it. The Godot
layer contributes a cursor position and a click.

Two rules make it safe:

- **The item is taken only after the placement succeeds.** A refused build must
  never cost the player the machine — that is the cruellest bug this code could
  have, and it is the first thing the tests pin down.
- **Every refusal is a distinct reason.** `Blocked`, `NoneCarried`,
  `NoResource`, `NoFluid`, `NeedsRecipe`, `NotPlaceableYet` all need a different
  sentence in front of the player. A build that goes dead without saying why is
  the most confusing thing a build UI can do, so the sim returns a reason and
  never a bool.

## Recipes are chosen before placing, not after

> **Superseded by ADR 0021.** A placed machine can now be retasked, and the
> eviction rule this section deferred is conservation: everything inside comes
> back to the player. The reasoning below is kept as the record of what was
> decided and why it did not survive a playthrough.

A machine needs to know what it makes before it exists. Changing a placed
machine's recipe means deciding what happens to the inputs already inside it —
evict them, void them, refuse while non-empty — and that is its own decision,
not a side effect of building. So the build menu asks first.

This is the one place the design knowingly departs from the Factorio gesture of
place-then-configure. Revisiting it means giving `Machine` a recipe change with
an eviction rule, and a save version.

## What a machine item makes

`BuildCatalogue` maps `<tier>_<machine>` to a `Buildable`. The tier prefix is
**stripped**, never matched as a suffix: `arc_arc_furnace` ends with `furnace`
as well as with `arc_furnace`, and suffix matching resolved it to the wrong
machine — the same bug this codebase has now hit twice.

`BuildKind` says what placing it actually creates, because a furnace and a power
pole are both machine items and only one runs a recipe.

Three findings came out of writing this, all from tests rather than reading:

- **A machine could only run recipes authored at exactly its own tier.** That
  left 63 buildables — most of the upper half of the tree — placeable and unable
  to run anything. A machine now runs its own tier and every tier below it,
  which is what the ladder has always promised.
- **The manual crafting bench was marked unplaceable.** It is the one machine
  the starter kit hands over, and placing it is how the player crafts their
  first furnace. Marking it carried-not-built made the opening loop impossible:
  you would hold a bench you could never put down.
- **The data has machines with no recipes at all** — the sifter, mixer, cutter
  and vacuum freezer are in the item list and the tech tree, and nothing in
  `recipes.json` uses them; the spinneret is offered two tiers below its first
  recipe. Building one produces a machine that can never run, so `Offerable`
  excludes them and `TryBuild` refuses them. `All` still lists them, so this is
  a display decision rather than a quiet deletion. This is a pre-existing gap in
  the progression data, exposed by building rather than caused by it.

## Poles are machines now

A grid is mostly poles and there was no pole item, so one could not be crafted
or carried. `pole` is a machine in the spec like any other, priced in cable,
with reach that grows up the ladder — the upgrade a player feels is the same
floor covered by fewer poles.

## Two systems, one tile

Machines and fluid nodes are tracked by separate structures and neither refuses
the other; code has always been free to layer them and the demo world does. What
a player builds by hand should not overlap, so the rule lives at the build
boundary (`CoversFluidNode`) rather than being retrofitted into either system,
where it would change the meaning of worlds that already exist.

## The ghost

The preview uses the same hull and attachment meshes, the same seating
arithmetic and the same tile size as the real machine, so what you see before
the click is what you get after. A generic box would lie about size on every
machine that is not 1x1, and footprint is exactly what a player is judging as
they move the cursor.

It asks the sim the same question the click will ask, so it can never promise a
placement the build then refuses. Green means allowed, red means not. Over the
menu itself it hides entirely: the panel eats the click, so previewing a
placement that will never happen is worse than showing nothing.

## Verifying a UI

The screenshot found two defects that reading the code did not: every tier of a
machine rendered as the same row (three identical "Alloy Smelter" entries in
front of someone carrying a Steam, an Arc and a Fusion one), and the HUD never
mentioned the build key. Both are now fixed, and the tier-name one is pinned by
a test asserting no two offerable buildables share a display name.

But a screenshot cannot prove the ghost appeared — a green translucent hull and
a green Working attachment look alike at play distance. So the headless smoke
run turns build mode on, takes what the menu offers, and reports whether the
ghost is actually visible, with CI asserting it. A build UI that lists machines
and previews nothing photographs perfectly and cannot be used.
