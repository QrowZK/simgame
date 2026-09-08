using Sim;
using Sim.Data;
using Sim.Save;

namespace Sim.Tests;

/// Research, the Uplink, and the gate they put in front of the recipe graph
/// (ADR 0023).
///
/// Every one of these runs against the repository's real `data/techs.json` and
/// `data/recipes.json` rather than a fixture. That is deliberate: the whole
/// claim of ADR 0023 is that the questline *is* the shipped tech graph, and a
/// fixture would test a graph nobody plays.
public class ResearchTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World NewWorld() => NewGame.Create(seed: 4242, Data);

    /// Walks the opening ladder (docs/0030), which now stands between a new
    /// game and the Steam tier. Written as real deliveries of real items rather
    /// than `UnlockAll`, so a test that says "delivering a hull opens Steam"
    /// still says it about a player who got there the way a player does.
    ///
    /// Returns what the rungs paid out, because two of the tests below care
    /// that the payout happened exactly once.
    internal static void CompleteTheOpening(Research research)
    {
        research.Deliver("magnetite", 1);
        research.Deliver("iron_ingot", 3);
        research.Deliver("iron_ingot", 6);
        research.Deliver("iron_ingot", 12);

        if (!research.IsTechUnlocked(Guide.TechLine))
            throw new InvalidOperationException(
                "the opening ladder did not complete, so every test below it is testing " +
                "the wrong world");
    }

    /// The same ladder, walked through a world so the payouts actually land --
    /// and then taken straight back out again, because a test measuring an
    /// inventory after a hull must not be reading the opening's belts.
    internal static void CompleteTheOpening(World world)
    {
        Hand(world, "magnetite", 1);
        Hand(world, "iron_ingot", 3);
        Hand(world, "iron_ingot", 6);
        Hand(world, "iron_ingot", 12);

        Assert.True(world.Research!.IsTechUnlocked(Guide.TechLine),
            "the opening ladder did not complete, so every assertion below it is about " +
            "the wrong world");

        foreach (var tech in new[] { Guide.TechFirstOre, Guide.TechFirstMetal,
                                     Guide.TechHaulage, Guide.TechLine })
            foreach (var reward in world.Research.RewardsFor(tech))
                world.PlayerInventory.Take(Data.Item(reward.Item), reward.Count);
    }

    private static void Hand(World world, string item, int count)
    {
        var id = Data.Item(item);
        world.PlayerInventory.Add(id, count);
        Assert.Equal(count, world.DeliverByHand(id, count).Accepted);
    }

    // ---- the opening must stay playable ------------------------------------

    /// The single property that keeps a new game from being a locked door: the
    /// four Manual techs have no `requires_item`, so they are open at tick zero.
    [Fact]
    public void AtTickZero_ExactlyTheManualTechsAreUnlocked()
    {
        var research = new Research(Data);

        Assert.Equal(
            new[]
            {
                "tech_man_metallurgy", "tech_man_processing",
                "tech_man_chemistry", "tech_man_fabrication",
            }.OrderBy(x => x, StringComparer.Ordinal),
            research.UnlockedTechs.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// Every Manual-tier recipe is open from the start, and nothing above it is.
    /// Asserted as two exact counts rather than "some are open": a gate that
    /// unlocked everything and a gate that unlocked nothing would both pass a
    /// "greater than zero" check, and each is a way of shipping no gate at all.
    [Fact]
    public void AtTickZero_EveryManualRecipeIsOpenAndNothingAboveIt()
    {
        var research = new Research(Data);

        // The two exceptions are named, not tolerated: the belt and the
        // inserter moved behind the opening ladder's second and third rungs
        // (docs/0030), where they are the reward rather than a row in a menu
        // the player cannot use. Both are Manual-tier recipes that need a Steam
        // hull, so nothing that was buildable at tick zero stopped being so.
        var openingRewards = new[] { "build_stm_inserter", "build_stm_transport_belt" };

        var manual = Data.Data.Recipes
            .Where(r => r.Tier == "MAN" && !openingRewards.Contains(r.Id)).ToList();
        var above = Data.Data.Recipes.Where(r => r.Tier != "MAN").ToList();

        Assert.All(openingRewards, id => Assert.False(research.IsUnlocked(id)));
        Assert.Equal(manual.Count, manual.Count(r => research.IsUnlocked(r.Id)));
        Assert.Equal(0, above.Count(r => research.IsUnlocked(r.Id)));

        // And the numbers are what the data actually holds, so a data change
        // that emptied the Manual tier could not pass this by making both
        // counts zero.
        Assert.Equal(56, manual.Count);
        Assert.Equal(688, above.Count);
    }

    /// The bench a new game carries can still make the things the opening route
    /// needs. This is the narrowest statement of "the player is not locked out".
    [Fact]
    public void ANewGame_CanStillCraftItsFirstFurnaceAndItsFirstMiner()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var offered = Buildables.RecipesFor(bench, world.Research).Select(r => r.Id).ToHashSet();

        Assert.Contains("build_man_furnace", offered);
        Assert.Contains("build_stm_miner", offered);

        // And the build menu offers the bench itself, or none of the above is
        // reachable at all.
        Assert.Contains(Buildables.OfferableWith(world.Research), b => b.ItemId == "man_manual_crafting");
    }

    /// The picker is gated too, and this is the assertion with the teeth in it:
    /// a build path that refuses what the picker offered is a dead end the
    /// player cannot reason about. Stated as exact numbers on a real machine --
    /// a Steam furnace on a new game offers only its inherited Manual recipes,
    /// and every Steam one is missing.
    [Fact]
    public void ThePicker_OffersOnlyResearchedRecipes()
    {
        var world = NewWorld();
        var furnace = Buildables.Find("stm_furnace")!;

        var all = Buildables.RecipesFor(furnace);
        var gated = Buildables.RecipesFor(furnace, world.Research);

        Assert.NotEqual(all.Count, gated.Count);
        Assert.All(gated, r => Assert.True(world.Research!.IsUnlocked(r)));
        Assert.Equal(all.Count(r => world.Research!.IsUnlocked(r)), gated.Count);

        // Everything the picker still offers must also be buildable, or the
        // menu is lying about what a click will do.
        world.PlayerInventory.Add(furnace.Item, gated.Count);
        var x = 60;
        foreach (var recipe in gated)
            Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, x += 2, 60, recipe));
    }

    /// A machine every one of whose recipes is locked is not offered at all.
    /// The build menu's existing rule -- never hand over a machine that can do
    /// nothing -- has to hold under the gate as well as under the tier ladder.
    [Fact]
    public void AMachineWithNothingItMayYetRun_IsNotOffered()
    {
        var world = NewWorld();

        var ungated = Buildables.Offerable.Select(b => b.ItemId).ToHashSet();
        var gated = Buildables.OfferableWith(world.Research).Select(b => b.ItemId).ToHashSet();

        Assert.ProperSubset(ungated, gated);

        // The assembler is the clearest case: it exists from Steam upwards and
        // has no Manual recipes at all, so nothing it could run is open yet.
        Assert.Contains("stm_assembler", ungated);
        Assert.DoesNotContain("stm_assembler", gated);

        // And the two things a new game carries are both still there, or the
        // filter has taken the game away from the player.
        Assert.Contains("man_manual_crafting", gated);
        Assert.Contains("man_uplink", gated);
    }

    /// The gate refuses a Steam recipe on a new game, with its own reason.
    /// `NeedsRecipe` would send the player back to the picker, where the recipe
    /// they picked is not listed -- a loop with no exit.
    [Fact]
    public void ASteamRecipe_IsRefusedWithItsOwnReasonUntilItIsResearched()
    {
        var world = NewWorld();
        var furnace = Buildables.Find("stm_furnace")!;
        var steam = Buildables.RecipesFor(furnace).First(r => !world.Research!.IsUnlocked(r));
        world.PlayerInventory.Add(furnace.Item, 1);

        Assert.Equal(BuildResult.NotResearched, world.TryBuild(Buildables, furnace.Item, 40, 40, steam));

        // Refused builds cost nothing -- the same invariant every other refusal
        // holds, and the one a gate is most likely to break.
        Assert.Equal(1, world.PlayerInventory.Count(furnace.Item));
        Assert.Equal(0, world.MachineCount);
    }

    /// A machine already on the ground cannot be retasked onto a locked recipe
    /// either. Retasking is the second door into the recipe set (ADR 0021), and
    /// a gate on only one of the two doors is not a gate.
    [Fact]
    public void RetaskingOntoALockedRecipe_IsRefusedAndChangesNothing()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var first = Buildables.RecipesFor(bench, world.Research)[0];
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, bench.Item, 40, 40, first));

        // A recipe this bench could physically run, at a tier it has not
        // reached. Manual crafting exists only at MAN, so use a furnace instead.
        var furnace = Buildables.Find("stm_furnace")!;
        world.Research!.UnlockAll();
        world.PlayerInventory.Add(furnace.Item, 1);
        var steam = Buildables.RecipesFor(furnace).First(r => r.Id.StartsWith("smelt_"));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, furnace.Item, 44, 44, steam));

        // Now lock it all again and try to move that furnace onto another of
        // its own recipes.
        var relocked = new Research(Data);
        world.Research = relocked;
        var other = Buildables.RecipesFor(furnace).First(r => !ReferenceEquals(r, steam));

        Assert.Equal(RecipeChangeResult.NotResearched,
                     world.TryChangeRecipe(Buildables, 1, other, out var evicted));
        Assert.Equal(0, evicted);
        Assert.Same(steam, world.Machines[1].Recipe);
    }

    // ---- delivery ----------------------------------------------------------

    /// The core loop: deliver the tier's hull, get the tier's recipes. Against
    /// the real data, so the count is the number the shipped graph holds.
    [Fact]
    public void DeliveringASteamHull_UnlocksExactlyThatTechsRecipes()
    {
        var research = new Research(Data);
        CompleteTheOpening(research);
        var metallurgy = Data.Data.Recipes.Where(r => r.UnlockedBy == "tech_stm_metallurgy").ToList();
        var processing = Data.Data.Recipes.Where(r => r.UnlockedBy == "tech_stm_processing").ToList();

        Assert.All(metallurgy, r => Assert.False(research.IsUnlocked(r.Id)));

        var report = research.Deliver("stm_machine_hull", 1);

        // One hull, one tech. Four techs want the same hull, and a delivery
        // that credited all of them would buy a whole tier for a quarter of it.
        Assert.Equal(1, report.Accepted);
        Assert.Equal(new[] { "tech_stm_metallurgy" }, report.Completed);

        Assert.All(metallurgy, r => Assert.True(research.IsUnlocked(r.Id)));
        Assert.All(processing, r => Assert.False(research.IsUnlocked(r.Id)));
        Assert.Equal(26, metallurgy.Count);
    }

    /// Four hulls open the four Steam lines, in data order, and open the four
    /// Voltaic objectives behind them. This is one full turn of the loop the
    /// whole design rests on.
    [Fact]
    public void FourSteamHulls_OpenTheWholeSteamTierAndTheVoltaicObjectivesBehindIt()
    {
        var research = new Research(Data);

        // The four Steam lines are what is open once the opening ladder is
        // walked -- at tick zero the only objective is the first rung of it.
        Assert.Single(research.Objectives);
        CompleteTheOpening(research);
        Assert.Equal(4, research.Objectives.Count);

        var report = research.Deliver("stm_machine_hull", 4);

        Assert.Equal(4, report.Accepted);
        Assert.Equal(
            new[]
            {
                "tech_stm_metallurgy", "tech_stm_processing",
                "tech_stm_chemistry", "tech_stm_fabrication",
            },
            report.Completed);

        Assert.Equal(
            new[]
            {
                "tech_vlt_metallurgy", "tech_vlt_processing",
                "tech_vlt_chemistry", "tech_vlt_fabrication",
            },
            research.Objectives.Select(o => o.Id));
    }

    /// A delivery is never credited past what is open. The fifth hull is
    /// refused rather than banked against the Voltaic techs, which are not open
    /// yet: crediting forward would let a player buy the top of the tree with a
    /// pile of Steam hulls.
    [Fact]
    public void AFifthSteamHull_IsRefusedRatherThanBankedAgainstTheNextTier()
    {
        var research = new Research(Data);
        CompleteTheOpening(research);

        var report = research.Deliver("stm_machine_hull", 5);

        Assert.Equal(4, report.Accepted);
        Assert.Equal(4, report.Completed.Count);
        Assert.Equal(0, research.Delivered("tech_vlt_metallurgy", "vlt_machine_hull"));
    }

    /// Something nothing wants is refused entirely. An Uplink that swallowed a
    /// misrouted belt would cost a player their throughput silently.
    [Fact]
    public void SomethingNothingWants_IsRefusedEntirely()
    {
        var research = new Research(Data);

        var report = research.Deliver("iron_ingot", 100);

        Assert.Equal(0, report.Accepted);
        Assert.Empty(report.Completed);
    }

    /// Completing the first tech of a line pays out its kit, once. This is the
    /// only handout in the game and it must not be repeatable.
    [Fact]
    public void TheFirstTechOfALine_PaysItsKitExactlyOnce()
    {
        var world = NewWorld();
        CompleteTheOpening(world);
        var belt = Data.Item("stm_transport_belt");
        var hull = Data.Item("stm_machine_hull");

        Assert.Equal(0, world.PlayerInventory.Count(belt));

        world.PlayerInventory.Add(hull, 1);
        world.DeliverByHand(hull, 1);
        Assert.Equal(12, world.PlayerInventory.Count(belt));

        // Deliver another. Metallurgy is already done, so this one goes to
        // Processing, whose kit is inserters -- the belt count must not move.
        world.PlayerInventory.Add(hull, 1);
        world.DeliverByHand(hull, 1);
        Assert.Equal(12, world.PlayerInventory.Count(belt));
        Assert.Equal(4, world.PlayerInventory.Count(Data.Item("stm_inserter")));
    }

    /// A hand delivery takes exactly what was accepted out of the player's
    /// hands, and nothing when nothing was wanted.
    [Fact]
    public void AHandDelivery_TakesOnlyWhatWasAccepted()
    {
        var world = NewWorld();
        CompleteTheOpening(world);
        var hull = Data.Item("stm_machine_hull");
        var ingot = Data.Item("iron_ingot");
        world.PlayerInventory.Add(hull, 10);
        world.PlayerInventory.Add(ingot, 10);

        world.DeliverByHand(hull, 10);
        Assert.Equal(6, world.PlayerInventory.Count(hull));

        world.DeliverByHand(ingot, 10);
        Assert.Equal(10, world.PlayerInventory.Count(ingot));
    }

    // ---- the Uplink --------------------------------------------------------

    /// The Uplink as a placed building: a starter-kit item, put down like any
    /// machine, that turns what is pushed into it into research.
    [Fact]
    public void ItemsPushedIntoAPlacedUplink_BecomeResearchOnTheNextTick()
    {
        var world = NewWorld();
        CompleteTheOpening(world);
        var uplink = Buildables.Find("man_uplink")!;
        var recipe = Buildables.RecipesFor(uplink, world.Research).Single();

        Assert.Equal(Research.UplinkRecipe, recipe.Id);
        Assert.Equal(1, world.PlayerInventory.Count(uplink.Item));
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, uplink.Item, 40, 40, recipe));

        var machine = world.Machines[0];
        machine.PushInput(Data.Item("stm_machine_hull"), 2);

        Assert.False(world.Research!.IsTechUnlocked("tech_stm_metallurgy"));
        world.Tick();

        Assert.True(world.Research.IsTechUnlocked("tech_stm_metallurgy"));
        Assert.True(world.Research.IsTechUnlocked("tech_stm_processing"));
        Assert.Empty(machine.InputContents);

        // And a 2x2 Uplink covers four tiles, so a belt can reach it from any
        // of them -- which is what makes feeding it by belt possible at all.
        Assert.Equal(2, world.PlacementOf(0).Size);
        Assert.True(world.TryMachineAt(41, 41, out _, out var index) && index == 0);
    }

    /// What no objective wants stays in the hopper rather than vanishing. A
    /// player who misroutes a belt into the Uplink must be able to see it and
    /// take it back, not discover the loss hours later.
    [Fact]
    public void WhatNothingWants_StaysInTheUplinkRatherThanVanishing()
    {
        var world = NewWorld();
        var uplink = Buildables.Find("man_uplink")!;
        var recipe = Buildables.RecipesFor(uplink, world.Research).Single();
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, uplink.Item, 40, 40, recipe));

        var ingot = Data.Item("iron_ingot");
        world.Machines[0].PushInput(ingot, 7);
        world.Tick(10);

        Assert.Equal(7, world.Machines[0].GetInputCount(ingot));
    }

    /// The Uplink never runs a cycle, however long it is left alone. It has no
    /// inputs and no outputs, so an ordinary machine tick would spin it
    /// endlessly and report Working on a building that is doing nothing.
    [Fact]
    public void AnUplink_NeverReportsWorking()
    {
        var world = NewWorld();
        CompleteTheOpening(world);
        var uplink = Buildables.Find("man_uplink")!;
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, uplink.Item, 40, 40,
                                                    Buildables.RecipesFor(uplink, world.Research).Single()));

        world.Tick(600);

        Assert.Equal(MachineState.Idle, world.Machines[0].State);

        // And it is still listening after all that. An Uplink that cycled would
        // eventually be Blocked or Starved and stop accepting, which is the
        // failure a state check alone would not catch.
        world.Machines[0].PushInput(Data.Item("stm_machine_hull"), 1);
        world.Tick();
        Assert.True(world.Research!.IsTechUnlocked("tech_stm_metallurgy"));
        Assert.Empty(world.Machines[0].InputContents);
    }

    // ---- the whole tree ----------------------------------------------------

    /// The property the gate lives or dies by: **every tech in the shipped data
    /// is reachable**, using only recipes unlocked by techs already earned.
    ///
    /// This is the test that says the gate did not lock the player out of the
    /// game somewhere in the middle of the tree, which is a failure no opening
    /// test could ever catch. It plays the whole ladder as a closure over the
    /// recipe graph: unlock what is open, craft everything craftable with it,
    /// deliver whatever that made, repeat.
    [Fact]
    public void EveryTech_AndTheSeed_IsReachableFromTheManualTierAlone()
    {
        var research = new Research(Data);
        var have = Data.Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();

        for (var round = 0; round < Data.Data.Techs.Count + 2; round++)
        {
            bool grew;
            do
            {
                grew = false;
                foreach (var recipe in Data.Data.Recipes)
                {
                    if (!research.IsUnlocked(recipe.Id)) continue;
                    if (!recipe.Inputs.All(i => have.Contains(i.Item))) continue;
                    foreach (var output in recipe.Outputs)
                        grew |= have.Add(output.Item);
                }
            } while (grew);

            foreach (var objective in research.Objectives.ToList())
                foreach (var need in objective.Needs)
                {
                    // A need is a set of acceptable items, not one id: the
                    // opening rungs take any ore and any ingot. Deliver the
                    // first one the closure has actually made.
                    var have_ = need.Accepts.FirstOrDefault(have.Contains);
                    if (have_ is not null)
                        research.Deliver(have_, need.Outstanding);
                }

            if (research.SeedDelivered) break;
        }

        var locked = Data.Data.Techs.Where(t => !research.IsTechUnlocked(t.Id)).Select(t => t.Id);
        Assert.Empty(locked);
        Assert.True(research.SeedDelivered);
    }

    /// The Seed is the last objective and it is not open until the tree is.
    /// It is also the only objective with more than one need, which is what
    /// makes the final delivery a logistics problem rather than one more hull.
    [Fact]
    public void TheSeed_IsOnlyOpenOnceEveryTechIsResearched()
    {
        var research = new Research(Data);
        Assert.DoesNotContain(research.Objectives, o => o.Id == Research.SeedObjective);

        research.UnlockAll();

        var seed = Assert.Single(research.Objectives);
        Assert.Equal(Research.SeedObjective, seed.Id);
        Assert.Equal(6, seed.Needs.Count);
        Assert.Contains(seed.Needs, n => n.Item == "sng_machine_hull" && n.Required == 2);

        foreach (var need in seed.Needs)
            research.Deliver(need.Item, need.Required);

        Assert.True(research.SeedDelivered);
        Assert.Empty(research.Objectives);
    }

    // ---- saves -------------------------------------------------------------

    /// Research survives a save round trip: unlocks, part deliveries, and the
    /// win. A factory that reloads with its research forgotten is worse than
    /// none, because every machine standing on the map becomes unbuildable.
    [Fact]
    public void Research_SurvivesASaveRoundTripExactly()
    {
        var world = NewWorld();
        CompleteTheOpening(world);

        // A messy state on purpose: one line finished, one part delivered, and
        // a tier above that untouched. A world with every tech at the same
        // stage would round-trip a bug in the progress table unnoticed.
        world.PlayerInventory.Add(Data.Item("stm_machine_hull"), 4);
        world.DeliverByHand(Data.Item("stm_machine_hull"), 4);
        world.Research!.Deliver("vlt_machine_hull", 1);

        // Three Voltaic lines left open, because the fourth hull completed
        // Voltaic Metallurgy on arrival: a tech wants exactly one hull, so the
        // only objective that can ever be part-delivered is the Seed.
        Assert.Equal(3, world.Research.Objectives.Count(o => o.Id.StartsWith("tech_vlt_")));
        Assert.True(world.Research.IsTechUnlocked("tech_vlt_metallurgy"));
        // Nine rows: the four opening rungs, the four Steam lines, and the one
        // part-delivered Voltaic line. The opening's rows are the interesting
        // ones here -- they are filed under a *group* key ("any metal ingot"),
        // which is not an item id, and a save that round-tripped them through
        // the item table would lose them.
        Assert.Equal(9, world.Research.Progress.Count());
        Assert.Contains(world.Research.Progress, p => p.Item == "any metal ingot" && p.Count == 12);

        var json = SaveGame.ToJson(SaveGame.Capture(world));
        var loaded = SaveGame.Restore(SaveGame.FromJson(json), Data.Recipes, world.Ground.Gen);

        Assert.NotNull(loaded.Research);
        Assert.Equal(world.Research.UnlockedInOrder, loaded.Research!.UnlockedInOrder);
        Assert.Equal(world.Research.Progress, loaded.Research.Progress);
        Assert.Equal(world.Research.Objectives.Select(o => o.Id),
                     loaded.Research.Objectives.Select(o => o.Id));

        // Byte-identical, which is the property the save format actually
        // claims. Equality of the fields above would pass on a file that wrote
        // them in a different order every time.
        Assert.Equal(json, SaveGame.ToJson(SaveGame.Capture(loaded)));
    }

    /// A part delivery is state a player paid for, so it is saved even though
    /// it unlocked nothing. Asserted separately from the round trip above,
    /// because "the file is identical" would also hold if both sides dropped it.
    [Fact]
    public void APartDeliveredObjective_KeepsItsProgressAcrossASave()
    {
        var world = NewWorld();
        world.Research!.UnlockAll();
        world.Research.Deliver("sng_machine_hull", 1);

        Assert.Equal(1, world.Research.Delivered(Research.SeedObjective, "sng_machine_hull"));

        var loaded = SaveGame.Restore(
            SaveGame.FromJson(SaveGame.ToJson(SaveGame.Capture(world))), Data.Recipes,
            world.Ground.Gen);

        Assert.Equal(1, loaded.Research!.Delivered(Research.SeedObjective, "sng_machine_hull"));
        Assert.False(loaded.Research.SeedDelivered);
    }

    /// The win survives a reload. A player who finished the game and reloaded
    /// into an unfinished one would have lost the only thing the game is for.
    [Fact]
    public void TheWin_SurvivesAReload()
    {
        var world = NewWorld();
        world.Research!.UnlockAll();
        foreach (var need in world.Research.SeedObjectiveNow().Needs)
            world.Research.Deliver(need.Item, need.Required);

        Assert.True(world.Research.SeedDelivered);

        var loaded = SaveGame.Restore(
            SaveGame.FromJson(SaveGame.ToJson(SaveGame.Capture(world))), Data.Recipes,
            world.Ground.Gen);

        Assert.True(loaded.Research!.SeedDelivered);
    }

    /// The save version moved with the format, and a version 9 file is refused
    /// rather than loaded with nothing researched.
    [Fact]
    public void AVersion9Save_IsRefusedRatherThanLoadedWithNoResearch()
    {
        var save = SaveGame.Capture(NewWorld());
        Assert.Equal(SaveFile.CurrentVersion, save.Version);

        save.Version = 9;
        Assert.Throws<SaveLoadException>(() => SaveGame.Restore(save, Data.Recipes));
    }

    /// A world with no research at all round-trips as one. Headless analysis
    /// builds these, and restoring one with an empty tree would silently gate a
    /// world that was never meant to be gated.
    [Fact]
    public void AnUngatedWorld_ReloadsUngated()
    {
        var world = new World(seed: 1, Data.Items);
        Assert.Null(world.Research);

        var loaded = SaveGame.Restore(
            SaveGame.FromJson(SaveGame.ToJson(SaveGame.Capture(world))), Data.Recipes);

        Assert.Null(loaded.Research);
    }
}
