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
    /// The three facts QA found still hold: nothing removes a placed machine,
    /// no recipe makes a second bench, and the route to the first miner needs
    /// ten distinct bench recipes. What changed is the fourth: a placed machine
    /// can be retasked (ADR 0021), so those ten fit in one bench.
    ///
    /// Asserted by playing it rather than by counting it. The counting version
    /// of this test could only ever have been satisfied by *more benches*,
    /// which is one of three possible fixes and not the one taken; running the
    /// route is agnostic about which fix is in place and fails for all three if
    /// none is. It is also strictly stronger: it fails if the recipes exist and
    /// the route still cannot be walked.
    [Fact]
    public void TheRouteToTheFirstMiner_FitsInTheBenchesAPlayerCanEverHave()
    {
        var route = RouteToTheFirstMiner();
        var benches = BenchesObtainable();

        var world = NewGame.Create(seed: 20260907, Data);
        var runner = new OpeningRouteRunner(world, Data, Buildables);

        var reached = runner.Reach("stm_miner", 1);

        Assert.True(reached && runner.StuckOn is null,
            $"the route to the first miner stopped at {runner.StuckOn}: it needs " +
            $"{route.Count} distinct crafting-bench recipes " +
            $"({string.Join(", ", route.OrderBy(r => r))}) and a player can only ever " +
            $"obtain {(benches == int.MaxValue ? "any number of" : benches.ToString())} bench(es).");

        Assert.Equal(1, world.PlayerInventory.Count(Data.Item("stm_miner")));

        // The whole point: those recipes ran on ONE machine. A bench locked to
        // what it was placed with makes this 1, whatever else passes.
        var benchRecipes = runner.RanOn
            .Where(kv => Data.Data.Recipes.Any(d => d.Id == kv.Key && d.Machine == "manual_crafting"))
            .ToList();

        Assert.Single(benchRecipes.Select(kv => kv.Value).Distinct());
        Assert.True(benchRecipes.Count >= route.Count,
            $"only {benchRecipes.Count} of the {route.Count} route recipes ran on the bench");
    }

    /// The starter kit is still the only source of benches, and the stone in it
    /// is still exactly two manual machines. Retasking is what makes the route
    /// fit; it must not have been paid for by quietly widening the kit.
    [Fact]
    public void TheFixIsRetasking_NotASecondBench()
    {
        Assert.Equal(1, BenchesObtainable());

        var world = NewGame.Create(seed: 20260907, Data);
        var runner = new OpeningRouteRunner(world, Data, Buildables);
        Assert.True(runner.Reach("stm_miner", 1), runner.StuckOn);

        // Three machines placed all told -- the kit's bench, and the furnace
        // and alloy smelter its 24 stone pays for -- and the bench did the work
        // of ten.
        Assert.Equal(3, world.MachineCount);
        Assert.True(runner.MostRecipesOnOneMachine >= 10,
            $"the busiest machine ran {runner.MostRecipesOnOneMachine} recipes");
    }

    /// S3 -- crude oil is a fluid deposit and can be dug out with bare hands.
    ///
    /// `NewGame.OreSpecs` buries every non-ambient raw item, crude oil
    /// included, and `HandOps.Mine` does not ask what form the deposit is. The
    /// oil derrick is the gate, and the player's hands walk straight past it.
    [Fact]
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

    /// F2's actual fix (ADR 0026): not "a smeltable ore exists somewhere within
    /// a 300-tile budget", which was true on every seed while the opening was
    /// still a lottery, but that the resource the survey device points at first
    /// is one the player can use.
    ///
    /// Pinned against the device the player actually carries -- its default
    /// radius, not the 400-tile radius the other tests use to prove existence.
    /// A guarantee outside the prospector's range would be no guarantee at all.
    [Fact]
    public void EveryStart_PutsSomethingSmeltableUnderTheProspector()
    {
        var smeltable = Buildables.RecipesFor(Buildables.Find("man_furnace")!)
            .SelectMany(r => r.Inputs)
            .Select(i => Data.Items.GetName(i.Item))
            .ToHashSet();

        Assert.NotEmpty(smeltable);

        for (var seed = 1; seed <= 20; seed++)
        {
            var world = NewGame.Create(seed, Data);

            // The carried device, at its own radius.
            var hits = new Prospector().Scan(world.Ground.Gen, NewGame.SpawnX, NewGame.SpawnY);
            var index = hits.FindIndex(h => smeltable.Contains(world.Items.GetName(h.Item)));

            Assert.True(index >= 0,
                $"seed {seed}: the prospector sees nothing the manual furnace can smelt, so " +
                "the opening is a walk in a direction the game never names");

            var hit = hits[index];
            Assert.True(hit.Distance <= WorldGen.StarterPatchRange,
                $"seed {seed}: nearest smeltable ore is {hit.Distance} tiles away, past the " +
                $"{WorldGen.StarterPatchRange}-tile guarantee");

            // Not underfoot either: the opening is meant to be a short walk with
            // the device, and a patch you spawn on skips it.
            Assert.True(hit.Distance >= 12,
                $"seed {seed}: smeltable ore {hit.Distance} tiles from spawn is close enough " +
                "to skip the walk the prospector exists for");

            Assert.True(world.Ground.RemainingAt(hit.X, hit.Y) > 0,
                $"seed {seed}: the guaranteed patch holds nothing");
        }
    }

    /// The other half of the fix (ADR 0026): the device says which hits the
    /// player can act on. Marked, not filtered -- a bauxite field you cannot
    /// smelt yet is still something to plan around.
    [Fact]
    public void TheProspector_MarksWhatResearchCanActuallyConsume()
    {
        var world = NewGame.Create(seed: 20260907, Data);
        var usable = world.Research!.ConsumableNow(world.Items);

        var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0, usable);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Usable);
        Assert.Contains(hits, h => !h.Usable);

        var smeltable = Buildables.RecipesFor(Buildables.Find("man_furnace")!)
            .SelectMany(r => r.Inputs)
            .Select(i => Data.Items.GetName(i.Item))
            .ToHashSet();

        // Every mark has to be right in both directions. A panel that marked
        // everything usable would pass "contains a usable hit" and tell the
        // player nothing.
        foreach (var hit in hits)
        {
            var name = world.Items.GetName(hit.Item);
            if (smeltable.Contains(name))
                Assert.True(hit.Usable, $"{name} is smeltable at manual tier but is not marked");
        }

        Assert.All(hits.Where(h => h.Usable),
                   h => Assert.Contains(world.Items.GetId(world.Items.GetName(h.Item)).Value,
                                        usable));

        // Unmarked without a research set at all: the flag says "researched",
        // not "exists", so a caller that forgets to pass one must not get a
        // list that claims everything is usable.
        var unaware = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0);
        Assert.All(unaware, h => Assert.False(h.Usable));
    }

    /// What the survey device marks has to widen as research lands, or the mark
    /// is a static list of five ores wearing a dynamic name.
    [Fact]
    public void WhatIsUsable_WidensWithResearch()
    {
        var world = NewGame.Create(seed: 20260907, Data);
        var atStart = world.Research!.ConsumableNow(world.Items);

        world.Research.UnlockAll();

        var afterEverything = world.Research.ConsumableNow(world.Items);

        Assert.True(afterEverything.Count > atStart.Count,
            "unlocking every tech consumed no new kind of item, so the mark is not " +
            "reading research at all");
        Assert.All(atStart, id => Assert.Contains(id, afterEverything));
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
