using Sim;

namespace Sim.Tests;

/// A machine that claims nine tiles has to actually hold them, or "size scales
/// with effect" is a label rather than a cost.
public class FootprintTests
{
    private static Recipe Trivial()
    {
        var db = new ItemDatabase();
        return new Recipe("noop", 5,
                          new[] { new RecipeInput(db.Register("in"), 1) },
                          new[] { new RecipeOutput(db.Register("out"), 1) });
    }

    [Fact]
    public void APlacedMachine_OccupiesEveryTileOfItsFootprint()
    {
        var world = new World(1);
        world.TryPlaceMachine(Trivial(), new MachinePlacement(4, 4, 0, 0, 3));

        for (var y = 4; y < 7; y++)
            for (var x = 4; x < 7; x++)
                Assert.True(world.TryMachineAt(x, y, out _, out _), $"tile {x},{y} should be covered");

        Assert.False(world.TryMachineAt(7, 4, out _, out _));
        Assert.False(world.TryMachineAt(3, 4, out _, out _));
    }

    [Fact]
    public void MachinesCannotBeSlidOverEachOther()
    {
        var world = new World(1);
        Assert.NotNull(world.TryPlaceMachine(Trivial(), new MachinePlacement(0, 0, 0, 0, 3)));

        // Anchors outside the first machine, but the footprints still collide.
        Assert.Null(world.TryPlaceMachine(Trivial(), new MachinePlacement(2, 2, 0, 0, 2)));
        Assert.Null(world.TryPlaceMachine(Trivial(), new MachinePlacement(-1, -1, 0, 0, 2)));

        // Flush alongside is fine.
        Assert.NotNull(world.TryPlaceMachine(Trivial(), new MachinePlacement(3, 0, 0, 0, 2)));
    }

    [Fact]
    public void ADefaultPlacement_IsOneTileNotZero()
    {
        var placement = default(MachinePlacement);
        Assert.Equal(1, placement.Size);
        Assert.Equal(1, placement.Area);
    }

    [Fact]
    public void ParallelismComesFromTheFootprint_NotFromTheCaller()
    {
        var world = new World(1);
        var machine = world.TryPlaceMachine(Trivial(), new MachinePlacement(0, 0, 0, 0, 2))!;
        Assert.Equal(4, machine.Parallelism);
    }

    [Fact]
    public void ClickingAnyTileResolvesToTheSameMachine()
    {
        // What the inspection GUI will hang off: a 3x3 is one machine, not nine.
        var world = new World(1);
        var placed = world.TryPlaceMachine(Trivial(), new MachinePlacement(-5, 8, 2, 3, 3))!;

        for (var y = 8; y < 11; y++)
            for (var x = -5; x < -2; x++)
            {
                Assert.True(world.TryMachineAt(x, y, out var found, out var index));
                Assert.Same(placed, found);
                Assert.Equal(3, world.PlacementOf(index).Size);
            }
    }

    [Fact]
    public void EvenSidedMachines_CentreOnATileBoundary()
    {
        // The renderer needs this to seat a 2x2 mesh correctly; the occupancy
        // grid deliberately stays in integers so it never has to care.
        var even = new MachinePlacement(0, 0, 0, 0, 2);
        var odd = new MachinePlacement(0, 0, 0, 0, 3);

        Assert.Equal(1f, even.CentreX);
        Assert.Equal(1.5f, odd.CentreX);
    }
}
