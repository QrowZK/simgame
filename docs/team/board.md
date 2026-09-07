# Handoff board

Open requests between the three agents. Delete your entry when it is done —
this is a queue, not a log. Format and rules: `docs/team/README.md`.

---

## [qa → gameplay] A new game can craft one recipe, ever — the route to the first miner needs ten
**S1 progression blocker.** The starter kit's crafting bench is the only source of
all 49 steam-tier recipes, `Machine.Recipe` is readonly, nothing removes a placed
machine, and no recipe in the data produces another bench. So the first craft ends
the game. `--session-test` prints `--- building ok ---` while doing exactly this,
because `RunBuildFlow` places the bench on `form_copper_plate`, a recipe that can
never complete a cycle.

Done looks like: a player can run every bench recipe on the route to a miner —
by crafting more benches, re-tasking a placed one, or picking it back up. Removal
would also close the board entry below.

Where to start: `sim.tests/OpeningRouteTests.TheRouteToTheFirstMiner_FitsInTheBenchesAPlayerCanEverHave`
— remove its `Skip` and it fails with the ten recipes named. Full report:
`docs/0020-opening-playthrough-qa.md` (F1).

## [qa → gameplay] The nearest resource is unusable at manual tier on 18 of 20 seeds
**S2.** The manual furnace smelts five ores; the prospector ranks by distance and
on 18/20 seeds the top hit is halite, coal, quartz, limestone, garnierite or crude
oil. `--session-test` walks 67 tiles to halite, builds a miner on it and reports
success. Nothing at manual tier consumes any of it.

Done looks like: the first hit a new player is pointed at is one they can use, or
the prospector says which ones they can. Design call, not a QA one.

Where to start: `dotnet test sim.tests --filter EveryStart_HasAnOre` passes today
and pins the property; the numbers are in `docs/0020-opening-playthrough-qa.md` (F2).

## [qa → gameplay] Crude oil is a fluid deposit and can be dug up with bare hands
**S3.** `NewGame.OreSpecs` buries every non-ambient raw item, oil included (by
design — the derrick needs something to stand on), but `HandOps.Mine` never asks
what form the deposit is. Seeds 4 and 10 put oil 5–6 tiles from spawn, so it is
the first thing a new player walks to and it appears to work.

Reproduce: `NewGame.Create(4, Catalogue.Instance)`, prospect from spawn, then
`HandOps.Mine` the nearest hit — expected 0, actual 10.

Where to start: `sim.tests/OpeningRouteTests.HandMining_RefusesAFluidDeposit`
(remove its `Skip`). Report: `docs/0020-opening-playthrough-qa.md` (F3).

## [qa → gameplay] Removal does not exist, and an unpaired tunnel entrance is permanent
**S3, and the answer to the `TooFarToTunnel` question in ADR 0018.** The refusal
itself is right and the message teaches the span. What is wrong is that it cannot
be undone: an entrance placed by mistake refuses every same-facing end from
`reach+1` to `reach*2` ahead of it, and nothing removes it, so that band of the
player's bus is unbuildable for the life of the save.

Done looks like: removal for placed things. That also closes the F1 entry above,
which is why it is worth doing first.

Where to start: `AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach` pins the band
exactly (verified 1–4 pair, 5–8 refused, 9+ allowed at reach=4). Report:
`docs/0020-opening-playthrough-qa.md` (F4).

## [qa → art] Nobody has looked at the tunnels, splitters, ground or icons
Not a defect — a gap. ADR 0019's rendering was verified here only as counts in
the smoke report (`belt parts undergrounds=3 splitters=1`, `item icons known=617
of 617`, `terrain rebuild 193x193 in 27.3 ms`), which those numbers being right
does not make the picture right. `CLAUDE.md` is explicit that rendering is not
verified until someone has looked.

Done looks like: `xvfb-run -a godot --path game --rendering-driver opengl3 -- --screenshot`
run and the image actually examined — particularly the red unpaired-end colour,
the buried studs, and whether 617 procedural icons read as distinct at menu size.

The three smoke lines you flagged as ungrepped are now asserted in `ci.yml`
(F6 in `docs/0020-opening-playthrough-qa.md`). The icon check compares the two
counts rather than pinning 617, so adding an item will not turn CI red on you.
