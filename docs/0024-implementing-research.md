# ADR 0024: What implementing ADR 0023 taught us

ADR 0023 is the design and it stands. This is the record of building it: the
three places the design turned out to be underspecified, the one thing that
made the Uplink cheap, and the defect the work exposed in somebody else's half.

Everything here was landed. Save format is 10.

## The tech graph was already a playable questline, and that was checked

ADR 0023's central claim is that `data/techs.json` plus every recipe's
`unlocked_by` *is* the questline. That was an assertion about data nobody had
ever executed. Before writing the gate, the closure was computed: start from the
four Manual techs, craft everything the unlocked recipes can make from raw
materials, deliver whatever that produced, repeat.

**All 32 techs and the Seed are reachable.** No hand-holding, no exceptions, no
recipe that needs a machine locked behind itself. The tier ladder had been
authored carefully enough that the gate could simply be switched on.

That closure is now `ResearchTests.EveryTech_AndTheSeed_IsReachableFromTheManualTierAlone`,
and it runs against the real `data/`, not a fixture. It is the only test in the
suite that can catch a progression lock-out introduced by a balance edit, and it
is cheap: a data change that strands a tier turns it red.

## Three things the design did not settle

### A tech wants exactly one hull, so only the Seed can be part-delivered

ADR 0023 says "a tier costs four hulls (one per line)". That is true, but it
arrives as four separate objectives of one hull each, not one objective of four.
The consequence was not obvious until the save tests were written: **no tech
objective can ever be partly delivered**, because one item completes it. The
only objective with more than one need is the Seed.

The progress table is still saved per objective per item, because the Seed needs
it and because a future `requires_item` count of more than one should not need a
save format change. But the save round-trip test had to be built around the Seed
to exercise it at all.

### What no objective wants must not be eaten

The design says the Uplink "accepts delivered items". It does not say what
happens to an item nothing wants, and the obvious implementation -- take
everything, credit what fits -- silently destroys a misrouted belt of ore. That
is the same class of bug as a machine that voids its input, and in a factory
game it is the expensive kind: the player finds out hours later.

So `Deliver` returns what it **accepted**, the caller keeps the rest, and the
Uplink leaves unwanted items sitting in its input buffer. A misrouted belt backs
up and stops, which is a problem the player can see and undo. The panel names
that buffer "Unwanted, still in the hopper" rather than "In:", because a full
hopper is a routing mistake and not a queue.

### Credit does not run forward

A delivery is credited only against objectives that are open **at the moment it
arrives**. Five Steam hulls unlock the four Steam techs and the fifth is refused,
rather than being banked against the Voltaic techs it has just opened. Without
that rule a player could stockpile one tier's hulls and buy the next tier with
them, which would collapse the loop the whole design rests on -- the reward for
automating hulls is supposed to be that the *next* tier needs different ones.

## The Uplink is an ordinary Machine, and that was the whole trick

The tempting implementation was a new structure with its own list on `World`,
like miners and extractors. That would have meant a new `EndpointKind`, belt and
inserter delivery, drone tasks, occupancy, click-to-inspect, the save format and
the renderer -- every one of them touched for one building.

Instead the Uplink is a machine in `data/machines.json` with a real recipe,
`uplink_deliver`, that has **no inputs and no outputs**. Everything above comes
for free: it is fed by belt, inserter, drone or hand through the paths that
already exist, it saves and loads through `MachineSave`, it is clicked and
inspected like anything else, and `BuildCatalogue` places it without a special
case.

The sim then does exactly one special thing with it: `World.Tick` drains its
input buffer into `Research` instead of running its cycle. That is the entire
divergence, and it is four lines.

The cost of the trick is honest and worth naming: a no-input, no-output recipe
is a shape nothing else in the data has, and the invariant "every recipe
produces something" is now false. It survived the data tests because those only
assert `Outputs.Single()` on `build_` recipes. A future data invariant that
assumes every recipe has an output will trip over this one, and should exempt it
rather than "fix" it.

The Uplink is granted in the starter kit rather than crafted, because it is the
premise -- the lander's fabricator, the thing that survived. But it is *also*
craftable from stone (`build_man_uplink`, 12 stone), because there is still no
removal (board item S3) and a player who sites the only one badly would
otherwise be stuck with it for the life of the save.

## The gate: nullable, and two overloads rather than two systems

`BuildCatalogue.RecipesFor` and `Offerable` grew a `Research?` parameter where
null means ungated. That is not laziness: headless throughput analysis and the
demo world build machines directly and want the whole graph, and giving them
their own copy of the tech order would have been a second gate to keep honest.

Every player-facing caller passes `world.Research`. The gate is enforced in
three places and all three were mutation-tested: the picker (`RecipesFor`), the
build path (`TryBuild` -> `NotResearched`) and retasking (`TryChangeRecipe` ->
`NotResearched`). Retasking matters as much as building: it is the second door
into the recipe set that ADR 0021 opened, and a gate on only one of two doors is
not a gate.

**The mutation that survived the first pass was exactly the one to worry about.**
Removing the research check from `RecipesFor` -- so the picker offers locked
recipes and the build path then refuses them -- killed no test, because the
tests only asserted that the right things were *present*. A picker that offers
what the build path refuses is a dead end the player cannot reason about. Two
tests were added that assert absence and that everything still offered is
actually buildable, and the mutation now dies.

Non-machines are never hidden by the gate. Owning a belt already means its build
recipe was unlocked, and a build menu that refuses to place something the player
is holding is the one thing a build menu must never do.

## What this exposed elsewhere

Giving the Uplink a capture path (`--uplink-shot`) put a single machine on real
terrain in front of a camera for the first time. It is invisible.

`MachineRenderer.WriteOne` writes every hull at **y = 0**, while
`TerrainRenderer` lifts each tile by `gen.HeightAt(x, y) / 127.5 - 1`. On any
tile whose terrain sits above zero the machine is underneath the ground. The
demo world the smoke run uses is flat, so this has never shown up in a capture.
It is a rendering defect, it predates this work, and it is on the board for art.

## What is not done

- **The intro is a panel, not a moment.** It is the premise at the top of the
  objective list on a new game, dismissed by closing it. That is deliberately
  the cheapest thing that satisfies "shown once, no dialogue, no characters",
  and it should not be mistaken for a considered opening.
- **The win is a toast and a line in the panel.** Nothing stops, nothing is
  taken away, and there is no credits screen. A factory game that closes the
  factory when you finish it has punished you for finishing it.
- **Nothing tests the Godot layer.** The gating, delivery and save behaviour are
  covered in `/sim`; the panels are covered only by a smoke line and by looking.
