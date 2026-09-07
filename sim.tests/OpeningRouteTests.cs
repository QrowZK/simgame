using System.Text.RegularExpressions;
using Sim;
using Sim.Data;

namespace Sim.Tests;

/// The opening playthrough, as a route rather than as a set of systems.
///
/// Every other suite here asks "does this mechanism work". These ask the one
/// question a new player asks: **can I get from the landing site to a working
/// factory using only what the previous step gave me?** A step that needs an
/// item no earlier step can produce is a progression blocker, and it passes
/// every unit test in the repository because each of those hands itself the
/// inputs it needs.
///
/// Written from the manual playthrough recorded in
/// `docs/0020-opening-playthrough-qa.md`. Two of these are `Skip`ped: they are
/// the reproductions of defects that are open against `gameplay`, kept in the
/// repository so the fix arrives with the test that proves it rather than
/// being rediscovered. Unskip when the board entry is closed.
public class OpeningRouteTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    /// How far an opening walk is allowed to cost, in tiles. The prospector
    /// reaches 400, so asserting "within 400" tolerates worldgen pushing every
    /// patch three rings further out and still passes -- measured, as a
    /// surviving mutation. The worst of the twenty seeds below is 245, so this
    /// is the real ceiling with headroom rather than the tool's range.
    private const int Budget = 300;

    /// The bench recipes that stand between the starter kit and the first
    /// miner, walked back from `build_stm_miner` through the recipes that make
    /// its inputs. Derived rather than listed, so adding a step to the chain
    /// makes the number move on its own.
    private static HashSet<string> RouteToTheFirstMiner()
    {
        var byOutput = new Dictionary<string, RecipeDef>();
        foreach (var recipe in Data.Data.Recipes.Where(r => r.Machine == "manual_crafting"))
            foreach (var output in recipe.Outputs)
                byOutput.TryAdd(output.Item, recipe);

        var needed = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue("stm_miner");

        while (queue.Count > 0)
        {
            if (!byOutput.TryGetValue(queue.Dequeue(), out var recipe)) continue;
            if (!needed.Add(recipe.Id)) continue;
            foreach (var input in recipe.Inputs) queue.Enqueue(input.Item);
        }

        // The furnace is on the route too: the ingots every step above needs
        // are smelted, and nothing but the bench can make a furnace.
        needed.Add("build_man_furnace");
        return needed;
    }

    /// How many crafting benches a player can ever hold: what the kit gives
    /// them, plus anything that can craft another one.
    private static int BenchesObtainable()
    {
        var kit = NewGame.StarterKit
            .Where(k => k.Item == "man_manual_crafting")
            .Sum(k => k.Count);

        var craftable = Data.Data.Recipes
            .Any(r => r.Outputs.Any(o => o.Item == "man_manual_crafting"));

        return craftable ? int.MaxValue : kit;
    }

    /// S1 -- the opening route dead-ends after one craft.
    ///
    /// A placed machine's recipe is chosen at build time and `Machine.Recipe`
    /// is readonly, nothing removes a placed machine, and no recipe in the data
    /// produces another crafting bench. So the one bench the starter kit gives
    /// the player is locked to one of the 49 recipes it can run, forever --
    /// while the route to the first miner needs seven of them.
    [Fact(Skip = "S1 open against gameplay: see docs/0020-opening-playthrough-qa.md")]
    public void TheRouteToTheFirstMiner_FitsInTheBenchesAPlayerCanEverHave()
    {
        var route = RouteToTheFirstMiner();
        var benches = BenchesObtainable();

        Assert.True(benches >= route.Count,
            $"the route to the first miner needs {route.Count} distinct crafting-bench " +
            $"recipes ({string.Join(", ", route.OrderBy(r => r))}), a placed bench runs " +
            $"exactly one recipe for its whole life, and a player can only ever obtain " +
            $"{benches} bench(es).");
    }

    /// S3 -- crude oil is a fluid deposit and can be dug out with bare hands.
    ///
    /// `NewGame.OreSpecs` buries every non-ambient raw item, crude oil
    /// included, and `HandOps.Mine` does not ask what form the deposit is. The
    /// oil derrick is the gate, and the player's hands walk straight past it.
    [Fact(Skip = "S3 open against gameplay: see docs/0020-opening-playthrough-qa.md")]
    public void HandMining_RefusesAFluidDeposit()
    {
        var fluids = Data.RawSolids.Where(i => i.Form == "fluid").Select(i => i.Id).ToHashSet();
        Assert.NotEmpty(fluids);

        var world = NewGame.Create(seed: 4, Data);
        var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0);
        var index = hits.FindIndex(h => fluids.Contains(world.Items.GetName(h.Item)));
        Assert.True(index >= 0, "seed 4 no longer has a fluid deposit near spawn");

        var taken = HandOps.Mine(world.Ground, hits[index].X, hits[index].Y,
                                 world.PlayerInventory, 10);

        Assert.Equal(0, taken);
    }

    /// The existing start test asserts only that *something* is minable within
    /// a short walk, which 20 seeds pass while 18 of them put a resource the
    /// manual tier cannot touch nearest to the player. What actually has to be
    /// reachable is an ore the starting furnace can smelt.
    [Fact]
    public void EveryStart_HasAnOreTheManualFurnaceCanSmeltWithinAWalk()
    {
        var smeltable = Buildables.RecipesFor(Buildables.Find("man_furnace")!)
            .SelectMany(r => r.Inputs)
            .Select(i => Data.Items.GetName(i.Item))
            .ToHashSet();

        Assert.NotEmpty(smeltable);

        for (var seed = 1; seed <= 20; seed++)
        {
            var world = NewGame.Create(seed, Data);
            var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0);
            var index = hits.FindIndex(h => smeltable.Contains(world.Items.GetName(h.Item)));

            Assert.True(index >= 0,
                $"seed {seed}: nothing the manual furnace can smelt within 400 tiles of " +
                "spawn, so the player can reach ore and still not make an ingot");
            Assert.True(hits[index].Distance <= Budget,
                $"seed {seed}: the nearest smeltable ore is {hits[index].Distance} tiles away, " +
                $"past the {Budget}-tile budget an opening walk is allowed to cost");
            Assert.True(world.Ground.RemainingAt(hits[index].X, hits[index].Y) > 0,
                $"seed {seed}: the nearest smeltable patch holds nothing");
        }
    }

    /// Stone is the only thing that opens a manual machine, and the kit holds
    /// exactly two machines' worth. A third one is a walk, so a seed that
    /// buries stone out of reach strands a player who spent their kit.
    [Fact]
    public void EveryStart_HasStoneWithinReachOfSpawn()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var world = NewGame.Create(seed, Data);
            var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0);

            var stone = hits.FindIndex(h => world.Items.GetName(h.Item) == "stone_deposit");
            Assert.True(stone >= 0, $"seed {seed}: no stone within 400 tiles of spawn");
            Assert.True(hits[stone].Distance <= Budget,
                $"seed {seed}: the nearest stone is {hits[stone].Distance} tiles away, past " +
                $"the {Budget}-tile budget an opening walk is allowed to cost");
        }
    }

    /// The kit has to open the first recipe exactly, with no slack: 24 stone is
    /// two manual machines and nothing over. Fewer would mean an unopenable
    /// first recipe; more would mean the walk for stone never happens.
    [Fact]
    public void TheStarterKitStone_IsExactlyTwoManualMachines()
    {
        var carried = NewGame.StarterKit.Single(k => k.Item == "stone_deposit").Count;
        var perMachine = Data.Recipe("build_man_furnace").Inputs
            .Single(i => i.Item.Equals(Data.Item("stone_deposit"))).Count;

        Assert.Equal(12, perMachine);
        Assert.Equal(2, carried / perMachine);
        Assert.Equal(0, carried % perMachine);

        // And the kit must actually be able to run that recipe on the bench it
        // carries, or the first click of the game is a refusal.
        var bench = Buildables.Find("man_manual_crafting")!;
        Assert.Contains(Buildables.RecipesFor(bench), r => r.Id == "build_man_furnace");
    }

    /// Every refusal has to reach the player as its own sentence. The build UI
    /// switches on `BuildResult` with a `_` fallback, so a value added later
    /// compiles, ships, and says "Cannot build a Steam Furnace" to a player who
    /// needed to be told why.
    ///
    /// `NotBuildable` is exempt and only that: the UI can only offer things the
    /// build catalogue knows, so it is unreachable from a click.
    [Fact]
    public void EveryBuildResult_HasItsOwnSentenceInTheBuildUI()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "game/scripts/GameRoot.cs"));
        var missing = Enum.GetNames<BuildResult>()
            .Where(name => name != nameof(BuildResult.NotBuildable))
            .Where(name => !source.Contains($"BuildResult.{name} =>"))
            .ToList();

        Assert.True(missing.Count == 0,
            "BuildResult values with no sentence of their own in GameRoot.PlaceHeld, so a " +
            "player gets the generic fallback: " + string.Join(", ", missing));
    }

    /// The messages must also be distinct. Two refusals sharing one sentence is
    /// the same silence the enum exists to prevent.
    [Fact]
    public void TheBuildRefusalSentences_AreAllDifferent()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "game/scripts/GameRoot.cs"));
        var arms = Regex.Matches(source, @"BuildResult\.\w+ =>\s*(\$?""[^""]*"")")
                        .Select(m => m.Groups[1].Value)
                        .ToList();

        Assert.True(arms.Count >= 8, $"only found {arms.Count} refusal sentences to check");
        Assert.Equal(arms.Count, arms.Distinct().Count());
    }

    /// An unpaired tunnel entrance refuses any same-facing end placed ahead of it
    /// out to *twice* its reach, not once. Within reach that refusal is the
    /// right answer -- the player is completing a tunnel and overshot. Beyond
    /// it, the second end is unrelated and is refused anyway, and because
    /// nothing removes a placed thing the band stays poisoned for good.
    ///
    /// Pinned here exactly so that narrowing it is a visible change rather than
    /// a silent one.
    [Fact]
    public void AnUnpairedEntrance_RefusesEndsOutToTwiceItsReach()
    {
        const int reach = 4;

        for (var ahead = 1; ahead <= reach * 2 + 2; ahead++)
        {
            var world = new World(7, new ItemDatabase());
            Assert.True(world.BeltMap.PlaceUnderground(
                100, 0, Direction.East, BeltUnits.SpeedBasic, reach, out _));

            var placed = world.BeltMap.PlaceUnderground(
                100 + ahead, 0, Direction.East, BeltUnits.SpeedBasic, reach, out var refusal);

            if (ahead <= reach)
            {
                // Behind and within reach: this end completes the tunnel.
                Assert.True(placed, $"{ahead} tiles ahead should have completed the tunnel");
                Assert.Equal(TunnelRefusal.None, refusal);
            }
            else if (ahead <= reach * 2)
            {
                Assert.False(placed, $"{ahead} tiles ahead was expected to be refused");
                Assert.Equal(TunnelRefusal.TooFar, refusal);
            }
            else
            {
                Assert.True(placed, $"{ahead} tiles ahead is out of scan range and must be allowed");
                Assert.Equal(TunnelRefusal.None, refusal);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
