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

        return world;
    }

    private static string Snapshot(World world, ItemDatabase db, IEnumerable<ItemId> itemsOfInterest)
    {
        var sb = new StringBuilder();
        sb.Append(world.TickCount).Append(';');
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
    }
}
