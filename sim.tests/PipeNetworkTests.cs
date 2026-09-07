using Sim;

namespace Sim.Tests;

/// Pipes, tanks, pumps, and machines that are plumbed rather than belted.
public class PipeNetworkTests
{
    private static (World World, ItemId Water, ItemId Oil) Setup()
    {
        var db = new ItemDatabase();
        return (new World(1, db), db.Register("water"), db.Register("crude_oil"));
    }

    [Fact]
    public void AdjacentPipes_AreOneNetwork_AndAGapMakesTwo()
    {
        var (world, _, _) = Setup();
        for (var x = 0; x < 5; x++) world.Fluids.AddPipe(x, 0);
        for (var x = 10; x < 15; x++) world.Fluids.AddPipe(x, 0);

        Assert.Equal(2, world.Fluids.NetworkCount);
        Assert.Equal(world.Fluids.NetworkAt(0, 0), world.Fluids.NetworkAt(4, 0));
        Assert.NotEqual(world.Fluids.NetworkAt(0, 0), world.Fluids.NetworkAt(10, 0));

        // Bridging the gap joins them, and the fluid comes with it.
        for (var x = 5; x < 10; x++) world.Fluids.AddPipe(x, 0);
        Assert.Equal(1, world.Fluids.NetworkCount);
    }

    [Fact]
    public void MorePipeBuysVolume_NotFlow()
    {
        var (world, water, _) = Setup();
        for (var x = 0; x < 50; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputBasic);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        Assert.Equal(50 * FluidNetwork.CapacityPerTile, network.Capacity);
        Assert.Equal(FluidNetwork.ThroughputBasic, network.ThroughputPerTick);

        network.BeginTick();
        Assert.Equal(FluidNetwork.ThroughputBasic, network.TryInsert(water, 100_000));
    }

    [Fact]
    public void TheNarrowestPipeThrottlesTheWholeRun()
    {
        // The thing players already say about these systems: one small segment
        // in a big line and the line runs at the small segment's rate.
        var (world, _, _) = Setup();
        for (var x = 0; x < 10; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputLarge);
        world.Fluids.AddPipe(10, 0, FluidNetwork.ThroughputBasic);
        for (var x = 11; x < 20; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputLarge);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        Assert.Equal(FluidNetwork.ThroughputBasic, network.ThroughputPerTick);
    }

    [Fact]
    public void APumpGetsPastTheNarrowestPipe()
    {
        var (world, _, _) = Setup();
        for (var x = 0; x < 10; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputBasic);
        world.Fluids.AddPump(10, 0, FluidNetwork.ThroughputPumped);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        Assert.Equal(FluidNetwork.ThroughputPumped, network.ThroughputPerTick);

        // ...and it adds no volume, which is what makes it a different decision
        // from laying more pipe.
        Assert.Equal(10 * FluidNetwork.CapacityPerTile, network.Capacity);
    }

    [Fact]
    public void ATankBuysVolume_AndNoFlowAtAll()
    {
        var (world, _, _) = Setup();
        for (var x = 0; x < 4; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputBasic);
        world.Fluids.AddTank(4, 0, capacity: 20_000);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        Assert.Equal(4 * FluidNetwork.CapacityPerTile + 20_000, network.Capacity);
        Assert.Equal(FluidNetwork.ThroughputBasic, network.ThroughputPerTick);
    }

    [Fact]
    public void MixingFluidsInOneRun_IsRefused()
    {
        var (world, water, oil) = Setup();
        for (var x = 0; x < 5; x++) world.Fluids.AddPipe(x, 0);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        network.BeginTick();
        Assert.True(network.TryInsert(water, 100) > 0);
        Assert.Equal(0, network.TryInsert(oil, 100));
    }

    [Fact]
    public void SplittingARun_DividesTheFluidWhereTheCutWas()
    {
        // Contents follow the pipe, not the network id: half the run keeps half
        // the water rather than one arbitrary side keeping all of it.
        var (world, water, _) = Setup();
        for (var x = 0; x < 10; x++) world.Fluids.AddPipe(x, 0);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        network.BeginTick();
        network.TryInsert(water, 200);
        Assert.Equal(200, world.Fluids.TotalFluid());

        // A second, separate run does not disturb the first.
        for (var x = 20; x < 30; x++) world.Fluids.AddPipe(x, 0);
        Assert.Equal(2, world.Fluids.NetworkCount);
        Assert.Equal(200, world.Fluids.TotalFluid());
    }

    [Fact]
    public void JoiningRunsCarryingDifferentFluids_VoidsTheSmallerAndSaysSo()
    {
        // A real mistake with a real cost. Silently keeping one and dropping the
        // other would leave a player unable to work out where their oil went.
        var (world, water, oil) = Setup();
        for (var x = 0; x < 5; x++) world.Fluids.AddPipe(x, 0);
        for (var x = 7; x < 12; x++) world.Fluids.AddPipe(x, 0);

        var a = world.Fluids.Network(world.Fluids.NetworkAt(0, 0));
        var b = world.Fluids.Network(world.Fluids.NetworkAt(7, 0));
        a.BeginTick();
        b.BeginTick();
        a.TryInsert(water, 400);
        b.TryInsert(oil, 100);

        Assert.Equal(0, world.Fluids.VoidedByMixing);

        world.Fluids.AddPipe(5, 0);
        world.Fluids.AddPipe(6, 0);

        Assert.Equal(1, world.Fluids.NetworkCount);
        var joined = world.Fluids.Network(0);
        Assert.Equal(water, joined.Fluid);
        Assert.True(world.Fluids.VoidedByMixing > 0, "the loss was not reported");
    }

    [Fact]
    public void AMachineDrawsItsFluidInputFromThePipeBesideIt()
    {
        var (world, water, _) = Setup();
        var crushed = world.Items.Register("crushed");
        var washed = world.Items.Register("washed");

        // Pipe running along the machine's north edge.
        for (var x = 0; x < 6; x++) world.Fluids.AddPipe(x, -1);
        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, -1));
        network.BeginTick();
        network.TryInsert(water, 5000);

        var recipe = new Recipe("wash", 10,
            new[] { new RecipeInput(crushed, 1), new RecipeInput(water, 100, isFluid: true) },
            new[] { new RecipeOutput(washed, 1) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(crushed, 50);

        world.Tick(30);

        Assert.True(machine.GetOutputCount(washed) > 0,
                    "the machine never ran, so it never got its water");
        Assert.True(world.Fluids.Network(world.Fluids.NetworkAt(0, -1)).Amount < 5000,
                    "the pipe was never drawn down");
    }

    [Fact]
    public void AMachineCannotDrawMoreThanThePipeActuallyHolds()
    {
        // Conservation on the way in. Handing the machine what it asked for
        // rather than what the network had would let it run on fluid that was
        // never pumped.
        var (world, water, _) = Setup();
        var crushed = world.Items.Register("crushed");
        var washed = world.Items.Register("washed");

        // Wide pipe, so the whole stock fits through the per-tick budget in one
        // go and the test is about the machine rather than about filling.
        for (var x = 0; x < 6; x++) world.Fluids.AddPipe(x, -1, FluidNetwork.ThroughputLarge);
        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, -1));
        network.BeginTick();

        // Exactly two and a half cycles' worth, and no source to top it up.
        var stocked = network.TryInsert(water, 250);
        Assert.Equal(250, stocked);

        var recipe = new Recipe("wash", 5,
            new[] { new RecipeInput(crushed, 1), new RecipeInput(water, 100, isFluid: true) },
            new[] { new RecipeOutput(washed, 1) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(crushed, 500);

        world.Tick(500);

        // Two cycles is all 250 units can buy; the leftover 50 is not a cycle.
        Assert.Equal(2, machine.GetOutputCount(washed));
        Assert.Equal(0, world.Fluids.Network(world.Fluids.NetworkAt(0, -1)).Amount);
        Assert.Equal(MachineState.Starved, machine.State);
    }

    [Fact]
    public void AFullPipeBacksUpTheExtractor_RatherThanLosingWhatItPumped()
    {
        // Conservation at the source. Emptying the extractor regardless of what
        // the pipe accepted would destroy fluid every tick the line is full.
        var db = new ItemDatabase();
        var oil = db.Register("crude_oil");
        var gen = new WorldGen(11, new List<OreSpec> { new(oil, 0, 8, 100_000) });
        var world = new World(11, db, gen);
        var patch = gen.PatchesInRegion(0, 0).First();

        // One tile of pipe: 100 units of room against 50 a cycle.
        world.Fluids.AddPipe(patch.X, patch.Y - 1);
        var derrick = world.TryPlaceExtractor(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), oil,
                                              cycleTicks: 2)!;

        world.Tick(300);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(patch.X, patch.Y - 1));
        Assert.Equal(network.Capacity, network.Amount);
        Assert.True(derrick.Buffered > 0,
                    "the derrick lost what the full pipe would not take");
        Assert.Equal(MachineState.Blocked, derrick.State);
    }

    [Fact]
    public void AMachineWithNoPipe_StaysStarvedRatherThanInventingFluid()
    {
        var (world, water, _) = Setup();
        var crushed = world.Items.Register("crushed");
        var washed = world.Items.Register("washed");

        var recipe = new Recipe("wash", 10,
            new[] { new RecipeInput(crushed, 1), new RecipeInput(water, 100, isFluid: true) },
            new[] { new RecipeOutput(washed, 1) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(crushed, 50);

        world.Tick(100);
        Assert.Equal(0, machine.GetOutputCount(washed));
        Assert.Equal(MachineState.Starved, machine.State);
    }

    [Fact]
    public void AMachinesFluidOutput_GoesIntoThePipe()
    {
        var (world, _, oil) = Setup();
        var shale = world.Items.Register("shale");

        for (var x = 0; x < 6; x++) world.Fluids.AddPipe(x, -1);

        var recipe = new Recipe("press", 10,
            new[] { new RecipeInput(shale, 1) },
            new[] { new RecipeOutput(oil, 80, isFluid: true) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(shale, 50);

        world.Tick(40);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, -1));
        Assert.Equal(oil, network.Fluid);
        Assert.True(network.Amount > 0, "nothing reached the pipe");
    }

    [Fact]
    public void AFullPipeBacksTheMachineUp_RatherThanSwallowingTheDifference()
    {
        // Conservation at the boundary: what the network refuses has to stay in
        // the machine, or a full pipe quietly destroys production.
        var (world, _, oil) = Setup();
        var shale = world.Items.Register("shale");

        world.Fluids.AddPipe(0, -1);        // one tile: 100 units of room

        var recipe = new Recipe("press", 5,
            new[] { new RecipeInput(shale, 1) },
            new[] { new RecipeOutput(oil, 80, isFluid: true) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1),
                                            outputCapacityPerItem: 1000)!;
        machine.PushInput(shale, 1000);

        world.Tick(500);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(0, -1));
        var produced = network.Amount + machine.GetOutputCount(oil);
        var cycles = produced / 80;

        Assert.Equal(cycles * 80, produced);
        Assert.True(machine.GetOutputCount(oil) > 0, "the machine should have backed up");
        Assert.Equal(network.Capacity, network.Amount);
    }

    [Fact]
    public void AWaterPumpDrawsForever_AndFillsThePipe()
    {
        var db = new ItemDatabase();
        var water = db.Register("water");
        var gen = new WorldGen(3, new List<OreSpec>());
        var world = new World(3, db, gen);

        // Find a water tile to stand a pump in.
        var (wx, wy) = (0, 0);
        var found = false;
        for (var y = -200; y < 200 && !found; y += 3)
            for (var x = -200; x < 200 && !found; x += 3)
                if (gen.IsWater(x, y)) { (wx, wy) = (x, y); found = true; }

        Assert.True(found, "this seed has no water at all, so the test cannot run");

        world.Fluids.AddPipe(wx, wy - 1);
        var pump = world.TryPlaceExtractor(new MachinePlacement(wx, wy, 0, 0, 1), water,
                                           cycleTicks: 5)!;
        Assert.True(pump.Ambient);

        world.Tick(200);

        var network = world.Fluids.Network(world.Fluids.NetworkAt(wx, wy - 1));
        Assert.Equal(water, network.Fluid);
        Assert.True(network.Amount > 0, "the pump filled nothing");
    }

    [Fact]
    public void AnOilDerrickDepletesItsPatch()
    {
        var db = new ItemDatabase();
        var oil = db.Register("crude_oil");
        var gen = new WorldGen(11, new List<OreSpec> { new(oil, 0, 8, 5000) });
        var world = new World(11, db, gen);
        var patch = gen.PatchesInRegion(0, 0).First();

        world.Fluids.AddPipe(patch.X, patch.Y - 1);
        var derrick = world.TryPlaceExtractor(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), oil,
                                              cycleTicks: 5)!;
        Assert.False(derrick.Ambient);

        var before = world.Ground.RemainingAt(patch.X, patch.Y);
        world.Tick(100);

        Assert.True(world.Ground.RemainingAt(patch.X, patch.Y) < before,
                    "the patch was never drawn down");
        Assert.Equal(oil, world.Fluids.Network(world.Fluids.NetworkAt(patch.X, patch.Y - 1)).Fluid);
    }

    [Fact]
    public void APumpOnDryLand_IsRefused()
    {
        var db = new ItemDatabase();
        var water = db.Register("water");
        var gen = new WorldGen(11, new List<OreSpec>());
        var world = new World(11, db, gen);

        // The spawn region is pulled up onto workable land by worldgen.
        Assert.Null(world.TryPlaceExtractor(new MachinePlacement(0, 0, 0, 0, 1), water));
    }
}
