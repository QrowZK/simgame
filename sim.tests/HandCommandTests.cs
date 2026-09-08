using Sim;
using Sim.Data;

namespace Sim.Tests;

/// Hand-feeding a machine and taking its output, as commands (ADR 0040).
///
/// This is the first hour of the game -- a bench, twelve stone, and a pair of
/// hands -- and until this slice it was the one part of the game two people
/// could not play together, because there was no command kind for it.
///
/// The world these tests use is a *real* new game: the real bench, the real
/// `build_man_furnace` recipe and the real starter kit. A hand-rolled machine
/// with a two-item recipe would pass everything here and prove nothing about
/// the opening the player actually meets.
public class HandCommandTests
{
    private const string Bench = "man_manual_crafting";
    private const string FurnaceRecipe = "build_man_furnace";
    private const string Stone = "stone_deposit";

    private sealed class Fixture
    {
        public required World World;
        public required BuildCatalogue Builds;
        public required Catalogue Catalogue;
        public int X;
        public int Y;

        public List<CommandResult> Apply(params PlayerCommand[] commands)
        {
            var results = new List<CommandResult>();
            World.ApplyCommands(Builds, Catalogue.Recipes, commands, results);
            return results;
        }

        /// Results in the order they were *applied*, which is the total order
        /// and not the order they were handed over.
        public CommandResult For(List<CommandResult> results, int player)
            => results.Single(r => r.Command.PlayerId == player);

        public Machine TheBench
        {
            get
            {
                Assert.True(World.TryMachineAt(X, Y, out var machine, out _));
                return machine;
            }
        }
    }

    /// A world with a bench standing two tiles from the player, running the
    /// furnace recipe, and a player holding the starter kit's 24 stone.
    private static Fixture Setup(int seed = 4242, int players = 1)
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(seed, catalogue);
        for (var i = 1; i < players; i++)
            NewGame.AddPlayer(world, catalogue, $"P{i}", teamId: world.Player.TeamId);

        var fixture = new Fixture
        {
            World = world,
            Builds = new BuildCatalogue(catalogue),
            Catalogue = catalogue,
            X = world.Player.TileX + 2,
            Y = world.Player.TileY,
        };

        var built = fixture.Apply(PlayerCommand.Build(world.TickCount, 0, 0,
                                                      fixture.X, fixture.Y, Bench,
                                                      FurnaceRecipe));
        Assert.Equal(CommandOutcome.Ok, built[0].Outcome);
        return fixture;
    }

    private static ItemId StoneId(World world)
    {
        Assert.True(world.Items.TryGetId(Stone, out var id));
        return id;
    }

    [Fact]
    public void LoadingByCommand_MovesExactlyOneCyclesWorth_AndSaysHowMany()
    {
        var f = Setup();
        var stone = StoneId(f.World);
        var perCycle = f.TheBench.InputPerCycle(stone);

        Assert.Equal(12, perCycle);
        Assert.Equal(24, f.World.Player.Inventory.Count(stone));

        var result = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y))[0];

        Assert.Equal(CommandOutcome.Ok, result.Outcome);
        Assert.Equal(12, result.Detail);
        Assert.Equal(12, f.TheBench.GetInputCount(stone));
        Assert.Equal(12, f.World.Player.Inventory.Count(stone));

        // Two cycles at once is two cycles' worth, not two of something else.
        var second = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 2, f.X, f.Y, 2))[0];
        Assert.Equal(CommandOutcome.Ok, second.Outcome);
        Assert.Equal(12, second.Detail);
        Assert.Equal(24, f.TheBench.GetInputCount(stone));
        Assert.Equal(0, f.World.Player.Inventory.Count(stone));
    }

    [Fact]
    public void AMachineThatWantsNothing_IsRefusedWithItsOwnReason_AndCostsNothing()
    {
        var f = Setup();
        var stone = StoneId(f.World);
        f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y));

        var before = f.World.Player.Inventory.Count(stone);
        var again = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 2, f.X, f.Y))[0];

        // Not Ok-with-zero: "it worked and moved nothing" is the shape of this
        // bug, and it reads to a player as a button that does nothing.
        Assert.Equal(CommandOutcome.WantsNothing, again.Outcome);
        Assert.False(again.Ok);
        Assert.Equal(0, again.Detail);
        Assert.Equal(before, f.World.Player.Inventory.Count(stone));
        Assert.Equal(12, f.TheBench.GetInputCount(stone));
    }

    [Fact]
    public void AMachineThatWantsSomethingYouDoNotHave_SaysSo()
    {
        var f = Setup();
        var stone = StoneId(f.World);
        f.World.Player.Inventory.Take(stone, 24);

        var result = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y))[0];

        // NoneCarried, not WantsNothing: the machine wants twelve stone and the
        // player's answer is to go and dig, which is a different walk from
        // "wait for it to finish".
        Assert.Equal(CommandOutcome.NoneCarried, result.Outcome);
        Assert.Equal(0, f.TheBench.GetInputCount(stone));
    }

    [Fact]
    public void ZeroCyclesIsNotAnAmount()
    {
        var f = Setup();
        var result = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y, 0))[0];
        Assert.Equal(CommandOutcome.BadAmount, result.Outcome);
        Assert.Equal(0, f.TheBench.InputContents.Values.Sum());
    }

    [Fact]
    public void LoadingAndTaking_TakeHandReach_NotBuildReach()
    {
        // The decision this slice had to make and ADR 0040 argues: the arms
        // that dig are the arms that load. Pinned as an inequality *and* as two
        // exact distances, so widening either radius is a visible change.
        Assert.True(Sim.Player.HandReachTiles < Sim.Player.BuildReachTiles);

        var f = Setup();

        // Standing eight tiles off: inside build reach, outside hand reach.
        f.World.Player.TeleportToTile(f.X - 8, f.Y);
        var farLoad = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y))[0];
        var farTake = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 2, f.X, f.Y))[0];

        Assert.True(f.World.InBuildReach(f.X, f.Y));
        Assert.False(f.World.InHandReach(f.X, f.Y));
        Assert.Equal(CommandOutcome.TooFar, farLoad.Outcome);
        Assert.Equal(CommandOutcome.TooFar, farTake.Outcome);

        // And a step in is enough.
        f.World.Player.TeleportToTile(f.X - 4, f.Y);
        var near = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 3, f.X, f.Y))[0];
        Assert.Equal(CommandOutcome.Ok, near.Outcome);
    }

    [Fact]
    public void TakingEmptiesTheOutput_AndTakingAgainSaysThereIsNothingWaiting()
    {
        var f = Setup();
        var furnace = f.Catalogue.Item("man_furnace");

        var early = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 1, f.X, f.Y))[0];
        Assert.Equal(CommandOutcome.NothingToTake, early.Outcome);

        f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 2, f.X, f.Y));
        f.World.Tick(f.Catalogue.Recipes[FurnaceRecipe].DurationTicks + 1);
        Assert.Equal(1, f.TheBench.GetOutputCount(furnace));

        var carried = f.World.Player.Inventory.Count(furnace);
        var took = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 3, f.X, f.Y))[0];

        Assert.Equal(CommandOutcome.Ok, took.Outcome);
        Assert.Equal(1, took.Detail);
        Assert.Equal(carried + 1, f.World.Player.Inventory.Count(furnace));
        Assert.Equal(0, f.TheBench.GetOutputCount(furnace));

        var again = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 4, f.X, f.Y))[0];
        Assert.Equal(CommandOutcome.NothingToTake, again.Outcome);
        Assert.Equal(carried + 1, f.World.Player.Inventory.Count(furnace));
    }

    [Fact]
    public void BareGroundIsNoMachine_AndAMinerWantsNothingButCanBeEmptied()
    {
        var f = Setup();
        var away = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1,
                                              f.World.Player.TileX + 1,
                                              f.World.Player.TileY + 1))[0];
        Assert.Equal(CommandOutcome.NoMachine, away.Outcome);

        // A miner eats nothing and holds what it has dug, so the two commands
        // give two different answers on the same tile.
        var ore = f.Catalogue.Item("magnetite");
        var placement = new MachinePlacement(f.World.Player.TileX + 1,
                                             f.World.Player.TileY + 1, 0, 0, 1);
        var miner = f.World.AddSavedMiner(ore, placement, Miner.DefaultCycleTicks, 1);
        miner.Restore(MachineState.Idle, 0, 7);

        var load = f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 2,
                                              placement.X, placement.Y))[0];
        Assert.Equal(CommandOutcome.WantsNothing, load.Outcome);

        var take = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 3,
                                              placement.X, placement.Y))[0];
        Assert.Equal(CommandOutcome.Ok, take.Outcome);
        Assert.Equal(7, take.Detail);
        Assert.Equal(7, f.World.Player.Inventory.Count(ore));
        Assert.Equal(0, miner.Buffered);
    }

    [Fact]
    public void ARivalsMachine_RefusesBothHands_AndTheRefusalCostsNothing()
    {
        var catalogue = Catalogue.Instance;
        var f = Setup();
        var blue = NewGame.AddTeam(f.World, catalogue, "Blue");
        var rival = NewGame.AddPlayer(f.World, catalogue, "Rival", blue.Id);
        rival.TeleportToTile(f.X, f.Y);

        var stone = StoneId(f.World);
        var carried = rival.Inventory.Count(stone);

        var results = f.Apply(
            PlayerCommand.Load(f.World.TickCount, 1, 0, f.X, f.Y),
            PlayerCommand.Take(f.World.TickCount, 1, 1, f.X, f.Y));

        Assert.Equal(CommandOutcome.OtherTeam, results[0].Outcome);
        Assert.Equal(CommandOutcome.OtherTeam, results[1].Outcome);
        Assert.Equal(carried, rival.Inventory.Count(stone));
        Assert.Equal(0, f.TheBench.InputContents.Values.Sum());
    }

    [Fact]
    public void TwoPlayersLoadingOneMachineOnOneTick_AreSettledByTheTotalOrder()
    {
        // The contested case, and the reason this is not "whoever clicked
        // first": arrival order is the one input two peers do not share.
        var f = Setup(players: 2);
        var second = f.World.Players[1];
        second.TeleportToTile(f.X - 1, f.Y);

        var tick = f.World.TickCount;
        var first = PlayerCommand.Load(tick, 0, 5, f.X, f.Y);
        var other = PlayerCommand.Load(tick, 1, 5, f.X, f.Y);

        var forwards = f.Apply(first, other);
        Assert.Equal(CommandOutcome.Ok, f.For(forwards, 0).Outcome);
        Assert.Equal(CommandOutcome.WantsNothing, f.For(forwards, 1).Outcome);

        var stone = StoneId(f.World);
        Assert.Equal(12, f.World.Players[0].Inventory.Count(stone));
        Assert.Equal(24, second.Inventory.Count(stone));

        // Now the same tick on the other peer, where the packets landed the
        // other way round. Same winner, same loser, same pockets.
        var mirror = Setup(players: 2);
        mirror.World.Players[1].TeleportToTile(mirror.X - 1, mirror.Y);
        var backwards = mirror.Apply(other, first);

        Assert.Equal(CommandOutcome.Ok, mirror.For(backwards, 0).Outcome);
        Assert.Equal(CommandOutcome.WantsNothing, mirror.For(backwards, 1).Outcome);
        Assert.Equal(f.World.StateHash(), mirror.World.StateHash());
    }

    [Fact]
    public void WhatALoadAndATakeChange_IsInTheStateHash()
    {
        // If it is not in the hash, two peers can disagree about it forever and
        // the desync detector will keep telling them everything is fine.
        //
        // The commands are deliberately *not* used to make the difference here.
        // Every command is folded into the command digest, and the digest is in
        // the hash, so "load on one world and compare" goes on passing when the
        // machine's buffers are dropped from the hash entirely -- the first
        // version of this test did exactly that and survived the mutation. What
        // is compared is two worlds that were given the same commands and then
        // differ only in where the items are.
        var f = Setup();
        var g = Setup();
        Assert.Equal(f.World.StateHash(), g.World.StateHash());

        var stone = StoneId(f.World);
        var furnace = f.Catalogue.Item("man_furnace");

        // The *hopper* only. Moving the stone out of the player's pockets as
        // well would make the two worlds differ in the inventory too, and the
        // hash would part for that reason while the machine's buffer went
        // unhashed -- which is the mutation this is here to catch.
        f.TheBench.PushInput(stone, 12);
        Assert.NotEqual(g.World.StateHash(), f.World.StateHash());

        g.TheBench.PushInput(stone, 12);
        Assert.Equal(f.World.StateHash(), g.World.StateHash());

        // And the same for what a take moves: out of the output buffer and into
        // a pair of pockets.
        var inputs = new[] { (Item: stone, Count: 12) };
        var outputs = new[] { (Item: furnace, Count: 1) };
        f.TheBench.Restore(MachineState.Idle, 0, inputs, outputs);
        g.TheBench.Restore(MachineState.Idle, 0, inputs, outputs);
        Assert.Equal(f.World.StateHash(), g.World.StateHash());

        Assert.Equal(1, f.TheBench.PullOutput(furnace, 1));
        Assert.NotEqual(g.World.StateHash(), f.World.StateHash());

        Assert.Equal(1, g.TheBench.PullOutput(furnace, 1));
        Assert.Equal(f.World.StateHash(), g.World.StateHash());

        // And the pockets, which is the other end of both operations.
        f.World.Player.Inventory.Add(furnace, 1);
        Assert.NotEqual(g.World.StateHash(), f.World.StateHash());
    }

    [Fact]
    public void ALoadAndATake_AreInTheCommandDigestAsWell()
    {
        // The other half: the command and its outcome, which is what makes a
        // *refused* load a hashable event.
        var f = Setup();
        var g = Setup();

        f.Apply(PlayerCommand.Load(f.World.TickCount, 0, 1, f.X, f.Y));
        g.Apply(PlayerCommand.Take(g.World.TickCount, 0, 1, g.X, g.Y));

        Assert.NotEqual(g.World.CommandDigest, f.World.CommandDigest);
    }

    [Fact]
    public void ARefusedHandCommand_ChangesNothingButTheDigest()
    {
        var f = Setup();
        var quiet = Setup();

        // One world is asked for something impossible; the other is asked
        // nothing at all. Nothing in the world moves, and the two must still be
        // distinguishable -- a peer that lost a refusal is a peer that diverged.
        var refused = f.Apply(PlayerCommand.Take(f.World.TickCount, 0, 9, f.X, f.Y))[0];

        Assert.Equal(CommandOutcome.NothingToTake, refused.Outcome);
        Assert.Equal(quiet.World.CommandsApplied, f.World.CommandsApplied);
        Assert.Equal(quiet.World.CommandsRefused + 1, f.World.CommandsRefused);
        Assert.NotEqual(quiet.World.CommandDigest, f.World.CommandDigest);
        Assert.NotEqual(quiet.World.StateHash(), f.World.StateHash());
    }

    [Fact]
    public void TheTwoNewKinds_SurviveTheRoundTripToBytes()
    {
        var batch = new[]
        {
            PlayerCommand.Load(11, 2, 3, -7, 900, 4),
            PlayerCommand.Take(11, 2, 4, int.MinValue, int.MaxValue),
        };

        var back = CommandCodec.Decode(CommandCodec.Encode(batch));

        Assert.Equal(CommandKind.Load, back[0].Kind);
        Assert.Equal(4, back[0].Amount);
        Assert.Equal(-7, back[0].X);
        Assert.Equal(900, back[0].Y);
        Assert.Equal(CommandKind.Take, back[1].Kind);
        Assert.Equal(int.MinValue, back[1].X);
        Assert.Equal(int.MaxValue, back[1].Y);
    }

    [Fact]
    public void ABatchFromABuildThatCannotSpeakTheseKinds_IsRefusedAtTheDoor()
    {
        // The format byte moved to 2 when the kinds were added. A peer that
        // speaks 1 must refuse the whole batch rather than the first load in
        // it: half a tick's commands is a desync with extra steps.
        Assert.Equal(2, CommandCodec.Format);

        var bytes = CommandCodec.Encode(PlayerCommand.Load(1, 0, 0, 2, 3));
        bytes[2] = 1;

        var thrown = Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));
        Assert.Contains("format 1", thrown.Message);
    }

    /// The slice, end to end: two peers, one furnace, one command stream in two
    /// different orders, and the contested tick in the middle of it.
    [Fact]
    public void TwoPeersHandFeedOneFurnace_AndEndOnOneHashAndIdenticalSaves()
    {
        var catalogue = Catalogue.Instance;
        var left = Setup(players: 2);
        var right = Setup(players: 2);
        foreach (var world in new[] { left.World, right.World })
            world.Players[1].TeleportToTile(left.X - 1, left.Y);

        Assert.Equal(left.World.StateHash(), right.World.StateHash());

        var ok = 0;
        var wantsNothing = 0;
        var nothingToTake = 0;

        for (var step = 0; step < 3; step++)
        {
            var tick = left.World.TickCount;

            // Both peers' players reach into the same bench on the same tick,
            // and then both try to empty it before it has finished.
            var batch = new[]
            {
                PlayerCommand.Load(tick, 0, step * 4 + 0, left.X, left.Y),
                PlayerCommand.Load(tick, 1, step * 4 + 1, left.X, left.Y),
                PlayerCommand.Take(tick, 1, step * 4 + 2, left.X, left.Y),
                PlayerCommand.Take(tick, 0, step * 4 + 3, left.X, left.Y),
            };

            var results = left.Apply(batch);
            foreach (var result in results)
            {
                if (result.Ok) ok++;
                if (result.Outcome == CommandOutcome.WantsNothing) wantsNothing++;
                if (result.Outcome == CommandOutcome.NothingToTake) nothingToTake++;
            }

            // The other peer, with the packets in the order the wire delivered
            // them, which is no order at all.
            right.Apply(batch[2], batch[0], batch[3], batch[1]);

            var duration = catalogue.Recipes[FurnaceRecipe].DurationTicks + 2;
            left.World.Tick(duration);
            right.World.Tick(duration);

            Assert.Equal(left.World.StateHash(), right.World.StateHash());
        }

        // The run has to have actually happened. Two worlds where every command
        // was refused also agree perfectly.
        var furnace = catalogue.Item("man_furnace");
        Assert.True(ok >= 4, $"only {ok} of the twelve hand commands did anything");
        Assert.True(wantsNothing >= 1, "the contested load never lost");
        Assert.True(nothingToTake >= 1, "nobody ever grabbed at an empty machine");
        // Three cycles ran; two furnaces were carried off and the third is
        // still sitting in the bench, because the last take happened on the
        // tick *before* it finished. Exact numbers, not "more than none":
        // "somebody carried something" is true of a world where the loads all
        // failed and the miner was emptied instead.
        Assert.Equal(2, left.World.Players.Sum(p => p.Inventory.Count(furnace)));
        Assert.Equal(1, left.TheBench.GetOutputCount(furnace));
        Assert.Equal(2, right.World.Players.Sum(p => p.Inventory.Count(furnace)));

        var leftJson = Save.SaveGame.ToJson(Save.SaveGame.Capture(left.World));
        var rightJson = Save.SaveGame.ToJson(Save.SaveGame.Capture(right.World));
        Assert.Equal(leftJson, rightJson);
        Assert.Equal(left.World.CommandDigest, right.World.CommandDigest);
    }

    // ---- the two numbers a removal used to lose (ADR 0039 -> ADR 0040) ------

    private static (World World, BuildCatalogue Builds) Plumbed(string item, int count)
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(4242, catalogue);
        world.Research!.UnlockAll();
        world.Player.Inventory.Add(catalogue.Item(item), count);
        return (world, new BuildCatalogue(catalogue));
    }

    [Fact]
    public void RemovingAPipeThroughTheCommandLayer_SaysHowMuchFluidDrained()
    {
        var (world, builds) = Plumbed("stm_pipe", 4);
        var catalogue = Catalogue.Instance;
        var x = 70;
        var y = 30;
        world.Player.TeleportToTile(x, y);

        var results = new List<CommandResult>();
        for (var i = 0; i < 4; i++)
            world.ApplyCommands(builds, catalogue.Recipes,
                                new[] { PlayerCommand.Build(world.TickCount, 0, i, x + i, y,
                                                            "stm_pipe") }, results);
        Assert.All(results, r => Assert.Equal(CommandOutcome.Ok, r.Outcome));

        var network = world.Fluids.Network(world.Fluids.NetworkAt(x, y));
        network.BeginTick();
        Assert.Equal(200, network.TryInsert(catalogue.Item("water"), 200));

        results.Clear();
        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Remove(world.TickCount, 0, 9, x + 1, y) },
                            results);

        // A quarter of the run went, so a quarter of the water went with it --
        // and the command layer says so again. Between ADR 0039 and this slice
        // it had one number to say it with, spent on the items returned, and a
        // player pulling up a full tank was told nothing about the fluid.
        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(50, results[0].Voided);
        Assert.Equal(50, world.Fluids.VoidedByRemoval);
        Assert.Equal(0, results[0].Spilled);
    }

    [Fact]
    public void RemovingABeltThroughTheCommandLayer_SaysWhatFellOffIt()
    {
        var (world, builds) = Plumbed("stm_transport_belt", 6);
        var catalogue = Catalogue.Instance;
        var x = 20;
        var y = 5;
        world.Player.TeleportToTile(x, y);

        var results = new List<CommandResult>();
        for (var i = 0; i < 6; i++)
            world.ApplyCommands(builds, catalogue.Recipes,
                                new[] { PlayerCommand.Build(world.TickCount, 0, i, x + i, y,
                                                            "stm_transport_belt", null,
                                                            Direction.East) }, results);
        Assert.All(results, r => Assert.Equal(CommandOutcome.Ok, r.Outcome));
        world.SyncBelts();

        // Mixed cargo, packed from the exit, because a lane of one item lets a
        // removal that took the wrong tile's items pass unnoticed.
        var ore = catalogue.Item("magnetite");
        var coal = catalogue.Item("coal_deposit");
        var lane = world.Belts.Segment(0).LaneAt(0);
        for (var i = 0; i < 6; i++) Assert.True(lane.TryPack(i == 4 ? coal : ore));

        results.Clear();
        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Remove(world.TickCount, 0, 9, x + 4, y) },
                            results);

        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(2, results[0].Detail);          // the two items on that tile
        Assert.Equal(0, results[0].Voided);

        // An honest zero, and the limit is named rather than hidden: nothing I
        // could build spills. Cutting a run hands back what stood on the cut
        // tile and the shortened run still holds the rest, and a whole tunnel
        // pulled up returns every item that was buried in it. `Spilled` is
        // carried, hashed and spoken all the same, because the alternative is
        // a number that goes missing again the day a belt does overflow.
        Assert.Equal(world.BeltMap.SpilledOnRemoval, results[0].Spilled);
        Assert.Equal(0, world.BeltMap.SpilledOnRemoval);
    }
}
