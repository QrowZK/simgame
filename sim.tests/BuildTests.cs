using Sim;
using Sim.Data;

namespace Sim.Tests;

/// Building from the player's inventory.
///
/// The properties worth holding: a build costs exactly one item, a refused
/// build costs nothing at all, and every refusal says which of the several
/// quite different problems it hit.
public class BuildTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World NewWorld() => NewGame.Create(seed: 4242, Data);

    private static Buildable Get(string itemId)
    {
        var buildable = Buildables.Find(itemId);
        Assert.NotNull(buildable);
        return buildable!;
    }

    private static void Give(World world, string itemId, int count = 1)
        => world.PlayerInventory.Add(Data.Item(itemId), count);

    /// A tile with nothing on it and nothing under it, well away from the
    /// starting area's ore and water.
    private static (int X, int Y) BareTile(World world)
    {
        for (var r = 0; r < 400; r++)
            for (var d = -r; d <= r; d++)
            {
                var (x, y) = (d, r);
                if (!world.Ground.TryResourceAt(x, y, out _, out _) &&
                    !world.Ground.Gen.IsWater(x, y) &&
                    world.CanPlace(new MachinePlacement(x, y, 0, 0, 3)))
                    return (x, y);
            }

        throw new InvalidOperationException("no bare tile found");
    }

    [Fact]
    public void BuildingAMachine_PlacesItAndCostsExactlyOne()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_furnace", 3);

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x, y, recipe));

        Assert.Equal(1, world.MachineCount);
        Assert.Equal(2, world.PlayerInventory.Count(furnace.Item));
        Assert.True(world.TryMachineAt(x, y, out var placed, out _));
        Assert.Equal(recipe.Id, placed.Recipe.Id);
    }

    [Fact]
    public void ARefusedBuild_CostsNothing()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_furnace", 2);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x, y, recipe));

        // Same tile again: blocked, and the second furnace must still be in
        // the player's pocket. A build that charges for a refusal is the
        // cruellest bug this code could have.
        Assert.Equal(BuildResult.Blocked, world.TryBuild(Buildables, furnace.Item, x, y, recipe));
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
        Assert.Equal(1, world.MachineCount);
    }

    [Fact]
    public void BuildingWithNoneCarried_IsRefused()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Assert.Equal(BuildResult.NoneCarried,
                     world.TryBuild(Buildables, furnace.Item, x, y, recipe));
        Assert.Equal(0, world.MachineCount);
    }

    [Fact]
    public void AMachineWithoutARecipe_IsRefused()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var (x, y) = BareTile(world);
        Give(world, "stm_furnace");

        Assert.Equal(BuildResult.NeedsRecipe,
                     world.TryBuild(Buildables, furnace.Item, x, y));
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
    }

    /// A furnace told to run an assembler's recipe. Refused, or the machine
    /// grid stops meaning anything: every machine could make everything.
    [Fact]
    public void AMachineToldToRunAnotherMachinesRecipe_IsRefused()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var assembler = Get("vlt_assembler");
        var foreign = Buildables.RecipesFor(assembler)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_furnace");
        Assert.Equal(BuildResult.NeedsRecipe,
                     world.TryBuild(Buildables, furnace.Item, x, y, foreign));
        Assert.Equal(0, world.MachineCount);
    }

    [Fact]
    public void AnItemThatIsNotAMachine_IsNotBuildable()
    {
        var world = NewWorld();
        var stone = Data.Item("stone_deposit");
        var (x, y) = BareTile(world);

        Give(world, "stone_deposit", 50);
        Assert.Equal(BuildResult.NotBuildable, world.TryBuild(Buildables, stone, x, y));
    }

    /// Belts are in the data and craftable, but nothing routes them yet. The
    /// player is told that, rather than the button quietly doing nothing.
    [Fact]
    public void ABelt_ReportsThatNothingPlacesItYet()
    {
        var world = NewWorld();
        var belt = Get("stm_transport_belt");
        var (x, y) = BareTile(world);

        Give(world, "stm_transport_belt", 5);
        Assert.Equal(BuildKind.NotPlaceable, belt.Kind);
        Assert.Equal(BuildResult.NotPlaceableYet,
                     world.TryBuild(Buildables, belt.Item, x, y));
        Assert.Equal(5, world.PlayerInventory.Count(belt.Item));
    }

    [Fact]
    public void AMinerOnBareRock_IsRefused_AndOnOreIsBuilt()
    {
        var world = NewWorld();
        var miner = Get("stm_miner");
        var (bareX, bareY) = BareTile(world);

        Give(world, "stm_miner", 2);
        Assert.Equal(BuildResult.NoResource,
                     world.TryBuild(Buildables, miner.Item, bareX, bareY));
        Assert.Equal(2, world.PlayerInventory.Count(miner.Item));

        var (oreX, oreY) = FindOre(world);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, miner.Item, oreX, oreY));
        Assert.Single(world.Miners);
        Assert.Equal(1, world.PlayerInventory.Count(miner.Item));
    }

    [Fact]
    public void APoleJoinsTheGrid_AndItsReachComesFromItsTier()
    {
        var world = NewWorld();
        var steam = Get("stm_pole");
        var arc = Get("arc_pole");
        var (x, y) = BareTile(world);

        Give(world, "stm_pole");
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, steam.Item, x, y));

        Assert.Single(world.Power.Poles);
        Assert.Equal(steam.PoleSupplyRadius, world.Power.Poles[0].SupplyRadius);

        // The upgrade a player actually feels: the same floor, fewer poles.
        Assert.True(arc.PoleSupplyRadius > steam.PoleSupplyRadius,
                    "a higher-tier pole should reach further");
    }

    /// A pole holds its tile like anything else. Poles live in the power grid
    /// rather than the machine array, so nothing else would stop a furnace
    /// being dropped straight on top of one.
    [Fact]
    public void APoleHoldsItsTileAgainstEverythingElse()
    {
        var world = NewWorld();
        var pole = Get("stm_pole");
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_pole", 2);
        Give(world, "stm_furnace");

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pole.Item, x, y));
        Assert.Equal(BuildResult.Blocked, world.TryBuild(Buildables, pole.Item, x, y));
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, furnace.Item, x, y, recipe));

        Assert.Single(world.Power.Poles);
        Assert.Equal(0, world.MachineCount);
        Assert.Equal(1, world.PlayerInventory.Count(pole.Item));
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
    }

    /// The whole point of building a generator and an accumulator: a machine
    /// that was dark runs, and the surplus is stored. Built entirely through
    /// the build path, with no hand-placed entity anywhere.
    [Fact]
    public void AGridBuiltEntirelyByHand_PowersAMachineAndStoresItsSurplus()
    {
        var world = NewWorld();
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace).First(r => r.PowerDraw > 0);
        var (x, y) = BareTile(world);

        Give(world, "stm_furnace");
        Give(world, "stm_pole");
        // Two generators, so there is a genuine surplus for the accumulator to
        // take. With exactly enough generation, storage correctly stores
        // nothing -- charging never competes with production.
        Give(world, "stm_generator", 2);
        Give(world, "stm_accumulator");

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x, y, recipe));
        var machine = world.Machines[0];
        foreach (var input in recipe.Inputs)
            machine.PushInput(input.Item, 1000);

        // Unpowered first, so the generator is demonstrably what starts it.
        world.Tick(120);
        Assert.Equal(MachineState.Unpowered, machine.State);

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, Get("stm_pole").Item, x + 1, y));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, Get("stm_generator").Item, x + 2, y));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, Get("stm_generator").Item, x + 3, y));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, Get("stm_accumulator").Item, x + 4, y));

        foreach (var generator in world.Power.Generators) generator.AddFuel(100);
        world.Tick(600);

        Assert.Equal(MachineState.Working, machine.State);
        Assert.True(world.StoredEnergy > 0, "the built accumulator never charged");
    }

    /// Two systems, one tile. Machines and pipes are tracked separately and
    /// neither refuses the other, so the build path is what keeps a player
    /// from stacking them.
    [Fact]
    public void APipeAndAMachine_CannotShareATile()
    {
        var world = NewWorld();
        var pipe = Get("stm_pipe");
        var furnace = Get("stm_furnace");
        var recipe = Buildables.RecipesFor(furnace)[0];
        var (x, y) = BareTile(world);

        Give(world, "stm_pipe", 2);
        Give(world, "stm_furnace");

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, pipe.Item, x, y));
        Assert.Equal(BuildResult.Blocked, world.TryBuild(Buildables, pipe.Item, x, y));
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, furnace.Item, x, y, recipe));

        Assert.Equal(1, world.PlayerInventory.Count(pipe.Item));
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
    }

    /// A 3x3 blocks all nine of its tiles, not just its corner.
    [Fact]
    public void ABigMachineBlocksItsWholeFootprint()
    {
        var world = NewWorld();
        var big = Buildables.All.FirstOrDefault(b => b.Kind == BuildKind.Machine && b.Size > 1);
        Assert.NotNull(big);

        var recipe = Buildables.RecipesFor(big!)[0];
        var pole = Get("stm_pole");
        var (x, y) = BareTile(world);

        world.PlayerInventory.Add(big!.Item, 1);
        Give(world, "stm_pole", 1);

        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, big.Item, x, y, recipe));

        // The far corner of the footprint, which a corner-only occupancy check
        // would happily let a pole sit on top of.
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, pole.Item, x + big.Size - 1, y + big.Size - 1));
        Assert.Equal(1, world.PlayerInventory.Count(pole.Item));
    }

    /// Every buildable resolves to the machine whose id it carries. The tier
    /// prefix has to be stripped, not matched as a suffix: `arc_arc_furnace`
    /// ends with `furnace` too, and suffix matching sent it to the wrong one.
    [Fact]
    public void EveryBuildableResolvesToItsOwnMachine()
    {
        foreach (var buildable in Buildables.All)
            Assert.Equal($"{buildable.TierId.ToLowerInvariant()}_{buildable.MachineId}",
                         buildable.ItemId);

        var confusable = Get("arc_arc_furnace");
        Assert.Equal("arc_furnace", confusable.MachineId);
    }

    /// Every machine item in the data is offered as a buildable. A machine that
    /// silently never appears is a machine the player can craft and never use.
    [Fact]
    public void EveryMachineItemInTheDataIsBuildable()
    {
        var missing = Data.Data.Items
            .Where(i => i.Category == "machine")
            .Where(i => !Buildables.All.Any(b => b.ItemId == i.Id))
            .Select(i => i.Id)
            .ToList();

        Assert.True(missing.Count == 0,
                    "machine items with no buildable: " + string.Join(", ", missing));
    }

    /// Nothing offered to the player can be a machine that could never run.
    ///
    /// This is why `Offerable` exists rather than the UI listing `All`: the
    /// data has machines with no recipes at all, and a build menu that hands
    /// you one is a trap.
    [Fact]
    public void NothingOfferedIsAMachineThatCouldNeverRun()
    {
        var dead = Buildables.Offerable
            .Where(b => b.Kind == BuildKind.Machine)
            .Where(b => Buildables.RecipesFor(b).Count == 0)
            .Select(b => b.ItemId)
            .ToList();

        Assert.True(dead.Count == 0,
                    "offered machines with no recipe: " + string.Join(", ", dead));
        Assert.NotEmpty(Buildables.Offerable);
    }

    /// And that filter has to be doing real work. If every buildable were
    /// offerable the test above would pass while proving nothing, so this
    /// pins the gap it is actually covering: machines the data never gave a
    /// recipe to. If someone later authors those recipes, this fails and
    /// should simply be deleted.
    [Fact]
    public void TheOfferableFilterExcludesTheMachinesWithNoRecipes()
    {
        var excluded = Buildables.All
            .Where(b => b.Kind == BuildKind.Machine)
            .Where(b => !Buildables.Offerable.Contains(b))
            .Select(b => b.MachineId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(new[] { "cutter", "mixer", "sifter", "spinneret", "vacuum_freezer" },
                     excluded);
    }

    /// Building one anyway is refused rather than producing a dead machine:
    /// there is no recipe to pass, so it cannot get past the recipe check.
    [Fact]
    public void AMachineWithNoRecipesCannotBeBuiltAtAll()
    {
        var world = NewWorld();
        var sifter = Get("stm_sifter");
        var (x, y) = BareTile(world);

        Give(world, "stm_sifter");
        Assert.Empty(Buildables.RecipesFor(sifter));
        Assert.Equal(BuildResult.NeedsRecipe,
                     world.TryBuild(Buildables, sifter.Item, x, y));
        Assert.Equal(1, world.PlayerInventory.Count(sifter.Item));
    }

    private static (int X, int Y) FindOre(World world)
    {
        for (var r = 0; r < 400; r++)
            for (var d = -r; d <= r; d++)
                foreach (var (x, y) in new[] { (d, r), (d, -r), (r, d), (-r, d) })
                    if (world.Ground.TryResourceAt(x, y, out _, out var amount) && amount > 0 &&
                        world.CanPlace(new MachinePlacement(x, y, 0, 0, 1)))
                        return (x, y);

        throw new InvalidOperationException("no ore found");
    }
}
