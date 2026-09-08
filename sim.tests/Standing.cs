using Sim;

namespace Sim.Tests;

/// Test-side helpers for a game where the player has a position (ADR 0033).
///
/// These exist because reach is real: a test that builds at (200, 40) is a test
/// about a player who walked there, and before this change it was a test about
/// a camera that did not. Nothing here widens a reach -- the radii are the
/// shipped ones and the checks are the shipped checks. All these do is put the
/// player where the test's fiction already said they were.
internal static class Standing
{
    /// Builds with the player standing on the tile. The ordinary case: a test
    /// that cares about placement rules, not about walking.
    public static BuildResult BuildStandingBy(this World world, BuildCatalogue catalogue,
                                              ItemId item, int x, int y,
                                              Recipe? recipe = null,
                                              Direction facing = Direction.East)
    {
        world.Player.TeleportToTile(x, y);
        return world.TryBuild(catalogue, item, x, y, recipe, facing);
    }

    /// Puts an Uplink down next to the player and leaves them beside it, so a
    /// hand delivery has something to deliver *into*. Returns its machine index.
    ///
    /// Before reach, `DeliverByHand` credited research on a map with no Uplink
    /// anywhere on it, which is the thing the premise says is impossible.
    public static int GiveAnUplinkInReach(this World world, BuildCatalogue catalogue,
                                          int x = 0, int y = 0)
    {
        var uplink = catalogue.Find(Research.UplinkItem)!;
        var recipe = catalogue.RecipesFor(uplink, world.Research).First();

        world.PlayerInventory.Add(uplink.Item, 1);
        var result = world.BuildStandingBy(catalogue, uplink.Item, x, y, recipe);
        if (result != BuildResult.Ok)
            throw new InvalidOperationException($"could not stand an Uplink at {x},{y}: {result}");

        return world.MachineCount - 1;
    }

    /// Removes with the player standing on the tile. Removal is reach-gated
    /// too (ADR 0033), and a test that built three machines eight tiles apart
    /// leaves the player standing on the last one.
    public static RemovalReport RemoveStandingBy(this World world, int x, int y)
    {
        world.Player.TeleportToTile(x, y);
        return world.TryRemove(x, y);
    }

    /// Puts the player within arm's reach of an Uplink, building one if the
    /// world has none. What "walk over to the lander and hand it your ore"
    /// means, for a test that is about the delivery rather than the walk.
    public static void StandByAnUplink(this World world, BuildCatalogue catalogue)
    {
        foreach (var index in world.Uplinks)
        {
            var placement = world.PlacementOf(index);
            world.Player.TeleportToTile(placement.X, placement.Y);
            return;
        }

        world.GiveAnUplinkInReach(catalogue);
    }
}
