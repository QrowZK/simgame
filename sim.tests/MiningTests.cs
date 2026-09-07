using Sim;

namespace Sim.Tests;

/// Ore leaving the ground and entering the item graph. Before this, worldgen
/// was scenery: it described patches nothing could consume.
public class MiningTests
{
    private static (World World, ItemId Ore, OrePatch Patch) WorldOnOre(int seed = 7)
    {
        var db = new ItemDatabase();
        var ore = db.Register("magnetite");
        var specs = new List<OreSpec> { new(ore, minRing: 0, patchRadius: 8, baseAmount: 500) };
        var gen = new WorldGen(seed, specs);
        var world = new World(seed, db, gen);

        // Mine at a real patch rather than at the origin, which may be bare.
        var patch = gen.PatchesInRegion(0, 0).First();
        return (world, ore, patch);
    }

    [Fact]
    public void HandMining_TakesOreFromTheGroundIntoYourHands()
    {
        var (world, ore, patch) = WorldOnOre();
        var before = world.Ground.RemainingAt(patch.X, patch.Y);

        var taken = HandOps.Mine(world.Ground, patch.X, patch.Y, world.PlayerInventory, 10);

        Assert.Equal(10, taken);
        Assert.Equal(10, world.PlayerInventory.Count(ore));
        Assert.Equal(before - 10, world.Ground.RemainingAt(patch.X, patch.Y));
    }

    [Fact]
    public void MiningBareGround_YieldsNothingRatherThanFreeOre()
    {
        var (world, _, _) = WorldOnOre();

        // Far enough out to be outside any patch this seed placed nearby.
        var empty = 0;
        for (var x = 100_000; x < 100_050; x++)
            empty += HandOps.Mine(world.Ground, x, 100_000, world.PlayerInventory, 5);

        Assert.Equal(0, empty);
    }

    [Fact]
    public void APatchRunsOut_AndStaysOut()
    {
        var (world, _, patch) = WorldOnOre();
        var total = world.Ground.RemainingAt(patch.X, patch.Y);

        var first = HandOps.Mine(world.Ground, patch.X, patch.Y, world.PlayerInventory, total + 500);
        var second = HandOps.Mine(world.Ground, patch.X, patch.Y, world.PlayerInventory, 10);

        // A short read, not the amount asked for: the patch had a bottom.
        Assert.Equal(total, first);
        Assert.Equal(0, second);
        Assert.Equal(0, world.Ground.RemainingAt(patch.X, patch.Y));
    }

    [Fact]
    public void AMinerExtractsOverTime_AndDepletesTheGround()
    {
        var (world, ore, patch) = WorldOnOre();
        var before = world.Ground.RemainingAt(patch.X, patch.Y);

        var miner = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1),
                                        cycleTicks: 10)!;
        Assert.Equal(ore, miner.Item);

        world.Tick(10);
        Assert.Equal(1, miner.Buffered);
        Assert.Equal(before - 1, world.Ground.RemainingAt(patch.X, patch.Y));

        world.Tick(90);
        Assert.Equal(10, miner.Buffered);
    }

    [Fact]
    public void ABiggerMinerIsProportionallyFaster()
    {
        // The same size-is-effect rule the machines use, so a player learns it
        // once rather than per building type.
        var (world, _, patch) = WorldOnOre();

        var small = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), 10)!;
        var big = world.TryPlaceMiner(new MachinePlacement(patch.X + 4, patch.Y, 0, 0, 3), 10)!;

        world.Tick(10);

        Assert.Equal(1, small.Buffered);
        Assert.Equal(9, big.Buffered);
    }

    [Fact]
    public void AMinerOnBareGround_IsRefusedRatherThanBuiltUseless()
    {
        var (world, _, _) = WorldOnOre();
        Assert.Null(world.TryPlaceMiner(new MachinePlacement(100_000, 100_000, 0, 0, 1)));
    }

    [Fact]
    public void AMinerReportsDepleted_WhenItsPatchIsFinished()
    {
        // Distinct from Starved: no belt will ever fix this, so the panel has
        // to be able to say something different.
        var (world, _, patch) = WorldOnOre();
        var miner = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), 1)!;

        var total = world.Ground.RemainingAt(patch.X, patch.Y);
        HandOps.Mine(world.Ground, patch.X, patch.Y, world.PlayerInventory, total);

        world.Tick(5);
        Assert.Equal(MachineState.Depleted, miner.State);
        Assert.Equal(0, miner.Buffered);
    }

    [Fact]
    public void AFullMinerStopsDigging_RatherThanVoidingOre()
    {
        // Backpressure has to reach the ground, or a miner with nowhere to put
        // its output quietly destroys the patch.
        var (world, _, patch) = WorldOnOre();
        var miner = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), 1)!;
        var before = world.Ground.RemainingAt(patch.X, patch.Y);

        world.Tick(5000);

        Assert.Equal(MachineState.Blocked, miner.State);
        Assert.Equal(miner.OutputCapacity, miner.Buffered);
        Assert.Equal(before - miner.OutputCapacity, world.Ground.RemainingAt(patch.X, patch.Y));
    }

    [Fact]
    public void OreReachesAMachineOverABelt()
    {
        // The whole point: ground -> miner -> inserter -> machine, with no hand
        // in the loop. This is what "connected to the machine graph" means.
        var (world, ore, patch) = WorldOnOre();
        var dust = world.Items.Register("magnetite_dust");

        var miner = world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 0, 0, 1), 10)!;

        var crusher = world.TryPlaceMachine(
            new Recipe("macerate", 20,
                       new[] { new RecipeInput(ore, 1) },
                       new[] { new RecipeOutput(dust, 2) }),
            new MachinePlacement(patch.X + 6, patch.Y, 0, 0, 1))!;

        world.Belts.AddInserter(Endpoint.Miner(0), Endpoint.Machine(0), swingTicks: 5, stackSize: 1);

        world.Tick(400);

        Assert.True(crusher.GetOutputCount(dust) > 0,
                    "no dust was produced, so ore never made it from the ground to the machine");
        Assert.True(world.Ground.RemainingAt(patch.X, patch.Y) < patch.Amount,
                    "the patch was never drawn down");
    }
}

/// A new game has to be playable from nothing: no factory, a starter kit, and
/// ore you can reach.
public class NewGameTests
{
    private static readonly Sim.Data.Catalogue Catalogue = Sim.Data.Catalogue.Instance;

    [Fact]
    public void ANewGameStartsWithNoFactory()
    {
        var world = NewGame.Create(seed: 4242, Catalogue);

        Assert.Equal(0, world.MachineCount);
        Assert.Empty(world.Miners);
        Assert.Empty(world.Belts.Segments);
    }

    [Fact]
    public void TheStarterKit_IsEnoughToBeginAndNoMore()
    {
        var world = NewGame.Create(seed: 4242, Catalogue);
        var carried = world.PlayerInventory.Contents;

        Assert.NotEmpty(carried);

        // The survey device and your hands, so the first decision is where to
        // walk rather than what to click.
        Assert.True(world.PlayerInventory.Count(Catalogue.Item("man_prospector")) > 0);
        Assert.True(world.PlayerInventory.Count(Catalogue.Item("man_manual_crafting")) > 0);

        // Nothing that automates anything.
        foreach (var (item, _) in carried)
        {
            var name = world.Items.GetName(item);
            Assert.DoesNotContain("miner", name);
            Assert.DoesNotContain("belt", name);
            Assert.DoesNotContain("inserter", name);
        }
    }

    [Fact]
    public void EveryStart_HasMineableOreWithinAShortWalk()
    {
        // If a seed can strand you, the starter kit is not a start.
        for (var seed = 1; seed <= 20; seed++)
        {
            var world = NewGame.Create(seed, Catalogue);
            var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, NewGame.SpawnX, NewGame.SpawnY);

            Assert.True(hits.Count > 0, $"seed {seed}: nothing to mine within 400 tiles of spawn");

            var nearest = hits[0];
            Assert.True(world.Ground.RemainingAt(nearest.X, nearest.Y) > 0,
                        $"seed {seed}: the nearest patch holds nothing");
        }
    }

    [Fact]
    public void TheFirstHour_CanBePlayedByHand()
    {
        // Walk to the nearest patch, dig, and have something the recipe graph
        // actually consumes. This is the bootstrap the whole flow depends on.
        var world = NewGame.Create(seed: 99, Catalogue);
        var hit = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0).First();

        var taken = HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory, 50);
        Assert.Equal(50, taken);

        var mined = world.Items.GetName(hit.Item);
        var consumed = Catalogue.Data.Recipes.Any(r => r.Inputs.Any(i => i.Item == mined));
        Assert.True(consumed, $"'{mined}' is minable but no recipe consumes it");
    }
}
