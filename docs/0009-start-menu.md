# ADR 0009: Start menu and the game/menu boundary

## Decision

`main.tscn` boots into **`Boot`**, a plain `Node` that owns one decision: title
screen, or straight into a world. `GameRoot` is instantiated as its child when a
game starts and freed when the player leaves.

That indirection is the point. Before this, `GameRoot` *was* the scene root, so
"start a different world" meant reloading the whole scene and rebuilding the
renderer, the camera and the lighting to change one object. Now the world is a
value that `Boot` hands in through `GameRoot.InitialWorld`, and a load is a
world swap rather than a scene reload — which is also why quick-load can keep
the camera exactly where the player left it.

## The headless paths bypass the menu entirely

`--smoke`, `--screenshot` and `--machines=N` go straight into a world. CI drives
those, and a title screen waiting for a click is a hang, not a test.

## What the menu does

Continue, New Game, Load Game, Quit — in that order, with Continue focused by
default. On every launch after the first, resuming is what the player came to
do. Continue and Load are *disabled* rather than hidden when there is nothing to
load, so the menu does not change shape between the first launch and the second.

The seed field is optional: blank means random, and a non-numeric seed is hashed
rather than rejected, so "banana" is a valid world. A required seed field would
be a wall in front of the button most players want.

Saves are listed newest first, showing seed and **ticks** rather than wall-clock
playtime — the sim's clock is the one that says how far a factory has come. A
save whose header will not parse is skipped with a warning rather than taking
the menu down with it: one corrupt file must not make the others unreachable.

## Pausing is a correctness requirement, not a courtesy

Opening the pause menu stops the tick. Saving a world mid-tick would capture a
state no single tick ever produced, and the save format's whole claim is that it
round-trips exactly. So the pause menu — the only in-game route to Save — cannot
be open while the world is running.

Escape backs out one level at a time: the machine inspection panel first, then
the pause menu. Jumping straight to a menu from an open panel reads as the game
ignoring the panel.

## Verification

Two headless modes exist because neither the unit tests nor a screenshot covers
this on its own.

`--session-test` drives the loop a player actually takes: new game, run it,
save to disk, list it, load it back, and confirm the world is still identical
after both copies run on 2,000 more ticks. The sim's own save tests stop at the
sim boundary and never touch the file layer, `user://` path resolution, or the
save lister. CI asserts all three result lines.

`--menu-shot` renders the title screen and saves a PNG, which is how the visual
defects were found: a clipped seed placeholder, a title too small to read as a
title, and a fixed-height card leaving dead space under the buttons.

## Known limits

`GameSession` resolves saves against `DemoWorld.Recipes`, because the game does
not yet read `/data` at runtime — only the tests do. When it does, that becomes
a lookup into the loaded recipe table and nothing else about saving changes.

"New Game" still builds the demo factory rather than an empty world with a
landing site, because worldgen is not yet connected to the machine graph.
