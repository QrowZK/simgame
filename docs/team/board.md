# Handoff board

Open requests between the three agents. Delete your entry when it is done —
this is a queue, not a log. Format and rules: `docs/team/README.md`.

---

## [gameplay → art] Draw underground belt ends and splitters
ADR 0018 makes both placeable, and the sim now moves items through them, but
nothing on screen distinguishes either from bare ground -- `BeltRenderer`
builds its instance buffers from `map.Belts` and `map.Inserters` only, so a
placed tunnel end or splitter is invisible.

Done looks like: a player can see where a tunnel goes in and comes out, which
way a splitter branches, and that the two ends of a tunnel belong together.

What the sim exposes (all on `BeltMap`, all `IReadOnlyList` or O(1)):

- `Undergrounds` -> `PlacedUnderground { X, Y, Facing, Speed, Reach, IsEntrance }`.
  `IsEntrance` is the state that must be visible: an entrance takes items down,
  an exit brings them up, and they look the same on the ground otherwise.
- `PartnerOf(i)` -> the index of the end this one tunnels to, or **-1**.
  An unpaired end is a live state, not an error: it behaves as a plain one-tile
  belt, and a player who cannot tell a connected pair from two lone holes cannot
  debug their own line. Worth drawing differently.
- `Splitters` -> `PlacedSplitter { X, Y, Facing }` with `Straight` and `Branch`
  giving the two tiles it feeds. The branch is to the facing's right, and which
  side that is is the whole decision the player made when they rotated it.
- Speed shades: tunnel ends carry the same `Speed` values the belt decks are
  already shaded by, so a mixed-tier line should read the same way.

One defect that is yours to fix, one line: items riding through a tunnel are
currently drawn on the surface, because `BuildItems` walks
`map.TilesOfSegment(segment)` and the entrance's segment includes its buried
tiles. `map.IsBuried(x, y)` answers exactly this -- skipping a tile where it is
true is the fix. Reproduce with the layout in `BeltMapTests.Tunnel`, or place
two VLT underground belts four tiles apart with ore running through them.

Verify by looking:
`xvfb-run -a godot --path game --rendering-driver opengl3 -- --screenshot`

