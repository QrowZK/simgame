using Sim;
using Sim.Data;

namespace Sim.Tests;

/// Belts and inserters as things on the map.
///
/// The properties worth holding: a straight run compiles to ONE segment however
/// long it is (the lane's O(1) advance is the whole reason it is built the way
/// it is), an inserter reaches the exact tile it faces, and extending a working
/// belt does not destroy what is already riding on it.
public class BeltMapTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World Bare()
    {
        var db = new ItemDatabase();
        return new World(7, db);
    }

    /// Feeds items onto a lane the way the factory does: one at a time, with
    /// travel in between. `TryInsertBack` fills the back of the lane, so a loop
    /// of bare inserts puts exactly ONE item on the belt and every later call
    /// silently fails -- which makes a conservation test that counts one item
    /// look like a conservation test that counts eight.
    private static void Feed(World world, int x, int y, ItemId item, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var lane = world.Belts.Segments[world.BeltMap.SegmentAt(x, y)].LaneAt(0);
            Assert.True(lane.TryInsertBack(item), $"item {i} would not fit on the belt");
            world.Tick(10);
        }

        Assert.True(world.Belts.ItemsInTransit() >= count,
                    "the belt is not actually carrying what the test thinks");
    }

    private static World Lay(out ItemId ore, int length = 6,
                             Direction facing = Direction.East)
    {
        var world = Bare();
        ore = world.Items.Register("ore");

        for (var i = 0; i < length; i++)
            world.BeltMap.PlaceBelt(i, 0, facing);

        world.SyncBelts();
        return world;
    }

    [Fact]
    public void AStraightRunCompilesToOneSegment()
    {
        var world = Lay(out _, length: 20);

        Assert.Single(world.Belts.Segments);
        Assert.Equal(20, world.Belts.Segments[0].Tiles);

        // Every tile has to resolve to it, or rendering and inserters cannot
        // find the belt they are standing on.
        for (var i = 0; i < 20; i++)
            Assert.Equal(0, world.BeltMap.SegmentAt(i, 0));
    }

    /// The performance claim in one assertion. A segment per tile would tick
    /// 200 lanes instead of 2, and the lane's O(1) advance would be pointless.
    [Fact]
    public void ALongRunDoesNotBecomeAsManySegmentsAsTiles()
    {
        var world = Lay(out _, length: 200);
        Assert.Single(world.Belts.Segments);
    }

    [Fact]
    public void ACornerEndsOneSegmentAndStartsTheNext()
    {
        var world = Bare();
        // The turn tile faces the new way, as it does in Factorio: (3,0) is
        // where the line stops going east and starts going south.
        for (var i = 0; i < 3; i++) world.BeltMap.PlaceBelt(i, 0, Direction.East);
        for (var i = 0; i < 4; i++) world.BeltMap.PlaceBelt(3, i, Direction.South);
        world.SyncBelts();

        Assert.Equal(2, world.Belts.Segments.Count);

        // And they are joined, or the corner silently drops everything.
        var first = world.BeltMap.SegmentAt(0, 0);
        var second = world.BeltMap.SegmentAt(3, 1);
        Assert.NotEqual(first, second);
        Assert.Equal(EndpointKind.Belt, world.Belts.OutputOf(first, 0).Kind);
        Assert.Equal(second, world.Belts.OutputOf(first, 0).Index);
    }

    [Fact]
    public void ItemsRideARunFromEndToEnd()
    {
        var world = Lay(out var ore, length: 8);
        var segment = world.BeltMap.SegmentAt(0, 0);

        world.Belts.Segments[segment].LaneAt(0).TryInsertBack(ore);
        Assert.Equal(1, world.Belts.ItemsInTransit());

        // 8 tiles at 256 units, 8 units a tick: 256 ticks to cross.
        world.Tick(300);

        // Nothing takes from the end, so it is still on the belt, at the exit.
        Assert.Equal(1, world.Belts.ItemsInTransit());
        Assert.True(world.Belts.Segments[segment].LaneAt(0).FrontReady);
    }

    /// The property that makes building a belt bearable: laying more track does
    /// not destroy what is already on it. A player extends a running line
    /// constantly, and a rebuild that voided the contents would be unusable.
    [Fact]
    public void ExtendingARunKeepsTheItemsAlreadyOnIt()
    {
        var world = Lay(out var ore, length: 6);
        Feed(world, 0, 0, ore, 3);

        var before = world.Belts.ItemsInTransit();
        var positionBefore = world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)]
                                  .LaneAt(0).PositionOf(0);
        Assert.Equal(3, before);

        // Lay two more tiles at the BACK of the run, which is where a player
        // extends from when the source moves further away.
        world.BeltMap.PlaceBelt(-1, 0, Direction.East);
        world.BeltMap.PlaceBelt(-2, 0, Direction.East);
        world.SyncBelts();

        Assert.Single(world.Belts.Segments);
        Assert.Equal(before, world.Belts.ItemsInTransit());

        // And they are where they were: the front item's distance from the exit
        // is unchanged, because the exit did not move.
        Assert.Equal(positionBefore,
                     world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)].LaneAt(0).PositionOf(0));
    }

    /// An inserter takes from the tile it faces, not from wherever the run it
    /// happens to touch ends. This is why segments break at an inserter's tiles.
    [Fact]
    public void AnInserterTakesFromTheExactTileItFaces()
    {
        var world = Lay(out var ore, length: 10);

        // Facing north out of the belt at tile 4: it reads tile 4, not tile 9.
        world.BeltMap.PlaceInserter(4, -1, Direction.North);
        world.SyncBelts();

        var readTile = world.BeltMap.SegmentAt(4, 0);
        var laterTile = world.BeltMap.SegmentAt(5, 0);
        Assert.NotEqual(readTile, laterTile);

        Assert.Single(world.Belts.Inserters);
        Assert.Equal(EndpointKind.Belt, world.Belts.Inserters[0].Source.Kind);
        Assert.Equal(readTile, world.Belts.Inserters[0].Source.Index);
    }

    /// The whole point of an inserter: ore off a belt and into a machine that
    /// then runs. End to end, with nothing hand-fed.
    [Fact]
    public void AnInserterFeedsAMachineFromABelt()
    {
        var db = new ItemDatabase();
        var world = new World(3, db);
        var ore = db.Register("ore");
        var ingot = db.Register("ingot");

        var recipe = new Recipe("smelt", 10,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(ingot, 1) });

        var furnace = world.TryPlaceMachine(recipe, new MachinePlacement(5, 0, 0, 0, 1))!;

        // The belt runs along y=2; the inserter stands at (5,1) facing north,
        // so it reaches back to the belt at (5,2) and forward into the furnace
        // at (5,0).
        for (var i = 0; i <= 5; i++) world.BeltMap.PlaceBelt(i, 2, Direction.East);
        world.BeltMap.PlaceInserter(5, 1, Direction.North);
        world.SyncBelts();

        Assert.Equal(EndpointKind.Machine, world.Belts.Inserters[0].Target.Kind);

        Feed(world, 0, 2, ore, 5);

        world.Tick(600);

        Assert.True(furnace.GetOutputCount(ingot) > 0,
                    "the furnace never ran, so nothing reached it from the belt");
    }

    /// Nothing may be created or destroyed by moving items around. The belt map
    /// recompiles constantly, and a rebuild that duplicated an item would look
    /// like a working factory until the numbers stopped adding up.
    [Fact]
    public void RebuildingConservesEveryItem()
    {
        var world = Lay(out var ore, length: 12);
        Feed(world, 0, 0, ore, 8);

        var before = world.Belts.ItemsInTransit();
        Assert.Equal(8, before);       // or this proves nothing about eight items

        // Ten rebuilds, each adding track somewhere that does not disturb the
        // existing run's tiles.
        for (var i = 0; i < 10; i++)
        {
            world.BeltMap.PlaceBelt(-1 - i, 0, Direction.East);
            world.SyncBelts();
        }

        Assert.Equal(before, world.Belts.ItemsInTransit());
        Assert.Equal(0, world.BeltMap.SpilledOnRemoval);
    }

    [Fact]
    public void TwoBeltsCannotShareATile()
    {
        var world = Bare();
        Assert.True(world.BeltMap.PlaceBelt(0, 0, Direction.East));
        Assert.False(world.BeltMap.PlaceBelt(0, 0, Direction.North));
        Assert.False(world.BeltMap.PlaceInserter(0, 0, Direction.North));
        Assert.Single(world.BeltMap.Belts);
    }

    /// A belt is built, not conjured: through the same path and the same costs
    /// as everything else, with the direction the player chose.
    [Fact]
    public void BeltsAndInsertersAreBuiltFromTheInventory()
    {
        var world = NewGame.Create(seed: 99, Data);
        var belt = Buildables.Find("stm_transport_belt")!;
        var inserter = Buildables.Find("stm_inserter")!;

        Assert.Equal(BuildKind.Belt, belt.Kind);
        Assert.Equal(BuildKind.Inserter, inserter.Kind);

        world.PlayerInventory.Add(belt.Item, 3);
        world.PlayerInventory.Add(inserter.Item, 1);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 0, 40, facing: Direction.South));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 0, 41, facing: Direction.South));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, inserter.Item, 1, 41, facing: Direction.East));

        Assert.Equal(1, world.PlayerInventory.Count(belt.Item));
        Assert.Equal(0, world.PlayerInventory.Count(inserter.Item));

        // Built facing the way it was asked for, not a default.
        Assert.Equal(Direction.South, world.BeltMap.Belts[0].Facing);
        Assert.Equal(Direction.East, world.BeltMap.Inserters[0].Facing);

        // And a second belt on the same tile is refused, without charging.
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, belt.Item, 0, 40, facing: Direction.South));
        Assert.Equal(1, world.PlayerInventory.Count(belt.Item));
    }

    /// A belt tile and a machine cannot occupy the same ground, in either order.
    [Fact]
    public void ABeltAndAMachineCannotShareATile()
    {
        var world = NewGame.Create(seed: 99, Data);
        var belt = Buildables.Find("stm_transport_belt")!;
        var furnace = Buildables.Find("stm_furnace")!;
        var recipe = Buildables.RecipesFor(furnace)[0];

        world.PlayerInventory.Add(belt.Item, 2);
        world.PlayerInventory.Add(furnace.Item, 2);

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, belt.Item, 0, 40));
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, furnace.Item, 0, 40, recipe));

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, furnace.Item, 0, 42, recipe));
        Assert.Equal(BuildResult.Blocked, world.TryBuild(Buildables, belt.Item, 0, 42));
    }

    /// Belt speed is a tier upgrade, and it has to reach the compiled segment
    /// rather than sitting in the item's description.
    [Fact]
    public void AFasterBeltActuallyMovesFaster()
    {
        var world = NewGame.Create(seed: 99, Data);
        var basic = Buildables.Find("stm_transport_belt")!;
        var better = Buildables.Find("arc_transport_belt")!;

        Assert.True(better.BeltSpeed > basic.BeltSpeed);

        world.PlayerInventory.Add(better.Item, 4);
        for (var i = 0; i < 4; i++)
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, better.Item, i, 40, facing: Direction.East));

        world.SyncBelts();
        Assert.Equal(better.BeltSpeed,
                     world.Belts.Segments[world.BeltMap.SegmentAt(0, 40)].Speed);
    }

    /// Belts of different speeds must not merge into one segment: a segment has
    /// a single speed, so the slow half would silently run fast.
    [Fact]
    public void BeltsOfDifferentSpeedsDoNotMerge()
    {
        var world = Bare();
        world.BeltMap.PlaceBelt(0, 0, Direction.East, BeltUnits.SpeedBasic);
        world.BeltMap.PlaceBelt(1, 0, Direction.East, BeltUnits.SpeedBasic);
        world.BeltMap.PlaceBelt(2, 0, Direction.East, BeltUnits.SpeedExpress);
        world.SyncBelts();

        Assert.Equal(2, world.Belts.Segments.Count);
        Assert.Equal(BeltUnits.SpeedBasic,
                     world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)].Speed);
        Assert.Equal(BeltUnits.SpeedExpress,
                     world.Belts.Segments[world.BeltMap.SegmentAt(2, 0)].Speed);
    }

    /// Two lines feeding one tile is a merge. Both must keep their own segment,
    /// or one would be swallowed into the other and stop backing up correctly.
    [Fact]
    public void TwoRunsMergingIntoOneTileEachKeepTheirSegment()
    {
        var world = Bare();
        world.BeltMap.PlaceBelt(0, 0, Direction.East);
        world.BeltMap.PlaceBelt(1, 0, Direction.East);      // from the west
        world.BeltMap.PlaceBelt(2, -1, Direction.South);    // from the north
        world.BeltMap.PlaceBelt(2, 0, Direction.East);      // the merge tile
        world.SyncBelts();

        var west = world.BeltMap.SegmentAt(0, 0);
        var north = world.BeltMap.SegmentAt(2, -1);
        var merge = world.BeltMap.SegmentAt(2, 0);

        Assert.NotEqual(west, merge);
        Assert.NotEqual(north, merge);
        Assert.Equal(merge, world.Belts.OutputOf(west, 0).Index);
        Assert.Equal(merge, world.Belts.OutputOf(north, 0).Index);
    }

    /// A tile-built belt has to survive a save. Segments are compiled from
    /// tiles, so a save that stored only the segments would reload a factory
    /// whose belts still moved items and could not be seen, extended or picked
    /// up -- the worst kind of working.
    [Fact]
    public void PlacedBeltsAndInsertersSurviveASave()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        // Deliberately not a single straight line at one speed: a save that
        // dropped facing or speed round-trips a uniform belt unnoticed.
        for (var i = 0; i < 6; i++) world.BeltMap.PlaceBelt(i, 0, Direction.East);
        for (var i = 0; i < 4; i++)
            world.BeltMap.PlaceBelt(6, i, Direction.South, BeltUnits.SpeedExpress);
        world.BeltMap.PlaceInserter(5, -1, Direction.North);
        world.SyncBelts();

        Feed(world, 0, 0, ore, 4);

        var carrying = world.Belts.ItemsInTransit();
        Assert.Equal(4, carrying);

        // The corner and the speed change both have to be in the saved world,
        // or this proves nothing about either.
        Assert.Contains(world.BeltMap.Belts, b => b.Facing == Direction.South);
        Assert.Contains(world.BeltMap.Belts, b => b.Speed == BeltUnits.SpeedExpress);

        var json = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world));
        var loaded = Sim.Save.SaveGame.Restore(
            Sim.Save.SaveGame.FromJson(json),
            new Dictionary<string, Recipe>(),
            world.Ground.Gen);

        Assert.Equal(world.BeltMap.Belts.Count, loaded.BeltMap.Belts.Count);
        Assert.Equal(world.BeltMap.Inserters.Count, loaded.BeltMap.Inserters.Count);
        Assert.Equal(Direction.North, loaded.BeltMap.Inserters[0].Facing);

        // Every tile comes back facing the way it was laid, at the speed it was
        // laid at.
        for (var i = 0; i < world.BeltMap.Belts.Count; i++)
        {
            Assert.Equal(world.BeltMap.Belts[i].Facing, loaded.BeltMap.Belts[i].Facing);
            Assert.Equal(world.BeltMap.Belts[i].Speed, loaded.BeltMap.Belts[i].Speed);
        }

        // The compiled topology has to come back the same, or an inserter is
        // reading a different belt than it was.
        Assert.Equal(world.Belts.Segments.Count, loaded.Belts.Segments.Count);
        Assert.Equal(world.BeltMap.SegmentAt(5, 0), loaded.BeltMap.SegmentAt(5, 0));

        // And what was riding on it is still riding on it, in the same places.
        Assert.Equal(carrying, loaded.Belts.ItemsInTransit());

        var before = world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)].LaneAt(0);
        var after = loaded.Belts.Segments[loaded.BeltMap.SegmentAt(0, 0)].LaneAt(0);
        Assert.True(before.Count > 0, "nothing on the belt, so its contents are not covered");
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Equal(before.PositionOf(i), after.PositionOf(i));
    }

    /// The two ways of making a segment cannot be mixed: compiling replaces the
    /// whole list, so a hand-built segment would vanish the moment a belt tile
    /// was placed. Loud rather than silent.
    [Fact]
    public void MixingHandBuiltSegmentsWithPlacedTilesIsRefused()
    {
        var world = Bare();
        world.Belts.AddSegment(tiles: 4);
        world.BeltMap.PlaceBelt(0, 20, Direction.East);

        Assert.Throws<InvalidOperationException>(() => world.SyncBelts());
    }

    // ---- underground belts and splitters (ADR 0018) -------------------------

    private const int Reach = 4;

    /// A tunnel: belts, an entrance, a span, an exit, more belts -- with a
    /// surface belt laid straight across the middle of the span, because the
    /// whole point of an underground belt is passing *under* something.
    private static World Tunnel(out ItemId ore)
    {
        var world = Bare();
        ore = world.Items.Register("ore");

        for (var x = 0; x < 3; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        Assert.True(world.BeltMap.PlaceUnderground(3, 0, Direction.East,
                                                   BeltUnits.SpeedBasic, Reach, out _));
        Assert.True(world.BeltMap.PlaceUnderground(3 + Reach, 0, Direction.East,
                                                   BeltUnits.SpeedBasic, Reach, out _));
        for (var x = 3 + Reach + 1; x < 11; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);

        // Crossing the span, going the other way.
        world.BeltMap.PlaceBelt(5, 0, Direction.North);

        world.SyncBelts();
        return world;
    }

    /// The property that matters: ore put on before the entrance comes out
    /// after the exit, and does not appear on the belt crossing over it.
    [Fact]
    public void OreTravelsThroughAnUndergroundPair()
    {
        var world = Tunnel(out var ore);

        Assert.Equal(1, world.BeltMap.PartnerOf(0));
        Assert.True(world.BeltMap.IsBuried(4, 0));
        Assert.True(world.BeltMap.IsBuried(6, 0));

        // Not buried: a belt is standing on it, and the belt is what a player
        // and an inserter see there.
        Assert.False(world.BeltMap.IsBuried(5, 0));

        Feed(world, 0, 0, ore, 4);
        var crossing = world.BeltMap.SegmentAt(5, 0);

        world.Tick(900);

        Assert.Equal(4, world.Belts.ItemsInTransit());
        Assert.Equal(0, world.Belts.Segments[crossing].ItemCount);
        Assert.Equal(4, world.Belts.Segments[world.BeltMap.SegmentAt(10, 0)].ItemCount);
    }

    /// A tunnel is a way past an obstacle, not a shortcut.
    ///
    /// The entrance's segment is as long as the span it covers, so the eleven
    /// tiles from 0,0 to 10,0 are eleven tiles of belt whether the middle of
    /// them is buried or not. The tunnelled line does arrive earlier, by
    /// exactly what its three extra segment breaks are worth: a hand-off puts
    /// an item one spacing inside the next segment, which is the same small
    /// gain every corner in the game has always given. Asserting the size of
    /// that gain rather than ignoring it is what makes this a test of the span
    /// and not of the hand-off.
    [Fact]
    public void ATunnelIsAsLongAsTheSurfaceItReplaces()
    {
        var tunnelled = Tunnel(out var ore);

        var surface = Bare();
        var plainOre = surface.Items.Register("ore");
        for (var x = 0; x < 11; x++) surface.BeltMap.PlaceBelt(x, 0, Direction.East);
        surface.SyncBelts();

        // The line 0,0 -> 10,0 is eleven tiles of belt either way. The crossing
        // belt at 5,0 is on the same map but not on this line.
        var tiles = 0;
        for (var segment = 0; segment < tunnelled.Belts.Segments.Count; segment++)
            if (tunnelled.BeltMap.TilesOfSegment(segment)[0].Facing == Direction.East)
                tiles += tunnelled.Belts.Segments[segment].Tiles;

        Assert.Equal(11, tiles);
        Assert.Equal(11, surface.Belts.Segments[0].Tiles);

        var buried = TicksToCross(tunnelled, ore);
        var plain = TicksToCross(surface, plainOre);

        const int breaks = 3;
        Assert.Equal(plain - breaks * BeltUnits.ItemSpacing / BeltUnits.SpeedBasic, buried);
    }

    /// Ticks for one item put on at 0,0 to reach the exit of the tile at 10,0.
    private static int TicksToCross(World world, ItemId ore)
    {
        Assert.True(world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)]
                         .LaneAt(0).TryInsertBack(ore));

        var last = world.BeltMap.SegmentAt(10, 0);
        for (var tick = 1; tick <= 2000; tick++)
        {
            world.Tick(1);
            if (world.Belts.Segments[last].LaneAt(0).FrontReady) return tick;
        }

        Assert.Fail("the ore never reached the end of the line");
        return 0;
    }

    /// Runs until the tunnel is actually carrying something, and says how
    /// much. A test that ticks a fixed number and hopes is a test that passes
    /// for the wrong reason the day belt speed changes.
    private static int TickUntilSomethingIsUnderground(World world)
    {
        for (var tick = 0; tick < 2000; tick++)
        {
            var count = world.Belts.Segments[world.BeltMap.SegmentAt(4, 0)].ItemCount;
            if (count > 0) return count;
            world.Tick(1);
        }

        Assert.Fail("nothing ever went underground, so this proves nothing");
        return 0;
    }

    /// An entrance with no exit is not a hole that eats ore. It is a one-tile
    /// belt, which is what the player can see it doing.
    [Fact]
    public void AnUnpairedEntranceIsAPlainBeltTile()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        for (var x = 0; x < 3; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        world.BeltMap.PlaceUnderground(3, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        for (var x = 4; x < 7; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        world.SyncBelts();

        Assert.Equal(-1, world.BeltMap.PartnerOf(0));
        Assert.False(world.BeltMap.IsBuried(4, 0));

        Feed(world, 0, 0, ore, 3);
        world.Tick(900);

        Assert.Equal(3, world.Belts.ItemsInTransit());
        Assert.Equal(3, world.Belts.Segments[world.BeltMap.SegmentAt(6, 0)].ItemCount);
    }

    /// Two tunnels sharing a line. The inner pair claims its own exit first, so
    /// an outer entrance cannot reach past it and swallow the wrong hole.
    [Fact]
    public void TheNearerExitWinsWhenTwoPairsOverlap()
    {
        var world = Bare();

        // Placed: entrance at 0, then an end at 2 (becomes its exit), then an
        // end at 3 (a fresh entrance -- 2 is already paired), then one at 5.
        world.BeltMap.PlaceUnderground(0, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceUnderground(2, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceUnderground(3, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceUnderground(5, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.SyncBelts();

        Assert.Equal(1, world.BeltMap.PartnerOf(0));
        Assert.Equal(0, world.BeltMap.PartnerOf(1));
        Assert.Equal(3, world.BeltMap.PartnerOf(2));
        Assert.Equal(2, world.BeltMap.PartnerOf(3));
    }

    /// The same question asked of a layout placement order cannot produce: one
    /// entrance with TWO exits in front of it, both in reach. A loaded save is
    /// exactly this -- roles come back as stored, in list order -- so the
    /// compiler has to answer it on its own, and the answer is the nearer one.
    [Fact]
    public void AnEntranceWithTwoCandidateExitsTakesTheNearer()
    {
        var world = Bare();
        world.BeltMap.RestoreUnderground(0, 0, Direction.East, BeltUnits.SpeedBasic,
                                         Reach, isEntrance: true);
        world.BeltMap.RestoreUnderground(2, 0, Direction.East, BeltUnits.SpeedBasic,
                                         Reach, isEntrance: false);
        world.BeltMap.RestoreUnderground(4, 0, Direction.East, BeltUnits.SpeedBasic,
                                         Reach, isEntrance: false);
        world.SyncBelts();

        Assert.Equal(1, world.BeltMap.PartnerOf(0));
        Assert.Equal(0, world.BeltMap.PartnerOf(1));
        Assert.Equal(-1, world.BeltMap.PartnerOf(2));

        // And the span is the near one's, so the tunnel is two tiles of belt
        // rather than four.
        Assert.Equal(2, world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)].Tiles);

        // This layout is also the one that proves roles are stored rather than
        // re-derived on load: placing these three in order would make the last
        // one an entrance, because by then the first pair is already complete.
        var loaded = Sim.Save.SaveGame.Restore(
            Sim.Save.SaveGame.FromJson(Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world))),
            new Dictionary<string, Recipe>(),
            world.Ground.Gen);

        Assert.False(loaded.BeltMap.Undergrounds[2].IsEntrance);
        Assert.Equal(1, loaded.BeltMap.PartnerOf(0));
        Assert.Equal(-1, loaded.BeltMap.PartnerOf(2));
    }

    /// Facing is part of pairing: two ends on the same line pointing different
    /// ways are two separate holes, not a tunnel.
    [Fact]
    public void EndsFacingDifferentWaysDoNotPair()
    {
        var world = Bare();

        // An exit facing across the line the entrance runs along. Placement
        // could not make this -- an end that does not line up with an entrance
        // becomes an entrance itself -- but a save restores roles as stored,
        // and the compiler must not join these two.
        world.BeltMap.PlaceUnderground(0, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.RestoreUnderground(2, 0, Direction.South, BeltUnits.SpeedBasic,
                                         Reach, isEntrance: false);
        world.SyncBelts();

        Assert.Equal(-1, world.BeltMap.PartnerOf(0));
        Assert.Equal(-1, world.BeltMap.PartnerOf(1));

        // Two separate one-tile holes, not a two-tile tunnel.
        Assert.Equal(1, world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)].Tiles);
    }

    /// Extending a line beside a tunnel recompiles everything, and what is
    /// underground at that moment must come back underground rather than being
    /// counted as spilled.
    [Fact]
    public void ItemsInTransitUndergroundSurviveARebuild()
    {
        var world = Tunnel(out var ore);
        Feed(world, 0, 0, ore, 4);

        var underground = TickUntilSomethingIsUnderground(world);

        world.BeltMap.PlaceBelt(11, 0, Direction.East);
        world.SyncBelts();

        Assert.Equal(4, world.Belts.ItemsInTransit());
        Assert.Equal(0, world.BeltMap.SpilledOnRemoval);
        Assert.Equal(underground,
                     world.Belts.Segments[world.BeltMap.SegmentAt(4, 0)].ItemCount);
    }

    /// A tunnel exit and a splitter feed a tile exactly as a belt does, so a
    /// tile fed by one of them AND by a belt is a merge and has to start its
    /// own segment.
    ///
    /// Counting only belts as feeders let that tile carry on the belt's run,
    /// which put everything arriving from the tunnel or the splitter in at the
    /// far back of that run -- items appearing several tiles upstream of the
    /// hole they came out of.
    [Fact]
    public void ATunnelExitMergingIntoABeltStartsItsOwnSegment()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        for (var x = 0; x < 4; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        world.BeltMap.PlaceUnderground(3, -3, Direction.South, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceUnderground(3, -1, Direction.South, BeltUnits.SpeedBasic, Reach, out _);
        world.SyncBelts();

        Assert.Equal(1, world.BeltMap.PartnerOf(0));
        Assert.NotEqual(world.BeltMap.SegmentAt(2, 0), world.BeltMap.SegmentAt(3, 0));

        var head = world.BeltMap.SegmentAt(0, 0);
        var join = world.BeltMap.SegmentAt(3, 0);

        Assert.True(world.Belts.Segments[world.BeltMap.SegmentAt(3, -3)]
                         .LaneAt(0).TryInsertBack(ore));
        world.Tick(900);

        // It came up at 3,0 and stayed there. On the merged-run reading it
        // would have been handed in at 0,0 instead.
        Assert.Equal(0, world.Belts.Segments[head].ItemCount);
        Assert.Equal(1, world.Belts.Segments[join].ItemCount);
    }

    [Fact]
    public void ASplitterMergingIntoABeltStartsItsOwnSegment()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        for (var x = 0; x < 4; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);

        // Facing south, so its straight output is the belt tile at 3,0.
        world.BeltMap.PlaceSplitter(3, -1, Direction.South);
        for (var y = -4; y < -1; y++) world.BeltMap.PlaceBelt(3, y, Direction.South);
        world.SyncBelts();

        Assert.NotEqual(world.BeltMap.SegmentAt(2, 0), world.BeltMap.SegmentAt(3, 0));

        var head = world.BeltMap.SegmentAt(0, 0);
        var join = world.BeltMap.SegmentAt(3, 0);

        Assert.True(world.Belts.Segments[world.BeltMap.SegmentAt(3, -4)]
                         .LaneAt(0).TryInsertBack(ore));
        world.Tick(900);

        Assert.Equal(0, world.Belts.Segments[head].ItemCount);
        Assert.Equal(1, world.Belts.Segments[join].ItemCount);
    }

    /// A splitter with a line in and two lines out.
    private static World Split(out ItemId ore)
    {
        var world = Bare();
        ore = world.Items.Register("ore");

        for (var x = 0; x < 3; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        Assert.True(world.BeltMap.PlaceSplitter(3, 0, Direction.East));

        // Straight on, and the branch to its right.
        for (var x = 4; x < 8; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        for (var y = 1; y < 5; y++) world.BeltMap.PlaceBelt(3, y, Direction.South);

        world.SyncBelts();
        return world;
    }

    /// The property: ore put on one line comes off two, evenly.
    [Fact]
    public void OreSplitsEvenlyAtASplitter()
    {
        var world = Split(out var ore);

        Feed(world, 0, 0, ore, 6);
        world.Tick(900);

        var straight = world.Belts.Segments[world.BeltMap.SegmentAt(7, 0)].ItemCount;
        var branch = world.Belts.Segments[world.BeltMap.SegmentAt(3, 4)].ItemCount;

        Assert.Equal(6, world.Belts.ItemsInTransit());
        Assert.Equal(3, straight);
        Assert.Equal(3, branch);
    }

    /// Each lane is split on its own, so a splitter does not halve a line's
    /// throughput by funnelling both lanes through one buffer. The cost is that
    /// it does not lane-balance: an item that went in on lane 1 comes out on
    /// lane 1. ADR 0018.
    [Fact]
    public void ASplitterSplitsEachLaneOnItsOwn()
    {
        var world = Split(out var ore);
        var head = world.BeltMap.SegmentAt(0, 0);

        // Both lanes loaded, because "each lane on its own" is not testable
        // with one lane in use: everything comes out somewhere either way.
        for (var i = 0; i < 4; i++)
        {
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                Assert.True(world.Belts.Segments[head].LaneAt(lane).TryInsertBack(ore),
                            $"item {i} would not fit on lane {lane}");
            world.Tick(10);
        }

        world.Tick(1500);

        var straight = world.Belts.Segments[world.BeltMap.SegmentAt(7, 0)];
        var branch = world.Belts.Segments[world.BeltMap.SegmentAt(3, 4)];

        Assert.Equal(8, world.Belts.ItemsInTransit());

        // Two of each lane down each side. A splitter that merged the lanes
        // would land all eight on one side's lanes in some other proportion.
        for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
        {
            Assert.Equal(2, straight.LaneAt(lane).Count);
            Assert.Equal(2, branch.LaneAt(lane).Count);
        }
    }

    /// A blocked side must not starve the other: the splitter tries both.
    [Fact]
    public void ASplitterWithOnlyOneOutputSendsEverythingThatWay()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        for (var x = 0; x < 3; x++) world.BeltMap.PlaceBelt(x, 0, Direction.East);
        world.BeltMap.PlaceSplitter(3, 0, Direction.East);
        for (var y = 1; y < 5; y++) world.BeltMap.PlaceBelt(3, y, Direction.South);
        world.SyncBelts();

        Feed(world, 0, 0, ore, 4);
        world.Tick(900);

        Assert.Equal(4, world.Belts.ItemsInTransit());
        Assert.Equal(4, world.Belts.Segments[world.BeltMap.SegmentAt(3, 4)].ItemCount);
    }

    /// A world with tunnel ends and a splitter but no belt tiles at all is
    /// still a compiled world.
    ///
    /// Reading "compiled" off the belt tile list alone made this one load as
    /// hand-built: the segments were added by hand on top of the ones the
    /// compile would make, the splitters were added a second time, and the
    /// first tick threw rather than running.
    [Fact]
    public void AWorldOfTunnelsAndSplittersWithNoBeltTilesStillLoads()
    {
        var world = Bare();
        var ore = world.Items.Register("ore");

        world.BeltMap.PlaceUnderground(0, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceUnderground(Reach, 0, Direction.East, BeltUnits.SpeedBasic, Reach, out _);
        world.BeltMap.PlaceSplitter(Reach + 1, 0, Direction.East);
        world.SyncBelts();

        Assert.Empty(world.BeltMap.Belts);
        Assert.True(world.Belts.Segments[world.BeltMap.SegmentAt(0, 0)]
                         .LaneAt(0).TryInsertBack(ore));

        var loaded = Sim.Save.SaveGame.Restore(
            Sim.Save.SaveGame.FromJson(Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world))),
            new Dictionary<string, Recipe>(),
            world.Ground.Gen);

        Assert.Equal(world.Belts.Segments.Count, loaded.Belts.Segments.Count);
        Assert.Equal(world.Belts.Splitters.Count, loaded.Belts.Splitters.Count);
        Assert.Equal(1, loaded.Belts.ItemsInTransit());

        loaded.Tick(100);
        Assert.Equal(1, loaded.Belts.ItemsInTransit());
    }

    /// Tunnels and splitters are placed things, so they are ground truth in the
    /// save exactly as belt tiles are -- and the segments compiled from them
    /// have to come back carrying what they were carrying.
    [Fact]
    public void UndergroundBeltsAndSplittersSurviveASave()
    {
        var world = Tunnel(out var ore);

        // A splitter as well, hanging off the end of the tunnelled line, so one
        // save covers both and neither can pass by being absent.
        world.BeltMap.PlaceSplitter(11, 0, Direction.East);
        for (var y = 1; y < 4; y++) world.BeltMap.PlaceBelt(11, y, Direction.South);
        world.SyncBelts();

        Feed(world, 0, 0, ore, 5);
        TickUntilSomethingIsUnderground(world);

        var carrying = world.Belts.ItemsInTransit();
        Assert.Equal(5, carrying);

        var json = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world));
        Assert.Contains("\"Version\": 10", json);

        var loaded = Sim.Save.SaveGame.Restore(
            Sim.Save.SaveGame.FromJson(json),
            new Dictionary<string, Recipe>(),
            world.Ground.Gen);

        Assert.Equal(world.BeltMap.Undergrounds.Count, loaded.BeltMap.Undergrounds.Count);
        Assert.Equal(world.BeltMap.Splitters.Count, loaded.BeltMap.Splitters.Count);
        Assert.Equal(world.Belts.Splitters.Count, loaded.Belts.Splitters.Count);

        // The roles and the pairing, not merely the count: two ends that both
        // came back as entrances would still be two ends.
        for (var i = 0; i < world.BeltMap.Undergrounds.Count; i++)
        {
            Assert.Equal(world.BeltMap.Undergrounds[i].IsEntrance,
                         loaded.BeltMap.Undergrounds[i].IsEntrance);
            Assert.Equal(world.BeltMap.Undergrounds[i].Reach,
                         loaded.BeltMap.Undergrounds[i].Reach);
            Assert.Equal(world.BeltMap.PartnerOf(i), loaded.BeltMap.PartnerOf(i));
        }

        Assert.Equal(world.BeltMap.Splitters[0].Facing, loaded.BeltMap.Splitters[0].Facing);
        Assert.Equal(carrying, loaded.Belts.ItemsInTransit());

        // And the reloaded factory keeps running the same way.
        world.Tick(600);
        loaded.Tick(600);
        Assert.Equal(world.Belts.Segments[world.BeltMap.SegmentAt(11, 3)].ItemCount,
                     loaded.Belts.Segments[loaded.BeltMap.SegmentAt(11, 3)].ItemCount);
        Assert.Equal(world.Belts.Segments[world.BeltMap.SegmentAt(4, 0)].ItemCount,
                     loaded.Belts.Segments[loaded.BeltMap.SegmentAt(4, 0)].ItemCount);
    }
}
