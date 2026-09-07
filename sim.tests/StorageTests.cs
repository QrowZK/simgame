using Sim;

namespace Sim.Tests;

/// Accumulators. The properties worth holding are that stored energy is
/// conserved, that storage never competes with production, and that a bank
/// empties and fills evenly.
public class StorageTests
{
    private static Recipe Smelt(ItemId ore, ItemId ingot, int power, int ticks = 10) =>
        new("smelt", ticks,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(ingot, 1) }, power);

    private static (World World, ItemId Ore, ItemId Ingot, ItemId Coal) Setup()
    {
        var db = new ItemDatabase();
        var world = new World(1, db);
        world.Power.AddPole(new Pole(4, 4, supplyRadius: 12, wireRadius: 9));
        return (world, db.Register("ore"), db.Register("ingot"), db.Register("coal"));
    }

    [Fact]
    public void SurplusChargesTheAccumulator()
    {
        var (world, _, _, coal) = Setup();
        var accumulator = world.TryPlaceAccumulator(new Accumulator(10_000, 100),
                                                    new MachinePlacement(4, 4, 0, 0, 1))!;

        var generator = new Generator(coal, outputPerTick: 60, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(50);

        // Nothing is drawing, so every unit generated should be stored --
        // capped by the accumulator's own rate, which is 60 here.
        Assert.Equal(50 * 60, accumulator.Charge);
    }

    [Fact]
    public void ChargingNeverCompetesWithProduction()
    {
        // The property that makes storage safe to build: a factory must not
        // brown out because its batteries were filling.
        var (world, ore, ingot, coal) = Setup();

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 40),
                                            new MachinePlacement(4, 5, 0, 0, 1))!;
        machine.PushInput(ore, 10_000);

        world.TryPlaceAccumulator(new Accumulator(100_000, 1_000),
                                  new MachinePlacement(6, 4, 0, 0, 1));

        // Exactly enough for the machine and not a unit more.
        var generator = new Generator(coal, outputPerTick: 40, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(1000);

        Assert.Equal(100, machine.GetOutputCount(ingot));
        Assert.Equal(0, world.StoredEnergy);
    }

    [Fact]
    public void StorageCoversAShortfall_AndTheFactoryKeepsRunning()
    {
        var (world, ore, ingot, coal) = Setup();

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 40),
                                            new MachinePlacement(4, 5, 0, 0, 1))!;
        machine.PushInput(ore, 10_000);

        var accumulator = world.TryPlaceAccumulator(new Accumulator(4_000, 1_000),
                                                    new MachinePlacement(6, 4, 0, 0, 1))!;

        // A generator with only enough fuel for the first stretch.
        var generator = new Generator(coal, outputPerTick: 200, ticksPerFuel: 100);
        generator.AddFuel(1);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(100);
        var storedAtBurnout = accumulator.Charge;
        var madeOnGenerator = machine.GetOutputCount(ingot);
        Assert.True(storedAtBurnout > 0, "the accumulator should have banked the surplus");

        // The generator is dry now; the machine runs on stored energy alone.
        world.Tick(100);

        Assert.True(machine.GetOutputCount(ingot) > madeOnGenerator,
                    "production stopped the moment the generator did");
        Assert.True(accumulator.Charge < storedAtBurnout, "the store was never drawn down");
    }

    [Fact]
    public void EnergyIsConserved_AcrossStorage()
    {
        // Nothing may be created in the storing. Everything generated is either
        // spent by the machine, sitting in the accumulator, or in the machine's
        // own buffer.
        var (world, ore, ingot, coal) = Setup();

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 30),
                                            new MachinePlacement(4, 5, 0, 0, 1))!;
        machine.PushInput(ore, 10_000);

        var accumulator = world.TryPlaceAccumulator(new Accumulator(50_000, 500),
                                                    new MachinePlacement(6, 4, 0, 0, 1))!;

        const int rate = 45;
        const int ticks = 400;
        var generator = new Generator(coal, outputPerTick: rate, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(ticks);

        var generated = rate * ticks;
        var spent = machine.GetOutputCount(ingot) * 10 * 30;   // cycles x ticks x draw
        var banked = accumulator.Charge;
        var inHand = machine.Energy;

        Assert.Equal(generated, spent + banked + inHand);
    }

    [Fact]
    public void ABankChargesEvenly()
    {
        // A row of accumulators should read as one number, not as stores at
        // unrelated levels.
        var (world, _, _, coal) = Setup();

        var bank = new List<Accumulator>();
        for (var i = 0; i < 4; i++)
            bank.Add(world.TryPlaceAccumulator(new Accumulator(10_000, 100),
                                               new MachinePlacement(i, 8, 0, 0, 1))!);

        var generator = new Generator(coal, outputPerTick: 100, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(200);

        var charges = bank.Select(a => a.Charge).ToList();
        Assert.True(charges.Max() - charges.Min() <= 1,
                    $"the bank charged unevenly: {string.Join(", ", charges)}");
        Assert.Equal(200 * 100, charges.Sum());
    }

    [Fact]
    public void ABankDischargesEvenly()
    {
        var (world, ore, ingot, _) = Setup();

        var bank = new List<Accumulator>();
        for (var i = 0; i < 4; i++)
        {
            var accumulator = world.TryPlaceAccumulator(new Accumulator(10_000, 100),
                                                        new MachinePlacement(i, 8, 0, 0, 1))!;
            accumulator.Restore(5_000);
            bank.Add(accumulator);
        }

        var machine = world.TryPlaceMachine(Smelt(ore, ingot, power: 40),
                                            new MachinePlacement(4, 5, 0, 0, 1))!;
        machine.PushInput(ore, 10_000);

        world.Tick(300);        // no generator at all: the bank is the supply

        var charges = bank.Select(a => a.Charge).ToList();
        Assert.True(charges.Max() - charges.Min() <= 1,
                    $"the bank discharged unevenly: {string.Join(", ", charges)}");
        Assert.True(machine.GetOutputCount(ingot) > 0, "the bank never powered anything");
    }

    [Fact]
    public void TheRateLimitsWhatCanMoveInATick()
    {
        // Capacity and rate are different purchases: a big slow store and a
        // small fast one solve different problems.
        var (world, _, _, coal) = Setup();
        var slow = world.TryPlaceAccumulator(new Accumulator(1_000_000, 10),
                                             new MachinePlacement(4, 4, 0, 0, 1))!;

        var generator = new Generator(coal, outputPerTick: 5_000, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(20);

        Assert.Equal(200, slow.Charge);          // 10 a tick, not 5000
    }

    [Fact]
    public void AnAccumulatorNoPoleReaches_StoresNothing()
    {
        var (world, _, _, coal) = Setup();
        var stranded = world.TryPlaceAccumulator(new Accumulator(10_000, 100),
                                                 new MachinePlacement(500, 500, 0, 0, 1))!;

        var generator = new Generator(coal, outputPerTick: 200, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(100);
        Assert.Equal(0, stranded.Charge);
    }

    [Fact]
    public void AFullAccumulatorTakesNoMore_AndAnEmptyOneGivesNone()
    {
        var accumulator = new Accumulator(100, 1000);

        Assert.Equal(100, accumulator.Absorb(500));
        Assert.Equal(0, accumulator.Absorb(500));
        Assert.Equal(MachineState.Blocked, accumulator.State);

        Assert.Equal(100, accumulator.Release(500));
        Assert.Equal(0, accumulator.Release(500));
        Assert.Equal(MachineState.Starved, accumulator.State);
    }

    /// The world's allocator already clamps a share to the rate before offering
    /// it, so the accumulator's own rate clamp never fires through the factory.
    /// It is still the accumulator's promise, and anything calling it directly
    /// -- a save restore, an editor, a future consumer -- depends on it, so it
    /// is tested where it lives.
    [Fact]
    public void TheRateIsEnforcedByTheAccumulatorItself()
    {
        var accumulator = new Accumulator(capacity: 10_000, ratePerTick: 25);

        Assert.Equal(25, accumulator.Absorb(10_000));
        Assert.Equal(25, accumulator.Charge);

        // Released from a full accumulator, so the rate is what limits the
        // hand-out rather than there simply being nothing left to give.
        accumulator.Restore(10_000);
        Assert.Equal(25, accumulator.Release(10_000));
        Assert.Equal(9_975, accumulator.Charge);
    }

    /// A negative offer must not run the accumulator backwards: absorbing -100
    /// would otherwise drain it, and releasing -100 would fill it for free.
    [Fact]
    public void NegativeAmountsMoveNothing()
    {
        var accumulator = new Accumulator(capacity: 1000, ratePerTick: 50);
        accumulator.Restore(400);

        Assert.Equal(0, accumulator.Absorb(-100));
        Assert.Equal(0, accumulator.Release(-100));
        Assert.Equal(400, accumulator.Charge);
    }

    /// Restore is the save path's door into the charge, so it has to refuse a
    /// figure the accumulator could not have reached on its own.
    [Fact]
    public void RestoreClampsToWhatTheAccumulatorCanHold()
    {
        var accumulator = new Accumulator(capacity: 1000, ratePerTick: 50);

        accumulator.Restore(5000);
        Assert.Equal(1000, accumulator.Charge);

        accumulator.Restore(-5000);
        Assert.Equal(0, accumulator.Charge);
    }

    /// A share that does not divide evenly must not quietly evaporate. Three
    /// accumulators splitting 100 a tick get 33 each if the remainder is
    /// thrown away, and the grid loses a unit every tick forever. The running
    /// carry is what makes the split exact, and this is the test that says so.
    [Fact]
    public void AnUnevenSplitLosesNothing()
    {
        var (world, _, _, coal) = Setup();

        var bank = new List<Accumulator>();
        for (var i = 0; i < 3; i++)
            bank.Add(world.TryPlaceAccumulator(new Accumulator(10_000, 100),
                                               new MachinePlacement(i, 8, 0, 0, 1))!);

        var generator = new Generator(coal, outputPerTick: 100, ticksPerFuel: 100_000);
        generator.AddFuel(10);
        world.TryPlaceGenerator(generator, new MachinePlacement(2, 4, 0, 0, 1));

        world.Tick(90);

        Assert.Equal(90 * 100, bank.Sum(a => a.Charge));
        Assert.True(bank.Max(a => a.Charge) - bank.Min(a => a.Charge) <= 1,
                    $"uneven: {string.Join(", ", bank.Select(a => a.Charge))}");
    }
}
