# 0032 — A screen for choosing a world

## Context

Loading a game was a fold-out list inside the main menu card: one `ItemList`
row per save, reading

```
quicksave   seed 1481765108   12.4h   2026-09-06 22:41
```

That is enough to tell two saves apart when they are two different worlds. It
is not enough when they are the same world an hour apart, which is what a
save list mostly contains — and it is the question the screen exists to
answer: *which of these is the one I want?*

The design (`templates/save-select/SaveSelect.dc.html`, marked PROPOSAL in the
Automation design system) answers it with a detail pane: machines, belts,
research, tier, save format. It also does something the old list did not.

## Decision

Build it as its own screen, opened from Load Game and closed with Back, over
the menu rather than instead of it.

Two parts of the design are behaviour rather than styling, and they are why it
was worth building rather than restyling:

**A save that will not parse is a row.** `GameSession.List()` caught the
exception, called `GD.PushWarning`, and dropped the file. So a corrupt save
disappeared from the game entirely: it looks like data loss, the player cannot
tell whether the file is gone or merely unreadable, and — worst — they cannot
delete the thing that is bothering them, because there is no row to select.
It is now listed, dimmed, marked in the starved amber with a reason, with Load
disabled and Delete enabled. That is the only useful thing to do with one.

The reason is a sentence, not the serializer's. `System.Text.Json` says

```
't' is an invalid start of a property name. Expected a '"'.
Path: $ | LineNumber: 0 | BytePositionInLine: 2.
```

which tells a player nothing they can act on, and is long enough to stretch
the list out of the card — which is how the clipping bug below was found. A
version mismatch *is* worth saying exactly ("save version 11 is not version
13") because it means the file is old, not broken. Everything else is "header
will not parse".

**The detail pane reads the save, not the filename.** Machines counts every
placed thing (machines, miners, generators, accumulators); belts counts placed
belt tiles; research is unlocked-of-total; tier is the furthest tier the save
has *researched into*, read from unlocked tech ids rather than from the
machines standing on the map — a player who has researched Steam and not built
a Steam machine yet has still got there.

## Consequences

`GameSession.Delete` was already there, and wrong in two ways that cancelled
out into silence: it passed a `user://` virtual path to `DirAccess.RemoveAbsolute`,
which takes a filesystem path, and it discarded the returned error. A delete
that failed looked exactly like one that worked. It globalizes the path now and
throws, and the screen shows what happened.

Two defects came out of looking at the rendered screen rather than the code:

- The card rendered wider than the viewport, its left edge and half the detail
  pane off-screen. A `Label`'s minimum size is its full text and a
  `PanelContainer` sizes to its contents, so one 90-character parse error
  pushed the whole card past the 800px it is designed at. Row text clips with
  an ellipsis now.
- Clipping then erased every value in the detail pane. The label expands to
  fill the row, so a clipped value is handed zero width and draws an empty
  string rather than an ellipsis. The label gives way; the value never does.

Verified by driving the real window: Load Game opens it, clicking a row moves
the detail pane to that save, and Delete removes the row *and* the file from
disk — checked with `ls`, not inferred from the screen.
