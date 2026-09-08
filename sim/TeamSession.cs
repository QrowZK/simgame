using Sim.Data;

namespace Sim;

/// The end-to-end check that teams and the roster actually work (ADR 0036).
///
/// It exists here rather than in the Godot layer for the same reason the sim
/// does: this is simulation behaviour, and a check that could only be run by
/// launching a renderer would not be run. `sim.harness --teams-test` and a
/// `sim.tests` case both drive this one scenario, so the headless run and the
/// unit test cannot drift apart and disagree about what passing means.
///
/// The scenario is deliberately *untidy*. A world where every player stands on
/// the same tile, on the same team, holding the same things would round-trip a
/// dropped field unnoticed -- this repository has been bitten by exactly that
/// (the belt-facing case in CLAUDE.md). So: three players, two teams, three
/// positions, three facings, three different inventories, and two teams at two
/// different points on the ladder, one of which got there by hand and the other
/// by machine.
public static class TeamSession
{
    public sealed class Report
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Failures = new();
        public bool Ok => Failures.Count == 0;

        internal void Say(string line) => Lines.Add(line);

        internal void Check(bool condition, string what)
        {
            Lines.Add($"{(condition ? "ok  " : "FAIL")} {what}");
            if (!condition) Failures.Add(what);
        }

        internal void Equal<T>(T expected, T actual, string what)
            => Check(EqualityComparer<T>.Default.Equals(expected, actual),
                     $"{what}: expected {expected}, got {actual}");
    }

    public static Report Run(int seed = 20260908)
    {
        var report = new Report();
        var catalogue = Catalogue.Instance;
        var world = NewGame.Create(seed, catalogue);

        // ---- roster --------------------------------------------------------

        world.Teams[0].Name = "Red";
        var ada = world.Player;
        ada.Name = "Ada";

        var byron = NewGame.AddPlayer(world, catalogue, "Byron", teamId: 0);
        var blue = NewGame.AddTeam(world, catalogue, "Blue");
        var cato = NewGame.AddPlayer(world, catalogue, "Cato", blue.Id);

        report.Equal(2, world.Teams.Count, "teams");
        report.Equal(3, world.Players.Count, "players");
        report.Equal(0, world.LocalIndex, "local player is Ada");
        report.Check(ReferenceEquals(world.PlayerInventory, ada.Inventory),
                     "World.PlayerInventory is the local player's");
        report.Check(ReferenceEquals(world.Research, world.Teams[0].Research),
                     "World.Research is the local player's team's");
        report.Check(!ReferenceEquals(world.Teams[0].Research, world.Teams[1].Research),
                     "the two teams have two research states");

        // ---- three players, three places, three facings ---------------------

        var uplinkItem = catalogue.Item(Research.UplinkItem);
        var uplinkRecipe = catalogue.Recipe(Research.UplinkRecipe);
        var builds = new BuildCatalogue(catalogue);

        // Ada finds ore for Red. Cato finds ore for Blue. The two walk to two
        // different patches, which is what puts them in two different places
        // with two different facings without any of it being staged.
        var (adaOreX, adaOreY) = NearestUsableOre(world, ada);
        Walk.To(world, ada, adaOreX, adaOreY);

        // Blue lands somewhere else. Teleport rather than a walk, because where
        // a second team starts is a lobby decision this slice does not make --
        // what matters to the sim is that they are two teams in one world.
        cato.TeleportToTile(-150, -150);
        var (catoOreX, catoOreY) = NearestUsableOre(world, cato, preferDifferentFrom: (adaOreX, adaOreY));
        Walk.To(world, cato, catoOreX, catoOreY);

        // Byron stays near Ada -- close enough to reach her buildings, which is
        // the point of a teammate.
        Walk.To(world, byron, adaOreX + 5, adaOreY + 3);

        report.Say($"positions       Ada {ada.TileX},{ada.TileY} facing {ada.FacingX},{ada.FacingY} | " +
                   $"Byron {byron.TileX},{byron.TileY} facing {byron.FacingX},{byron.FacingY} | " +
                   $"Cato {cato.TileX},{cato.TileY} facing {cato.FacingX},{cato.FacingY}");

        report.Check(ada.TileX != cato.TileX || ada.TileY != cato.TileY,
                     "the two teams are not standing on the same tile");

        // ---- everything placed carries an owning team ----------------------

        var adaUplink = (adaOreX + 2, adaOreY + 2);
        var byronUplink = (adaOreX + 6, adaOreY + 4);
        var catoUplink = (catoOreX + 2, catoOreY + 2);

        report.Equal(BuildResult.Ok,
                     world.TryBuild(builds, uplinkItem, adaUplink.Item1, adaUplink.Item2,
                                    uplinkRecipe, Direction.East, ada),
                     "Ada builds Red's Uplink");
        report.Equal(BuildResult.Ok,
                     world.TryBuild(builds, uplinkItem, byronUplink.Item1, byronUplink.Item2,
                                    uplinkRecipe, Direction.East, byron),
                     "Byron builds a second Red Uplink");
        report.Equal(BuildResult.Ok,
                     world.TryBuild(builds, uplinkItem, catoUplink.Item1, catoUplink.Item2,
                                    uplinkRecipe, Direction.East, cato),
                     "Cato builds Blue's Uplink");

        report.Equal(0, world.OwnerOfAnchor(adaUplink.Item1, adaUplink.Item2), "Ada's Uplink is Red's");
        report.Equal(0, world.OwnerOfAnchor(byronUplink.Item1, byronUplink.Item2), "Byron's Uplink is Red's");
        report.Equal(1, world.OwnerOfAnchor(catoUplink.Item1, catoUplink.Item2), "Cato's Uplink is Blue's");

        // ---- a rival cannot take it down; a teammate can -------------------

        // From across the map first: ownership is answered before reach, so a
        // player is never sent on a walk that ends in "that is not yours".
        report.Equal(RemoveResult.OtherTeam,
                     world.TryRemove(adaUplink.Item1, adaUplink.Item2, cato).Result,
                     "Cato cannot remove Red's Uplink from far away");

        var catoWasAt = (cato.X, cato.Y);
        Walk.To(world, cato, adaUplink.Item1, adaUplink.Item2, withinTiles: 1);
        report.Equal(RemoveResult.OtherTeam,
                     world.TryRemove(adaUplink.Item1, adaUplink.Item2, cato).Result,
                     "Cato cannot remove Red's Uplink standing next to it either");
        report.Equal(RecipeChangeResult.OtherTeam,
                     TryRetask(world, builds, adaUplink, uplinkRecipe, cato),
                     "Cato cannot retask Red's Uplink");

        // And a rival's Uplink is not somewhere Cato can deliver, either --
        // Blue's own is a hundred tiles away, so nothing is in reach at all.
        report.Equal(DeliveryRefusal.NoUplinkInReach,
                     world.DeliverByHand(catalogue.Item("stone_deposit"), 1, cato).Refusal,
                     "Cato standing on Red's Uplink has no Uplink of his own in reach");

        cato.Restore(catoWasAt.Item1, catoWasAt.Item2, cato.FacingX, cato.FacingY);

        // Ada takes down her teammate's Uplink. A team shares its factory, so
        // this succeeds -- and the item lands in the hands that did the work.
        Walk.To(world, ada, byronUplink.Item1, byronUplink.Item2, withinTiles: 1);
        var teammate = world.TryRemove(byronUplink.Item1, byronUplink.Item2, ada);
        report.Equal(RemoveResult.Ok, teammate.Result, "Ada removes her teammate's Uplink");
        report.Equal(1, ada.Inventory.Count(uplinkItem), "and it is in Ada's pockets, not Byron's");
        report.Equal(0, byron.Inventory.Count(uplinkItem), "Byron's pockets are unchanged");

        // ---- the two ladders move independently ----------------------------

        // Blue's ore arrives on a machine's input buffer, which is what a belt
        // or an inserter does; Red's ore is dug and held. So the two teams end
        // at two different rungs, by two different routes.
        Walk.To(world, ada, adaOreX, adaOreY);
        var adaDug = world.TryDigByHand(adaOreX, adaOreY, 6, ada);
        report.Equal(DigResult.Ok, adaDug.Result, "Ada digs Red's ore");

        var catoDug = world.TryDigByHand(catoOreX, catoOreY, 4, cato);
        report.Equal(DigResult.Ok, catoDug.Result, "Cato digs Blue's ore");

        var redBefore = world.Teams[0].Research!.UnlockedInOrder.Count();

        world.TryMachineAt(catoUplink.Item1, catoUplink.Item2, out var blueUplink, out _);
        blueUplink.PushInput(catoDug.Item, 1);
        cato.Inventory.Take(catoDug.Item, 1);
        world.Tick();

        report.Equal(1, world.Teams[1].UnattendedDeliveries,
                     "Blue scored the unattended delivery");
        report.Equal(0, world.Teams[0].UnattendedDeliveries,
                     "and Red scored nothing from Blue's belt");
        report.Equal(redBefore, world.Teams[0].Research!.UnlockedInOrder.Count(),
                     "Red's unlock list did not move when Blue delivered");
        report.Check(world.Teams[1].Research!.UnlockedInOrder.Count() > redBefore,
                     "Blue is ahead of Red on the ladder");

        report.Say($"ladder          Red unlocked={world.Teams[0].Research!.UnlockedInOrder.Count()} " +
                   $"unattended={world.Teams[0].UnattendedDeliveries} | " +
                   $"Blue unlocked={world.Teams[1].Research!.UnlockedInOrder.Count()} " +
                   $"unattended={world.Teams[1].UnattendedDeliveries}");

        // ---- save, reload, and it is the same world ------------------------

        var save = Save.SaveGame.Capture(world);
        report.Equal(Save.SaveFile.CurrentVersion, save.Version, "save version");

        var loaded = Save.SaveGame.Restore(save, catalogue.Recipes,
                                           new WorldGen(seed, NewGame.OreSpecs(catalogue)));
        var again = Save.SaveGame.Capture(loaded);

        report.Check(Save.SaveGame.ToJson(save) == Save.SaveGame.ToJson(again),
                     "the reloaded world saves to identical bytes");

        report.Equal(3, loaded.Players.Count, "the roster survived the round trip");
        report.Equal(2, loaded.Teams.Count, "the teams survived the round trip");

        for (var i = 0; i < world.Players.Count; i++)
        {
            var before = world.Players[i];
            var after = loaded.Players[i];
            report.Check(before.Name == after.Name && before.TeamId == after.TeamId
                         && before.X == after.X && before.Y == after.Y
                         && before.FacingX == after.FacingX && before.FacingY == after.FacingY
                         && SameContents(before.Inventory, after.Inventory),
                         $"player {i} ({before.Name}) round-tripped whole");
        }

        report.Check(loaded.OwnerOfAnchor(adaUplink.Item1, adaUplink.Item2) == 0
                     && loaded.OwnerOfAnchor(catoUplink.Item1, catoUplink.Item2) == 1,
                     "ownership round-tripped");

        report.Equal(RemoveResult.OtherTeam,
                     loaded.TryRemove(adaUplink.Item1, adaUplink.Item2, loaded.Players[2]).Result,
                     "and a rival is still refused after a reload");

        // ---- and the reloaded ladders are still two ------------------------

        var reloadedCato = loaded.Players[2];
        var reloadedAda = loaded.Players[0];
        Walk.To(loaded, reloadedAda, adaUplink.Item1, adaUplink.Item2, withinTiles: 1);

        var blueUnlockedBefore = loaded.Teams[1].Research!.UnlockedInOrder.Count();
        var handed = loaded.DeliverByHand(adaDug.Item, 1, reloadedAda);
        report.Equal(DeliveryRefusal.None, handed.Refusal, "Ada delivers by hand after the reload");
        report.Check(loaded.Teams[0].Research!.UnlockedInOrder.Count() > redBefore,
                     "Red caught up");
        report.Equal(blueUnlockedBefore, loaded.Teams[1].Research!.UnlockedInOrder.Count(),
                     "and Blue did not move when Red delivered");
        report.Equal(0, loaded.Teams[0].UnattendedDeliveries,
                     "a hand delivery is still not an unattended one");
        report.Check(reloadedCato.TeamId == 1, "Cato is still on Blue");

        report.Say(report.Ok ? "--- teams ok ---" : $"--- teams FAILED ({report.Failures.Count}) ---");
        return report;
    }

    private static RecipeChangeResult TryRetask(World world, BuildCatalogue builds,
                                                (int X, int Y) at, Recipe recipe, Player actor)
    {
        world.TryMachineAt(at.X, at.Y, out _, out var index);
        return world.TryChangeRecipe(builds, index, recipe, out _, actor);
    }

    private static bool SameContents(Inventory a, Inventory b)
    {
        var left = a.Contents.OrderBy(kv => kv.Key.Value).ToList();
        var right = b.Contents.OrderBy(kv => kv.Key.Value).ToList();
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!left[i].Key.Equals(right[i].Key) || left[i].Value != right[i].Value)
                return false;
        return true;
    }

    /// The nearest patch this player's team can actually use, which is the same
    /// question the prospector answers for them in the UI (ADR 0026).
    private static (int X, int Y) NearestUsableOre(World world, Player player,
                                                   (int X, int Y)? preferDifferentFrom = null)
    {
        var research = world.ResearchOf(player);
        var usable = new HashSet<int>();
        foreach (var recipe in Catalogue.Instance.Recipes.Values)
        {
            if (research is not null && !research.IsUnlocked(recipe)) continue;
            foreach (var input in recipe.Inputs)
                usable.Add(input.Item.Value);
        }

        var hits = new Prospector().Scan(world.Ground.Gen, player.TileX, player.TileY, usable);

        (int X, int Y)? fallback = null;

        foreach (var hit in hits)
        {
            if (!hit.Usable) continue;
            fallback ??= (hit.X, hit.Y);
            if (preferDifferentFrom is { } other && hit.X == other.X && hit.Y == other.Y) continue;
            return (hit.X, hit.Y);
        }

        if (fallback is { } only) return only;

        throw new InvalidOperationException(
            $"seed {world.Seed} deals no usable patch near {player.Name}; " +
            "the starter guarantee (ADR 0026) says there is one");
    }
}
