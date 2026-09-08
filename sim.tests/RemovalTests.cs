using Sim;
using Sim.Data;

namespace Sim.Tests;

/// Taking back what you built (ADR 0028).
///
/// The properties worth holding, and the ones every test below is a form of:
///
/// - **Conservation.** What you spent comes back, and so does everything that
///   was inside the thing when you pulled it up. Nothing is created either:
///   removing a machine mid-cycle refunds the batch's *inputs*, never its
///   unfinished outputs.
/// - **A refusal costs nothing**, the mirror of `ARefusedBuild_CostsNothing`.
/// - **The map is left in the state never having built it would have left.**
///   A pole's network splits, a run of belt splits, and an unpaired tunnel
///   entrance stops refusing the band ahead of it.
public class RemovalTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World NewWorld()
    {
        var world = NewGame.Create(seed: 4242, Data);
        world.Research!.UnlockAll();
        return world;
    }

    private static Buildable Get(string itemId)
    {
        var buildable = Buildables.Find(itemId);
        Assert.NotNull(buildable);
        return buildable!;
    }

    private static void Give(World world, string itemId, int count = 1)
        => world.PlayerInventory.Add(Data.Item(itemId), count);

    /// A tile with nothing on it and nothing under it, well clear of the
    /// starting area's ore and water.
    private static (int X, int Y) BareTile(World world, int size = 3)
    {
        for (var r = 0; r < 400; r++)
            for (var d = -r; d <= r; d++)
            {
                var (x, y) = (d, r);
                if (!world.Ground.TryResourceAt(x, y, out _, out _) &&
                    !world.Ground.Gen.IsWater(x, y) &&
                    world.CanPlace(new MachinePlacement(x, y, 0, 0, (byte)size)))
                    return (x, y);
            }

        throw new InvalidOperationException("no bare tile found");
    }

    private static (int X, int Y) OreTile(World world, out ItemId ore)
    {
        for (var r = 0; r < 400; r++)
            for (var d = -r; d <= r; d++)
                foreach (var (x, y) in new[] { (d, r), (r, d), (d, -r), (-r, d) })
                    if (world.Ground.TryResourceAt(x, y, out var item, out var amount)
                        && amount > 0 && world.CanPlace(new MachinePlacement(x, y, 0, 0)))
                    {
                        ore = item;
                        return (x, y);
                    }

        throw new InvalidOperationException("no ore tile found");
    }

    // ---- the machine, and everything inside it -----------------------------

    [Fact]
    public void RemovingAMachine_HandsBackTheMachineAndEverythingInsideIt()
    {
        var world = NewWorld();
        var furnace = Get("man_furnace");
        var recipe = Data.Recipe("smelt_chalcopyrite");
        Give(world, "man_furnace");
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, 0, 0, recipe));

        var machine = world.Machines[0];
        var ore = Data.Item("chalcopyrite");
        var ingot = Data.Item("copper_ingot");

        // Deliberately messy, as in `RecipeChangeTests`: a part-full input
        // buffer, a finished ingot waiting, and a cycle in flight whose input
        // has already left the buffer. A tidy machine would let a removal that
        // dropped two of the three through unnoticed.
        machine.PushInput(ore, 3);
        world.Tick(1);
        world.Tick(recipe.DurationTicks);
        Assert.Equal(1, machine.GetOutputCount(ingot));
        world.Tick(4);
        Assert.Equal(MachineState.Working, machine.State);

        var carriedOre = world.PlayerInventory.Count(ore);
        var carriedIngot = world.PlayerInventory.Count(ingot);

        var report = world.TryRemove(0, 0);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(furnace.Item, report.Item);
        Assert.Equal(0, report.X);
        Assert.Equal(0, report.Y);
        Assert.Equal(0, world.MachineCount);
        Assert.False(world.TryMachineAt(0, 0, out _, out _));
        Assert.True(world.CanPlace(furnace.PlacementAt(0, 0)));

        // The building itself, plus 1 left in the buffer + 1 in the cycle in
        // flight + 1 finished ingot. Exact, not "more than before": the
        // in-flight batch is the part a player is least able to notice going.
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
        Assert.Equal(3, report.Returned);
        Assert.Equal(carriedOre + 2, world.PlayerInventory.Count(ore));
        Assert.Equal(carriedIngot + 1, world.PlayerInventory.Count(ingot));
    }

    /// Conservation stated as itself, the way the retask tests state it: across
    /// a removal nothing is created and nothing is destroyed.
    [Fact]
    public void RemovingAMachine_CreatesAndDestroysNothing()
    {
        var world = NewWorld();
        var furnace = Get("man_furnace");
        var recipe = Data.Recipe("smelt_chalcopyrite");
        Give(world, "man_furnace");
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, 0, 0, recipe));

        var machine = world.Machines[0];
        machine.PushInput(Data.Item("chalcopyrite"), 5);
        world.Tick(recipe.DurationTicks + 3);

        int Total() =>
            world.PlayerInventory.Contents.Values.Sum() +
            (world.MachineCount > 0
                ? machine.InputContents.Values.Sum() + machine.OutputContents.Values.Sum() +
                  (machine.RawTicksRemaining > 0
                      ? machine.Recipe.Inputs.Sum(i => i.Count * machine.Parallelism)
                      : 0)
                  // The furnace itself is standing on the map rather than in
                  // the player's hands, and has to be counted somewhere.
                  + 1
                : 0);

        var before = Total();
        Assert.True(world.TryRemove(0, 0).Ok);
        Assert.Equal(before, Total());
    }

    [Fact]
    public void RemovingAMachine_MovesTheLastOneIntoItsSlotAndTheGridFollows()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];

        var (x, y) = BareTile(world);
        var first = (x, y);
        var second = (x + 8, y);
        var third = (x + 16, y);

        Give(world, "stm_furnace", 3);
        foreach (var (tx, ty) in new[] { first, second, third })
            Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, tx, ty, recipe));

        var survivor = world.Machines[2];

        Assert.True(world.TryRemove(0 + first.Item1, first.Item2).Ok);

        Assert.Equal(2, world.MachineCount);

        // The machine that was last is now in slot 0, and the occupancy grid
        // has to agree -- a stale index here hands a belt's items to the wrong
        // machine and is invisible until it does.
        Assert.True(world.TryMachineAt(third.Item1, third.Item2, out var found, out var index));
        Assert.Same(survivor, found);
        Assert.Equal(0, index);
        Assert.Equal(third.Item1, world.PlacementOf(index).X);
        Assert.Same(survivor, world.Machines[index]);

        // And the one that did not move is still where it was.
        Assert.True(world.TryMachineAt(second.Item1, second.Item2, out _, out var stayed));
        Assert.Equal(1, stayed);
        Assert.Equal(second.Item1, world.PlacementOf(stayed).X);
    }

    [Fact]
    public void RemovingAMachine_TakesItsUplinkRegistrationWithIt()
    {
        var world = NewWorld();
        var uplink = Get("man_uplink");
        var recipe = Buildables.RecipesFor(uplink)[0];
        var furnace = Get("stm_furnace");
        var furnaceRecipe = Buildables.RecipesFor(furnace)[0];

        var (x, y) = BareTile(world);
        Give(world, "man_uplink");
        Give(world, "stm_furnace");

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x, y, furnaceRecipe));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, uplink.Item, x + 8, y, recipe));
        Assert.Equal(new[] { 1 }, world.Uplinks.OrderBy(i => i).ToArray());

        // Removing the furnace moves the Uplink from slot 1 to slot 0. If the
        // registration did not move with it, an ordinary furnace built later
        // would be drained into research every tick.
        Assert.True(world.TryRemove(x, y).Ok);
        Assert.Equal(new[] { 0 }, world.Uplinks.OrderBy(i => i).ToArray());

        Assert.True(world.TryRemove(x + 8, y).Ok);
        Assert.Empty(world.Uplinks);
    }

    // ---- refusals ----------------------------------------------------------

    [Fact]
    public void RemovingBareGround_SaysSoAndCostsNothing()
    {
        var world = NewWorld();
        var (x, y) = BareTile(world);
        var carried = world.PlayerInventory.Contents.Count;

        var report = world.TryRemove(x, y);

        Assert.Equal(RemoveResult.NothingThere, report.Result);
        Assert.Equal(0, report.Returned);
        Assert.Equal(carried, world.PlayerInventory.Contents.Count);
    }

    [Fact]
    public void AMachineNothingPaidFor_IsRefusedRatherThanDestroyed()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        // Placed directly, the way a scenario or a headless analysis world
        // does: no item was ever spent, so there is nothing to hand back.
        world.TryPlaceMachine(recipe, furnace.PlacementAt(x, y));

        var report = world.TryRemove(x, y);

        Assert.Equal(RemoveResult.UnknownBuilding, report.Result);
        Assert.Equal(1, world.MachineCount);
        Assert.True(world.TryMachineAt(x, y, out _, out _));
        Assert.Equal(0, world.PlayerInventory.Count(furnace.Item));
    }

    // ---- the tunnel band, which is why this exists -------------------------

    [Fact]
    public void RemovingAnUnpairedEntrance_FreesTheBandItWasRefusing()
    {
        const int reach = 4;

        // The band `AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach` pins:
        // reach+1 .. reach*2 ahead is refused while the entrance stands.
        for (var ahead = reach + 1; ahead <= reach * 2; ahead++)
        {
            var world = NewWorld();
            var tunnel = Get("vlt_underground_belt");
            Assert.Equal(reach, tunnel.UndergroundReach);

            Give(world, "vlt_underground_belt", 2);
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, tunnel.Item, 100, 0, null, Direction.East));

            Assert.Equal(BuildResult.TooFarToTunnel,
                         world.TryBuild(Buildables, tunnel.Item, 100 + ahead, 0, null,
                                        Direction.East));

            var report = world.TryRemove(100, 0);
            Assert.Equal(RemoveResult.Ok, report.Result);
            Assert.Equal(2, world.PlayerInventory.Count(tunnel.Item));
            Assert.False(world.BeltMap.HasAnythingAt(100, 0));

            // The whole point: the tile that was permanently unbuildable now
            // takes an end, as a fresh entrance.
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, tunnel.Item, 100 + ahead, 0, null,
                                        Direction.East));
            Assert.True(world.BeltMap.HasUndergroundAt(100 + ahead, 0));
            Assert.Equal(1, world.PlayerInventory.Count(tunnel.Item));
        }
    }

    [Fact]
    public void RemovingOneEndOfAPair_LeavesTheOtherFreeToBePairedAgain()
    {
        var world = NewWorld();
        var tunnel = Get("vlt_underground_belt");
        Give(world, "vlt_underground_belt", 3);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, tunnel.Item, 10, 10, null, Direction.East));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, tunnel.Item, 13, 10, null, Direction.East));
        world.SyncBelts();
        Assert.True(world.BeltMap.PartnerOf(0) >= 0);

        // Pull up the exit. The entrance must go back to being unpaired, which
        // is what lets a new exit complete it.
        Assert.True(world.TryRemove(13, 10).Ok);
        world.SyncBelts();
        Assert.Equal(-1, world.BeltMap.PartnerOf(0));

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, tunnel.Item, 14, 10, null, Direction.East));
        world.SyncBelts();
        Assert.Equal(1, world.BeltMap.PartnerOf(0));
    }

    // ---- belts -------------------------------------------------------------

    [Fact]
    public void RemovingABeltTile_HandsBackWhatWasOnItAndLeavesTheRestStanding()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        Give(world, "stm_transport_belt", 6);

        for (var i = 0; i < 6; i++)
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, belt.Item, 20 + i, 5, null, Direction.East));

        world.SyncBelts();
        Assert.Single(world.Belts.Segments);

        var ore = Data.Item("magnetite");
        var coal = Data.Item("coal_deposit");

        // Mixed on purpose: a lane of one item would let a removal that took
        // the wrong tile's items pass. Packed rather than inserted at the back
        // -- `TryInsertBack` fills the back of the lane and every later call
        // fails silently, which is how an earlier conservation test in this
        // repository ended up counting one item while reading as eight.
        var lane = world.Belts.Segment(0).LaneAt(0);
        for (var i = 0; i < 6; i++) Assert.True(lane.TryPack(i == 4 ? coal : ore));
        Assert.Equal(6, lane.Count);

        // Packed from the exit, items sit a quarter tile apart: the first four
        // are on the exit tile 25,5 and the last two -- one of them the coal --
        // are on 24,5.
        var spilledBefore = world.BeltMap.SpilledOnRemoval;
        var report = world.TryRemove(24, 5);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(belt.Item, report.Item);
        Assert.Equal(1, world.PlayerInventory.Count(belt.Item));

        // Two items were standing on the removed tile, and both came back.
        Assert.Equal(2, report.Returned);
        Assert.Equal(1, world.PlayerInventory.Count(coal));
        Assert.Equal(1, world.PlayerInventory.Count(ore));

        // Nothing was destroyed on the way: what was not on that tile is still
        // riding somewhere.
        Assert.Equal(spilledBefore, world.BeltMap.SpilledOnRemoval);
        Assert.Equal(0, report.Spilled);

        var stillRiding = 0;
        for (var s = 0; s < world.Belts.Segments.Count; s++)
            stillRiding += world.Belts.Segments[s].ItemCount;
        Assert.Equal(4, stillRiding);

        // And the run is cut in two where the tile was taken out.
        Assert.Equal(2, world.Belts.Segments.Count);
    }

    [Fact]
    public void RemovingATunnelEnd_HandsBackWhatWasStillUnderground()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        var tunnel = Get("vlt_underground_belt");
        Give(world, "stm_transport_belt", 2);
        Give(world, "vlt_underground_belt", 2);

        // belt, entrance, three buried tiles, exit, belt.
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 40, 9, null, Direction.East));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, tunnel.Item, 41, 9, null, Direction.East));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, tunnel.Item, 45, 9, null, Direction.East));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 46, 9, null, Direction.East));
        world.SyncBelts();

        var ore = Data.Item("magnetite");
        var lane = world.Belts.Segment(world.BeltMap.SegmentAt(41, 9)).LaneAt(1);

        // Packed solid, so there are items on the entrance tile and on all
        // three that are underground: an entrance compiles to a segment as long
        // as the span it covers.
        var loaded = 0;
        while (lane.TryPack(ore)) loaded++;
        Assert.Equal(16, loaded);

        // And a neighbour that must not be touched: the surface belt past the
        // exit, carrying something else so a mix-up is visible.
        var coal = Data.Item("coal_deposit");
        var past = world.Belts.Segment(world.BeltMap.SegmentAt(46, 9)).LaneAt(1);
        var untouched = 0;
        while (past.TryPack(coal)) untouched++;
        Assert.Equal(4, untouched);

        var report = world.TryRemove(41, 9);

        Assert.Equal(RemoveResult.Ok, report.Result);

        // The entrance tile and the three buried tiles behind it: four tiles at
        // four items each. Items in a tunnel stand on tiles the player cannot
        // see, which is exactly why they are easy to destroy silently.
        Assert.Equal(16, report.Returned);
        Assert.Equal(16, world.PlayerInventory.Count(ore));
        Assert.Equal(0, report.Spilled);

        // And nothing evaporated: the belt past the exit still has its four,
        // and none of the ore that came back is still on the map.
        var stillRiding = 0;
        for (var s = 0; s < world.Belts.Segments.Count; s++)
            stillRiding += world.Belts.Segments[s].ItemCount;
        Assert.Equal(untouched, stillRiding);
        Assert.Equal(0, world.PlayerInventory.Count(coal));
    }

    [Fact]
    public void RemovingAnInserter_GivesTheRunItSplitBackAsOneSegment()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        var inserter = Get("stm_inserter");
        Give(world, "stm_transport_belt", 4);
        Give(world, "stm_inserter");

        for (var i = 0; i < 4; i++)
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, belt.Item, 60 + i, 3, null, Direction.East));

        // Facing north out of the second tile: the inserter reads it, which
        // forces a segment boundary there.
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, inserter.Item, 61, 2, null, Direction.North));
        world.SyncBelts();
        Assert.Equal(2, world.Belts.Segments.Count);

        Assert.True(world.TryRemove(61, 2).Ok);
        world.SyncBelts();

        // A long belt is one segment. Freeing the tiles the inserter read is
        // what puts it back together.
        Assert.Single(world.Belts.Segments);
        Assert.Equal(4, world.Belts.Segment(0).Tiles);
        Assert.Equal(1, world.PlayerInventory.Count(inserter.Item));
    }

    // ---- power -------------------------------------------------------------

    [Fact]
    public void RemovingAPole_SplitsTheNetworkTheWayNotBuildingItWould()
    {
        var world = NewWorld();
        var pole = Get("stm_pole");
        Give(world, "stm_pole", 3);

        // Three in a line, each within wire reach of the next but not of the
        // far one, so the middle pole is the only thing joining the two ends.
        var span = pole.PoleWireRadius;
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pole.Item, 0, 40));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pole.Item, span, 40));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pole.Item, span * 2, 40));
        Assert.Equal(1, world.Power.NetworkCount);

        var report = world.TryRemove(span, 40);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(2, world.Power.Poles.Count);
        Assert.Equal(2, world.Power.NetworkCount);
        Assert.Equal(1, world.PlayerInventory.Count(pole.Item));

        // The tile is free, and the pole that moved into the removed one's slot
        // still answers from its own tile.
        Assert.True(world.CanPlace(pole.PlacementAt(span, 40)));
        Assert.True(world.TryPoleAt(span * 2, 40, out var moved, out _));
        Assert.Equal(span * 2, moved.X);
        Assert.False(world.TryPoleAt(span, 40, out _, out _));
    }

    [Fact]
    public void RemovingAGenerator_HandsBackWholeFuelAndNotTheUnitBurning()
    {
        var world = NewWorld();
        var generator = Get("stm_generator");
        var pole = Get("stm_pole");
        Give(world, "stm_generator");
        Give(world, "stm_pole");

        var (x, y) = BareTile(world);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, generator.Item, x, y));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pole.Item, x + 1, y));

        var coal = Data.Item("coal_deposit");
        world.Power.Generators[0].AddFuel(5);

        // One tick puts a unit into the fire: five in the hopper become four,
        // and the fifth has already been paid out as power.
        world.Tick();
        Assert.True(world.Power.Generators[0].IsBurning);

        var report = world.TryRemove(x, y);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(4, report.Returned);
        Assert.Equal(4, world.PlayerInventory.Count(coal));
        Assert.Empty(world.Power.Generators);
        Assert.Equal(1, world.PlayerInventory.Count(generator.Item));
    }

    // ---- fluids ------------------------------------------------------------

    [Fact]
    public void RemovingAPipe_DrainsItsShareAndSaysHowMuchWentWithIt()
    {
        var world = NewWorld();
        var pipe = Get("stm_pipe");
        Give(world, "stm_pipe", 4);

        for (var i = 0; i < 4; i++)
            Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, 70 + i, 30));

        var water = Data.Item("water");
        var network = world.Fluids.Network(world.Fluids.NetworkAt(70, 30));
        network.BeginTick();
        Assert.Equal(400, network.Capacity);
        Assert.Equal(200, network.TryInsert(water, 200));

        var report = world.TryRemove(71, 30);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(1, world.PlayerInventory.Count(pipe.Item));

        // A quarter of the run went, so a quarter of the water went with it.
        Assert.Equal(50, report.FluidVoided);
        Assert.Equal(50, world.Fluids.VoidedByRemoval);

        // And the rest is still in the pipes -- split across the two runs the
        // cut made, without a drop appearing or disappearing between them.
        Assert.Equal(2, world.Fluids.NetworkCount);
        Assert.Equal(150, world.Fluids.TotalFluid());
        Assert.Equal(3, world.Fluids.Nodes.Count);
        Assert.False(world.Fluids.HasNodeAt(71, 30));
    }

    [Fact]
    public void RemovingAPump_DrainsWhatWasInItsBuffer()
    {
        var world = NewWorld();
        var pump = Get("stm_pump_station");
        Give(world, "stm_pump_station");
        var (x, y) = WaterTile(world);

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pump.Item, x, y));

        // Water in its buffer with nowhere to send it. Set rather than pumped:
        // a pump needs a grid to run, and what is under test is what happens to
        // the buffer, not how it filled.
        world.Extractors[0].Restore(MachineState.Idle, 0, 300, 0);
        var buffered = world.Extractors[0].Buffered;
        Assert.Equal(300, buffered);

        var report = world.TryRemove(x, y);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(buffered, report.FluidVoided);
        Assert.Equal(0, report.Returned);
        Assert.Empty(world.Extractors);
        Assert.Equal(1, world.PlayerInventory.Count(pump.Item));
    }

    [Fact]
    public void RemovingAPipe_DoesNotHandTheNodeThatMovesSomeoneElsesFluid()
    {
        var world = NewWorld();
        var pipe = Get("stm_pipe");
        Give(world, "stm_pipe", 4);

        // Two runs far apart, so they are two networks holding two fluids.
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, 0, 60));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, 1, 60));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, 30, 60));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, 31, 60));

        var water = Data.Item("water");
        var oil = Data.Item("crude_oil");

        var near = world.Fluids.Network(world.Fluids.NetworkAt(0, 60));
        var far = world.Fluids.Network(world.Fluids.NetworkAt(30, 60));
        near.BeginTick();
        far.BeginTick();
        Assert.Equal(100, near.TryInsert(water, 100));
        Assert.Equal(180, far.TryInsert(oil, 180));

        // Removing the first node moves the *last* node -- which belongs to the
        // other network entirely -- into its slot. If the per-node network
        // bookkeeping does not move with it, the next rebuild hands it a share
        // of the water and its oil is invented or destroyed.
        Assert.True(world.TryRemove(0, 60).Ok);

        Assert.Equal(2, world.Fluids.NetworkCount);
        var stillWater = world.Fluids.Network(world.Fluids.NetworkAt(1, 60));
        var stillOil = world.Fluids.Network(world.Fluids.NetworkAt(30, 60));

        Assert.Equal(oil, stillOil.Fluid);
        Assert.Equal(180, stillOil.Amount);
        Assert.Equal(water, stillWater.Fluid);
        Assert.Equal(50, stillWater.Amount);
        Assert.Equal(230, world.Fluids.TotalFluid());
    }

    [Fact]
    public void RemovingATileFromTheBeltMap_IsEnoughToMakeItRecompile()
    {
        // The map on its own, without the world calling `MarkDirty` around it:
        // `BeltMap.Remove` is public and has to leave the map knowing it has
        // changed, or a caller that trusts it gets a segment list describing a
        // belt that is no longer there.
        var map = new BeltMap();
        var network = new BeltNetwork();
        for (var i = 0; i < 4; i++)
            Assert.True(map.PlaceBelt(20 + i, 70, Direction.East));

        map.RebuildIfDirty(network, (_, _) => Endpoint.None);
        Assert.Single(network.Segments);
        Assert.Equal(4, network.Segment(0).Tiles);

        Assert.True(map.Remove(22, 70));
        map.RebuildIfDirty(network, (_, _) => Endpoint.None);

        Assert.Equal(2, network.Segments.Count);
        Assert.Equal(2, network.Segment(0).Tiles);
        Assert.Equal(1, network.Segment(1).Tiles);
    }

    [Fact]
    public void ARemovedTilesRecord_DoesNotPayForWhateverIsBuiltThereNext()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        var furnace = Get("man_furnace");
        Give(world, "stm_transport_belt");

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 0, 0, null, Direction.East));
        Assert.True(world.TryRemove(0, 0).Ok);

        // Placed directly, the way a scenario does: nothing was paid for it. If
        // the belt's record were still sitting on the tile, removing this would
        // hand out a free belt and delete a machine nobody bought.
        world.TryPlaceMachine(Data.Recipe("smelt_chalcopyrite"), furnace.PlacementAt(0, 0));

        Assert.Equal(RemoveResult.UnknownBuilding, world.TryRemove(0, 0).Result);
        Assert.Equal(1, world.MachineCount);
        Assert.Equal(1, world.PlayerInventory.Count(belt.Item));
    }

    // ---- everything placeable ----------------------------------------------

    [Fact]
    public void EveryKindOfThingAPlayerCanPlace_ComesBackWhenRemoved()
    {
        // One of each `BuildKind` that places something, on a tile that suits
        // it. The list is checked against the enum below, so a new kind cannot
        // be added without this test noticing it has no removal.
        var kinds = new (string Item, bool NeedsOre, bool NeedsWater)[]
        {
            ("stm_furnace", false, false),
            ("stm_miner", true, false),
            ("stm_generator", false, false),
            ("stm_accumulator", false, false),
            ("stm_pole", false, false),
            ("stm_pipe", false, false),
            ("vlt_storage_tank", false, false),
            ("stm_pump_station", false, true),
            ("stm_transport_belt", false, false),
            ("stm_inserter", false, false),
            ("vlt_underground_belt", false, false),
            ("vlt_splitter", false, false),
        };

        var covered = kinds.Select(k => Get(k.Item).Kind).ToHashSet();
        foreach (BuildKind kind in Enum.GetValues<BuildKind>())
            if (kind != BuildKind.NotPlaceable)
                Assert.Contains(kind, covered);

        foreach (var (itemId, needsOre, needsWater) in kinds)
        {
            var world = NewWorld();
            var buildable = Get(itemId);
            Give(world, itemId);

            var (x, y) = (0, 0);
            if (needsOre) (x, y) = OreTile(world, out _);
            else if (needsWater) (x, y) = WaterTile(world);
            else (x, y) = BareTile(world);

            var recipe = buildable.Kind == BuildKind.Machine
                ? Buildables.RecipesFor(buildable)[0]
                : null;

            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, buildable.Item, x, y, recipe));
            Assert.Equal(0, world.PlayerInventory.Count(buildable.Item));

            var report = world.TryRemove(x, y);

            Assert.Equal(RemoveResult.Ok, report.Result);
            Assert.Equal(buildable.Item, report.Item);
            Assert.Equal(1, world.PlayerInventory.Count(buildable.Item));

            // The tile is genuinely free again: the same thing goes straight
            // back onto it.
            Assert.Equal(BuildResult.Ok,
                         world.TryBuild(Buildables, buildable.Item, x, y, recipe));
        }
    }

    private static (int X, int Y) WaterTile(World world)
    {
        for (var r = 0; r < 400; r++)
            for (var d = -r; d <= r; d++)
                foreach (var (x, y) in new[] { (d, r), (r, d), (d, -r), (-r, d) })
                    if (world.Ground.Gen.IsWater(x, y) &&
                        !world.Ground.TryResourceAt(x, y, out _, out _) &&
                        world.CanPlace(new MachinePlacement(x, y, 0, 0)))
                        return (x, y);

        throw new InvalidOperationException("no water tile found");
    }

    // ---- the record that makes all of it possible --------------------------

    [Fact]
    public void WhatABuildingCostIsSaved_SoALoadedFactoryIsStillRemovable()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_transport_belt");
        Give(world, "stm_furnace");
        Give(world, "stm_pole");
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x, y, recipe));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, Get("stm_pole").Item, x + 3, y + 3));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, x + 6, y, null, Direction.South));

        var file = Sim.Save.SaveGame.Capture(world);
        var reloaded = Sim.Save.SaveGame.Restore(file, Data.Recipes,
                                                 new WorldGen(world.Seed, NewGame.OreSpecs(Data)));

        // A belt tile carries no tier and two tiers of belt run at the same
        // speed, so "which item was this" cannot be read back off the map. If
        // the record did not survive the save, this is where it shows.
        // A pole too: it is the one thing the save used to restore straight
        // into the power grid, leaving its tile unreserved.
        Assert.True(reloaded.TryPoleAt(x + 3, y + 3, out var pole, out _));
        Assert.Equal(x + 3, pole.X);
        Assert.False(reloaded.CanPlace(new MachinePlacement(x + 3, y + 3, 0, 0)));
        Assert.Equal(RemoveResult.Ok, reloaded.TryRemove(x + 3, y + 3).Result);
        Assert.Equal(1, reloaded.PlayerInventory.Count(Get("stm_pole").Item));

        Assert.True(reloaded.TryRemovableAt(x + 6, y, out var item, out _, out _));
        Assert.Equal(belt.Item, item);

        Assert.Equal(RemoveResult.Ok, reloaded.TryRemove(x + 6, y).Result);
        Assert.Equal(1, reloaded.PlayerInventory.Count(belt.Item));
        Assert.Equal(RemoveResult.Ok, reloaded.TryRemove(x, y).Result);
        Assert.Equal(1, reloaded.PlayerInventory.Count(furnace.Item));
    }

    [Fact]
    public void ARemovedTilesRecord_GoesWithIt()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        Give(world, "stm_transport_belt", 2);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, belt.Item, 80, 80, null, Direction.East));
        Assert.True(world.TryRemove(80, 80).Ok);

        // Nothing there any more, and the tile does not still claim to owe the
        // player a belt -- a stale record would hand out a free one for every
        // click on bare ground.
        Assert.False(world.TryRemovableAt(80, 80, out _, out _, out _));
        Assert.Equal(RemoveResult.NothingThere, world.TryRemove(80, 80).Result);
        Assert.Equal(2, world.PlayerInventory.Count(belt.Item));
    }

    [Fact]
    public void RemovalPreview_NamesTheAnchorOfWhateverTileIsClicked()
    {
        var world = NewWorld();
        var derrick = Get("stm_oil_derrick");
        Assert.Equal(2, derrick.Size);

        // A derrick has to stand on a fluid deposit, so borrow the machine's
        // 2x2 footprint from a furnace of the same size instead where there is
        // none: what is under test is the anchor, not the resource.
        var multi = Buildables.All.First(b => b.Kind == BuildKind.Machine && b.Size >= 2);
        var recipe = Buildables.RecipesFor(multi)[0];
        var (x, y) = BareTile(world, multi.Size);

        world.PlayerInventory.Add(multi.Item, 1);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, multi.Item, x, y, recipe));

        // Clicked on the far corner, not the anchor: the preview has to report
        // the corner the footprint is drawn from, or the ghost sits one tile
        // off on every machine bigger than 1x1.
        Assert.True(world.TryRemovableAt(x + multi.Size - 1, y + multi.Size - 1,
                                         out var item, out var ax, out var ay));
        Assert.Equal(multi.Item, item);
        Assert.Equal(x, ax);
        Assert.Equal(y, ay);
    }
}
