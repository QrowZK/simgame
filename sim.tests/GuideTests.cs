using Sim;
using Sim.Data;
using Sim.Save;

namespace Sim.Tests;

/// The first five minutes (docs/0030).
///
/// Every one of these runs against the shipped data and a real `NewGame` world,
/// for the same reason `ResearchTests` does: the guide is a reading of the tech
/// graph and the world, and a fixture would test a game nobody plays.
///
/// The property under test throughout is that a step is **derived**, never
/// recorded. Nothing here calls a "complete step" method, because there is not
/// one; the tests do what a player does and then ask the world.
public class GuideTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private const int Seed = 20260907;

    private static World NewWorld() => NewGame.Create(Seed, Data);

    private static IReadOnlyList<GuideStep> Steps(World world) => Guide.Steps(world);

    private static int DoneCount(World world) => Steps(world).Count(s => s.Done);

    private static string TitleOf(World world) => Guide.Current(world)?.Title ?? "<none>";

    // ---- the shape the UI is built against ---------------------------------

    [Fact]
    public void ANewGame_OpensOnTheFirstStepAndNothingIsDone()
    {
        var world = NewWorld();

        var current = Guide.Current(world);

        Assert.NotNull(current);
        Assert.Equal("Find ore and mine it", current!.Value.Title);
        Assert.Equal("P", current.Value.Key);
        Assert.False(current.Value.Done);
        Assert.Equal(0, DoneCount(world));

        // Nine steps, each with something to say and none of them repeated. A
        // guide with a blank detail line is a guide that has stopped guiding.
        var steps = Steps(world);
        Assert.Equal(9, steps.Count);
        Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.All(steps, s => Assert.True(s.Detail.Length > 30, s.Title));
        Assert.Equal(steps.Count, steps.Select(s => s.Title).Distinct().Count());
    }

    /// Only the keys that exist. A guide naming a key that does nothing is
    /// worse than one naming no key at all.
    [Fact]
    public void EveryKeyNamed_IsAKeyTheGameBinds()
    {
        var bound = new[] { "", "P", "B", "X", "R", "T" };
        Assert.All(Steps(NewWorld()), s => Assert.Contains(s.Key, bound));
    }

    /// A world with no research -- the demo factory, a throughput harness --
    /// has no opening, and gets nothing rather than a ladder it can never climb.
    [Fact]
    public void AWorldWithoutResearch_HasNoGuideAtAll()
    {
        var world = new World(1, new ItemDatabase());

        Assert.Empty(Guide.Steps(world));
        Assert.Null(Guide.Current(world));
    }

    // ---- the ladder, walked in order ---------------------------------------

    /// The whole opening, played the way it is meant to be played: mine, place
    /// the Uplink, deliver, smelt, deliver, automate, and hand over to the
    /// Steam tier. `Current` must advance exactly one rung at each step and
    /// never go backwards.
    [Fact]
    public void TheWholeLadder_AdvancesOneStepAtATime()
    {
        var world = NewWorld();
        var seen = new List<string>();

        void Reached(string title, int done)
        {
            seen.Add(TitleOf(world));
            Assert.Equal(title, TitleOf(world));
            Assert.Equal(done, DoneCount(world));
        }

        Reached("Find ore and mine it", 0);

        var ore = MineSomeOre(world, 30);
        Reached("Place the Uplink", 1);

        var uplink = PlaceUplink(world, 0, 0);
        Reached("Deliver one ore to the Uplink", 2);

        Assert.Equal(1, world.DeliverByHand(ore, 1).Accepted);
        Assert.True(world.Research!.IsTechUnlocked(Guide.TechFirstOre));
        Reached("Put down the Manual Furnace", 3);

        // The furnace was the reward for that delivery, not something crafted:
        // it is in the player's hands because the rung paid for it.
        Assert.Equal(1, world.PlayerInventory.Count(Data.Item("man_furnace")));
        var furnace = PlaceFurnace(world, ore, 6, 1);
        Reached("Deliver three ingots", 4);

        var ingot = Smelt(world, furnace, ore, 3);
        Assert.Equal(3, world.DeliverByHand(ingot, 3).Accepted);
        Assert.Equal(2, world.PlayerInventory.Count(Data.Item("stm_inserter")));
        Reached("Deliver six more ingots", 5);

        Smelt(world, furnace, ore, 6);
        Assert.Equal(6, world.DeliverByHand(ingot, 6).Accepted);
        Assert.Equal(8, world.PlayerInventory.Count(Data.Item("stm_transport_belt")));
        Reached("Let the belt make the delivery", 6);

        // The climax: a line from the furnace to the Uplink, built out of what
        // the last two rungs paid for, and then the player stands still.
        BeltFurnaceToUplink(world, furnace, uplink);
        Smelt(world, furnace, ore, 6, extract: false);
        world.Tick(600);

        Assert.Equal(6, world.UnattendedDeliveries);
        Assert.Equal(0, world.PlayerInventory.Count(ingot));
        Reached("Deliver twelve ingots without carrying them", 7);

        // Half the rung arrived without the player touching it. The other half
        // is the same line, left running.
        Assert.Equal(6, world.Research.Delivered(Guide.TechLine, "any metal ingot"));

        Smelt(world, furnace, ore, 6, extract: false);
        world.Tick(600);

        Assert.Equal(12, world.UnattendedDeliveries);
        Assert.True(world.Research.IsTechUnlocked(Guide.TechLine));
        Reached("Build a Steam Machine Hull and deliver it", 8);

        // And the ladder hands over to the authored tech tree, which is exactly
        // where the guide stops having anything to say.
        world.PlayerInventory.Add(Data.Item("stm_machine_hull"), 1);
        world.DeliverByHand(Data.Item("stm_machine_hull"), 1);

        Assert.Equal(9, DoneCount(world));
        Assert.Null(Guide.Current(world));
        Assert.All(Steps(world), s => Assert.True(s.Done));

        // Every rung was visited once, in order: no step was skipped and none
        // was shown twice.
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    /// The finding this whole design rests on: the player may do it wrongly,
    /// out of order, or with their own layout, and it still counts. Here the
    /// Uplink goes down before anything is mined, which reverses the first two
    /// rungs.
    [Fact]
    public void PlayingItOutOfOrder_StillCounts()
    {
        var world = NewWorld();

        PlaceUplink(world, 0, 0);

        var steps = Steps(world);
        Assert.True(steps[1].Done, "the Uplink is on the map and the guide did not notice");
        Assert.False(steps[0].Done);
        Assert.Equal("Find ore and mine it", TitleOf(world));

        var ore = MineSomeOre(world, 4);
        Assert.Equal("Deliver one ore to the Uplink", TitleOf(world));
        Assert.Equal(2, DoneCount(world));

        // And a player who builds the furnace before they have delivered
        // anything -- from the 24 stone in the kit, which is the only reason
        // they can -- gets credit for that too, out of turn.
        world.DeliverByHand(ore, 1);
        PlaceFurnace(world, ore, 6, 1);
        Assert.Equal("Deliver three ingots", TitleOf(world));
    }

    /// Steps do not un-tick. "You are holding ore" stops being true the moment
    /// the ore is smelted, and a guide that walked backwards there would be
    /// telling a player to go and redo something they had finished.
    [Fact]
    public void SpendingWhatAStepAskedFor_DoesNotUndoIt()
    {
        var world = NewWorld();
        var ore = MineSomeOre(world, 1);
        Assert.True(Steps(world)[0].Done);

        PlaceUplink(world, 0, 0);
        Assert.Equal(1, world.DeliverByHand(ore, 1).Accepted);

        // Nothing in hand any more, and the first step is still done.
        Assert.Equal(0, world.PlayerInventory.Count(ore));
        Assert.True(Steps(world)[0].Done);
        Assert.Equal(3, DoneCount(world));
    }

    // ---- the milestone the sim has to record itself ------------------------

    /// Hand-delivery is not automation. The counter that marks the opening's
    /// climax must not move when the player carries something in themselves --
    /// this is the one step whose state the sim has to store rather than infer,
    /// so it is the one that can most easily lie.
    [Fact]
    public void AHandDelivery_DoesNotCountAsAnUnattendedOne()
    {
        var world = NewWorld();
        var ore = MineSomeOre(world, 4);
        PlaceUplink(world, 0, 0);

        Assert.Equal(1, world.DeliverByHand(ore, 4).Accepted);
        Assert.Equal(0, world.UnattendedDeliveries);
        Assert.False(Steps(world)[6].Done);
    }

    /// And it does move for something the machinery brought. Pushed into the
    /// Uplink's buffer is exactly what a belt, an inserter or a drone does.
    [Fact]
    public void SomethingTheFactoryBrought_CountsAndSurvivesASave()
    {
        var world = NewWorld();
        var ore = MineSomeOre(world, 2);
        var uplink = PlaceUplink(world, 0, 0);

        world.Machines[uplink].PushInput(ore, 1);
        world.Tick();

        Assert.Equal(1, world.UnattendedDeliveries);
        Assert.True(Steps(world)[6].Done);

        var json = SaveGame.ToJson(SaveGame.Capture(world));
        var loaded = SaveGame.Restore(SaveGame.FromJson(json), Data.Recipes, world.Ground.Gen);

        Assert.Equal(1, loaded.UnattendedDeliveries);
        Assert.True(Guide.Steps(loaded)[6].Done);
        Assert.Equal(json, SaveGame.ToJson(SaveGame.Capture(loaded)));
    }

    // ---- what the rungs pay out --------------------------------------------

    /// The reward has to be sayable. "Unlocks 26 recipes" is what the panel
    /// used to offer a player and it names nothing they can picture.
    [Fact]
    public void EveryOpeningRung_NamesWhatItGives()
    {
        var research = new Research(Data);

        var rungs = new[]
        {
            (Guide.TechFirstOre, "Gives you 1 Manual Furnace"),
            (Guide.TechFirstMetal, "Gives you 2 Steam Inserters"),
            (Guide.TechHaulage, "Gives you 8 Steam Transport Belts"),
        };

        foreach (var (tech, expected) in rungs)
        {
            var objective = research.Objective(Data.Data.Techs.Single(t => t.Id == tech));
            Assert.Equal(expected, objective.RewardSummary);
        }

        // The last rung pays in access rather than items, and says so.
        var last = research.Objective(Data.Data.Techs.Single(t => t.Id == Guide.TechLine));
        Assert.Empty(last.Rewards);

        // The four Steam kits still pay out, through the same accessor, so the
        // UI has one place to ask what a tech gives.
        Assert.Equal("Gives you 12 Steam Transport Belts",
                     research.Objective(Data.Data.Techs.Single(t => t.Id == "tech_stm_metallurgy"))
                             .RewardSummary);
    }

    /// A rung accepts a *set*, because which ore is near spawn is a property of
    /// the seed. Six ingots of two different metals is six ingots.
    [Fact]
    public void MixedMetals_CountTowardTheSameRung()
    {
        var research = new Research(Data);
        research.Deliver("magnetite", 1);

        research.Deliver("iron_ingot", 2);
        Assert.False(research.IsTechUnlocked(Guide.TechFirstMetal));

        research.Deliver("copper_ingot", 1);
        Assert.True(research.IsTechUnlocked(Guide.TechFirstMetal));
    }

    /// The first rung must be completable on any seed. It accepts exactly what
    /// a Manual furnace smelts, which is the same set worldgen guarantees a
    /// patch of near spawn (ADR 0026) -- if those two ever disagree, some seed
    /// opens on an objective the player cannot reach.
    [Fact]
    public void TheFirstRung_AcceptsExactlyWhatTheStartIsGuaranteedToHave()
    {
        var research = new Research(Data);
        var need = research.Objective(Data.Data.Techs.Single(t => t.Id == Guide.TechFirstOre))
                           .Needs.Single();

        Assert.Equal(NewGame.StarterOres(Data).OrderBy(x => x, StringComparer.Ordinal),
                     need.Accepts.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(need.IsGroup);
        Assert.Equal(1, need.Required);

        // And the guarantee is a real patch, on every seed the route tests use.
        for (var seed = 1; seed <= 10; seed++)
        {
            var world = NewGame.Create(seed, Data);
            var usable = world.Research!.ConsumableNow(world.Items);
            var hits = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0, usable);

            Assert.Contains(hits, h => h.Usable && need.Accepts.Contains(world.Items.GetName(h.Item)));
        }
    }

    /// Stone is not ore. The player lands holding 24 of it, and a first
    /// objective it satisfied would be a rung stepped over without mining.
    [Fact]
    public void TheFirstRung_IsNotSatisfiedByTheStoneInTheStarterKit()
    {
        var world = NewWorld();
        PlaceUplink(world, 0, 0);

        Assert.Equal(0, world.DeliverByHand(Data.Item("stone_deposit"), 24).Accepted);
        Assert.Equal(24, world.PlayerInventory.Count(Data.Item("stone_deposit")));
        Assert.False(world.Research!.IsTechUnlocked(Guide.TechFirstOre));

        // The Uplink is down, so that step is done -- and the first step is
        // not, because stone is not ore and nothing has been mined.
        Assert.Equal(1, DoneCount(world));
        Assert.False(Steps(world)[0].Done);
        Assert.Equal("Find ore and mine it", TitleOf(world));
    }

    /// At tick zero the objectives list is the one rung a player can actually
    /// do. Four Steam hulls -- which is what it used to open on -- is four
    /// tasks with no route to them from a landing site.
    [Fact]
    public void AtTickZero_TheOnlyObjectiveIsOneAchievableRung()
    {
        var research = new Research(Data);

        var objective = Assert.Single(research.Objectives);
        Assert.Equal(Guide.TechFirstOre, objective.Id);
        Assert.Equal(1, objective.Needs.Single().Required);
    }

    // ---- helpers: only operations a player has ------------------------------

    private static ItemId MineSomeOre(World world, int count)
    {
        var wanted = NewGame.StarterOres(Data);
        var usable = world.Research!.ConsumableNow(world.Items);

        foreach (var hit in new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0, usable))
        {
            if (!wanted.Contains(world.Items.GetName(hit.Item))) continue;

            while (world.PlayerInventory.Count(hit.Item) < count &&
                   HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory,
                                count - world.PlayerInventory.Count(hit.Item)) > 0) { }

            if (world.PlayerInventory.Count(hit.Item) >= count) return hit.Item;
        }

        throw new InvalidOperationException($"seed {world.Seed} has no reachable starter ore");
    }

    private static int PlaceUplink(World world, int x, int y)
    {
        var uplink = Buildables.Find("man_uplink")!;
        var recipe = Buildables.RecipesFor(uplink, world.Research).Single();
        Assert.Equal(BuildResult.Ok, world.BuildStandingBy(Buildables, uplink.Item, x, y, recipe));
        return world.MachineCount - 1;
    }

    /// Places the furnace and sets it to smelt whatever ore the player found,
    /// which is how a player does it: the recipe list is on the machine panel.
    private static int PlaceFurnace(World world, ItemId ore, int x, int y)
    {
        var furnace = Buildables.Find("man_furnace")!;
        var smelt = Buildables.RecipesFor(furnace, world.Research)
                              .Single(r => r.Inputs.Any(i => i.Item.Equals(ore)));

        Assert.Equal(BuildResult.Ok, world.BuildStandingBy(Buildables, furnace.Item, x, y, smelt));
        return world.MachineCount - 1;
    }

    /// Hand-feeds the furnace and runs it, one cycle at a time. Returns the
    /// ingot it makes.
    private static ItemId Smelt(World world, int furnace, ItemId ore, int count,
                                bool extract = true)
    {
        var machine = world.Machines[furnace];
        var ingot = machine.Recipe.Outputs[0].Item;

        for (var i = 0; i < count; i++)
        {
            if (world.PlayerInventory.Count(ore) == 0) MineSomeOre(world, 1);
            HandOps.Insert(world.PlayerInventory, machine, ore, 1);
            world.Tick(machine.Recipe.DurationTicks);
            if (extract) HandOps.ExtractAll(machine, world.PlayerInventory);
        }

        return ingot;
    }

    /// The line the sixth rung pays for: an inserter out of the furnace onto a
    /// belt, the belt, and an inserter off the belt into the Uplink. Laid out by
    /// hand rather than by a helper, because "does a belt actually reach the
    /// Uplink" is half of what this is checking -- and it does not reach it on
    /// its own: a belt pointed at a machine backs up, and the inserter at the
    /// far end is the part a player has to work out.
    private static void BeltFurnaceToUplink(World world, int furnace, int uplink)
    {
        var from = world.PlacementOf(furnace);   // 1x1 at (6,1)
        var to = world.PlacementOf(uplink);      // 2x2 at (0,0)..(1,1)

        // Each inserter's source is the tile behind it, its target the tile
        // ahead: out of the furnace at one end, into the Uplink at the other.
        Assert.True(world.BeltMap.PlaceInserter(from.X - 1, from.Y, Direction.West));

        for (var x = from.X - 2; x > to.X + 2; x--)
            Assert.True(world.BeltMap.PlaceBelt(x, from.Y, Direction.West));

        Assert.True(world.BeltMap.PlaceInserter(to.X + 2, from.Y, Direction.West));

        world.SyncBelts();
        Assert.Single(world.Belts.Segments);
        Assert.Equal(2, world.Belts.Inserters.Count);
        Assert.Equal(EndpointKind.Machine, world.Belts.Inserters[1].Target.Kind);
    }
}
