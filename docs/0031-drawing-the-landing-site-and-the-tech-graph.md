# 0031 — Drawing the landing site, and the tech tree as a tree

Status: accepted
Owner: art

## The problem

A new game opened on an empty green field. Two of the most important things in
the game were not drawn at all:

* **Where you are and why.** The premise -- a Von Neumann probe came apart on
  entry, the fabricator survived -- was three sentences in a panel and nothing
  on the ground. The player spawns at tile 0,0 and the most important moment in
  the game was a patch of grass.
* **Where you are going.** 36 techs across 8 tiers, five research lines and a
  dependency graph were presented as a scrolling list of sentences in
  `QuestPanel`. It is a graph, drawn as prose. A player could read the next
  delivery but not see where they were, what it led to, or why the thing they
  wanted was out of reach.

## The landing site: `LandingSite.cs`

A crash site drawn at spawn: a burn scar dragged across the ground, the broken
hull at its leading end, a torn plate standing on edge, a snapped dish, the
struts driven in on impact, and debris thinning back down the furrow.

Three constraints decided its shape, and each rejected an easier alternative.

**It is scenery, not simulation.** Nothing in it is in `World`, occupies a
tile, or is asked about by placement. A player may build straight through it.
The alternative -- scenery that blocks tiles -- would leave a player who wants
the spawn tile stuck forever, because there is no verb in this game for
demolishing something that was never built. `LandingSite` therefore takes a
tile and draws; it never reads or writes sim state, and it is not part of any
`Sync`.

**The ground stays flat.** Land tops are at y=0 and machines seat on that
plane. The crater is a colour on the plane plus geometry standing on it, never
a hole in it: every vertex is clamped to y >= 0 on the way into the buffer, so
no future tweak can push a piece through the terrain plates and show their
undersides through the gaps between tiles.

**It costs two draw batches, once.** The whole site is two `ArrayMesh`
instances with per-vertex colour and per-face normals -- solids, and the burn --
rather than a MultiMesh pool. A MultiMesh is the wrong tool here: there is
exactly one landing site in a game, its forty pieces share no mesh, and a pool
would be a batch per piece. The split into two instances is not cosmetic:
shadow casting is per instance, and the burn must not cast one (see below).

### Two things only a screenshot found

*The scar was invisible.* Wound one way, the sheet's front faces pointed down
and back-face culling removed it entirely. It is now wound both ways with
culling left on, so one triangle of each pair survives whichever way the strip
runs.

*Then the scar was a black hole.* With a double-sided material Godot flips the
shading normal on a back face, so the sheet was lit from underneath and showed
ambient only -- a near-black blob that read as a pit in ground that is supposed
to be flat. Double-sided materials are therefore not used for anything
flat-lying here; the winding carries it.

Both were invisible in the code and obvious in the picture, which is the
argument for the harness below.

## The progression screen: `TechTreePanel.cs`

The graph, drawn as a graph, with the quest list folded into it rather than
kept beside it. In this game research *is* a physical delivery to the Uplink,
so "deliver three metal ingots" and the node "First Metal" are one object seen
from two sides, and splitting them across two screens would be two places to
look for one fact.

**Rows are dependency depth, not tier.** Tier was the obvious choice and is
wrong: the opening rungs are four techs in one tier of one line, which a
tier-per-row grid draws on top of each other. Depth -- the longest chain of
prerequisites behind a tech -- gives every edge a downward direction and gives
any future data a layout without a code change.

**Every node answers three questions**, because a greyed-out box that will not
say what is missing is the failure mode of every opaque tech tree, and this
project already has a house rule against it (refusals carry reasons):

* *What it wants*: the delivery, what has been delivered, and how much of it
  the player is carrying. Group requirements ("any metal ingot") are named as
  groups; the label is not an item id and is not phrased as one.
* *What it gives*: the handout and the machines it opens, by name. "Gives you
  2x Steam Inserter, lets you build the Steam Alloy Smelter" is a reason to do
  something. "Unlocks 26 recipes", which the list said, is not.
* *Why it is locked*: as a sentence, on the node itself and in full in the
  detail pane. A locked node names the prerequisite in its way; an open one
  names the recipe that makes the missing item, the machine that runs it and
  its inputs -- or, when nothing buildable makes it, the tech that opens the
  recipe that would.

All of that is derived from `data/techs.json`, `data/recipes.json` and
`Research`; the panel authors nothing and holds no progression state.

`BodyText` renders the whole screen as plain text for the same reason
`QuestPanel.BodyText` exists: a drawn graph photographs identically whether its
labels are right or empty.

## The capture harness: `scenes/art_preview.tscn`

Every capture flag lives in `Boot.cs` and `GameRoot.cs`, which are frequently
being edited by whoever is wiring gameplay. `ArtPreview` is a scene run
directly that builds the world `NewGame.Create` gives a player, frames spawn at
the opening zoom, draws the site, optionally opens the progression screen on a
real research state, writes a png and quits:

    godot --path game --rendering-driver opengl3 res://scenes/art_preview.tscn \
        -- --shot=landing --out=/tmp/landing.png
    godot --path game --rendering-driver opengl3 res://scenes/art_preview.tscn \
        -- --shot=tech --out=/tmp/tech.png

It uses the game's own lighting values on purpose: a preview lit differently
from the game hides exactly the contrast problems it exists to find.

## What was rejected

* **A machine at spawn instead of scenery.** It would have been simpler to
  place a real building. It also gives the player a machine they did not build,
  in a tile they cannot choose, that removal has to handle.
* **A crater in the ground.** Placement knows nothing about height and machines
  seat on y=0. Depth here would be a lie the renderer told the sim.
* **A MultiMesh for the wreck.** A pool of one instance per piece is a batch
  per piece for a thing that exists once.
* **A tech tree beside the quest list.** One screen, one place to look.
