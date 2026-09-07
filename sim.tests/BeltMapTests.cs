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
}
