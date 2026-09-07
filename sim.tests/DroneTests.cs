using Sim;

namespace Sim.Tests;

/// Drones and the haul queue. Dispatch has to be dull and repeatable, and
/// nothing may be created or destroyed in the carrying.
public class DroneTests
{
    private static (World World, ItemId Ore, Machine Source, Machine Sink) Setup(int distance = 10)
    {
        var db = new ItemDatabase();
        var rock = db.Register("rock");
        var ore = db.Register("magnetite");
        var ingot = db.Register("iron_ingot");
        var world = new World(1, db);

        var dig = new Recipe("dig", 2,
            new[] { new RecipeInput(rock, 1) }, new[] { new RecipeOutput(ore, 10) });
        var smelt = new Recipe("smelt", 4,
            new[] { new RecipeInput(ore, 1) }, new[] { new RecipeOutput(ingot, 1) });

        var source = world.TryPlaceMachine(dig, new MachinePlacement(0, 0, 0, 0, 1),
                                           outputCapacityPerItem: 100_000)!;
        source.PushInput(rock, 100_000);
        var sink = world.TryPlaceMachine(smelt, new MachinePlacement(distance, 0, 0, 0, 1))!;

        return (world, ore, source, sink);
    }

    [Fact]
    public void ADroneCarriesOreFromOneMachineToAnother()
    {
        var (world, ore, _, sink) = Setup();
        world.Logistics.AddDrone(new Drone(0, 0));
        world.Logistics.AddTask(new HaulTask(ore, 40, 0, 0, 10, 0));

        world.Tick(400);

        Assert.Equal(HaulState.Done, world.Logistics.Tasks[0].State);
        Assert.Equal(40, world.Logistics.Tasks[0].Delivered);
        Assert.True(sink.GetInputCount(ore) > 0 || sink.GetOutputCount(world.Items.GetId("iron_ingot")) > 0);
    }

    [Fact]
    public void NothingIsCreatedOrDestroyedInTheCarrying()
    {
        // Conservation across the trip. The target deliberately cannot start a
        // cycle -- it needs a flux that never arrives -- because a machine
        // mid-cycle is holding inputs it has already taken out of its buffer,
        // and that ore would be in neither buffer nor drone.
        var db = new ItemDatabase();
        var rock = db.Register("rock");
        var ore = db.Register("magnetite");
        var flux = db.Register("flux");
        var world = new World(1, db);

        var dig = new Recipe("dig", 2,
            new[] { new RecipeInput(rock, 1) }, new[] { new RecipeOutput(ore, 10) });
        var stuck = new Recipe("smelt", 4,
            new[] { new RecipeInput(ore, 1), new RecipeInput(flux, 1) },
            new[] { new RecipeOutput(db.Register("iron_ingot"), 1) });

        var source = world.TryPlaceMachine(dig, new MachinePlacement(0, 0, 0, 0, 1),
                                           outputCapacityPerItem: 100_000)!;
        source.PushInput(rock, 100_000);
        var sink = world.TryPlaceMachine(stuck, new MachinePlacement(10, 0, 0, 0, 1))!;

        world.Logistics.AddDrone(new Drone(0, 0, capacity: 25));
        world.Logistics.AddTask(new HaulTask(ore, 100, 0, 0, 10, 0));

        world.Tick(60);       // stop mid-haul, with a drone in the air

        var inFlight = world.Logistics.Drones[0].CargoCount;
        var delivered = sink.GetInputCount(ore);

        Assert.True(inFlight > 0 || delivered > 0, "nothing moved, so nothing is being tested");
        Assert.Equal(MachineState.Starved, sink.State);
        Assert.Equal(world.Logistics.Tasks[0].Delivered, delivered);
    }

    [Fact]
    public void ADroneNeverCarriesMoreThanItsCapacity()
    {
        var (world, ore, _, _) = Setup();
        world.Logistics.AddDrone(new Drone(0, 0, capacity: 15));
        world.Logistics.AddTask(new HaulTask(ore, 500, 0, 0, 10, 0));

        for (var i = 0; i < 400; i++)
        {
            world.Tick();
            Assert.True(world.Logistics.Drones[0].CargoCount <= 15);
        }
    }

    [Fact]
    public void DispatchIsTheOldestTaskToTheLowestNumberedIdleDrone()
    {
        // Dull on purpose. Nearest-drone dispatch would depend on distance
        // tie-breaks, which is how a reproducible factory stops being one.
        var (world, ore, _, _) = Setup();
        world.Logistics.AddDrone(new Drone(0, 0));
        world.Logistics.AddDrone(new Drone(0, 0));

        world.Logistics.AddTask(new HaulTask(ore, 10, 0, 0, 10, 0));
        world.Logistics.AddTask(new HaulTask(ore, 10, 0, 0, 12, 0));

        world.Tick();

        Assert.Equal(0, world.Logistics.Tasks[0].Drone);
        Assert.Equal(1, world.Logistics.Tasks[1].Drone);
        Assert.Equal(0, world.Logistics.Drones[0].Task);
        Assert.Equal(1, world.Logistics.Drones[1].Task);
    }

    [Fact]
    public void ATaskWithADrySource_FinishesShortRatherThanHoveringForever()
    {
        var db = new ItemDatabase();
        var ore = db.Register("magnetite");
        var world = new World(1, db);

        // A machine that produces nothing at all.
        var recipe = new Recipe("idle", 10,
            new[] { new RecipeInput(db.Register("nothing"), 1) },
            new[] { new RecipeOutput(ore, 1) });
        world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1));
        world.TryPlaceMachine(recipe, new MachinePlacement(6, 0, 0, 0, 1));

        world.Logistics.AddDrone(new Drone(0, 0));
        world.Logistics.AddTask(new HaulTask(ore, 50, 0, 0, 6, 0));

        // Past the drone's patience, which exists so that a source still
        // warming up is not mistaken for one that will never deliver.
        world.Tick(LogisticsSystem.Patience + 100);

        Assert.Equal(HaulState.Done, world.Logistics.Tasks[0].State);
        Assert.Equal(0, world.Logistics.Tasks[0].Delivered);
        Assert.True(world.Logistics.Drones[0].IsIdle, "the drone must be released for other work");
    }

    [Fact]
    public void AFurtherTargetTakesLonger()
    {
        // Distance has to cost something, or logistics is not a problem.
        static long Ticks(int distance)
        {
            var db = new ItemDatabase();
            var ore = db.Register("magnetite");
            var rock = db.Register("rock");
            var world = new World(1, db);

            var dig = new Recipe("dig", 1,
                new[] { new RecipeInput(rock, 1) }, new[] { new RecipeOutput(ore, 50) });
            var sink = new Recipe("hold", 10_000,
                new[] { new RecipeInput(ore, 10_000) }, new[] { new RecipeOutput(rock, 1) });

            var src = world.TryPlaceMachine(dig, new MachinePlacement(0, 0, 0, 0, 1),
                                            outputCapacityPerItem: 100_000)!;
            src.PushInput(rock, 100_000);
            world.TryPlaceMachine(sink, new MachinePlacement(distance, 0, 0, 0, 1));

            world.Logistics.AddDrone(new Drone(0, 0));
            world.Logistics.AddTask(new HaulTask(ore, 20, 0, 0, distance, 0));

            for (var i = 0; i < 5000; i++)
            {
                world.Tick();
                if (world.Logistics.Tasks[0].State == HaulState.Done) return world.TickCount;
            }

            return -1;
        }

        var near = Ticks(5);
        var far = Ticks(40);

        Assert.True(near > 0 && far > 0, "both hauls should finish");
        Assert.True(far > near * 2, $"distance barely mattered: {near} vs {far}");
    }

    [Fact]
    public void FinishedTasksAreCleanedUp_ButOnlyWhileNoDroneHoldsAnIndex()
    {
        var (world, ore, _, _) = Setup(distance: 4);
        world.Logistics.AddDrone(new Drone(0, 0));
        world.Logistics.AddTask(new HaulTask(ore, 10, 0, 0, 4, 0));

        world.Tick(300);
        Assert.Equal(HaulState.Done, world.Logistics.Tasks[0].State);

        world.Logistics.Compact();
        Assert.Empty(world.Logistics.Tasks);
    }
}
