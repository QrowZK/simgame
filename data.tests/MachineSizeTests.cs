using Sim;

namespace Data.Tests;

/// Machine size is not decoration. A machine's footprint IS its throughput
/// multiplier and its build cost multiplier, so these tests hold the three to
/// each other: a designer cannot make something big and weak, or small and
/// overwhelming, without one of them failing.
public class MachineSizeTests
{
    private static readonly GameData Data = GameData.Instance;

    [Fact]
    public void EveryMachine_HasAWholeSquareFootprint()
    {
        foreach (var machine in Data.Machines)
            Assert.True(machine.Size is >= 1 and <= 5,
                $"{machine.Id} has an implausible footprint of {machine.Size}");
    }

    [Fact]
    public void EffectIsExactlyFootprint()
    {
        // The whole point: there is no separate speed dial to fall out of step
        // with the size the player sees on the ground.
        foreach (var machine in Data.Machines)
            Assert.Equal(machine.Size * machine.Size, machine.Parallelism);
    }

    [Fact]
    public void BiggerMachines_CostProportionallyMoreToBuild()
    {
        // Without this, footprint is free throughput and every plant is 3x3.
        var byId = Data.Machines.ToDictionary(m => m.Id);

        foreach (var recipe in Data.Recipes.Where(r => r.Id.StartsWith("build_")))
        {
            // A machine item is exactly "{tier}_{machine}", so strip the tier
            // rather than suffix-matching: "arc_arc_furnace" ends with
            // "_furnace" too, and matching that would silently test the wrong
            // machine's size.
            var built = recipe.Outputs.Single().Item;
            var underscore = built.IndexOf('_');
            if (underscore < 0) continue;
            if (!byId.TryGetValue(built[(underscore + 1)..], out var machine)) continue;
            if (machine.Size == 1) continue;
            // A bootstrap Manual-tier machine is piled-up stone, not a kit.
            if (recipe.Tier == "MAN") continue;

            var hull = recipe.Inputs.FirstOrDefault(i => i.Item.EndsWith("_machine_hull"));
            if (hull is null) continue;

            Assert.Equal(machine.Size * machine.Size, hull.Count);
        }
    }

    [Fact]
    public void TheBulkProcessUnits_AreTheLargeOnes()
    {
        // Sanity on the design intent itself: the things that are physically
        // enormous in a real plant should be the things that are large here.
        var byId = Data.Machines.ToDictionary(m => m.Id, m => m.Size);

        foreach (var bulk in new[] { "vacuum_distillation", "steam_cracker", "fcc_unit",
                                     "arc_furnace", "fusion_reactor", "coker", "gasifier" })
            Assert.True(byId[bulk] >= 3, $"{bulk} should be a bulk unit, it is {byId[bulk]}x{byId[bulk]}");

        foreach (var bench in new[] { "transport_belt", "inserter", "splitter", "pipe", "lathe" })
            Assert.Equal(1, byId[bench]);
    }

    [Fact]
    public void AParallelMachine_ActuallyProducesItsBatch()
    {
        // The data says 3x3 means nine batches; the sim has to agree.
        var db = new ItemDatabase();
        var ore = db.Register("ore");
        var plate = db.Register("plate");
        var recipe = new Recipe("smelt", 10,
                                new[] { new RecipeInput(ore, 2) },
                                new[] { new RecipeOutput(plate, 1) });

        var world = new World(1);
        var small = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, 1))!;
        var large = world.TryPlaceMachine(recipe, new MachinePlacement(10, 10, 0, 0, 3))!;

        Assert.Equal(1, small.Parallelism);
        Assert.Equal(9, large.Parallelism);

        small.PushInput(ore, 1000);
        large.PushInput(ore, 1000);
        world.Tick(10);

        Assert.Equal(1, small.GetOutputCount(plate));
        Assert.Equal(9, large.GetOutputCount(plate));
        // ...and it ate nine times the feed to do it.
        Assert.Equal(1000 - 2, small.GetInputCount(ore));
        Assert.Equal(1000 - 18, large.GetInputCount(ore));
    }
}
