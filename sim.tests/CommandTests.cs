using Sim;
using Sim.Data;

namespace Sim.Tests;

/// The command layer: the encoding, the total order, and what a command does
/// to the world (ADR 0037).
public class CommandTests
{
    /// A raw ore the first rung of the tech ladder accepts (`tech_start_uplink`
    /// takes any raw ore). Named here once so a delivery test says what it is
    /// delivering and why the Uplink wants it.
    private const string Ore = "magnetite";

    private static (World World, BuildCatalogue Builds, Catalogue Catalogue) NewWorld(
        int seed = 4242)
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(seed, catalogue);
        return (world, new BuildCatalogue(catalogue), catalogue);
    }

    private static readonly PlayerCommand[] Messy =
    {
        PlayerCommand.Move(7, 0, 0, -1, 1),
        PlayerCommand.Dig(7, 0, 1, -12, 340, 5),
        PlayerCommand.Build(7, 1, 0, 3, -4, "man_uplink", "build_man_uplink", Direction.North),
        PlayerCommand.Remove(7, 2, 9, int.MinValue, int.MaxValue),
        PlayerCommand.ChangeRecipe(8, 1, 1, 0, 0, "smelt_iron_plate"),
        PlayerCommand.Deliver(long.MaxValue, 3, int.MaxValue, "stone_deposit", 24),
        new PlayerCommand(9, 0, 2, CommandKind.Build, 1, 1, 0, Direction.West, "", ""),
    };

    [Fact]
    public void EveryFieldOfEveryKind_SurvivesTheRoundTripToBytes()
    {
        var bytes = CommandCodec.Encode(Messy);
        var back = CommandCodec.Decode(bytes);

        Assert.Equal(Messy.Length, back.Count);
        for (var i = 0; i < Messy.Length; i++)
        {
            // Field by field rather than by Equals, so a comparison that
            // stopped looking at a field cannot hide a codec that dropped it.
            Assert.Equal(Messy[i].Tick, back[i].Tick);
            Assert.Equal(Messy[i].PlayerId, back[i].PlayerId);
            Assert.Equal(Messy[i].Sequence, back[i].Sequence);
            Assert.Equal(Messy[i].Kind, back[i].Kind);
            Assert.Equal(Messy[i].X, back[i].X);
            Assert.Equal(Messy[i].Y, back[i].Y);
            Assert.Equal(Messy[i].Amount, back[i].Amount);
            Assert.Equal(Messy[i].Facing, back[i].Facing);
            Assert.Equal(Messy[i].Item, back[i].Item);
            Assert.Equal(Messy[i].Recipe, back[i].Recipe);
        }
    }

    [Fact]
    public void TheEncodingIsExact_SoTwoPeersCannotDisagreeAboutABatchsLength()
    {
        // 7 bytes of header, then a fixed 30 per command plus the two names.
        var one = CommandCodec.Encode(PlayerCommand.Dig(1, 0, 0, 5, 5, 3));
        Assert.Equal(7 + 30 + 2, one.Length);

        var named = CommandCodec.Encode(
            PlayerCommand.Build(1, 0, 0, 0, 0, "man_uplink", "build_man_uplink"));
        Assert.Equal(7 + 30 + 2 + "man_uplink".Length + "build_man_uplink".Length, named.Length);
    }

    [Fact]
    public void AnEmptyBatch_RoundTrips()
    {
        Assert.Empty(CommandCodec.Decode(CommandCodec.Encode(Array.Empty<PlayerCommand>())));
    }

    [Theory]
    [InlineData(0)]   // truncated header
    [InlineData(9)]   // header plus a fragment of a command
    [InlineData(20)]
    public void ATruncatedBatch_IsRefusedRatherThanGuessedAt(int keep)
    {
        var bytes = CommandCodec.Encode(Messy);
        Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes.AsSpan(0, keep)));
    }

    [Fact]
    public void TrailingBytes_AreRefused()
    {
        var bytes = CommandCodec.Encode(Messy).Concat(new byte[] { 0 }).ToArray();
        var thrown = Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));
        Assert.Contains("left over", thrown.Message);
    }

    [Fact]
    public void ABatchFromSomeOtherChannel_IsNotDecodedAsCommands()
    {
        var thrown = Assert.Throws<CommandFormatException>(
            () => CommandCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Contains("magic", thrown.Message);
    }

    [Fact]
    public void ABatchFromAnotherBuildOfTheFormat_IsRefusedByNumber()
    {
        var bytes = CommandCodec.Encode(PlayerCommand.Move(0, 0, 0, 1, 0));
        bytes[2] = CommandCodec.Format + 1;
        var thrown = Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));
        Assert.Contains($"speaks {CommandCodec.Format}", thrown.Message);
    }

    [Fact]
    public void AKindOrADirectionThisBuildDoesNotKnow_IsRefusedAtTheDoor()
    {
        var bytes = CommandCodec.Encode(PlayerCommand.Dig(1, 0, 0, 5, 5, 3));
        var kindAt = 7 + 8 + 4 + 4;
        bytes[kindAt] = 99;
        Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));

        bytes[kindAt] = (byte)CommandKind.Dig;
        bytes[kindAt + 1 + 12] = 77;   // the facing byte
        Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));
    }

    [Fact]
    public void ALengthPrefixNoBatchCouldHave_DoesNotMakeADecoderAllocate()
    {
        var bytes = CommandCodec.Encode(Messy);
        bytes[3] = 0xFF;
        bytes[4] = 0xFF;
        bytes[5] = 0xFF;
        bytes[6] = 0x7F;
        Assert.Throws<CommandFormatException>(() => CommandCodec.Decode(bytes));
    }

    [Fact]
    public void AnItemNameThatIsNotAsciiOrIsTooLong_IsRefusedRatherThanTruncated()
    {
        Assert.Throws<CommandFormatException>(
            () => CommandCodec.Encode(PlayerCommand.Deliver(0, 0, 0, "iron_pläte", 1)));
        Assert.Throws<CommandFormatException>(
            () => CommandCodec.Encode(PlayerCommand.Deliver(0, 0, 0, new string('a', 256), 1)));
    }

    // ---- the total order ---------------------------------------------------

    [Fact]
    public void TheOrderIsTotal_SoNoTwoDistinguishableCommandsCanTie()
    {
        // Every pair in `Messy` differs in some field, and every pair must
        // therefore compare non-zero -- in both directions, and consistently.
        for (var i = 0; i < Messy.Length; i++)
            for (var j = 0; j < Messy.Length; j++)
            {
                var forward = Messy[i].CompareTo(Messy[j]);
                var backward = Messy[j].CompareTo(Messy[i]);
                if (i == j) Assert.Equal(0, forward);
                else Assert.NotEqual(0, forward);
                Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
            }
    }

    [Fact]
    public void TwoCommandsFromOnePlayerForOneTick_AreSeparatedBySequence()
    {
        var first = PlayerCommand.Dig(5, 1, 0, 0, 0, 1);
        var second = PlayerCommand.Dig(5, 1, 1, 0, 0, 1);
        Assert.True(first.CompareTo(second) < 0);
        // ... and player id outranks sequence, so a player with a high counter
        // never jumps ahead of a lower-numbered player's later command.
        Assert.True(PlayerCommand.Dig(5, 0, 99, 0, 0, 1).CompareTo(second) < 0);
        // ... and tick outranks both.
        Assert.True(PlayerCommand.Dig(4, 9, 99, 0, 0, 1).CompareTo(second) < 0);
    }

    [Fact]
    public void TwoCommandsDifferingOnlyInPayload_StillOrderTheSameWayEveryTime()
    {
        // The same key, different payload: the pathological case, and the one
        // where a comparison on (tick, player, sequence) alone would tie and
        // leave the answer to arrival order.
        var a = new PlayerCommand(3, 0, 0, CommandKind.Dig, 1, 1, 1);
        var b = new PlayerCommand(3, 0, 0, CommandKind.Dig, 1, 1, 2);
        Assert.NotEqual(0, a.CompareTo(b));
        Assert.Equal(a.Key, b.Key);

        var one = new List<PlayerCommand> { a, b };
        var other = new List<PlayerCommand> { b, a };
        one.Sort();
        other.Sort();
        Assert.Equal(one[0].Amount, other[0].Amount);
    }

    [Fact]
    public void AShuffledBatch_SortsBackToOneSequence()
    {
        var rng = new Random(7);
        var wanted = Messy.OrderBy(c => c).ToList();

        for (var trial = 0; trial < 50; trial++)
        {
            var shuffled = Messy.OrderBy(_ => rng.Next()).ToList();
            shuffled.Sort();
            Assert.Equal(wanted.Select(c => c.ToString()), shuffled.Select(c => c.ToString()));
        }
    }

    // ---- application -------------------------------------------------------

    [Fact]
    public void ACommandForAnotherTick_OrAPlayerWhoDoesNotExist_IsRefusedWithItsOwnReason()
    {
        var (world, builds, catalogue) = NewWorld();
        var results = new List<CommandResult>();

        world.ApplyCommands(builds, catalogue.Recipes, new[]
        {
            PlayerCommand.Dig(1, 0, 0, 0, 0, 1),        // world is on tick 0
            PlayerCommand.Dig(0, 44, 0, 0, 0, 1),       // no such player
            PlayerCommand.Dig(0, 0, 1, 0, 0, 0),        // no such amount
            PlayerCommand.Deliver(0, 0, 2, "not_an_item", 1),
            PlayerCommand.ChangeRecipe(0, 0, 3, 0, 0, "not_a_recipe"),
        }, results);

        Assert.Equal(new[]
        {
            CommandOutcome.WrongTick, CommandOutcome.BadAmount, CommandOutcome.UnknownItem,
            CommandOutcome.UnknownRecipe, CommandOutcome.UnknownPlayer,
        }.OrderBy(o => o), results.Select(r => r.Outcome).OrderBy(o => o));

        Assert.Equal(0, world.CommandsApplied);
        Assert.Equal(5, world.CommandsRefused);
    }

    [Fact]
    public void TheSameCommandTwice_AppliesOnceAndSaysSo()
    {
        var (world, builds, catalogue) = NewWorld();
        var stone = catalogue.Item("stone_deposit");
        var uplink = Research.UplinkItem;

        var build = PlayerCommand.Build(0, 0, 0, 2, 2, uplink, Research.UplinkRecipe);
        var results = new List<CommandResult>();
        world.ApplyCommands(builds, catalogue.Recipes, new[] { build, build }, results);

        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(CommandOutcome.Duplicate, results[1].Outcome);
        Assert.Equal(1, world.MachineCount);
        // The duplicate cost nothing: one Uplink out of the pocket, not two.
        Assert.Equal(0, world.Player.Inventory.Count(catalogue.Item(uplink)));
        Assert.Equal(24, world.Player.Inventory.Count(stone));
    }

    [Fact]
    public void EveryKind_ReachesTheWorldAndReportsItsNumber()
    {
        var (world, builds, catalogue) = NewWorld();
        var player = world.Player;
        var results = new List<CommandResult>();

        // Move: an intent, in force until the next one.
        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Move(0, 0, 0, 1, 0) }, results);
        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        var wasX = player.X;
        world.Tick();
        Assert.Equal(wasX + Player.SpeedPerTick, player.X);

        // Eight ore in hand, because the first rung of the ladder wants raw
        // ore and the starter kit contains none (`data/techs.json`).
        player.Inventory.Add(catalogue.Item(Ore), 8);

        // Build, then retask it to what it already runs, then remove it.
        results.Clear();
        world.ApplyCommands(builds, catalogue.Recipes, new[]
        {
            PlayerCommand.Build(1, 0, 1, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.ChangeRecipe(1, 0, 2, 2, 2, Research.UplinkRecipe),
            PlayerCommand.Deliver(1, 0, 3, Ore, 4),
        }, results);

        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(CommandOutcome.AlreadyRunning, results[1].Outcome);
        Assert.Equal(CommandOutcome.Ok, results[2].Outcome);
        // How many the Uplink took is data (the first rung wants a handful),
        // so the assertion is the conservation rather than the number: what
        // left the pocket is exactly what the command reported.
        Assert.InRange(results[2].Detail, 1, 4);
        Assert.Equal(8 - results[2].Detail, player.Inventory.Count(catalogue.Item(Ore)));

        world.Tick();
        results.Clear();
        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Remove(2, 0, 4, 2, 2) }, results);
        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(1, player.Inventory.Count(catalogue.Item(Research.UplinkItem)));
    }

    [Fact]
    public void ADigCommand_CarriesTheDigsOwnReasons()
    {
        var (world, builds, catalogue) = NewWorld();
        var results = new List<CommandResult>();

        world.ApplyCommands(builds, catalogue.Recipes, new[]
        {
            PlayerCommand.Dig(0, 0, 0, 900, 900, 3),      // far away, and bare
            PlayerCommand.Dig(0, 0, 1, 0, 0, 3),          // under our feet
        }, results);

        // Bare ground is answered before reach, exactly as `TryDigByHand`
        // answers it: what is under a tile is decided before how far away it
        // is. The command layer dispatches to that rule rather than inventing
        // a second one.
        Assert.Equal(CommandOutcome.NothingThere, results[0].Outcome);
        Assert.Contains(results[1].Outcome,
                        new[] { CommandOutcome.Ok, CommandOutcome.NothingThere });
    }

    [Fact]
    public void ARivalsBuilding_IsRefusedIdenticallyThroughACommand()
    {
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(99, catalogue);
        var builds = new BuildCatalogue(catalogue);
        var blue = NewGame.AddTeam(world, catalogue, "Blue");
        var rival = NewGame.AddPlayer(world, catalogue, "Rival", blue.Id);

        var results = new List<CommandResult>();
        world.ApplyCommands(builds, catalogue.Recipes, new[]
        {
            PlayerCommand.Build(0, 0, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.Remove(0, 1, 0, 2, 2),
            PlayerCommand.ChangeRecipe(0, 1, 1, 2, 2, Research.UplinkRecipe),
        }, results);

        Assert.Equal(CommandOutcome.Ok, results[0].Outcome);
        Assert.Equal(CommandOutcome.OtherTeam, results[1].Outcome);
        Assert.Equal(CommandOutcome.OtherTeam, results[2].Outcome);
        // A refusal costs nothing: the rival gained no Uplink.
        Assert.Equal(1, rival.Inventory.Count(catalogue.Item(Research.UplinkItem)));
        Assert.Equal(1, world.MachineCount);
    }

    [Fact]
    public void ARefusal_MovesTheDigestAndNothingElse()
    {
        var (world, builds, catalogue) = NewWorld();
        var before = world.StateHash();
        var digestBefore = world.CommandDigest;

        world.ApplyCommands(builds, catalogue.Recipes,
                            new[] { PlayerCommand.Dig(0, 0, 0, 5000, 5000, 1) });

        Assert.NotEqual(digestBefore, world.CommandDigest);
        Assert.NotEqual(before, world.StateHash());
        Assert.Equal(1, world.CommandsRefused);
        Assert.Equal(0, world.CommandsApplied);
    }

    [Fact]
    public void TwoDifferentRefusalReasons_ProduceTwoDifferentDigests()
    {
        // The reason is simulation state, not a UI decision: two peers that
        // refused the same command for two different reasons have diverged,
        // and this is what makes that visible.
        var (a, buildsA, catalogue) = NewWorld();
        var (b, buildsB, _) = NewWorld();

        a.ApplyCommands(buildsA, catalogue.Recipes,
                        new[] { PlayerCommand.Dig(0, 0, 0, 5000, 5000, 1) });   // TooFar
        b.ApplyCommands(buildsB, catalogue.Recipes,
                        new[] { PlayerCommand.Dig(0, 0, 0, 5000, 5000, 0) });   // BadAmount

        Assert.NotEqual(a.CommandDigest, b.CommandDigest);
    }

    [Fact]
    public void CollectingResultsOrNot_MakesNoDifferenceToTheWorld()
    {
        var (a, buildsA, catalogue) = NewWorld();
        var (b, buildsB, _) = NewWorld();
        var batch = new[]
        {
            PlayerCommand.Build(0, 0, 0, 2, 2, Research.UplinkItem, Research.UplinkRecipe),
            PlayerCommand.Dig(0, 0, 1, 900, 900, 2),
        };

        a.ApplyCommands(buildsA, catalogue.Recipes, batch, new List<CommandResult>());
        b.ApplyCommands(buildsB, catalogue.Recipes, batch);

        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ApplyingABatch_DoesNotReorderTheCallersList()
    {
        var (world, builds, catalogue) = NewWorld();
        var batch = new List<PlayerCommand>
        {
            PlayerCommand.Dig(0, 0, 5, 1, 1, 1),
            PlayerCommand.Dig(0, 0, 0, 2, 2, 1),
        };

        world.ApplyCommands(builds, catalogue.Recipes, batch);
        Assert.Equal(5, batch[0].Sequence);
    }

    /// `Say` ends in a `_ =>` fallback, so an outcome added to the enum
    /// compiles, ships, and tells a player "That cannot be done." when they
    /// needed to know why. Both halves below catch that -- one outcome falling
    /// through is named by the fallback check, two collide on distinctness --
    /// and both name the offending values, because a failure reading
    /// "expected 30, got 29" sends the next person counting.
    [Fact]
    public void EveryOutcome_HasASentenceOfItsOwn()
    {
        const string fallback = "That cannot be done.";

        var said = Enum.GetValues<CommandOutcome>()
                       .ToDictionary(o => o, CommandOutcomes.Say);

        var generic = said.Where(kv => kv.Value == fallback)
                          .Select(kv => kv.Key.ToString())
                          .ToList();
        Assert.True(generic.Count == 0,
            "outcomes that fall through to the generic sentence, so a player is " +
            "refused without being told why: " + string.Join(", ", generic));

        var shared = said.GroupBy(kv => kv.Value)
                         .Where(g => g.Count() > 1)
                         .Select(g => $"{string.Join(" and ", g.Select(kv => kv.Key))} both say \"{g.Key}\"")
                         .ToList();
        Assert.True(shared.Count == 0,
            "two refusals sharing one sentence is the same silence the enum " +
            "exists to prevent: " + string.Join("; ", shared));
    }
}
