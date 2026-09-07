# ADR 0016: Belts and inserters as things on the map

`BeltNetwork` moved items beautifully and knew nothing about where any of them
were: a segment had a tile *count*, not tile *positions*, and endpoints were
list indices. That is exactly the gap fluids had before ADR 0012 — a system that
works perfectly and that a player cannot build, see, or reason about.

`BeltMap` is the tile layer over it.

## Tiles are what the player places; segments are compiled

A belt is one tile with a facing. Runs of tiles are **compiled** into segments,
and that compilation is the whole design decision.

The obvious alternative — one segment per tile — throws away the reason `Lane`
is built the way it is. A lane stores items back-to-front with the gap ahead of
each, so advancing it is a single subtraction however many items it holds: a
saturated 100-tile belt costs exactly as much to tick as an empty one. A segment
per tile would turn a 200-tile run into 400 lane advances a tick and make that
property worthless. `ALongRunDoesNotBecomeAsManySegmentsAsTiles` is that
reasoning as an assertion.

So segments break only where they must:

| Break | Why |
|---|---|
| A corner | a segment is a straight run |
| A speed change | a segment has one speed, so the slow half would run fast |
| A merge (two belts into one tile) | both feeders must hand on independently and back up on their own |
| A tile an inserter reads or writes | so the arm reaches the exact tile it faces |

That last one is the subtle one and the reason inserters work at all. Endpoints
address a *segment*, and a segment's exit is where items leave. An inserter
handed to the "back" of a long run would drop items at the far end of the belt,
and one taking from its exit would pick up from wherever the run happened to
end rather than from the tile beside the arm. Breaking the run at those tiles
makes the endpoint and the tile the same thing.

## Extending a belt must not destroy what is on it

The compilation runs whenever tiles change, which is constantly — a player
extends a running line as a matter of course. So every item's world position
(tile, offset within it, lane) is taken before the recompile and restored
after. `ExtendingARunKeepsTheItemsAlreadyOnIt` asserts the front item's distance
from the exit is unchanged, not merely that the count survived.

Items whose tile no longer has a belt are destroyed and counted in
`SpilledOnRemoval`, for the same reason fluids count what mixing voids: a player
who cannot see the loss will never work out where their ore went.

## The two ways of building a segment do not mix

Compiling replaces the whole segment list, so a hand-built segment would vanish
the moment the player placed a belt tile — silently, taking a factory with it.
Worlds that build segments directly (the movement-layer unit tests, the
placeholder factory, an older save) never trigger a compile, and mixing the two
throws with an explanation rather than losing the work.

Keeping `AddSegment` matters: the tests of lanes, splitters, backpressure and
inserter swings are tests of the *movement* layer, and forcing them through
tiles would make them worse tests of it.

## Rendering draws the simulation, not an animation of it

Items on belts are drawn from the sim's own numbers: a lane says how far an item
is from its segment's exit, the map says which tiles that segment crosses, and
together they give a world position. Nothing interpolates or approximates, so
what you see is what the tick computed.

This matters more than it sounds. A belt with nothing drawn on it is a grey
strip, and "is anything actually moving, and where does it stop" is the only
question a player asks about a line. A backed-up belt shows as packed items,
which is how a bottleneck is found without opening a single panel.

## Two things the tests caught

- **A test feeding a belt with a loop of `TryInsertBack` put exactly one item on
  it.** The call fills the back of the lane, so every later one fails silently.
  A conservation test that read as though it counted eight items was counting
  one, and a mutation that destroyed items on rebuild survived because there was
  only ever one item to destroy. Feeding now goes through travel and asserts the
  belt is carrying what the test thinks.
- **The save stored only segments.** Since segments are compiled, a reloaded
  factory had belts that still moved items and could not be seen, extended or
  picked up — the worst kind of working. Format 7 stores the tiles as ground
  truth; the segments come back by recompiling, which is deterministic given the
  same tiles in the same order, so lane contents still restore by index.

  The first version of that save test laid every belt east at one speed, so
  mutations dropping facing and speed both survived. The saved world now has a
  corner and a speed change in it.

## Not yet placeable

An underground belt is a *pair* of tiles with a span between them, and a
splitter straddles two. Both are their own placement gesture rather than the
single tile a belt or an inserter is, so both still report `NotPlaceableYet`
rather than pretending.
