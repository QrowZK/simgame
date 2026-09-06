using Sim;

namespace Sim.Tests;

public class MachineTests
{
    private static (ItemDatabase db, Recipe recipe) MakeSmeltRecipe()
    {
        var db = new ItemDatabase();
        var ore = db.Register("iron_ore");
        var plate = db.Register("iron_plate");
        var recipe = new Recipe(
            "smelt_iron_plate",
            durationTicks: 10,
            inputs: new[] { new RecipeInput(ore, 1) },
            outputs: new[] { new RecipeOutput(plate, 1) });
        return (db, recipe);
    }

    [Fact]
    public void Throughput_ProducesExactExpectedOutputAfterKnownTicks()
    {
        var (db, recipe) = MakeSmeltRecipe();
        var ore = db.GetId("iron_ore");
        var plate = db.GetId("iron_plate");

        var machine = new Machine(recipe);
        machine.PushInput(ore, 100);

        // Each cycle takes exactly duration_ticks (10) ticks. Running 50 ticks completes
        // exactly 5 full cycles, with the 6th not yet started.
        for (var i = 0; i < 50; i++)
            machine.Tick();

        Assert.Equal(5, machine.GetOutputCount(plate));
        Assert.Equal(95, machine.GetInputCount(ore));
    }

    [Fact]
    public void Starvation_NoInputMeansNoOutputAndNoCrash()
    {
        var (db, recipe) = MakeSmeltRecipe();
        var plate = db.GetId("iron_plate");

        var machine = new Machine(recipe);

        for (var i = 0; i < 50; i++)
            machine.Tick();

        Assert.Equal(MachineState.Starved, machine.State);
        Assert.Equal(0, machine.GetOutputCount(plate));
    }

    [Fact]
    public void Backpressure_FullOutputStallsMachineWithoutConsumingInputs()
    {
        var (db, recipe) = MakeSmeltRecipe();
        var ore = db.GetId("iron_ore");
        var plate = db.GetId("iron_plate");

        var machine = new Machine(recipe, outputCapacityPerItem: 1);
        machine.PushInput(ore, 10);

        // First cycle completes and fills the output buffer to capacity (1).
        for (var i = 0; i < 10; i++)
            machine.Tick();
        Assert.Equal(1, machine.GetOutputCount(plate));

        var inputBeforeStall = machine.GetInputCount(ore);

        // Further ticks must not start a new cycle: output is full.
        for (var i = 0; i < 20; i++)
            machine.Tick();

        Assert.Equal(MachineState.Blocked, machine.State);
        Assert.Equal(1, machine.GetOutputCount(plate));
        Assert.Equal(inputBeforeStall, machine.GetInputCount(ore));

        // Draining the output buffer lets the machine resume.
        machine.PullOutput(plate, 1);
        for (var i = 0; i < 10; i++)
            machine.Tick();

        Assert.Equal(1, machine.GetOutputCount(plate));
        Assert.Equal(inputBeforeStall - 1, machine.GetInputCount(ore));
    }
}
