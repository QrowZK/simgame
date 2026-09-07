using System.Text;
using Sim;

namespace Sim.Tests;

public class DeterminismTests
{
    private static World BuildWorld(int seed)
    {
        var db = new ItemDatabase();
        var ore = db.Register("iron_ore");
        var plate = db.Register("iron_plate");
        var copperOre = db.Register("copper_ore");
        var copperPlate = db.Register("copper_plate");
        var gear = db.Register("iron_gear");

        var world = new World(seed);

        var smeltIron = world.AddMachine(new Recipe(
            "smelt_iron_plate", 192,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) }));
        smeltIron.PushInput(ore, 5000);

        var smeltCopper = world.AddMachine(new Recipe(
            "smelt_copper_plate", 192,
            new[] { new RecipeInput(copperOre, 1) },
            new[] { new RecipeOutput(copperPlate, 1) }));
        smeltCopper.PushInput(copperOre, 5000);

        var assembler = world.AddMachine(new Recipe(
            "assemble_iron_gear", 120,
            new[] { new RecipeInput(plate, 2) },
            new[] { new RecipeOutput(gear, 1) }));

        // A powered corner with storage on it. Without this the 10,000-tick
        // determinism run never touches the grid, and the storage allocator --
        // which rotates its distribution by tick count and carries a remainder
        // between accumulators -- would be the one system the strongest test
        // in the suite does not cover.
        var coal = db.Register("coal");
        world.Power.AddPole(new Pole(0, 0, supplyRadius: 8, wireRadius: 8));

        // Fuelled for 9,000 of the 10,000 ticks and generating more than the
        // machine below draws. So the bank charges on the surplus for most of
        // the run, and then covers the machine on its own once the fuel is
        // gone -- both halves of the storage path, in one run.
        var generator = new Generator(coal, outputPerTick: 12, ticksPerFuel: 300);
        generator.AddFuel(30);
        world.TryPlaceGenerator(generator, new MachinePlacement(1, 1, 0, 0, 1));

        // Three, so the remainder of a split cannot come out even.
        for (var i = 0; i < 3; i++)
            world.TryPlaceAccumulator(new Accumulator(capacity: 20_000, ratePerTick: 11),
                                      new MachinePlacement(2 + i, 3, 0, 0, 1));

        // A hungry machine on the same grid, so the bank is asked to cover a
        // shortfall as well as to absorb a surplus.
        var hungry = world.TryPlaceMachine(new Recipe(
            "smelt_iron_plate_slowly", 40,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) }, powerDraw: 9),
            new MachinePlacement(4, 1, 0, 0, 1), outputCapacityPerItem: 100_000)!;
        hungry.PushInput(ore, 5000);

        return world;
    }

    private static string Snapshot(World world, ItemDatabase db, IEnumerable<ItemId> itemsOfInterest)
    {
        var sb = new StringBuilder();
        sb.Append(world.TickCount).Append(';');
        foreach (var accumulator in world.Power.Accumulators)
            sb.Append(accumulator.Charge).Append(',').Append(accumulator.State).Append(';');
        foreach (var machine in world.Machines)
        {
            sb.Append(machine.State).Append(':');
            foreach (var item in itemsOfInterest)
                sb.Append(machine.GetInputCount(item)).Append(',').Append(machine.GetOutputCount(item)).Append(';');
        }
        return sb.ToString();
    }

    [Fact]
    public void SameSeedAndInputs_ProduceByteIdenticalStateAfter10000Ticks()
    {
        const int seed = 12345;
        const int ticks = 10_000;

        var dbA = new ItemDatabase();
        var itemsA = new[] { dbA.Register("iron_ore"), dbA.Register("iron_plate"), dbA.Register("copper_ore"), dbA.Register("copper_plate"), dbA.Register("iron_gear") };
        var worldA = BuildWorld(seed);
        worldA.Tick(ticks);
        var snapshotA = Snapshot(worldA, dbA, itemsA);

        var dbB = new ItemDatabase();
        var itemsB = new[] { dbB.Register("iron_ore"), dbB.Register("iron_plate"), dbB.Register("copper_ore"), dbB.Register("copper_plate"), dbB.Register("iron_gear") };
        var worldB = BuildWorld(seed);
        worldB.Tick(ticks);
        var snapshotB = Snapshot(worldB, dbB, itemsB);

        Assert.Equal(snapshotA, snapshotB);

        // Sanity: the simulation actually did something (not comparing two no-ops).
        var plate = dbA.GetId("iron_plate");
        var smeltIron = worldA.Machines[0];
        Assert.True(smeltIron.GetOutputCount(plate) > 0 || smeltIron.GetInputCount(dbA.GetId("iron_ore")) < 5000);

        // And that the storage added above is genuinely part of the run rather
        // than three accumulators sitting at zero for 10,000 ticks.
        Assert.True(worldA.StoredEnergy > 0,
                    "the accumulators never charged, so storage is not being covered here");
        Assert.True(worldA.StoredEnergy < worldA.StorageCapacity,
                    "the bank simply filled and sat there; the discharge path is untested here");
    }
}
