using Sim;

namespace Sim.Tests;

public class PlacementTests
{
    private static Recipe SmeltRecipe(ItemDatabase db)
    {
        var ore = db.Register("iron_ore");
        var plate = db.Register("iron_plate");
        return new Recipe("smelt", 4,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) });
    }

    [Fact]
    public void Placements_StayAlignedWithMachineOrder()
    {
        var db = new ItemDatabase();
        var recipe = SmeltRecipe(db);
        var world = new World(1);

        for (var i = 0; i < 200; i++)
            world.AddMachine(recipe, new MachinePlacement(i, i * 2, (byte)(i % 8), (byte)(i % 10)));

        Assert.Equal(200, world.MachineCount);
        Assert.Equal(200, world.Placements.Length);
        Assert.Equal(200, world.MachineStates.Length);

        // Growth past the initial capacity must not reorder or drop entries.
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(i, world.Placements[i].X);
            Assert.Equal(i * 2, world.Placements[i].Y);
            Assert.Equal((byte)(i % 8), world.Placements[i].Tier);
            Assert.Equal((byte)(i % 10), world.Placements[i].Category);
        }
    }

    [Fact]
    public void MachineStates_MirrorTheMachinesAfterEachTick()
    {
        var db = new ItemDatabase();
        var recipe = SmeltRecipe(db);
        var ore = db.GetId("iron_ore");
        var world = new World(1);

        var fed = world.AddMachine(recipe, new MachinePlacement(0, 0, 0, 0));
        var starved = world.AddMachine(recipe, new MachinePlacement(1, 0, 0, 0));
        var blocked = world.AddMachine(recipe, new MachinePlacement(2, 0, 0, 0), outputCapacityPerItem: 1);

        fed.PushInput(ore, 100);
        blocked.PushInput(ore, 100);

        world.Tick(40);

        Assert.Equal(fed.State, world.MachineStates[0]);
        Assert.Equal(starved.State, world.MachineStates[1]);
        Assert.Equal(blocked.State, world.MachineStates[2]);

        // The renderer colours by these, so the three states must be distinguishable.
        Assert.Equal(MachineState.Starved, world.MachineStates[1]);
        Assert.Equal(MachineState.Blocked, world.MachineStates[2]);
    }

    [Fact]
    public void AddMachine_WithoutPlacement_StillWorksForHeadlessSimUse()
    {
        var db = new ItemDatabase();
        var recipe = SmeltRecipe(db);
        var world = new World(1);

        world.AddMachine(recipe);

        Assert.Equal(1, world.MachineCount);
        Assert.Equal(0, world.Placements[0].X);
        Assert.Equal(0, world.Placements[0].Y);
    }
}
