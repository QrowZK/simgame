using Sim;
using Sim.Data;

namespace Sim.Tests;

/// The state hash and the lockstep property (ADR 0037).
public class LockstepTests
{
    private static (World World, BuildCatalogue Builds, Catalogue Catalogue) NewWorld(
        int seed = 31337)
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(seed, catalogue);
        return (world, new BuildCatalogue(catalogue), catalogue);
    }

    [Fact]
    public void TenThousandTicks_OfAShuffledCommandStream_StayHashIdentical()
    {
        // The same scenario `sim.harness --lockstep-test` runs, driven from the
        // suite as well, so the headless check and `dotnet test` cannot
        // disagree about what passing means.
        var report = LockstepSession.Run(20260908, ticks: 10_000);
        Assert.True(report.Ok, string.Join('\n', report.Lines));
        Assert.Contains(report.Lines, l => l.Contains("first tick the two peers disagreed"));
    }

    // ---- what the hash covers ---------------------------------------------

    [Fact]
    public void OneMilliTileOfOnePlayer_ChangesTheHash()
    {
        // The mutation the whole detector exists for: a position that drifts by
        // the smallest unit the sim has.
        var (world, _, _) = NewWorld();
        var before = world.StateHash();
        var player = world.Player;
        player.Restore(player.X + 1, player.Y, player.FacingX, player.FacingY);
        Assert.NotEqual(before, world.StateHash());
    }

    [Fact]
    public void OneTickOfDrift_ChangesTheHash()
    {
        var (world, _, _) = NewWorld();
        var before = world.StateHash();
        world.Tick();
        Assert.NotEqual(before, world.StateHash());
    }

    [Fact]
    public void EveryPartOfTheWorldWeClaimToCover_MovesTheHash()
    {
        var catalogue = Catalogue.Instance;
        var builds = new BuildCatalogue(catalogue);

        // A messy world rather than a tidy one, because a tidy world hides a
        // field: everything below is off its default, and every mutation is a
        // single field of a single record.
        World Fresh()
        {
            var w = NewGame.Create(31337, catalogue);
            NewGame.AddPlayer(w, catalogue, "Byron", 0);
            var blue = NewGame.AddTeam(w, catalogue, "Blue");
            NewGame.AddPlayer(w, catalogue, "Cato", blue.Id);
            w.TryBuild(builds, catalogue.Item(Research.UplinkItem), 2, 2,
                       catalogue.Recipe(Research.UplinkRecipe));
            // A belt, hand-placed rather than built: the belt item is a later
            // tier than a new game can afford, and what the hash has to see is
            // the tile and the items on it, not who paid for it.
            w.BeltMap.PlaceBelt(4, 2, Direction.East, 8);
            w.SyncBelts();
            w.Tick(3);
            return w;
        }

        var reference = Fresh().StateHash();

        void Mutated(string what, Action<World> change)
        {
            var world = Fresh();
            Assert.Equal(reference, world.StateHash());
            change(world);
            Assert.True(reference != world.StateHash(), $"the hash ignores {what}");
        }

        Mutated("the tick", w => w.RestoreTick(w.TickCount + 1));
        Mutated("a player's position", w =>
            w.Players[2].Restore(w.Players[2].X + 1, w.Players[2].Y,
                                 w.Players[2].FacingX, w.Players[2].FacingY));
        Mutated("a player's facing", w =>
            w.Players[1].Restore(w.Players[1].X, w.Players[1].Y, 1, -1));
        Mutated("a player's team", w => w.Players[1].TeamId = 1);
        Mutated("a player's name", w => w.Players[0].Name = "Someone else");
        Mutated("a player's pockets", w =>
            w.Players[0].Inventory.Add(Catalogue.Instance.Item("stone_deposit"), 1));
        Mutated("a team's name", w => w.Teams[1].Name = "Green");
        Mutated("a team's unattended deliveries", w => w.Teams[0].RestoreUnattendedDeliveries(3));
        Mutated("a team's research", w =>
            w.Teams[0].Research!.Deliver("magnetite", 1));
        Mutated("a machine's input buffer", w =>
            w.Machines[0].PushInput(Catalogue.Instance.Item("stone_deposit"), 1));
        Mutated("what is on the ground", w =>
        {
            // A real patch, found the way the prospector finds one: mining bare
            // ground takes nothing and would leave this mutation testing that
            // the hash notices nothing at all.
            var hit = new Prospector().Scan(w.Ground.Gen, 0, 0, new HashSet<int>())[0];
            Assert.Equal(1, HandOps.Mine(w.Ground, hit.X, hit.Y, new Inventory(), 1));
        });
        Mutated("a belt tile", w => w.BeltMap.PlaceBelt(5, 2, Direction.North, 8));
        Mutated("an item on a belt", w =>
        {
            w.SyncBelts();
            w.Belts.Segments[0].LaneAt(0).TryInsertBack(Catalogue.Instance.Item("stone_deposit"));
        });
        Mutated("a pole", w => w.AddPole(new Pole(9, 9, 4, 4), new MachinePlacement(9, 9, 0, 0, 1)));
        Mutated("a fluid node", w => w.Fluids.AddPipe(7, 7));
        Mutated("who owns something", w => w.RegisterBuilt(2, 2, w.Items.GetId("man_uplink"), 1));
        Mutated("a controller's program", w => w.AddController("x = 1"));
        Mutated("a refused command", w =>
            w.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Dig(w.TickCount, 0, 0, 9000, 9000, 1) }));
    }

    /// Research *progress* is hashed, and not merely research *unlocks*.
    ///
    /// The line for "a team's research" in the coverage test above passes even
    /// with the per-objective progress counters left out of the hash, because
    /// the delivery it makes completes an objective and the unlock moves the
    /// hash on its own. Found by mutation: deleting the progress loop from
    /// `StateHash` left all 376 tests green.
    ///
    /// Two peers whose research has advanced by different amounts towards the
    /// same unfinished objective are in different states, and a desync check
    /// that cannot see it would let them run on believing they agree. So this
    /// delivers a *partial* amount -- one of the three ingots
    /// `tech_start_smelting` wants -- which moves nothing but the counter.
    [Fact]
    public void ResearchPartwayToAnObjective_MovesTheHash()
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(31337, catalogue);
        var research = world.Research!;

        // Past the opening objective, so the next one is open and wants more
        // than one of something.
        research.Deliver("magnetite", 1);

        var objective = research.Objectives.First(o => !o.Complete && o.Needs[0].Required > 1);
        var before = world.StateHash();

        var report = research.Deliver(objective.Needs[0].Accepts[0], 1);

        Assert.Equal(1, report.Accepted);
        Assert.False(objective.Complete,
                     "the delivery has to leave the objective unfinished, or this passes on the unlock");
        Assert.True(before != world.StateHash(),
                    "the hash ignores how far a team has got towards an objective");
    }

    [Fact]
    public void TwoPeersRefusingOneCommandForTwoReasons_HaveTwoStateHashes()
    {
        // The reason a peer computes is simulation state. Both worlds below
        // refuse exactly one command and change nothing else, so their tick,
        // their counts and every object in them agree -- only the *reason*
        // differs, and only the command digest carries it into the hash.
        var (a, buildsA, catalogue) = NewWorld();
        var (b, buildsB, _) = NewWorld();

        a.ApplyCommands(buildsA, catalogue.Recipes,
                        new[] { PlayerCommand.Dig(0, 0, 0, 900, 900, 1) });   // NothingThere
        b.ApplyCommands(buildsB, catalogue.Recipes,
                        new[] { PlayerCommand.Dig(0, 0, 0, 900, 900, 0) });   // BadAmount

        Assert.Equal(a.CommandsRefused, b.CommandsRefused);
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void TwoNamesSplitDifferently_AreTwoDifferentHashes()
    {
        // Without a length in front of every string, "ab" then "c" hashes
        // exactly as "a" then "bc": the field boundary disappears and two
        // different worlds collide. Asserted on the primitive, because inside a
        // world there is always some other field between two names, and a test
        // that leans on that is testing the padding rather than the rule.
        var split = Hashing.Mix(Hashing.Mix(Hashing.Seed, "ab"), "c");
        var other = Hashing.Mix(Hashing.Mix(Hashing.Seed, "a"), "bc");
        Assert.NotEqual(split, other);

        // And the empty string is not nothing.
        Assert.NotEqual(Hashing.Seed, Hashing.Mix(Hashing.Seed, ""));
    }

    [Fact]
    public void WhatTheHashExcludes_IsExcludedOnPurposeAndStatedHere()
    {
        var (world, _, _) = NewWorld();
        var before = world.StateHash();

        // The local view. Every peer has a different one by definition, so
        // hashing it would make two correct peers disagree immediately.
        world.SetLocalPlayer(0);
        NewGame.AddPlayer(world, Catalogue.Instance, "Byron", 0);
        var withRoster = world.StateHash();
        Assert.NotEqual(before, withRoster);      // the roster is covered...
        world.SetLocalPlayer(1);
        Assert.Equal(withRoster, world.StateHash());   // ... the view is not.

        // The keyboard. Not saved, and observable through position on the very
        // next tick, which is the reason it is safe to leave out.
        var held = world.StateHash();
        world.Players[0].Intent = new MoveIntent(1, 1);
        Assert.Equal(held, world.StateHash());
        world.Tick();
        Assert.NotEqual(held, world.StateHash());
    }

    [Fact]
    public void TwoDistinctIntents_AlwaysMoveAPlayerToTwoDistinctPlaces()
    {
        // The claim that makes excluding `Intent` from the hash safe: an intent
        // that differs between peers shows up as a position that differs one
        // tick later. If two intents could ever produce one displacement, a
        // peer could hold the wrong one forever unnoticed.
        var seen = new HashSet<(int, int)>();
        for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                var world = new World(1);
                var start = (world.Player.X, world.Player.Y);
                world.Player.Intent = new MoveIntent(x, y);
                world.Tick();
                Assert.True(seen.Add((world.Player.X - start.X, world.Player.Y - start.Y)),
                            $"intent ({x},{y}) moves the player exactly as another one does");
            }

        Assert.Equal(9, seen.Count);
    }

    [Fact]
    public void TheHashSurvivesASaveAndALoad()
    {
        var (world, builds, catalogue) = NewWorld();
        world.ApplyCommands(builds, catalogue.Recipes, new[]
        {
            PlayerCommand.Build(0, 0, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.Dig(0, 0, 1, 900, 900, 1),
        });
        world.Tick(30);

        var save = Sim.Save.SaveGame.Capture(world);
        var loaded = Sim.Save.SaveGame.Restore(
            save, catalogue.Recipes, new WorldGen(31337, NewGame.OreSpecs(catalogue)));

        Assert.Equal(world.StateHash(), loaded.StateHash());
        Assert.Equal(world.CommandDigest, loaded.CommandDigest);
        Assert.Equal(world.CommandsApplied, loaded.CommandsApplied);
        Assert.Equal(world.CommandsRefused, loaded.CommandsRefused);
    }

    [Fact]
    public void TheSaveFormatIsSixteen_AndTheCommandLogIsInIt()
    {
        Assert.Equal(16, Sim.Save.SaveFile.CurrentVersion);

        var (world, builds, catalogue) = NewWorld();
        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Dig(0, 0, 0, 900, 900, 1) });

        var json = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world));
        Assert.Contains($"\"CommandDigest\": \"{world.CommandDigest}\"", json);
        Assert.Contains("\"CommandsRefused\": 1", json);
    }

    [Fact]
    public void ApplyingATicksCommandsInAnyOrder_LandsOnOneWorld()
    {
        // The property, stated small and directly rather than through the
        // 10,000-tick harness: the same batch, six ways round, one hash.
        var catalogue = Catalogue.Instance;
        var hashes = new HashSet<ulong>();

        var batch = new List<PlayerCommand>
        {
            // Three players wanting overlapping things on one tile, which is
            // the case where the applied order decides the outcome.
            PlayerCommand.Build(0, 0, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.Build(0, 1, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.Build(0, 2, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
        };

        foreach (var order in Permutations(batch))
        {
            var world = NewGame.Create(31337, catalogue);
            NewGame.AddPlayer(world, catalogue, "Byron", 0);
            var blue = NewGame.AddTeam(world, catalogue, "Blue");
            NewGame.AddPlayer(world, catalogue, "Cato", blue.Id);

            world.ApplyCommands(new BuildCatalogue(catalogue), catalogue.Recipes, order);
            world.Tick(5);
            hashes.Add(world.StateHash());

            // And the winner is always the lowest player id, never whoever was
            // first in the list.
            Assert.Equal(0, world.OwnerOfAnchor(2, 2));
        }

        Assert.Single(hashes);
    }

    private static IEnumerable<List<PlayerCommand>> Permutations(List<PlayerCommand> items)
    {
        if (items.Count <= 1)
        {
            yield return new List<PlayerCommand>(items);
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = new List<PlayerCommand>(items);
            rest.RemoveAt(i);
            foreach (var tail in Permutations(rest))
            {
                tail.Insert(0, items[i]);
                yield return tail;
            }
        }
    }
}
