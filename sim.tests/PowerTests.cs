using Sim;

namespace Sim.Tests;

/// Generators, poles, and machines that stop when the lights go out.
public class PowerTests
{
    private static Recipe Smelt(ItemId ore, ItemId ingot, int power, int ticks = 10) =>
        new("smelt", ticks,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(ingot, 1) }, power);

    private static (World World, ItemId Ore, ItemId Ingot, ItemId Coal) Setup()
    {
        var db = new ItemDatabase();
        return (new World(1, db), db.Register("ore"), db.Register("ingot"), db.Register("coal"));
    }

    [Fact]
    public void AMachineWithNoPowerRequirement_RunsWithoutAGrid()
    {
        // The Manual tier, and the whole game before the first generator.
        var (world, ore, ingot, _) = Setup();
        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 0),
                                            new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(ore, 10);

        world.Tick(10);
        Assert.Equal(1, machine.GetOutputCount(ingot));
    }

    [Fact]
    public void AMachineThatNeedsPower_DoesNotRunWithoutIt()
    {
        var (world, ore, ingot, _) = Setup();
        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                            new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(ore, 10);

        world.Tick(100);

        Assert.Equal(MachineState.Unpowered, machine.State);
        Assert.Equal(0, machine.GetOutputCount(ingot));
    }

    [Fact]
    public void AGeneratorAndAPole_BringAMachineToLife()
    {
        var (world, ore, ingot, coal) = Setup();
        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                            new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(ore, 100);

        world.Power.AddPole(new Pole(2, 0, supplyRadius: 4, wireRadius: 9));
        var generator = new Generator(coal, outputPerTick: 40, ticksPerFuel: 200);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(4, 0, 0, 0, 1));

        world.Tick(10);

        Assert.Equal(1, machine.GetOutputCount(ingot));
        Assert.True(generator.IsBurning);
    }

    [Fact]
    public void APoleOutOfReach_LeavesTheMachineDark()
    {
        // Coverage is a placement problem, which is the whole reason poles have
        // a radius at all.
        var (world, ore, ingot, coal) = Setup();
        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                            new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(ore, 100);

        world.Power.AddPole(new Pole(50, 50, supplyRadius: 4));
        var generator = new Generator(coal, outputPerTick: 40, ticksPerFuel: 200);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(52, 50, 0, 0, 1));

        world.Tick(50);
        Assert.Equal(MachineState.Unpowered, machine.State);
    }

    [Fact]
    public void PolesOutOfWireReach_AreSeparateNetworks()
    {
        var (world, _, _, _) = Setup();
        world.Power.AddPole(new Pole(0, 0, 4, wireRadius: 9));
        world.Power.AddPole(new Pole(8, 0, 4, wireRadius: 9));    // reaches the first
        world.Power.AddPole(new Pole(80, 0, 4, wireRadius: 9));   // reaches nothing

        Assert.Equal(2, world.Power.NetworkCount);
        Assert.Equal(world.Power.NetworkAt(0, 0), world.Power.NetworkAt(8, 0));
        Assert.NotEqual(world.Power.NetworkAt(0, 0), world.Power.NetworkAt(80, 0));
    }

    [Fact]
    public void ALongReachPylon_CannotDragInAPoleThatCouldNotReachBack()
    {
        // Connection is mutual. If the pylon's reach alone decided, a player
        // could accidentally join two networks by building one big pole near a
        // small one that has no idea it is now sharing a grid.
        var (world, _, _, _) = Setup();
        world.Power.AddPole(new Pole(0, 0, supplyRadius: 4, wireRadius: 30));
        world.Power.AddPole(new Pole(20, 0, supplyRadius: 4, wireRadius: 5));

        Assert.Equal(2, world.Power.NetworkCount);
        Assert.NotEqual(world.Power.NetworkAt(0, 0), world.Power.NetworkAt(20, 0));

        // Within the smaller pole's own reach, they do connect.
        var near = new World(2, new ItemDatabase());
        near.Power.AddPole(new Pole(0, 0, supplyRadius: 4, wireRadius: 30));
        near.Power.AddPole(new Pole(4, 0, supplyRadius: 4, wireRadius: 5));
        Assert.Equal(1, near.Power.NetworkCount);
    }

    [Fact]
    public void APoleChain_CarriesPowerAcrossTheMap()
    {
        // Wire reach exceeds supply reach, so a line of poles is how a distant
        // mine gets fed. This is the thing that makes hauling and wiring two
        // different problems.
        var (world, ore, ingot, coal) = Setup();
        for (var x = 0; x <= 80; x += 8)
            world.Power.AddPole(new Pole(x, 0, supplyRadius: 4, wireRadius: 9));

        Assert.Equal(1, world.Power.NetworkCount);

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                            new MachinePlacement(80, 0, 0, 0, 1))!;
        machine.PushInput(ore, 100);

        var generator = new Generator(coal, outputPerTick: 40, ticksPerFuel: 500);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(0, 2, 0, 0, 1));

        world.Tick(10);
        Assert.Equal(1, machine.GetOutputCount(ingot));
    }

    [Fact]
    public void HalfThePowerIsHalfTheSpeed_NotHalfTheMachinesStopped()
    {
        // A brownout has to be proportional. Culling machines instead would
        // mean the same ones always lose, for a reason invisible on screen.
        var (world, ore, ingot, coal) = Setup();

        world.Power.AddPole(new Pole(4, 0, supplyRadius: 8, wireRadius: 9));

        var machines = new List<Machine>();
        for (var i = 0; i < 4; i++)
        {
            var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 10),
                                                new MachinePlacement(i, 0, 0, 0, 1))!;
            machine.PushInput(ore, 1000);
            machines.Add(machine);
        }

        // Four machines want 10 each; supply exactly half of that.
        var generator = new Generator(coal, outputPerTick: 20, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(4, 2, 0, 0, 1));

        world.Tick(1000);

        // Every machine ran, and every machine ran at about half speed: 1000
        // ticks of full power would be 100 cycles.
        foreach (var machine in machines)
            Assert.InRange(machine.GetOutputCount(ingot), 45, 55);

        // ...and no machine was starved of the shortfall while others ran on.
        var counts = machines.Select(m => m.GetOutputCount(ingot)).ToList();
        Assert.True(counts.Max() - counts.Min() <= 1,
                    $"the brownout was not shared evenly: {string.Join(", ", counts)}");
    }

    [Fact]
    public void EveryUnitGenerated_ReachesAConsumer()
    {
        // The allocator must not leak or invent energy: shares have to sum to
        // exactly the supply, or a factory silently loses power to rounding.
        //
        // Measured as work done rather than as energy sitting in buffers,
        // because by the end of a tick the machines have already spent it.
        var (world, ore, ingot, coal) = Setup();
        world.Power.AddPole(new Pole(3, 0, supplyRadius: 8));

        // Three machines at 7 each against a supply of 20: a demand that does
        // not divide evenly, which is where a sloppy allocator loses units.
        for (var i = 0; i < 3; i++)
        {
            var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 7),
                                                new MachinePlacement(i, 0, 0, 0, 1))!;
            machine.PushInput(ore, 10_000);
        }

        var generator = new Generator(coal, outputPerTick: 20, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(3, 2, 0, 0, 1));

        world.Tick(1000);

        // 20,000 units generated; a cycle costs 10 powered ticks at 7 a tick.
        var cycles = world.Machines.Sum(m => m.GetOutputCount(ingot));
        Assert.InRange(cycles, 282, 288);
    }

    [Fact]
    public void AMachineThatLosesPowerMidCycle_KeepsItsProgressAndItsInputs()
    {
        // The bug this design exists to avoid: re-entering the start path each
        // unpowered tick would consume the recipe's inputs again and again.
        var (world, ore, ingot, coal) = Setup();
        world.Power.AddPole(new Pole(0, 0, supplyRadius: 4));

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 8, ticks: 20),
                                            new MachinePlacement(0, 0, 0, 0, 1))!;
        machine.PushInput(ore, 5);

        var generator = new Generator(coal, outputPerTick: 8, ticksPerFuel: 10);
        generator.AddFuel(1);                       // ten ticks of power, then nothing
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 0, 0, 0, 1));

        world.Tick(10);
        Assert.Equal(MachineState.Working, machine.State);
        Assert.Equal(4, machine.GetInputCount(ore));    // one cycle's input consumed, once

        world.Tick(50);
        Assert.Equal(MachineState.Unpowered, machine.State);
        Assert.Equal(4, machine.GetInputCount(ore));    // and still only once

        // Restore power: the half-finished cycle finishes rather than restarting.
        generator.AddFuel(5);
        world.Tick(11);
        Assert.Equal(1, machine.GetOutputCount(ingot));
    }

    [Fact]
    public void AGeneratorRunsOutOfFuel_AndSaysSo()
    {
        var (world, _, _, coal) = Setup();
        var generator = new Generator(coal, outputPerTick: 10, ticksPerFuel: 5);
        generator.AddFuel(1);

        world.Power.AddPole(new Pole(0, 0));
        world.TryPlaceGenerator(generator, new MachinePlacement(0, 0, 0, 0, 1));

        world.Tick(4);
        Assert.Equal(MachineState.Working, generator.State);

        // The fifth tick is the last one that unit of fuel pays for.
        world.Tick(1);
        Assert.Equal(MachineState.Starved, generator.State);
        Assert.False(generator.IsBurning);
    }

    [Fact]
    public void ABiggerMachineDrawsProportionallyMore()
    {
        // Footprint is effect, and effect costs power -- otherwise a 3x3 would
        // be nine times the throughput for one machine's electricity bill.
        var (world, ore, ingot, _) = Setup();
        var small = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                          new MachinePlacement(0, 0, 0, 0, 1))!;
        var large = world.TryPlaceMachine(Smelt(ore, ingot, power: 8),
                                          new MachinePlacement(10, 10, 0, 0, 3))!;

        Assert.Equal(8, small.PowerDraw);
        Assert.Equal(72, large.PowerDraw);
    }

    [Fact]
    public void MinersDrawPowerToo()
    {
        var db = new ItemDatabase();
        var ore = db.Register("magnetite");
        var gen = new WorldGen(5, new List<OreSpec> { new(ore, 0, 8, 500) });
        var world = new World(5, db, gen);
        var patch = gen.PatchesInRegion(0, 0).First();

        var miner = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1),
                                        cycleTicks: 10, powerDraw: 4)!;

        world.Tick(30);
        Assert.Equal(MachineState.Unpowered, miner.State);
        Assert.Equal(0, miner.Buffered);

        world.Power.AddPole(new Pole(patch.X, patch.Y, supplyRadius: 4));
        var generator = new Generator(db.Register("coal"), outputPerTick: 20, ticksPerFuel: 1000);
        generator.AddFuel(5);
        world.TryPlaceGenerator(generator, new MachinePlacement(patch.X + 2, patch.Y, 0, 0, 1));

        world.Tick(10);
        Assert.Equal(1, miner.Buffered);
    }
}
