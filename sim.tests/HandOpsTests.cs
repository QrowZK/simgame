using Sim;

namespace Sim.Tests;

/// The early game is a player standing at a machine with things in their hands.
/// These are the operations that has to be made of, and they are in the sim
/// because they change world state.
public class HandOpsTests
{
    private static (World World, Machine Machine, ItemId Ore, ItemId Plate) Setup(byte size = 1)
    {
        var db = new ItemDatabase();
        var ore = db.Register("ore");
        var plate = db.Register("plate");
        var recipe = new Recipe("smelt", 10,
                                new[] { new RecipeInput(ore, 2) },
                                new[] { new RecipeOutput(plate, 1) });

        var world = new World(1);
        var machine = world.TryPlaceMachine(recipe, new MachinePlacement(0, 0, 0, 0, size))!;
        return (world, machine, ore, plate);
    }

    [Fact]
    public void HandLoadingAMachine_MovesItemsAndReportsHowMany()
    {
        var (world, machine, ore, plate) = Setup();
        var bag = new Inventory();
        bag.Add(ore, 5);

        Assert.Equal(5, HandOps.Insert(bag, machine, ore, 10));
        Assert.Equal(0, bag.Count(ore));
        Assert.Equal(5, machine.GetInputCount(ore));

        world.Tick(10);
        Assert.Equal(1, machine.GetOutputCount(plate));
    }

    [Fact]
    public void AMachineRefusesItemsItsRecipeCannotUse()
    {
        // Otherwise a mistyped hand load buries an item somewhere it can never
        // be retrieved from.
        var (_, machine, _, plate) = Setup();
        var bag = new Inventory();
        bag.Add(plate, 4);

        Assert.Equal(0, HandOps.Insert(bag, machine, plate, 4));
        Assert.Equal(4, bag.Count(plate));
    }

    [Fact]
    public void EmptyingAMachineByHand_ReturnsEverythingItMade()
    {
        var (world, machine, ore, plate) = Setup();
        machine.PushInput(ore, 100);
        world.Tick(50);

        var bag = new Inventory();
        var taken = HandOps.ExtractAll(machine, bag);

        Assert.Equal(5, taken);
        Assert.Equal(5, bag.Count(plate));
        Assert.Equal(0, machine.GetOutputCount(plate));
    }

    [Fact]
    public void TakingMoreThanIsThere_TakesWhatIsThere()
    {
        var bag = new Inventory();
        bag.Add(new ItemId(0), 3);
        Assert.Equal(3, bag.Take(new ItemId(0), 99));
        Assert.Equal(0, bag.Count(new ItemId(0)));
    }

    [Fact]
    public void ThePanelCanSeeProgressAndContents()
    {
        // Everything a GUI needs to show "is this doing the right thing".
        var (world, machine, ore, plate) = Setup();
        machine.PushInput(ore, 10);

        Assert.Equal(0f, machine.Progress);
        world.Tick(5);

        Assert.Equal(MachineState.Working, machine.State);
        Assert.InRange(machine.Progress, 0.4f, 0.6f);
        Assert.Equal(5, machine.TicksRemaining);
        Assert.Equal(8, machine.InputContents[ore]);

        world.Tick(5);
        Assert.Equal(1, machine.OutputContents[plate]);
    }

    [Fact]
    public void ThePanelReportsTheBatchSize_NotTheRecipeCard()
    {
        // A 2x2 consumes four times the recipe's listed amount. Showing the
        // recipe's own numbers on a parallel machine would make the panel lie
        // about the machine it is attached to.
        var (_, machine, ore, plate) = Setup(size: 2);
        Assert.Equal(8, machine.InputPerCycle(ore));
        Assert.Equal(4, machine.OutputPerCycle(plate));
    }

    [Fact]
    public void ASnapshotOfContents_CannotBeUsedToMutateTheMachine()
    {
        var (_, machine, ore, _) = Setup();
        machine.PushInput(ore, 7);

        var snapshot = machine.InputContents;
        machine.PullOutput(ore, 0);
        machine.PushInput(ore, 3);

        Assert.Equal(7, snapshot[ore]);          // the snapshot did not follow
        Assert.Equal(10, machine.GetInputCount(ore));
    }
}
