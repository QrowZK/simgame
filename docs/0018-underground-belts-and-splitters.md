# ADR 0018: Underground belts and splitters

ADR 0016 put belts and inserters on the map and left these two out, with a
reason that was really a design problem in disguise:

> An underground belt is a *pair* of tiles with a span between them, and a
> splitter straddles two. Both are their own placement gesture rather than the
> single tile a belt or an inserter is.

Both halves of that turn out to be assumptions imported from Factorio rather
than facts about this game. Neither needs a new placement gesture.

## A splitter is one tile

Factorio's splitter is 1x2. Here, **a machine's footprint is square and its
size is its throughput dial** (ADR 0007), and the occupancy grid, the build
ghost, the picking code and the rotation rules all assume that. A 1x2 building
would be the only rectangle on the map, and it would buy nothing the simulation
needs: `Splitter` already balances between two outputs and has never cared how
wide it is.

So a splitter is **one tile with a facing**. It takes whatever is fed into it —
from any direction, exactly as a belt tile does — and alternates between two
outputs: the tile **straight ahead** and the tile **to its right**.

Right rather than left is not a coin toss. Rotating reaches every ordered pair
of adjacent directions: a line running east splits east/south with the splitter
facing east, and east/north with it facing north. "Which side does the branch
leave by" is therefore a decision the player makes with the rotate key, and the
single output pair keeps the tile symmetric under rotation.

**What this costs against Factorio.** Two things:

- Factorio's splitter merges as well as splits, because it is two tiles wide
  and both halves take input. This one has a single tile of input, so two lines
  meeting still meet the way belts have always merged here — by both pointing
  at the same tile.
- A balanced split into two *parallel* lines needs one corner belt on the
  branch side, where Factorio needs none. That is one tile, and in exchange the
  splitter is placeable with the same click that places a belt.

## A splitter splits each lane on its own

`Splitter` moves items and knows nothing about lanes, so this had to be
decided rather than inherited. Three options:

1. **Merge both lanes into one buffer.** Simplest, and wrong: a splitter would
   halve the throughput of any line it stood in, which makes the correct play
   never to use one.
2. **Balance lanes as well as sides**, as Factorio does. This needs a second
   round-robin inside `Splitter` and makes it a lane-aware object.
3. **Split each lane independently**: two `Splitter` objects per placed tile,
   one per lane, wired lane-for-lane.

Three, and it needs no change to `Splitter` at all — `SplitterIndex(placed,
lane)` is the whole of it. Full throughput, items stay on the side they were
already riding, and the round-robin on each lane is exact.

**What this costs:** it does not lane-balance. A line arriving with one lane
full and the other empty leaves just as lopsided. That is a real Factorio
technique this does not support, and the honest place to note it is here rather
than in a player's head at 3am.

## An underground belt is placed one end at a time

The pair is not a gesture problem either. Each end is **one tile with a
facing**, placed by the same call as everything else, and costs one item — so a
tunnel costs two.

**The role is placement order.** An end placed within reach *behind* an
unpaired entrance that faces the same way completes it as the exit; anything
else is a new entrance. Place, walk, place: the gesture Factorio already
taught, with no second key.

The alternative was deriving the role from the surrounding belts — "the end
with a belt feeding it is the entrance". Every such rule changes its mind when
a neighbouring belt is rotated, and a tunnel that reverses direction because
something two tiles away turned is not a system anybody can reason about.

Because roles are chosen and not derived, **they are stored in the save** (which
is what takes the format to version 8). A reload does not place tiles in the
order they were built; it places them in list order. Re-deriving would turn the
layout entrance–exit–exit into entrance–exit–*entrance*, which is a different
factory. `AnEntranceWithTwoCandidateExitsTakesTheNearer` round-trips exactly
that layout.

### Span, and what happens when it is exceeded

Reach is **two tiles per tier**, measured between the two ends: four at VLT,
twelve at QNT. Flat reach was rejected because it makes tiering an underground
belt pointless — you would upgrade the surface belt around it and leave the
holes alone. Two per tier means the same obstacle needs fewer holes and, at the
top, a four-wide bus can be crossed in one piece.

Overshooting is **refused**, with its own reason (`TooFarToTunnel`), rather than
quietly placed as a second entrance. A player who has just walked out from an
entrance and clicked is completing a tunnel; handing them a broken line and a
spare hole teaches them nothing, whereas "a Voltaic Underground Belt tunnels 4
tiles" teaches them the number. The cost is that placing a genuinely unrelated
entrance in line with an unpaired one, facing the same way, beyond the span,
has to wait until the first is paired or be placed facing another way.

### Pairing, overlaps, and a missing partner

Every entrance claims the **nearest unclaimed exit** ahead of it that faces the
same way and is within both ends' reach, walking entrances in placement order.

Nearest-first is what makes overlapping tunnels behave: an inner pair claims its
own exit before an outer entrance can reach past it, so a tunnel never swallows
another one's exit.

An end whose partner is missing — never placed, or a save that arrived that way
— is **a one-tile belt**. It carries what is put on it and hands it to whatever
is in front. That is the honest degradation: a hole that silently ate items
would be indistinguishable from a bug, and it is exactly what a player sees on
the map anyway.

## The span is real belt, not a teleport

This is the decision with teeth. The entrance's compiled segment is **as long
as the span it covers** — the entrance tile plus every buried tile — so an item
crosses a tunnel in exactly the time it would have taken to cross the same
number of surface tiles. A tunnel that handed items straight to its exit would
be a free speed-up, and the correct play would become to tunnel everywhere.

`ATunnelIsAsLongAsTheSurfaceItReplaces` asserts the eleven tiles between two
points are eleven tiles of belt either way. It also asserts the *difference* in
arrival time rather than ignoring it: the tunnelled line does arrive 24 ticks
early, because it has three more segment breaks, and a hand-off places an item
one item-spacing inside the next segment. That is the same small gain every
corner in this game has always given; naming its size is what makes the test a
test of the span rather than of the hand-off.

## Buried tiles are tiles, and things can be built on them

A buried tile is in the segment's tile list but not in the belt tile map, so:

- `HasAnythingAt` says no, and a machine, a pipe or **another belt** may be
  built on top of it. Passing under things is the entire point.
- `IsBuried` says yes, and is what rendering needs so that ore in transit
  underground is not drawn on top of whatever was built over it.
- An inserter cannot reach a tunnel end. It is a hole in the ground, not a belt
  deck, and an arm reaching into an entrance would be taking from the far side
  of the span it faces. An inserter *may* load a splitter, on the same lane-0
  convention it uses for a belt tile.

Because a buried tile and a surface belt can share coordinates, the tile-to-
segment map had to split in two, and the snapshot taken before every recompile
had to record which of the two an item was standing on. Without that flag, ore
in a tunnel running under a belt comes back on the belt.

## A feeder is not only a belt

`BeltMap` broke a run at a merge by counting **belts** pointing into a tile.
A tunnel exit and a splitter feed a tile in exactly the same sense, and both
hand to a segment's *entry*. Counting only belts let a tile fed by a belt *and*
a tunnel exit carry on the belt's run — which put everything arriving from the
tunnel in at the far back of that run, so ore surfaced several tiles upstream of
the hole it came out of. `ATunnelExitMergingIntoABeltStartsItsOwnSegment` is
that bug as an assertion.

## Splitters survive a rebuild; segments do not

Segments are compiled and thrown away on every change. Splitters are **placed
things**, like inserters, and are reused across a rebuild rather than remade —
otherwise everything buffered in one would vanish the moment a player extended
a belt beside it.

That also means a save restores only what a splitter is *holding*, not what it
is wired to, in any world that has tiles. "Has tiles" had to grow to include
tunnel ends and splitter tiles: a world made only of those and no belt at all
loaded as hand-built, added its segments twice, and threw on the first tick.

## What is still not here

- **Removal.** Nothing in `BeltMap` removes anything yet, for belts either, so
  "the player picks up half a tunnel" is currently only reachable from a save.
  The behaviour it would produce is defined and tested (a one-tile belt); the
  gesture is not.
- **Lane balancing**, as above.
- **Priority splitting** — Factorio's input and output priority. It is a real
  decision and a good one, but it is a second UI on a tile that does not have
  one yet.
