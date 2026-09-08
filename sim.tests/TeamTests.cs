using Sim;
using Sim.Data;
using Sim.Save;

namespace Sim.Tests;

/// Teams, the roster, and ownership (ADR 0036).
///
/// Three properties run through everything here.
///
/// **The aliases are aliases.** `World.Player`, `World.PlayerInventory` and
/// `World.Research` mean "the local player's" and nothing else, so they must
/// follow `LocalIndex` rather than being a fourth copy of the same state.
///
/// **Ownership is per building and per team, and it refuses for free.** A
/// refused action costs the actor nothing at all -- no item, no eviction, no
/// hole in the map -- which is the same rule `TryBuild` has always had.
///
/// **Progression is team-owned.** Two teams in one world race the same ladder
/// with two unlock lists, and nothing one team's belt does moves the other's.
public class TeamTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World NewWorld() => NewGame.Create(seed: 4242, Data);

    private static ItemId Uplink => Data.Item(Research.UplinkItem);
    private static Recipe UplinkRecipe => Data.Recipe(Research.UplinkRecipe);

    // ---- the aliases -------------------------------------------------------

    /// A fresh world is one team and one player, and every alias points at it.
    /// This is the property that let 293 call sites survive the change.
    [Fact]
    public void AFreshWorld_IsOneTeamAndOnePlayer_AndTheAliasesPointAtThem()
    {
        var world = NewWorld();

        Assert.Single(world.Teams);
        Assert.Single(world.Players);
        Assert.Equal(0, world.LocalIndex);
        Assert.Same(world.Players[0], world.Player);
        Assert.Same(world.Players[0].Inventory, world.PlayerInventory);
        Assert.Same(world.Teams[0], world.LocalTeam);
        Assert.Same(world.Teams[0].Research, world.Research);
        Assert.Equal(0, world.Player.Id);
        Assert.Equal(0, world.Player.TeamId);
    }

    /// Moving the local view moves all three aliases together. A world where
    /// `Player` followed `LocalIndex` and `Research` did not would hand one
    /// player another team's unlocks and look entirely reasonable doing it.
    [Fact]
    public void TheAliases_FollowTheLocalPlayer_Together()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        // Something in each pocket that is not in the other's, so an alias that
        // pointed at the wrong inventory shows up as a count rather than as a
        // reference comparison a refactor could satisfy by accident.
        var ore = Data.Item("magnetite");
        world.Players[0].Inventory.Add(ore, 7);
        cato.Inventory.Add(ore, 3);

        Assert.Equal(7, world.PlayerInventory.Count(ore));
        Assert.Same(world.Teams[0].Research, world.Research);

        world.SetLocalPlayer(1);

        Assert.Same(cato, world.Player);
        Assert.Equal(3, world.PlayerInventory.Count(ore));
        Assert.Same(world.Teams[1], world.LocalTeam);
        Assert.Same(world.Teams[1].Research, world.Research);
        Assert.NotSame(world.Teams[0].Research, world.Research);
    }

    /// `World.Research`'s setter writes the *local team's* research, which is
    /// what `NewGame.Create` and every existing caller meant by it.
    [Fact]
    public void SettingWorldResearch_SetsTheLocalTeamsAndNobodyElses()
    {
        var world = NewWorld();
        NewGame.AddTeam(world, Data, "Blue");
        var blueResearch = world.Teams[1].Research;

        var replacement = new Research(Data);
        world.Research = replacement;

        Assert.Same(replacement, world.Teams[0].Research);
        Assert.Same(blueResearch, world.Teams[1].Research);
    }

    /// A player id past the end of the roster is a programming error, not a
    /// silent clamp to player 0 -- which is how one peer's command would end up
    /// applied with another peer's hands under lockstep.
    [Fact]
    public void AskingForAPlayerOrTeamThatDoesNotExist_Throws()
    {
        var world = NewWorld();
        Assert.Throws<ArgumentOutOfRangeException>(() => world.SetLocalPlayer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.SetLocalPlayer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.AddPlayer("Nobody", 4));
    }

    // ---- ownership ---------------------------------------------------------

    /// What a player builds belongs to their team, and the record survives the
    /// build going through the ordinary path.
    [Fact]
    public void WhatAPlayerBuilds_BelongsToTheirTeam()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);
        cato.TeleportToTile(40, 40);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe, Direction.East));
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, Uplink, 42, 42, UplinkRecipe, Direction.East, cato));

        Assert.Equal(0, world.OwnerOfAnchor(2, 2));
        Assert.Equal(1, world.OwnerOfAnchor(42, 42));

        // And through a covered tile, not just the anchor: an Uplink is 1x1
        // here, so the interesting case is the tile with nothing on it.
        Assert.Equal(0, world.OwnerAt(2, 2));
        Assert.Equal(Team.NoTeam, world.OwnerAt(9, 9));
    }

    /// A machine placed outside `TryBuild` -- demo worlds, headless analysis --
    /// is owned by nobody, and nobody's is everybody's. That is what keeps
    /// those worlds behaving exactly as they did before teams existed.
    [Fact]
    public void ABuildingPlacedOutsideTryBuild_IsUnownedAndNobodyIsRefused()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        world.TryPlaceMachine(UplinkRecipe, new MachinePlacement(3, 3, 0, 0, 1),
                              sourceItem: Uplink);
        world.RegisterBuilt(3, 3, Uplink);

        Assert.Equal(Team.NoTeam, world.OwnerOfAnchor(3, 3));
        Assert.True(world.MayAct(cato, 3, 3));
        Assert.True(world.MayAct(world.Player, 3, 3));

        Assert.Equal(RemoveResult.Ok, world.TryRemove(3, 3, cato).Result);
    }

    /// A rival is refused, and refused *before* reach: walking closer to
    /// somebody else's smelter never helps, so telling them to walk is a
    /// wasted trip per refusal.
    [Fact]
    public void ARivalIsRefused_BeforeReach_AndItCostsThemNothing()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe);

        var carriedBefore = cato.Inventory.Count(Uplink);

        // Two hundred tiles away: `TooFar` would also be true, and is the
        // wrong sentence.
        cato.TeleportToTile(200, 200);
        Assert.Equal(RemoveResult.OtherTeam, world.TryRemove(2, 2, cato).Result);

        // Standing on it: still refused, and now `TooFar` is not even true, so
        // this is the ownership rule on its own.
        cato.TeleportToTile(2, 2);
        Assert.Equal(RemoveResult.OtherTeam, world.TryRemove(2, 2, cato).Result);

        // Nothing moved: the Uplink is still there, still Red's, and Cato is
        // carrying exactly what he was.
        Assert.True(world.TryMachineAt(2, 2, out _, out _));
        Assert.Equal(0, world.OwnerOfAnchor(2, 2));
        Assert.Equal(carriedBefore, cato.Inventory.Count(Uplink));
    }

    /// A teammate's building is yours. That is what makes a team a team, and
    /// it is the case that would silently disappear if ownership compared
    /// player ids instead of team ids.
    [Fact]
    public void ATeammatesBuilding_ComesUpAndLandsInTheHandsThatPulledIt()
    {
        var world = NewWorld();
        var ada = world.Player;
        var byron = NewGame.AddPlayer(world, Data, "Byron", teamId: 0);

        byron.TeleportToTile(6, 6);
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, Uplink, 6, 6, UplinkRecipe, Direction.East, byron));

        var adaBefore = ada.Inventory.Count(Uplink);
        var byronBefore = byron.Inventory.Count(Uplink);

        var report = world.TryRemove(6, 6, ada);

        Assert.Equal(RemoveResult.Ok, report.Result);
        Assert.Equal(adaBefore + 1, ada.Inventory.Count(Uplink));
        Assert.Equal(byronBefore, byron.Inventory.Count(Uplink));
        Assert.Equal(Team.NoTeam, world.OwnerOfAnchor(6, 6));
    }

    /// Retasking a rival's machine is its own refusal, and it evicts nothing.
    /// A refusal that dumped the machine's buffer into the rival's pockets
    /// would be a way to rob a factory you cannot remove.
    [Fact]
    public void RetaskingARivalsMachine_IsRefused_AndEvictsNothing()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe);
        Assert.True(world.TryMachineAt(2, 2, out var machine, out var index));

        var ore = Data.Item("magnetite");
        machine.PushInput(ore, 5);
        var catoBefore = cato.Inventory.Count(ore);

        var result = world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"),
                                           out var evicted, cato);

        Assert.Equal(RecipeChangeResult.OtherTeam, result);
        Assert.Equal(0, evicted);
        Assert.Equal(catoBefore, cato.Inventory.Count(ore));
        Assert.Equal(5, machine.InputContents[ore]);
    }

    /// A rival's Uplink is not an Uplink you can reach. Skipped in the search
    /// rather than found and refused, so the player is told the one useful
    /// thing: none of yours is near.
    [Fact]
    public void ARivalsUplink_IsNotAnUplinkInReach()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe);
        cato.TeleportToTile(2, 2);

        Assert.False(world.TryUplinkInHandReach(out _, cato));
        Assert.True(world.TryUplinkInHandReach(out var mine, world.Player));
        Assert.True(mine >= 0);

        var stone = Data.Item("stone_deposit");
        Assert.Equal(DeliveryRefusal.NoUplinkInReach,
                     world.DeliverByHand(stone, 1, cato).Refusal);
    }

    // ---- progression is team-owned ----------------------------------------

    /// The Uplink credits the team that *built* it, not the team of whoever is
    /// at the keyboard. Two Uplinks four tiles apart feed two ladders.
    [Fact]
    public void AnUplinkCreditsTheTeamThatBuiltIt_NotTheLocalOne()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);
        cato.TeleportToTile(8, 0);

        world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe);
        world.TryBuild(Buildables, Uplink, 8, 2, UplinkRecipe, Direction.East, cato);

        var ore = Data.Item("magnetite");
        Assert.True(world.TryMachineAt(8, 2, out var blueUplink, out _));
        blueUplink.PushInput(ore, 1);

        var redUnlocked = world.Teams[0].Research!.UnlockedInOrder.Count();
        world.Tick();

        Assert.Equal(1, world.Teams[1].UnattendedDeliveries);
        Assert.Equal(0, world.Teams[0].UnattendedDeliveries);
        Assert.Equal(redUnlocked, world.Teams[0].Research!.UnlockedInOrder.Count());
        Assert.True(world.Teams[1].Research!.UnlockedInOrder.Count() > redUnlocked);
    }

    /// The reward for an unattended delivery lands with the owning team's
    /// first player, not with the local one. A belt has no hands, and roster
    /// order is the only tie-break that does not depend on where anybody is
    /// standing -- which is exactly what a lockstep peer cannot afford.
    [Fact]
    public void AnUnattendedRewardLandsWithTheOwningTeamsFirstPlayer()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);
        var dora = NewGame.AddPlayer(world, Data, "Dora", blue.Id);
        cato.TeleportToTile(8, 0);

        world.TryBuild(Buildables, Uplink, 8, 2, UplinkRecipe, Direction.East, cato);
        Assert.True(world.TryMachineAt(8, 2, out var blueUplink, out _));

        var adaBefore = Snapshot(world.Players[0].Inventory);
        var catoBefore = Snapshot(cato.Inventory);
        var doraBefore = Snapshot(dora.Inventory);

        blueUplink.PushInput(Data.Item("magnetite"), 1);
        world.Tick();

        // The first Blue in roster order is Cato, so the payout is his.
        Assert.NotEqual(catoBefore, Snapshot(cato.Inventory));
        Assert.Equal(adaBefore, Snapshot(world.Players[0].Inventory));
        Assert.Equal(doraBefore, Snapshot(dora.Inventory));
    }

    /// A hand delivery credits the delivering player's team and pays them, and
    /// it is not an unattended delivery however it is routed.
    [Fact]
    public void AHandDelivery_CreditsTheDeliveringPlayersTeam()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);
        cato.TeleportToTile(8, 0);

        world.TryBuild(Buildables, Uplink, 8, 2, UplinkRecipe, Direction.East, cato);

        var ore = Data.Item("magnetite");
        cato.Inventory.Add(ore, 1);

        var redUnlocked = world.Teams[0].Research!.UnlockedInOrder.Count();
        var report = world.DeliverByHand(ore, 1, cato);

        Assert.Equal(DeliveryRefusal.None, report.Refusal);
        Assert.Equal(1, report.Accepted);
        Assert.True(world.Teams[1].Research!.UnlockedInOrder.Count() > redUnlocked);
        Assert.Equal(redUnlocked, world.Teams[0].Research!.UnlockedInOrder.Count());
        Assert.Equal(0, world.Teams[1].UnattendedDeliveries);
        Assert.Equal(0, world.Teams[0].UnattendedDeliveries);
    }

    /// A build is gated on the *actor's* team's research, not the local one's.
    /// The whole point of two ladders is that one team can build what the
    /// other cannot yet.
    [Fact]
    public void ABuildIsGatedOnTheActorsTeamsResearch()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        // Something gated at tick zero, found from the graph rather than named:
        // a data change that opened the recipe this test picked would otherwise
        // turn it green while testing nothing.
        var gate = new Research(Data);
        var (recipe, buildable) =
            Buildables.All
                      .SelectMany(b => Data.Recipes.Values
                                           .OrderBy(r => r.Id, StringComparer.Ordinal)
                                           .Where(r => Buildables.CanRun(b, r))
                                           .Select(r => (Recipe: r, Buildable: b)))
                      .First(pair => !gate.IsUnlocked(pair.Recipe));

        world.Teams[0].Research!.UnlockAll();

        world.Player.Inventory.Add(buildable.Item, 1);
        cato.Inventory.Add(buildable.Item, 1);
        cato.TeleportToTile(40, 40);

        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, buildable.Item, 2, 2, recipe));
        Assert.Equal(BuildResult.NotResearched,
                     world.TryBuild(Buildables, buildable.Item, 42, 42, recipe,
                                    Direction.East, cato));

        // And the refused build cost Cato nothing.
        Assert.Equal(1, cato.Inventory.Count(buildable.Item));
    }

    // ---- the save ----------------------------------------------------------

    /// The save format moved, and the previous one is refused rather than read
    /// as a world with one team and everybody's unlocks in it.
    [Fact]
    public void TheSaveVersionMoved_AndFourteenIsRefused()
    {
        Assert.Equal(15, SaveFile.CurrentVersion);

        var save = SaveGame.Capture(NewWorld());
        save.Version = 14;

        var thrown = Assert.ThrowsAny<Exception>(
            () => SaveGame.Restore(save, Data.Recipes));
        Assert.Contains("14", thrown.Message);
    }

    /// The round trip, on a deliberately untidy world: three players, two
    /// teams, three positions, three facings, three different inventories and
    /// two ladders at two different rungs.
    ///
    /// Untidy on purpose. A save test where everybody sits at the same spot on
    /// the same team with the same pockets round-trips a dropped field without
    /// noticing -- which is what happened to belt facing (CLAUDE.md).
    [Fact]
    public void AMessyRoster_RoundTripsFieldForField()
    {
        var world = MessyWorld();

        var save = SaveGame.Capture(world);
        var loaded = SaveGame.Restore(save, Data.Recipes);

        Assert.Equal(3, loaded.Players.Count);
        Assert.Equal(2, loaded.Teams.Count);
        Assert.Equal(world.LocalIndex, loaded.LocalIndex);

        for (var i = 0; i < world.Players.Count; i++)
        {
            var before = world.Players[i];
            var after = loaded.Players[i];

            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.TeamId, after.TeamId);
            Assert.Equal(before.X, after.X);
            Assert.Equal(before.Y, after.Y);
            Assert.Equal(before.FacingX, after.FacingX);
            Assert.Equal(before.FacingY, after.FacingY);
            Assert.Equal(Snapshot(before.Inventory), Snapshot(after.Inventory));
        }

        for (var i = 0; i < world.Teams.Count; i++)
        {
            Assert.Equal(world.Teams[i].Name, loaded.Teams[i].Name);
            Assert.Equal(world.Teams[i].UnattendedDeliveries,
                         loaded.Teams[i].UnattendedDeliveries);
            Assert.Equal(world.Teams[i].Research!.UnlockedInOrder,
                         loaded.Teams[i].Research!.UnlockedInOrder);
            Assert.Equal(world.Teams[i].Research!.Progress.ToList(),
                         loaded.Teams[i].Research!.Progress.ToList());
        }

        Assert.Equal(0, loaded.OwnerOfAnchor(2, 2));
        Assert.Equal(1, loaded.OwnerOfAnchor(42, 42));

        // And the whole file, byte for byte, which catches anything the field
        // list above forgot to name.
        Assert.Equal(SaveGame.ToJson(save), SaveGame.ToJson(SaveGame.Capture(loaded)));
    }

    /// The three per-player fields that a tidy save test cannot tell apart.
    /// Every player differs from every other in position, facing *and* pocket,
    /// so swapping two players or dropping one field fails rather than
    /// round-tripping unnoticed.
    [Fact]
    public void EveryPlayerDiffersFromEveryOther_SoASwapWouldShow()
    {
        var world = MessyWorld();

        Assert.Equal(3, world.Players.Select(p => (p.X, p.Y)).Distinct().Count());
        Assert.Equal(3, world.Players.Select(p => (p.FacingX, p.FacingY)).Distinct().Count());
        Assert.Equal(3, world.Players.Select(p => Snapshot(p.Inventory)).Distinct().Count());
        Assert.Equal(2, world.Players.Select(p => p.TeamId).Distinct().Count());
        Assert.NotEqual(world.Teams[0].Research!.UnlockedInOrder.Count(),
                        world.Teams[1].Research!.UnlockedInOrder.Count());
    }

    /// Player 0 is not always on team 0, and a load that assumed so would put
    /// the founding player back on the founding team.
    ///
    /// Worth its own test because a `World` is *born* with player 0 on team 0:
    /// every other player is restored by being added, which carries the team
    /// with it, and player 0 is the only one restored in place.
    [Fact]
    public void PlayerZeroOnASecondTeam_RoundTrips()
    {
        var world = NewWorld();
        var blue = NewGame.AddTeam(world, Data, "Blue");
        world.Players[0].TeamId = blue.Id;
        world.Players[0].Name = "Defector";

        var loaded = SaveGame.Restore(SaveGame.Capture(world), Data.Recipes);

        Assert.Equal(1, loaded.Players[0].TeamId);
        Assert.Equal("Defector", loaded.Players[0].Name);
        Assert.Same(loaded.Teams[1], loaded.LocalTeam);
        Assert.Same(loaded.Teams[1].Research, loaded.Research);
    }

    /// Ownership survives the round trip as a *behaviour*, not just as a
    /// number: a rival is still refused after a reload.
    [Fact]
    public void OwnershipSurvivesAReload_AsARefusal()
    {
        var world = MessyWorld();
        var loaded = SaveGame.Restore(SaveGame.Capture(world), Data.Recipes);

        var cato = loaded.Players[2];
        cato.TeleportToTile(2, 2);

        Assert.Equal(RemoveResult.OtherTeam, loaded.TryRemove(2, 2, cato).Result);
        Assert.Equal(RemoveResult.Ok, loaded.TryRemove(2, 2, loaded.Players[0]).Result);
    }

    // ---- determinism -------------------------------------------------------

    /// Two worlds, the same seed, the same commands from three players on two
    /// teams: identical bytes after a thousand ticks. This is the premise
    /// slice 3 is built on, and the roster is new state inside it.
    [Fact]
    public void TwoWorlds_WithTheSameCommands_StayByteIdentical()
    {
        var a = MessyWorld();
        var b = MessyWorld();

        for (var tick = 0; tick < 1000; tick++)
        {
            // Three players walking different ways, so the per-player tick
            // order is exercised rather than one player and two statues.
            a.Players[0].Intent = b.Players[0].Intent = new MoveIntent(1, 0);
            a.Players[1].Intent = b.Players[1].Intent = new MoveIntent(-1, 1);
            a.Players[2].Intent = b.Players[2].Intent = new MoveIntent(0, -1);
            a.Tick();
            b.Tick();
        }

        Assert.Equal(SaveGame.ToJson(SaveGame.Capture(a)),
                     SaveGame.ToJson(SaveGame.Capture(b)));
    }

    // ---- the headless harness ---------------------------------------------

    /// The scenario `sim.harness --teams-test` runs, run here too, so a green
    /// CI headless run and a green test suite mean the same thing.
    [Fact]
    public void TheHeadlessTeamsSession_Passes()
    {
        var report = TeamSession.Run();
        Assert.True(report.Ok, string.Join("\n", report.Failures));
        Assert.Contains("--- teams ok ---", report.Lines);
    }

    // ---- helpers -----------------------------------------------------------

    /// Two teams, three players, nothing shared and nothing symmetrical.
    private static World MessyWorld()
    {
        var world = NewWorld();
        world.Teams[0].Name = "Red";

        var ada = world.Player;
        ada.Name = "Ada";

        var byron = NewGame.AddPlayer(world, Data, "Byron", teamId: 0);
        var blue = NewGame.AddTeam(world, Data, "Blue");
        var cato = NewGame.AddPlayer(world, Data, "Cato", blue.Id);

        // Three positions, and not on tile centres: a save that stored a tile
        // instead of milli-tiles would pass a test that only used centres.
        ada.Restore(1_234, 5_678, 1, 0);
        byron.Restore(-9_876, 4_321, 0, -1);
        cato.Restore(41_500, 41_500, -1, 1);

        // Three different pockets.
        ada.Inventory.Add(Data.Item("magnetite"), 13);
        byron.Inventory.Add(Data.Item("stone_deposit"), 5);
        cato.Inventory.Add(Data.Item("cassiterite"), 2);

        // One building each side, so ownership has two values to confuse.
        ada.TeleportToTile(2, 2);
        world.TryBuild(Buildables, Uplink, 2, 2, UplinkRecipe);
        ada.Restore(1_234, 5_678, 1, 0);

        world.TryBuild(Buildables, Uplink, 42, 42, UplinkRecipe, Direction.East, cato);

        // Two ladders at two rungs: Blue delivers by machine, Red by nothing.
        Assert.True(world.TryMachineAt(42, 42, out var blueUplink, out _));
        blueUplink.PushInput(Data.Item("magnetite"), 1);
        world.Tick();

        // And the local view is not looking through player 0, so a load that
        // forgot to restore `LocalPlayer` shows up rather than landing on the
        // default and looking right.
        world.SetLocalPlayer(1);

        // The tick above walked nobody -- every intent is Still -- so the
        // hand-placed positions above are still exactly what was set.
        return world;
    }

    private static string Snapshot(Inventory inventory)
        => string.Join(",", inventory.Contents
                                     .OrderBy(kv => kv.Key.Value)
                                     .Select(kv => $"{kv.Key.Value}:{kv.Value}"));
}
