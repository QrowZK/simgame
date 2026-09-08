using Sim;
using Sim.Data;

namespace Sim.Tests;

/// The player as simulation state (ADR 0033).
///
/// Two properties run through everything here. **Movement is exact**: a walk of
/// any length is integer addition, so the same intents from the same seed put
/// the player on exactly the same milli-tile, forever. And **reach is a wall
/// with a number on it**: the radii are inclusive, they are checked before the
/// action costs anything, and every refusal has its own reason.
public class PlayerTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    private static World NewWorld() => NewGame.Create(seed: 4242, Data);

    // ---- fixed point -------------------------------------------------------

    /// A second of walking east is exactly six tiles. Not "about six": the
    /// whole reason position is an integer is that this number is the same on
    /// every machine that ever runs this seed.
    [Fact]
    public void OneSecondOfWalking_IsExactlySixThousandMilliTiles()
    {
        var world = NewWorld();
        var startX = world.Player.X;
        var startY = world.Player.Y;

        world.Player.Intent = new MoveIntent(1, 0);
        world.Tick(60);

        Assert.Equal(6000, world.Player.X - startX);
        Assert.Equal(0, world.Player.Y - startY);
        Assert.Equal(startX / Player.MilliPerTile + 6, world.Player.TileX);
    }

    /// A diagonal is 71 per axis per tick, and it is 71 on both axes rather
    /// than 100 on one and 71 on the other. A test asserting only the total
    /// would pass on a bug that moved X fast and Y slow.
    [Fact]
    public void ADiagonalStep_IsSeventyOneOnEachAxis()
    {
        var world = NewWorld();
        var (startX, startY) = (world.Player.X, world.Player.Y);

        world.Player.Intent = new MoveIntent(-1, 1);
        world.Tick(10);

        Assert.Equal(-710, world.Player.X - startX);
        Assert.Equal(710, world.Player.Y - startY);
    }

    /// The intent is a sign, not a magnitude. A layer that handed the sim an
    /// analogue stick would be handing it a float in the one place floats may
    /// not go, so anything but -1/0/+1 is clamped on the way in.
    [Fact]
    public void AnIntentBiggerThanOne_IsClampedRatherThanScaled()
    {
        var world = NewWorld();
        var startX = world.Player.X;

        world.Player.Intent = new MoveIntent(9, 0);
        world.Tick(1);

        Assert.Equal(Player.SpeedPerTick, world.Player.X - startX);
    }

    /// Standing still costs nothing and changes nothing, including the facing.
    [Fact]
    public void StandingStill_KeepsThePositionAndTheFacing()
    {
        var world = NewWorld();
        world.Player.Intent = new MoveIntent(0, -1);
        world.Tick(5);

        var (x, y) = (world.Player.X, world.Player.Y);
        world.Player.Intent = MoveIntent.Still;
        world.Tick(500);

        Assert.Equal(x, world.Player.X);
        Assert.Equal(y, world.Player.Y);
        Assert.Equal(0, world.Player.FacingX);
        Assert.Equal(-1, world.Player.FacingY);
    }

    /// A tile west of the origin is tile -1, not tile 0. C# integer division
    /// truncates towards zero, so the naive version puts a player standing at
    /// -0.5 tiles in the same tile as one standing at +0.5.
    [Fact]
    public void WestOfTheOrigin_FloorsRatherThanTruncates()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);

        world.Player.Intent = new MoveIntent(-1, -1);
        world.Tick(10);      // 710 milli-tiles from the centre of tile 0

        Assert.Equal(-210, world.Player.X);
        Assert.Equal(-1, world.Player.TileX);
        Assert.Equal(-1, world.Player.TileY);
    }

    /// The property the whole fixed-point choice exists for: the same intents
    /// from the same seed put the player on exactly the same milli-tile after
    /// 10,000 ticks, and the two worlds are byte-identical with it.
    [Fact]
    public void TenThousandTicks_OfTheSameIntents_LandOnTheSameMilliTile()
    {
        var a = NewGame.Create(seed: 991, Data);
        var b = NewGame.Create(seed: 991, Data);

        // A messy script rather than a straight line: a bug that only affects
        // diagonals, or only the moment an axis stops, hides in a straight walk.
        static void Drive(World world)
        {
            for (var tick = 0; tick < 10_000; tick++)
            {
                world.Player.Intent = ((tick / 37) % 5) switch
                {
                    0 => new MoveIntent(1, 0),
                    1 => new MoveIntent(1, 1),
                    2 => new MoveIntent(0, -1),
                    3 => new MoveIntent(-1, 1),
                    _ => MoveIntent.Still,
                };
                world.Tick();
            }
        }

        Drive(a);
        Drive(b);

        Assert.Equal(a.Player.X, b.Player.X);
        Assert.Equal(a.Player.Y, b.Player.Y);

        // Exact numbers, not just agreement: two worlds that both froze at the
        // origin would agree perfectly and prove nothing.
        // Worked out independently of the code under test: 270 whole blocks of
        // 37 ticks plus a 10-tick tail, at 100 per cardinal step and 71 per
        // diagonal one, from the spawn tile's centre at (500, 500).
        Assert.Equal(201_300, a.Player.X);
        Assert.Equal(84_416, a.Player.Y);

        Assert.Equal(Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(a)),
                     Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(b)));
    }

    // ---- reach, as a boundary ----------------------------------------------

    /// The hand radius is inclusive and the tile one further out is not. The
    /// two assertions are the boundary either side, so an off-by-one in the
    /// comparison fails one of them whichever way it goes.
    [Fact]
    public void HandReach_ReachesExactlySixTilesAndNotSeven()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(10, 10);

        Assert.Equal(6, Player.HandReachTiles);
        Assert.True(world.InHandReach(16, 10));
        Assert.False(world.InHandReach(17, 10));
        Assert.True(world.InHandReach(4, 10));
        Assert.False(world.InHandReach(3, 10));
        Assert.True(world.InHandReach(10, 16));
        Assert.False(world.InHandReach(10, 17));
    }

    /// Reach is a circle, not a square. Six tiles east is in; six east *and*
    /// six north is 8.49 tiles away and is not.
    [Fact]
    public void Reach_IsACircleRatherThanASquare()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);

        Assert.True(world.InHandReach(6, 0));
        Assert.False(world.InHandReach(6, 6));
        Assert.True(world.InHandReach(4, 4));       // 5.66 tiles
        Assert.False(world.InHandReach(5, 4));      // 6.40 tiles
    }

    [Fact]
    public void BuildReach_IsTwelveTilesAndTwiceTheHands()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);

        Assert.Equal(12, Player.BuildReachTiles);
        Assert.Equal(2 * Player.HandReachTiles, Player.BuildReachTiles);
        Assert.True(world.InBuildReach(12, 0));
        Assert.False(world.InBuildReach(13, 0));
    }

    /// A footprint is reached at its nearest tile. Standing beside a 3x3 puts
    /// it in reach even though its far corner is two tiles further away, which
    /// is how a person and a machine actually meet.
    [Fact]
    public void AFootprint_IsReachedAtItsNearestTile()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);

        // Anchored at x=5, so it covers 5..7. Its near edge is 5 tiles away and
        // its far edge is 7 -- past the hands.
        var placement = new MachinePlacement(5, -1, 0, 0, 3);

        Assert.True(world.InHandReach(placement));
        Assert.False(world.InHandReach(7, 0));
    }

    /// Half a milli-tile past the line is past the line. The radius is compared
    /// in squared milli-tiles, so this is the smallest step that can change the
    /// answer -- and it does.
    [Fact]
    public void OneMilliTilePastTheEdge_IsOutOfReach()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);
        Assert.True(world.InHandReach(6, 0));

        world.Player.Restore(world.Player.X - 1, world.Player.Y, 0, 1);
        Assert.False(world.InHandReach(6, 0));
    }

    // ---- what reach gates --------------------------------------------------

    /// A build out of reach is refused with its own reason, and costs nothing.
    /// The second half is the older invariant this must not break: a refused
    /// build never takes the item.
    [Fact]
    public void ABuildOutOfReach_IsRefusedByReasonAndCostsNothing()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var recipe = Buildables.RecipesFor(bench, world.Research)
                               .First(r => r.Id == "build_man_furnace");
        var carried = world.PlayerInventory.Count(bench.Item);
        Assert.True(carried > 0);

        world.Player.TeleportToTile(0, 0);

        Assert.Equal(BuildResult.TooFar,
                     world.TryBuild(Buildables, bench.Item, 13, 0, recipe));
        Assert.Equal(carried, world.PlayerInventory.Count(bench.Item));
        Assert.Equal(0, world.MachineCount);

        // One tile closer, and the same click builds.
        Assert.Equal(BuildResult.Ok,
                     world.TryBuild(Buildables, bench.Item, 12, 0, recipe));
        Assert.Equal(carried - 1, world.PlayerInventory.Count(bench.Item));
    }

    /// Reach is checked before the footprint, so a player is never told what is
    /// standing on a tile they cannot walk to. The two refusals send them to
    /// two different places and only one of them is true.
    [Fact]
    public void AnOccupiedTileOutOfReach_SaysTooFarRatherThanBlocked()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var recipe = Buildables.RecipesFor(bench, world.Research)
                               .First(r => r.Id == "build_man_furnace");

        world.Player.TeleportToTile(30, 0);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, bench.Item, 30, 0, recipe));

        world.PlayerInventory.Add(bench.Item, 1);
        world.Player.TeleportToTile(0, 0);
        Assert.Equal(BuildResult.TooFar, world.TryBuild(Buildables, bench.Item, 30, 0, recipe));
    }

    /// The whole opening, in one assertion: the guaranteed starter patch is
    /// 12-40 tiles out (ADR 0026), so from the landing site you can put the
    /// Uplink down and you cannot touch the ore. The first thing the game asks
    /// you to do is walk.
    [Fact]
    public void FromTheLandingSite_TheStarterOreIsOutOfReachAndTheGroundUnderYouIsNot()
    {
        var world = NewGame.Create(seed: 20260907, Data);
        world.Player.TeleportToTile(NewGame.SpawnX, NewGame.SpawnY);

        var usable = world.Research!.ConsumableNow(world.Items);
        var hit = new Prospector(radius: 400)
            .Scan(world.Ground.Gen, NewGame.SpawnX, NewGame.SpawnY, usable)
            .First(h => h.Usable);

        Assert.InRange(hit.Distance, 12, 40);
        Assert.Equal(DigResult.TooFar, world.TryDigByHand(hit.X, hit.Y, 5).Result);
        Assert.True(world.InBuildReach(NewGame.SpawnX + 4, NewGame.SpawnY));

        // And the walk is short. At six tiles a second the far edge of the
        // guaranteed band is under eight seconds -- a walk, not a chore.
        var ticks = Walk.To(world, hit.X, hit.Y);
        Assert.InRange(ticks, 1, 8 * 60);
        Assert.Equal(DigResult.Ok, world.TryDigByHand(hit.X, hit.Y, 5).Result);
    }

    /// Every dig refusal, told apart. A zero means five different things to a
    /// player and only one of them is "keep clicking".
    [Fact]
    public void EveryDigRefusal_HasItsOwnReason()
    {
        var world = NewGame.Create(seed: 20260907, Data);

        var solid = new Prospector(radius: 400)
            .Scan(world.Ground.Gen, 0, 0)
            .First(h => world.Ground.Gen.TryPatchAt(h.X, h.Y, out var p) && !p.IsFluid);
        var fluid = new Prospector(radius: 400)
            .Scan(world.Ground.Gen, 0, 0)
            .First(h => world.Ground.Gen.TryPatchAt(h.X, h.Y, out var p) && p.IsFluid);

        // Nothing under it. The player is standing right on it, so this cannot
        // be reach in disguise.
        var bare = FindBareTile(world);
        world.Player.TeleportToTile(bare.X, bare.Y);
        Assert.Equal(DigResult.NothingThere, world.TryDigByHand(bare.X, bare.Y, 5).Result);

        // Too far, and it reports the reach before the patch's state.
        world.Player.TeleportToTile(solid.X + 100, solid.Y);
        Assert.Equal(DigResult.TooFar, world.TryDigByHand(solid.X, solid.Y, 5).Result);

        // Hands cannot lift a fluid, standing on it or not.
        world.Player.TeleportToTile(fluid.X, fluid.Y);
        Assert.Equal(DigResult.CannotLiftFluid, world.TryDigByHand(fluid.X, fluid.Y, 5).Result);

        // Worked out: dug flat, then asked again.
        world.Player.TeleportToTile(solid.X, solid.Y);
        while (world.TryDigByHand(solid.X, solid.Y, 10_000).Taken > 0) { }
        Assert.Equal(DigResult.WorkedOut, world.TryDigByHand(solid.X, solid.Y, 5).Result);

        // And reach outranks *every* other reason, not just the ones a player
        // standing next to a full patch would meet. A mutation that moved the
        // reach check below the patch's state survived until these two: a
        // fluid you cannot walk to, and a worked-out patch you cannot walk to,
        // both answered with the state of a tile the player cannot touch.
        world.Player.TeleportToTile(fluid.X + 100, fluid.Y);
        Assert.Equal(DigResult.TooFar, world.TryDigByHand(fluid.X, fluid.Y, 5).Result);

        world.Player.TeleportToTile(solid.X, solid.Y + 100);
        Assert.Equal(DigResult.TooFar, world.TryDigByHand(solid.X, solid.Y, 5).Result);
    }

    /// A dig reports what came out, what was there, and what is left -- exact
    /// numbers, because "greater than zero" would pass on a dig that took one.
    [Fact]
    public void ADigInReach_ReportsTheExactNumbers()
    {
        var world = NewGame.Create(seed: 20260907, Data);
        var hit = new Prospector(radius: 400)
            .Scan(world.Ground.Gen, 0, 0)
            .First(h => world.Ground.Gen.TryPatchAt(h.X, h.Y, out var p) && !p.IsFluid);

        Walk.To(world, hit.X, hit.Y);
        var before = world.Ground.RemainingAt(hit.X, hit.Y);

        var report = world.TryDigByHand(hit.X, hit.Y, 5);

        Assert.Equal(DigResult.Ok, report.Result);
        Assert.Equal(5, report.Taken);
        Assert.Equal(before, report.RemainingBefore);
        Assert.Equal(before - 5, report.RemainingAfter);
        Assert.Equal(before - 5, world.Ground.RemainingAt(hit.X, hit.Y));
        Assert.Equal(5, world.PlayerInventory.Count(report.Item));
    }

    /// Hand delivery needs an Uplink you can touch. Before reach existed this
    /// credited research on a map with no Uplink anywhere on it, which the
    /// premise says is impossible.
    [Fact]
    public void HandDelivery_NeedsAnUplinkWithinArmsReach()
    {
        var world = NewWorld();
        var ore = Data.Item("magnetite");
        world.PlayerInventory.Add(ore, 4);

        // No Uplink placed at all.
        var nothing = world.DeliverByHand(ore, 1);
        Assert.Equal(0, nothing.Accepted);
        Assert.Equal(DeliveryRefusal.NoUplinkInReach, nothing.Refusal);
        Assert.Equal(4, world.PlayerInventory.Count(ore));

        world.GiveAnUplinkInReach(Buildables, 0, 0);

        // Placed, but walked away from: still refused, and still costs nothing.
        world.Player.TeleportToTile(40, 40);
        var away = world.DeliverByHand(ore, 1);
        Assert.Equal(DeliveryRefusal.NoUplinkInReach, away.Refusal);
        Assert.Equal(4, world.PlayerInventory.Count(ore));

        // Walk back, and the same delivery lands.
        Walk.To(world, 0, 0);
        var landed = world.DeliverByHand(ore, 1);
        Assert.Equal(1, landed.Accepted);
        Assert.Equal(DeliveryRefusal.None, landed.Refusal);
        Assert.Equal(3, world.PlayerInventory.Count(ore));
    }

    /// Carrying nothing and carrying something nothing wants are different
    /// problems, and neither of them is "walk closer".
    [Fact]
    public void TheOtherDeliveryRefusals_AreNotConfusedWithDistance()
    {
        var world = NewWorld();
        world.GiveAnUplinkInReach(Buildables, 0, 0);

        var ore = Data.Item("magnetite");
        Assert.Equal(DeliveryRefusal.NotCarried, world.DeliverByHand(ore, 1).Refusal);

        var stone = Data.Item("stone_deposit");
        Assert.True(world.PlayerInventory.Count(stone) > 0);
        Assert.Equal(DeliveryRefusal.NothingWanted, world.DeliverByHand(stone, 1).Refusal);
    }

    /// The same radius that put something down takes it back. Anything else
    /// would mean a mistake you have to walk to before you can undo it.
    [Fact]
    public void RemovalUsesTheBuildRadius_AndRefusesByReason()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var recipe = Buildables.RecipesFor(bench, world.Research)
                               .First(r => r.Id == "build_man_furnace");

        world.Player.TeleportToTile(50, 0);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, bench.Item, 50, 0, recipe));

        world.Player.TeleportToTile(50 - 13, 0);
        Assert.Equal(RemoveResult.TooFar, world.TryRemove(50, 0).Result);
        Assert.Equal(1, world.MachineCount);

        world.Player.TeleportToTile(50 - 12, 0);
        Assert.Equal(RemoveResult.Ok, world.TryRemove(50, 0).Result);
        Assert.Equal(0, world.MachineCount);
    }

    /// Looking is not touching. A machine on the other side of the map opens,
    /// reads and answers every question about itself; only its hands-on
    /// operations refuse.
    [Fact]
    public void Inspection_IsFreeAtAnyDistance()
    {
        var world = NewWorld();
        var bench = Buildables.Find("man_manual_crafting")!;
        var recipe = Buildables.RecipesFor(bench, world.Research)
                               .First(r => r.Id == "build_man_furnace");

        world.Player.TeleportToTile(300, 300);
        Assert.Equal(BuildResult.Ok, world.TryBuild(Buildables, bench.Item, 300, 300, recipe));

        world.Player.TeleportToTile(0, 0);

        Assert.True(world.TryMachineAt(300, 300, out var machine, out var index));
        Assert.Equal(0, index);
        Assert.Equal(recipe.Id, machine.Recipe.Id);
        Assert.False(world.InHandReach(world.PlacementOf(index)));
    }

    // ---- saves -------------------------------------------------------------

    /// A position saved mid-stride comes back mid-stride, not snapped to a tile
    /// centre. A save that quietly moved the player by half a tile every time
    /// would be a save that moved them across a long game.
    [Fact]
    public void APositionMidStride_RoundTripsExactly()
    {
        var world = NewWorld();
        world.Player.Intent = new MoveIntent(1, -1);
        world.Tick(17);      // 1207 milli-tiles, deliberately not a tile edge

        Assert.Equal(1707, world.Player.X);
        Assert.NotEqual(0, world.Player.X % Player.MilliPerTile);

        var loaded = Reload(world);

        Assert.Equal(world.Player.X, loaded.Player.X);
        Assert.Equal(world.Player.Y, loaded.Player.Y);
        Assert.Equal(1, loaded.Player.FacingX);
        Assert.Equal(-1, loaded.Player.FacingY);
        Assert.Equal(Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world)),
                     Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(loaded)));
    }

    /// The limitation, named and tested rather than papered over: the movement
    /// intent is not saved, so a world written while walking reloads standing
    /// still. Resuming a walk nobody is asking for would be worse, and a save
    /// should describe the world rather than the keyboard.
    [Fact]
    public void AWalkInProgress_IsNotSaved_AndTheLoadedWorldStandsStill()
    {
        var world = NewWorld();
        world.Player.Intent = new MoveIntent(1, 0);
        world.Tick(30);

        var loaded = Reload(world);
        Assert.False(loaded.Player.Intent.IsMoving);

        var at = loaded.Player.X;
        loaded.Tick(600);
        Assert.Equal(at, loaded.Player.X);

        // And the original keeps walking, which is what makes the difference
        // above a property of the save rather than of the world.
        world.Tick(600);
        Assert.NotEqual(at, world.Player.X);
    }

    /// The save version moved, and an older file is refused rather than loaded
    /// with the player standing at the origin inside whatever is built there.
    [Fact]
    public void TheSaveVersionMoved_AndThirteenIsRefused()
    {
        // 15 since teams landed (ADR 0036); 14 is what this test was written
        // against and is still refused, which is the property it guards.
        Assert.Equal(15, Sim.Save.SaveFile.CurrentVersion);

        var save = Sim.Save.SaveGame.Capture(NewWorld());
        save.Version = 13;

        var thrown = Assert.ThrowsAny<Exception>(
            () => Sim.Save.SaveGame.Restore(save, new Dictionary<string, Recipe>()));
        Assert.Contains("13", thrown.Message);
    }

    // ---- the walker --------------------------------------------------------

    /// The harness walker arrives, and arrives on the tile rather than near it.
    [Fact]
    public void TheWalker_ArrivesExactlyOnTheTile()
    {
        var world = NewWorld();
        world.Player.TeleportToTile(0, 0);

        var ticks = Walk.To(world, -37, 91);

        Assert.True(ticks > 0);
        Assert.Equal(-37, world.Player.TileX);
        Assert.Equal(91, world.Player.TileY);
        Assert.Equal(ticks, world.TickCount);
        Assert.False(world.Player.Intent.IsMoving);
    }

    // ---- helpers -----------------------------------------------------------

    /// Save and load, with the recipe table and worldgen the loader needs.
    private static World Reload(World world)
    {
        var recipes = new Dictionary<string, Recipe>();
        foreach (var machine in world.Machines) recipes[machine.Recipe.Id] = machine.Recipe;
        return Sim.Save.SaveGame.Restore(Sim.Save.SaveGame.Capture(world), recipes,
                                         world.Ground.Gen);
    }

    /// A tile with nothing under it. Searched rather than assumed: a hard-coded
    /// "bare" coordinate is one worldgen change from being ore, and a dig test
    /// that silently started standing on a patch would assert the wrong thing.
    private static (int X, int Y) FindBareTile(World world)
    {
        for (var d = 0; d < 200; d++)
            if (!world.Ground.Gen.TryPatchAt(d, -d, out _))
                return (d, -d);

        throw new InvalidOperationException("no bare tile on the diagonal");
    }
}
