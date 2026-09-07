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

    /// A new game with the whole tech tree already open.
    ///
    /// Recipes are gated on research (ADR 0023), and these tests hand
    /// themselves a Steam furnace rather than earning one -- so they have to
    /// hand themselves the research that goes with it, or they would be
    /// asserting the gate rather than the build path. `OpeningRouteTests` is
    /// where the gate is walked properly, from an ungated Manual tier up.
    private static World NewWorld()
    {
        var world = NewGame.Create(seed: 4242, Data);
        world.Research!.UnlockAll();
        return world;
    }

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

    /// A splitter is one tile with a facing, and costs the item like anything
    /// else. ADR 0018.
    [Fact]
    public void ASplitter_IsBuiltAndCostsTheItem()
    {
        var world = NewWorld();
        var splitter = Get("vlt_splitter");
        var (x, y) = BareTile(world);

        Give(world, "vlt_splitter", 5);
        Assert.Equal(BuildKind.Splitter, splitter.Kind);
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, splitter.Item, x, y, facing: Direction.East));
        Assert.Equal(4, world.PlayerInventory.Count(splitter.Item));

        // And it is a thing on the map, so nothing else can go on top of it.
        Assert.Equal(BuildResult.Blocked,
                     world.TryBuild(Buildables, splitter.Item, x, y, facing: Direction.East));
        Assert.Equal(4, world.PlayerInventory.Count(splitter.Item));
    }

    /// The distinct refusal an underground belt has of its own: an end placed
    /// in line with an unpaired entrance, facing the same way, but beyond what
    /// the tier can tunnel. A silent second entrance there would leave the
    /// player with a broken line and no idea why.
    [Fact]
    public void AnUndergroundEndBeyondTheSpan_IsRefused_AndCostsNothing()
    {
        var world = NewWorld();
        var under = Get("vlt_underground_belt");
        var (x, y) = BareTile(world);

        Give(world, "vlt_underground_belt", 4);
        Assert.Equal(BuildKind.UndergroundBelt, under.Kind);
        Assert.Equal(4, under.UndergroundReach);

        // The upgrade is the span, so it has to actually move up the ladder.
        Assert.Equal(6, Get("arc_underground_belt").UndergroundReach);
        Assert.Equal(12, Get("qnt_underground_belt").UndergroundReach);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, under.Item, x, y, facing: Direction.East));

        // One past the reach: refused, and the item stays in the bag.
        Assert.Equal(BuildResult.TooFarToTunnel,
                     world.TryBuild(Buildables, under.Item, x + 5, y, facing: Direction.East));
        Assert.Equal(3, world.PlayerInventory.Count(under.Item));

        // Exactly at the reach: the pair completes.
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, under.Item, x + 4, y, facing: Direction.East));
        Assert.Equal(2, world.PlayerInventory.Count(under.Item));

        world.SyncBelts();
        Assert.True(world.BeltMap.Undergrounds[0].IsEntrance);
        Assert.False(world.BeltMap.Undergrounds[1].IsEntrance);
        Assert.Equal(1, world.BeltMap.PartnerOf(0));
        Assert.Equal(0, world.BeltMap.PartnerOf(1));
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

    /// The opening loop: the starter kit's bench goes down, and it can run a
    /// hand recipe. Nothing else in the kit is placeable, so if the bench is
    /// not, a new game can never build anything at all.
    [Fact]
    public void TheStarterKitsCraftingBenchCanBePlacedAndHasRecipes()
    {
        var world = NewWorld();
        var bench = Get("man_manual_crafting");
        var (x, y) = BareTile(world);

        Assert.Equal(BuildKind.Machine, bench.Kind);
        Assert.NotEmpty(Buildables.RecipesFor(bench));

        // Straight from the starter kit, with nothing added.
        Assert.Equal(1, world.PlayerInventory.Count(bench.Item));

        var recipe = Buildables.RecipesFor(bench)[0];
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, bench.Item, x, y, recipe));
        Assert.Equal(0, world.PlayerInventory.Count(bench.Item));
        Assert.Equal(1, world.MachineCount);
    }

    /// Two tiers of the same machine must not read identically. A build menu
    /// showing only the machine name put three rows of "Alloy Smelter x3" in
    /// front of a player carrying a Steam, an Arc and a Fusion one.
    [Fact]
    public void TiersOfTheSameMachineAreToldApartByName()
    {
        var steam = Get("stm_furnace");
        var arc = Get("arc_furnace");

        Assert.Equal(steam.Name, arc.Name);
        Assert.NotEqual(steam.DisplayName, arc.DisplayName);

        // And no two offerable things share a display name at all, or the menu
        // is ambiguous somewhere else instead.
        var duplicates = Buildables.Offerable
            .GroupBy(b => b.DisplayName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
                    "buildables sharing a display name: " + string.Join(", ", duplicates));
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
    /// Every placeable machine can run something.
    ///
    /// This used to be impossible to assert. The sifter, mixer, cutter and
    /// vacuum freezer were in the item list and the tech tree with nothing in
    /// recipes.json naming them, and the spinneret was offered two tiers below
    /// its only recipe; `Offerable` existed to hide them from the build menu.
    /// They have jobs now, so the filter should exclude nothing -- and if a
    /// machine is ever added without recipes again, this is what says so.
    [Fact]
    public void EveryPlaceableMachineHasSomethingItCanMake()
    {
        var dead = Buildables.All
            .Where(b => b.Kind == BuildKind.Machine)
            .Where(b => Buildables.RecipesFor(b).Count == 0)
            .Select(b => b.ItemId)
            .ToList();

        Assert.True(dead.Count == 0,
                    "machines that can be built and can never run: " + string.Join(", ", dead));

        Assert.Equal(Buildables.All.Count(b => b.Kind == BuildKind.Machine),
                     Buildables.Offerable.Count(b => b.Kind == BuildKind.Machine));
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
