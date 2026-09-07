using Sim;

namespace Sim.Tests;

/// Player-written Lua driving drones. The properties that matter are that it
/// cannot hang the simulation, cannot reach outside the sandbox, and produces
/// the same factory every time.
public class ControllerTests
{
    private static (World World, ItemId Ore, ItemId Ingot) Setup()
    {
        var db = new ItemDatabase();
        return (new World(1, db), db.Register("magnetite"), db.Register("iron_ingot"));
    }

    [Fact]
    public void AnInfiniteLoop_DoesNotHangTheSimulation()
    {
        // The single most important property. A player will write this on their
        // first day, and without a step budget it freezes the whole game.
        var (world, _, _) = Setup();
        var controller = world.AddController("while true do end");

        Assert.Null(controller.Error);

        world.Tick(50);

        Assert.False(controller.Finished);
        Assert.Null(controller.Error);
        Assert.Equal(50, controller.Resumes);
    }

    [Fact]
    public void ASyntaxError_IsReportedRatherThanThrown()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController("this is not lua {{{");

        Assert.NotNull(controller.Error);
        world.Tick(10);          // and it must not crash the tick either
    }

    [Fact]
    public void ARuntimeError_StopsThatControllerAndNothingElse()
    {
        var (world, _, _) = Setup();
        var broken = world.AddController("local t = nil; print(t.field)");
        var fine = world.AddController("state.set('ran', 'yes')");

        world.Tick(5);

        Assert.NotNull(broken.Error);
        Assert.Equal("yes", fine.State["ran"]);
    }

    [Fact]
    public void TheSandboxIsClosed()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController(@"
            state.set('os', tostring(os))
            state.set('io', tostring(io))
            state.set('random', tostring(math.random))
            state.set('load', tostring(load))
        ");

        world.Tick(3);

        Assert.Equal("nil", controller.State["os"]);
        Assert.Equal("nil", controller.State["io"]);
        Assert.Equal("nil", controller.State["random"]);
        Assert.Equal("nil", controller.State["load"]);
    }

    [Fact]
    public void AControllerQueuesHaulTasks()
    {
        var (world, ore, _) = Setup();
        var controller = world.AddController(@"
            queue.haul('magnetite', 50, 0, 0, 10, 0)
            queue.haul('magnetite', 50, 0, 0, 12, 0)
        ");

        world.Tick(3);

        Assert.Null(controller.Error);
        Assert.Equal(2, world.Logistics.Tasks.Count);
        Assert.Equal(ore, world.Logistics.Tasks[0].Item);
        Assert.Equal(10, world.Logistics.Tasks[0].ToX);
    }

    [Fact]
    public void HaulingAnUnknownItem_IsAnErrorTheProgrammerSees()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController("queue.haul('unobtanium', 1, 0, 0, 1, 1)");

        world.Tick(3);

        Assert.NotNull(controller.Error);
        Assert.Contains("unobtanium", controller.Error);
    }

    [Fact]
    public void AControllerCanReadWhatAMachineIsHolding()
    {
        // The whole point of a controller rather than a fixed rule: it can look
        // at the factory and decide.
        var (world, ore, ingot) = Setup();
        var recipe = new Recipe("smelt", 10,
            new[] { new RecipeInput(ore, 1) }, new[] { new RecipeOutput(ingot, 1) });

        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(5, 5, 0, 0, 1))!;
        machine.PushInput(ore, 40);

        var controller = world.AddController(@"
            state.set('stock', tostring(inventory.input(5, 5, 'magnetite')))
        ");

        world.Tick(2);

        // 39, not 40: controllers run at the end of a tick, after the machines
        // have moved. A program sees a settled world rather than one that
        // depends on where in the tick it happened to look -- and the smelter
        // has already taken one ore for the cycle it started.
        Assert.Equal("39", controller.State["stock"]);
    }

    [Fact]
    public void StateSurvivesForTheProgramToReadBack()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController(@"
            local n = tonumber(state.get('runs')) or 0
            state.set('runs', tostring(n + 1))
            world.sleep(1)
            local m = tonumber(state.get('runs')) or 0
            state.set('runs', tostring(m + 1))
        ");

        world.Tick(10);
        Assert.Equal("2", controller.State["runs"]);
    }

    [Fact]
    public void SleepParksTheProgramAndItCarriesOn()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController(@"
            state.set('phase', 'one')
            world.sleep(1)
            state.set('phase', 'two')
        ");

        world.Tick(1);
        Assert.Equal("one", controller.State["phase"]);

        world.Tick(3);
        Assert.Equal("two", controller.State["phase"]);
        Assert.True(controller.Finished);
    }

    [Fact]
    public void PrintGoesToABoundedLog()
    {
        var (world, _, _) = Setup();
        var controller = world.AddController(@"
            for i = 1, 500 do print('line ' .. i) end
        ");

        world.Tick(20);

        Assert.NotEmpty(controller.Log);
        Assert.True(controller.Log.Count <= Controller.LogLines,
                    "the log must not grow without bound; a program in a loop prints forever");
    }

    [Fact]
    public void TheSameProgramProducesTheSameFactoryEveryTime()
    {
        // Determinism, which is the contract the whole sim rests on. Two
        // identical worlds running an identical controller must agree exactly.
        static string Run()
        {
            var db = new ItemDatabase();
            var ore = db.Register("magnetite");
            var world = new World(7, db);

            var source = new Recipe("dig", 4,
                new[] { new RecipeInput(db.Register("rock"), 1) },
                new[] { new RecipeOutput(ore, 5) });
            var sink = new Recipe("smelt", 8,
                new[] { new RecipeInput(ore, 2) },
                new[] { new RecipeOutput(db.Register("iron_ingot"), 1) });

            var from = world.TryPlaceMachine(source, new MachinePlacement(0, 0, 0, 0, 1))!;
            from.PushInput(db.GetId("rock"), 10_000);
            world.TryPlaceMachine(sink, new MachinePlacement(20, 6, 0, 0, 1));

            for (var i = 0; i < 4; i++)
                world.Logistics.AddDrone(new Drone(0, 0));

            world.AddController(@"
                while true do
                  if queue.pending() < 2 and inventory.output(0, 0, 'magnetite') > 20 then
                    queue.haul('magnetite', 20, 0, 0, 20, 6)
                  end
                  world.sleep(5)
                end
            ");

            world.Tick(3000);

            var sb = new System.Text.StringBuilder();
            sb.Append(world.Logistics.Tasks.Count).Append(';');
            foreach (var drone in world.Logistics.Drones)
                sb.Append(drone.X).Append(',').Append(drone.Y).Append(',')
                  .Append(drone.CargoCount).Append(',').Append(drone.Task).Append(';');
            foreach (var task in world.Logistics.Tasks)
                sb.Append(task.State).Append(',').Append(task.Delivered).Append(';');
            return sb.ToString();
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a, b);

        // ...and it actually did something worth comparing.
        Assert.DoesNotContain("0;", a[..2]);
    }
}
