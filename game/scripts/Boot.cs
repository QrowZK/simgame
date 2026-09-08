using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Game;

/// The entry point. Decides whether this launch shows the title screen or goes
/// straight into a world.
///
/// The headless paths -- `--smoke`, `--screenshot`, `--machines=N` -- bypass the
/// menu entirely. CI drives those, and a title screen waiting for a click is a
/// hang rather than a test.
public sealed partial class Boot : Node
{
    private MainMenu? _menu;
    private GameRoot? _game;

    private int _menuShotCountdown = -1;

    public override void _Ready()
    {
        if (Cli.Has("--menu-shot"))
        {
            ShowMenu();
            _menuShotCountdown = 12;    // let the theme settle before capturing
            return;
        }

        if (Cli.Has("--session-test"))
        {
            CallDeferred(nameof(RunSessionTest));
            return;
        }

        // The one path a player actually takes, and the one nothing covered:
        // menu -> New Game with a clock-derived seed. Every other headless run
        // builds its world directly and skips the menu entirely, which is how a
        // release shipped with New Game broken.
        if (Cli.Has("--guide-test"))
        {
            CallDeferred(nameof(RunGuideTest));
            return;
        }

        if (Cli.Has("--save-select-shot"))
        {
            CallDeferred(nameof(RunSaveSelectShot));
            return;
        }

        if (Cli.Has("--menu-test"))
        {
            CallDeferred(nameof(RunMenuTest));
            return;
        }

        if (Cli.Has("--net-test"))
        {
            CallDeferred(nameof(RunNetTest));
            return;
        }

        if (Cli.Has("--net-lockstep-test"))
        {
            CallDeferred(nameof(RunNetLockstepTest));
            return;
        }

        if (Cli.Has("--net-shot"))
        {
            CallDeferred(nameof(RunNetShot));
            return;
        }

        if (Cli.WantsHeadlessRun())
        {
            // --start-shot renders a real new game; the other headless runs want
            // the dense placeholder factory, because they exist to measure the
            // renderer rather than to play the game.
            StartGame(Cli.Has("--start-shot")
                ? GameSession.NewGame(Cli.ReadInt("--seed", 20260907))
                : GameSession.DemoFactory(
                    seed: 1234, Cli.ReadInt("--machines", GameSession.DefaultMachineCount)));
            return;
        }

        ShowMenu();
    }

    private int _screenshotCountdown = -1;

    public override void _Process(double delta)
    {
        if (_screenshotCountdown > 0 && --_screenshotCountdown == 0)
        {
            var shot = GetViewport().GetTexture().GetImage();
            shot.SavePng("user://shot.png");
            GD.Print($"screenshot {shot.GetWidth()}x{shot.GetHeight()} -> " +
                     ProjectSettings.GlobalizePath("user://shot.png"));
            GD.Print("=== SAVE SELECT OK ===");
            GetTree().Quit();
        }

        if (_menuShotCountdown > 0 && --_menuShotCountdown == 0)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng("user://menu.png");
            GD.Print($"menu screenshot -> {ProjectSettings.GlobalizePath("user://menu.png")}");
            GetTree().Quit();
        }
    }

    /// Capture path for the load screen. Writes three real saves and one file
    /// that is deliberately not a save, because the row for a file that will
    /// not parse is half the point of the design and cannot be photographed
    /// without one.
    private void RunSaveSelectShot()
    {
        GD.Print("=== SAVE SELECT ===");

        GameSession.NewGame(seed: 1481765108);
        for (var i = 0; i < 240; i++) GameSession.World!.Tick();
        GameSession.Save("quicksave");

        for (var i = 0; i < 120; i++) GameSession.World!.Tick();
        GameSession.Save("autosave");

        GameSession.NewGame(seed: 1132414339);
        for (var i = 0; i < 60; i++) GameSession.World!.Tick();
        GameSession.Save("banana");

        using (var broken = FileAccess.Open("user://saves/steam-run.json", FileAccess.ModeFlags.Write))
            broken?.StoreString("{ this is not a save file");

        ShowMenu();
        _saveSelect = null;
        ShowSaveSelect();
        GD.Print($"slots           {GameSession.List().Count}");

        _screenshotCountdown = 8;
    }

    /// Presses New Game the way a player does: through the menu, with the seed
    /// the menu would invent, and then runs the world the button produced.

    /// Walks the opening ladder the way a person walks it, and checks that the
    /// guide keeps up.
    ///
    /// `GuideTests` cover the logic; this covers the thing the logic is for. A
    /// guide is only useful if it advances when the player does the work, and
    /// the failure that matters -- a card that still says "find ore" after you
    /// have mined half a patch -- is a stuck step, not a wrong sentence. So
    /// every stage here does the deed with the same calls the interface makes,
    /// then asks the guide what is next and refuses to accept the same answer
    /// twice.
    private void RunGuideTest()
    {
        GD.Print("=== GUIDE ===");

        var world = GameSession.NewGame(seed: 20260907);
        var catalogue = new Sim.BuildCatalogue(Sim.Data.Catalogue.Instance);
        var seen = new List<string>();
        var stuck = "";

        string Now()
        {
            var step = Sim.Guide.Current(world);
            return step is { } s ? s.Title : "(ladder finished)";
        }

        void Stage(string did)
        {
            var title = Now();
            if (seen.Count > 0 && title == seen[seen.Count - 1] && stuck.Length == 0)
                stuck = $"after \"{did}\" the guide still says \"{title}\"";

            seen.Add(title);
            GD.Print($"  after {did,-28} -> {title}");
        }

        GD.Print($"  at tick zero{new string(' ', 24)} -> {Now()}");
        seen.Add(Now());

        // 1. Mine, the way a click mines.
        var hits = new Sim.Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0,
                                                       world.Research?.ConsumableNow(world.Items));
        var dug = 0;
        var ore = default(Sim.ItemId);
        for (var i = 0; i < hits.Count && dug == 0; i++)
        {
            Sim.Walk.To(world, hits[i].X, hits[i].Y);
            dug = world.TryDigByHand(hits[i].X, hits[i].Y, 40).Taken;
            if (dug > 0) ore = hits[i].Item;
        }
        Stage($"mining {dug} by hand");

        // 2. Put the Uplink down.
        var uplink = catalogue.Find(Sim.Research.UplinkItem);
        var uplinkRecipe = uplink is null || world.Research is null
            ? null
            : catalogue.RecipesFor(uplink, world.Research).FirstOrDefault();

        var placed = Sim.BuildResult.NoneCarried;
        var (uplinkX, uplinkY) = (0, 2);
        for (var d = 2; d < 60 && placed != Sim.BuildResult.Ok && uplink is not null; d++)
        {
            Sim.Walk.To(world, d, 2);
            placed = world.TryBuild(catalogue, uplink.Item, d, 2, uplinkRecipe!);
            if (placed == Sim.BuildResult.Ok) (uplinkX, uplinkY) = (d, 2);
        }

        // Delivering is a thing you do with your hands, so every delivery below
        // is preceded by the walk back to the lander. That walk is the whole
        // reason the Uplink's siting is a decision (ADR 0033).
        void AtTheUplink() => Sim.Walk.To(world, uplinkX, uplinkY);
        Stage($"placing the Uplink ({placed})");

        // 3. Hand it one ore. This is the whole research loop, once.
        AtTheUplink();
        var first = world.DeliverByHand(ore, 1);
        Stage($"delivering 1 ore ({first.Accepted} taken)");

        // 4-6. The rungs that follow are ingot deliveries. Smelting them needs a
        // furnace, a fuel and time; what this stage checks is the guide, so the
        // ingots are granted rather than smelted, and the furnace is placed so
        // the step that asks for it is genuinely satisfied.
        var furnace = catalogue.Find("man_furnace");
        if (furnace is not null && world.PlayerInventory.Count(furnace.Item) > 0)
        {
            var recipe = catalogue.RecipesFor(furnace, world.Research).FirstOrDefault();
            for (var d = 2; d < 60; d++)
            {
                Sim.Walk.To(world, d, 6, Sim.Player.BuildReachTiles);
                if (world.TryBuild(catalogue, furnace.Item, d, 6, recipe!) == Sim.BuildResult.Ok)
                    break;
            }
        }
        Stage("placing the Manual Furnace");

        if (world.Items.TryGetId("copper_ingot", out var ingot))
        {
            world.PlayerInventory.Add(ingot, 3);
            AtTheUplink();
            world.DeliverByHand(ingot, 3);
            Stage("delivering 3 ingots");

            world.PlayerInventory.Add(ingot, 6);
            AtTheUplink();
            world.DeliverByHand(ingot, 6);
            Stage("delivering 6 ingots");
        }

        var steps = Sim.Guide.Steps(world);
        var done = steps.Count(s => s.Done);
        GD.Print($"  ladder          {done} of {steps.Count} steps done");

        if (stuck.Length > 0)
        {
            GD.Print($"guide           FAILED: {stuck}");
            GetTree().Quit(1);
            return;
        }

        if (done < 6)
        {
            GD.Print($"guide           FAILED: only {done} steps completed by playing them");
            GetTree().Quit(1);
            return;
        }

        GD.Print("=== GUIDE OK ===");
        GetTree().Quit();
    }

    private void RunMenuTest()
    {
        GD.Print("=== MENU ===");

        ShowMenu();

        if (_menu is null)
        {
            GD.Print("menu            FAILED: no menu instantiated");
            GetTree().Quit(1);
            return;
        }

        // The menu's own "surprise me" seed, not a fixed one: a new game that
        // works on seed 20260907 and nowhere else is still a broken new game.
        var seed = MainMenu.SurpriseMeSeed();
        GD.Print($"menu seed       {seed}");

        _menu.EmitSignal(MainMenu.SignalName.NewGameRequested, seed);

        if (_game?.World is not { } world)
        {
            GD.Print("new game        FAILED: no world after New Game");
            GetTree().Quit(1);
            return;
        }

        for (var i = 0; i < 240; i++)
            world.Tick();

        GD.Print($"new game        tick={world.TickCount} machines={world.MachineCount} " +
                 $"carrying={world.PlayerInventory.Contents.Count} kinds");
        GD.Print($"research        {(world.Research is null ? "MISSING" : "present")}");

        // ---- and the solo input path, which is the same path ----------------
        //
        // Every mutating click goes through `PlayerActions` now, shared world or
        // not (docs/0039). Solo it must feel *exactly* as it did: the world
        // changes inside the click, with no queue and no wait. That is asserted
        // here rather than assumed, because the obvious way to write one path is
        // to make the solo one wait for a tick that never comes -- and nothing
        // else headless drives `GameRoot`'s click handlers on a real new game.
        var root = _game!;
        var builds = new Sim.BuildCatalogue(Sim.Data.Catalogue.Instance);
        var bench = builds.Find("man_manual_crafting");
        // A recipe the starter kit can actually pay for, so the hand-feed below
        // is a load rather than a "you are not carrying any of what it wants".
        var recipe = bench is null
            ? null
            : builds.RecipesFor(bench, world.Research)
                    .FirstOrDefault(r => r.Inputs.All(
                        i => world.PlayerInventory.Count(i.Item) >= i.Count))
              ?? builds.RecipesFor(bench, world.Research).FirstOrDefault();

        var spoken = new List<string>();
        var prior = root.Actions.Speak;
        root.Actions.Speak = m => { prior?.Invoke(m); spoken.Add(m); };

        if (bench is null || recipe is null)
        {
            GD.Print("solo input      FAILED: no bench and recipe to click with");
            GetTree().Quit(1);
            return;
        }

        var machinesBefore = world.MachineCount;
        var tile = (X: world.Player.TileX + 2, Y: world.Player.TileY);

        root.Hold(bench, recipe);
        root.PlaceHeld(tile.X, tile.Y);
        var immediately = world.MachineCount;

        root.PlaceHeld(tile.X, tile.Y);          // refused: only one bench carried
        root.DigOrClose(tile.X + 40, tile.Y + 40);  // bare ground: not an action at all

        // Hand-feeding, solo, in the click (ADR 0040). The same two commands a
        // shared world sends over the wire, and the same assertion as the
        // build: solo waits for nobody.
        var stoneBefore = world.PlayerInventory.Contents
                               .Where(kv => world.Items.GetName(kv.Key) == "stone_deposit")
                               .Sum(kv => kv.Value);
        root.Actions.Load(tile.X, tile.Y, 1, $"The bench at {tile.X},{tile.Y}");
        var loadedInTheClick = world.TryMachineAt(tile.X, tile.Y, out var fed, out _)
            ? fed.InputContents.Values.Sum()
            : -1;
        root.Actions.Take(tile.X, tile.Y, $"The bench at {tile.X},{tile.Y}");  // nothing yet

        GD.Print($"solo hand-feed  bench holds {loadedInTheClick} in the click, " +
                 $"stone {stoneBefore} -> " +
                 $"{world.PlayerInventory.Contents.Where(kv => world.Items.GetName(kv.Key) == "stone_deposit").Sum(kv => kv.Value)}");

        if (loadedInTheClick <= 0)
        {
            foreach (var line in spoken) GD.Print($"solo said       \"{Trim(line)}\"");
            GD.Print("solo hand-feed  FAILED: a solo load took effect on no tick");
            GetTree().Quit(1);
            return;
        }

        root.RemoveAt(tile.X, tile.Y);              // and take it back
        var afterRemoval = world.MachineCount;

        GD.Print($"solo input      machines {machinesBefore} -> {immediately} in the click " +
                 $"-> {afterRemoval} after removal, in flight " +
                 $"{root.Actions.InFlight.Count}");
        GD.Print($"solo actions    {root.Actions.Issued} issued, {root.Actions.Applied} " +
                 $"applied, {root.Actions.Refused} refused: {root.Actions.Tally()}");
        foreach (var line in spoken) GD.Print($"solo said       \"{Trim(line)}\"");

        if (immediately != machinesBefore + 1)
        {
            GD.Print($"solo input      FAILED: a solo build took effect on no tick " +
                     $"({machinesBefore} -> {immediately}) -- solo must not wait");
            GetTree().Quit(1);
            return;
        }

        if (afterRemoval != machinesBefore)
        {
            GD.Print($"solo input      FAILED: removal left {afterRemoval} machines");
            GetTree().Quit(1);
            return;
        }

        if (root.Actions.InFlight.Count != 0)
        {
            GD.Print("solo input      FAILED: a solo click queued something");
            GetTree().Quit(1);
            return;
        }

        if (root.Actions.CountOf(Sim.CommandOutcome.NothingToTake) == 0)
        {
            GD.Print("solo hand-feed  FAILED: taking from a bench that has made nothing " +
                     "was not refused");
            GetTree().Quit(1);
            return;
        }

        if (root.Actions.Applied < 2 || root.Actions.Refused < 1 || spoken.Count < 3)
        {
            GD.Print($"solo input      FAILED: {root.Actions.Applied} applied, " +
                     $"{root.Actions.Refused} refused, {spoken.Count} sentences");
            GetTree().Quit(1);
            return;
        }

        GD.Print("=== MENU OK ===");
        GetTree().Quit();
    }

    /// Drives the whole save loop headlessly: new game, run it, save, load it
    /// back, and check the world that returns is the one that was saved. This is
    /// the path a player actually takes, and none of the unit tests cover it --
    /// they stop at the sim boundary and never touch the file layer.
    private void RunSessionTest()
    {
        GD.Print("=== SESSION ===");

        var world = GameSession.DemoFactory(seed: 777, machineCount: 64);
        world.Tick(300);

        // Storage has to be genuinely part-full before the save, or a save
        // that dropped the charge would round-trip an empty bank unnoticed.
        GD.Print($"storage         {world.StoredEnergy}/{world.StorageCapacity} " +
                 $"part-full: {world.StoredEnergy > 0 && world.StoredEnergy < world.StorageCapacity}");

        var before = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world));
        GameSession.Save("session test");

        var slots = GameSession.List();
        GD.Print($"saves listed    {slots.Count}");
        GD.Print($"slot name       {(slots.Count > 0 ? slots[0].Name : "<none>")}");
        GD.Print($"slot tick       {(slots.Count > 0 ? slots[0].Tick : -1)}");

        var loaded = GameSession.Load(GameSession.PathFor("session test"));
        var after = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(loaded));

        GD.Print($"round trip      identical: {before == after}");

        // And it must still be identical after both run on from there.
        world.Tick(2000);
        loaded.Tick(2000);
        var beforeRun = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(world));
        var afterRun = Sim.Save.SaveGame.ToJson(Sim.Save.SaveGame.Capture(loaded));
        GD.Print($"after 2000      identical: {beforeRun == afterRun}");

        GameSession.Delete(GameSession.PathFor("session test"));
        GD.Print($"deleted         {!GameSession.List().Any(s => s.Name == "session test")}");

        RunStartFlow();
        GD.Print("=== SESSION OK ===");

        GetTree().Quit();
    }

    /// The first ten minutes of a real new game, headlessly: land with a starter
    /// kit and no factory, prospect, walk to ore, dig by hand, build a miner on
    /// the patch, and watch the ground go down. If any of that is broken the
    /// game is unplayable no matter what the unit tests say.
    private void RunStartFlow()
    {
        GD.Print("--- start flow ---");

        var world = GameSession.NewGame(seed: 20260907);
        GD.Print($"machines        {world.MachineCount} (a new game must have none)");
        GD.Print($"miners          {world.Miners.Count}");
        GD.Print($"carrying        {world.PlayerInventory.Contents.Count} kinds");

        var usable = world.Research?.ConsumableNow(world.Items);
        var hits = new Sim.Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0, usable);
        GD.Print($"prospected      {hits.Count} resources within 400 tiles");

        // What the carried device sees, and whether the top of its list is
        // something the player can act on. This is the line that would have
        // caught F2: it read "nearest ore halite" and reported success anyway,
        // because nothing checked that halite was any use (ADR 0026).
        var carried = new Sim.Prospector().Scan(world.Ground.Gen, 0, 0, usable);
        var firstUsable = carried.FindIndex(h => h.Usable);
        GD.Print($"survey          {carried.Count} in prospector range, " +
                 $"{carried.Count(h => h.Usable)} usable now");
        GD.Print(firstUsable >= 0
            ? $"first usable    {world.Items.GetName(carried[firstUsable].Item)} at " +
              $"{carried[firstUsable].Distance} tiles, rank {firstUsable + 1} of {carried.Count}"
            : "first usable    NONE -- the opening is a walk in a direction the game never names");
        if (hits.Count == 0)
        {
            GD.Print("start flow      FAILED: nothing to mine near spawn");
            return;
        }

        var hit = hits[0];
        var name = world.Items.GetName(hit.Item);
        var before = world.Ground.RemainingAt(hit.X, hit.Y);

        // Reach is real now (ADR 0033), so the ore has to be walked to before
        // it can be dug -- and the walk is worth reporting, because "how long
        // is the opening walk" is the number that decides whether the first
        // minute of the game is a stroll or a chore.
        var refusedFromSpawn = world.TryDigByHand(hit.X, hit.Y, 25).Result;
        var walked = Sim.Walk.To(world, hit.X, hit.Y);
        var dug = world.TryDigByHand(hit.X, hit.Y, 25).Taken;

        GD.Print($"nearest ore     {name} at {hit.X},{hit.Y} ({hit.Distance} tiles), {before} units");
        GD.Print($"from spawn      {refusedFromSpawn} (digging it without walking)");
        GD.Print($"walked          {walked} ticks ({walked / 60.0:0.0} s) to stand on it");
        GD.Print($"hand mined      {dug}");

        var miner = world.TryPlaceMiner(
            new Sim.MachinePlacement(hit.X, hit.Y, 0, 0, 1), cycleTicks: 30);
        GD.Print($"miner built     {miner is not null}");

        world.Tick(600);
        var after = world.Ground.RemainingAt(hit.X, hit.Y);
        GD.Print($"after 600       buffered={miner?.Buffered} ground={after}");
        GD.Print($"ground fell     {after < before - dug}");

        RunBuildFlow();
        RunBeltFlow();
        RunPowerFlow(world, hit.X, hit.Y);
        RunFluidFlow(world);
        GD.Print("--- start flow ok ---");
    }

    /// Building as a player does it: a new game, the starter kit's bench put
    /// down on a chosen tile, and the refusals that protect them on the way.
    ///
    /// The unit tests cover TryBuild; this covers the path the GUI actually
    /// takes to it, including that a new game can build anything at all. If
    /// the starter kit ever stops containing something placeable, the opening
    /// loop is broken and this is what says so.
    private void RunBuildFlow()
    {
        GD.Print("--- building ---");

        var world = GameSession.NewGame(seed: 20260907);
        var catalogue = new Sim.BuildCatalogue(Sim.Data.Catalogue.Instance);

        var carried = catalogue.Offerable
                               .Where(b => world.PlayerInventory.Count(b.Item) > 0)
                               .ToList();
        GD.Print($"can build       {carried.Count} kinds from the starter kit");

        if (carried.Count == 0)
        {
            GD.Print("building        FAILED: a new game can place nothing");
            return;
        }

        var bench = carried[0];

        // The bench is placed on a recipe it can actually finish. It used to be
        // placed on `RecipesFor(bench).FirstOrDefault()` -- form_copper_plate,
        // which needs a copper ingot, which needs a furnace, which needs the
        // bench -- and the flow then printed "ok". See docs/0021.
        var recipe = catalogue.RecipesFor(bench).FirstOrDefault(r => r.Id == "build_man_furnace")
                     ?? catalogue.RecipesFor(bench).FirstOrDefault();
        GD.Print($"holding         {bench.Name} ({catalogue.RecipesFor(bench).Count} recipes)");

        // A bare tile near spawn, chosen the way the ghost chooses one.
        var (x, y) = (0, 0);
        for (var r = 0; r < 200 && !Free(world, bench, x, y); r++)
            (x, y) = (r, 0);

        var first = world.TryBuild(catalogue, bench.Item, x, y, recipe);
        GD.Print($"built           {first} at {x},{y}");
        GD.Print($"machines        {world.MachineCount}");

        // The same tile again: refused, and it must not have cost anything.
        world.PlayerInventory.Add(bench.Item, 1);
        var again = world.TryBuild(catalogue, bench.Item, x, y, recipe);
        GD.Print($"same tile       {again} (still carrying {world.PlayerInventory.Count(bench.Item)})");

        // And a machine with no recipe chosen, which is what an impatient click
        // through the build menu does.
        var nowhere = world.TryBuild(catalogue, bench.Item, x + 6, y + 6);
        GD.Print($"no recipe       {nowhere}");

        var ok = first == Sim.BuildResult.Ok
                 && again == Sim.BuildResult.Blocked
                 && nowhere == Sim.BuildResult.NeedsRecipe
                 && world.PlayerInventory.Count(bench.Item) == 1;

        GD.Print(ok ? "--- building ok ---" : "building        FAILED");

        RunRemovalFlow();
        RunOpeningRoute();
    }

    /// Removal, played as a player plays it: put something down, take it back,
    /// and find both the item and the tile where they were before.
    ///
    /// The second half is the case that made removal exist (ADR 0028): an
    /// underground belt entrance placed by mistake refuses every same-facing
    /// end from reach+1 to reach*2 ahead of it, and before this that band of
    /// the player's bus was unbuildable for the life of the save.
    private void RunRemovalFlow()
    {
        GD.Print("--- removal ---");

        var world = GameSession.NewGame(seed: 20260907);
        var catalogue = new Sim.BuildCatalogue(Sim.Data.Catalogue.Instance);

        var bench = catalogue.Offerable
                             .First(b => world.PlayerInventory.Count(b.Item) > 0);
        var recipe = catalogue.RecipesFor(bench).FirstOrDefault(r => r.Id == "build_man_furnace")
                     ?? catalogue.RecipesFor(bench).FirstOrDefault();

        var (x, y) = (0, 0);
        for (var r = 0; r < 200 && !Free(world, bench, x, y); r++)
            (x, y) = (r, 0);

        var carried = world.PlayerInventory.Count(bench.Item);
        var built = world.TryBuild(catalogue, bench.Item, x, y, recipe);
        var report = world.TryRemove(x, y);
        var backAgain = world.TryBuild(catalogue, bench.Item, x, y, recipe);

        GD.Print($"built           {built} at {x},{y}");
        GD.Print($"removed         {report.Result} returning " +
                 $"{world.Items.GetName(report.Item)} +{report.Returned} inside");
        GD.Print($"carrying        {carried} before, " +
                 $"{world.PlayerInventory.Count(bench.Item)} after rebuild");
        GD.Print($"tile reusable   {backAgain}");
        GD.Print($"bare ground     {world.TryRemove(x + 40, y + 40).Result}");

        // The tunnel band, end to end.
        var tunnel = catalogue.Find("vlt_underground_belt")!;
        var band = tunnel.UndergroundReach + 1;
        world.PlayerInventory.Add(tunnel.Item, 2);
        Sim.Walk.To(world, 200 + band / 2, 0);
        var entrance = world.TryBuild(catalogue, tunnel.Item, 200, 0, null, Sim.Direction.East);
        var refused = world.TryBuild(catalogue, tunnel.Item, 200 + band, 0, null,
                                     Sim.Direction.East);
        var freed = world.TryRemove(200, 0);
        var afterward = world.TryBuild(catalogue, tunnel.Item, 200 + band, 0, null,
                                       Sim.Direction.East);

        GD.Print($"tunnel band     entrance={entrance} then {refused} at +{band}");
        GD.Print($"entrance pulled {freed.Result}, band now {afterward}");

        var ok = built == Sim.BuildResult.Ok
                 && report.Result == Sim.RemoveResult.Ok
                 && backAgain == Sim.BuildResult.Ok
                 && world.PlayerInventory.Count(bench.Item) == carried - 1
                 && refused == Sim.BuildResult.TooFarToTunnel
                 && freed.Result == Sim.RemoveResult.Ok
                 && afterward == Sim.BuildResult.Ok;

        GD.Print(ok ? "--- removal ok ---" : "removal         FAILED");
    }

    /// The opening route, played end to end on what a new game is actually
    /// given: one bench, 24 stone, a prospector and the ground.
    ///
    /// The milestone is a steam miner **in the player's hands** -- the first
    /// thing that automates anything, and the step the game dead-ended before.
    /// Reaching it needs ten distinct bench recipes and the bench is the only
    /// one there will ever be, so this passes only if a placed machine can be
    /// retasked. It is deliberately the assertion the old build flow was not:
    /// break any link in the chain and it goes red rather than printing "ok".
    private void RunOpeningRoute()
    {
        GD.Print("--- opening route ---");

        var data = Sim.Data.Catalogue.Instance;
        var buildables = new Sim.BuildCatalogue(data);
        var world = GameSession.NewGame(seed: 20260907);

        var route = new OpeningRoute(world, data, buildables, GD.Print);
        var reached = route.Reach("stm_miner", 1);

        GD.Print($"stuck on        {route.StuckOn ?? "nothing"}");
        // One bench, and the whole steam tier is crafted on it. If a machine
        // can only ever run the recipe it was placed with, this is 1.
        var benchRecipes = route.RecipesRun
            .Count(r => data.Data.Recipes.Any(d => d.Id == r && d.Machine == "manual_crafting"));
        GD.Print($"bench recipes   {benchRecipes} distinct on one bench");
        GD.Print($"retasks         {route.Retasks}");
        GD.Print($"machines built  {world.MachineCount}");
        var minersInHand = world.PlayerInventory.Count(data.Item("stm_miner"));
        GD.Print($"miner in hand   {minersInHand}");

        // And it is a real miner: placed on ore, it digs.
        var patch = new Sim.Prospector(radius: 400)
            .Scan(world.Ground.Gen, 0, 0)
            .FirstOrDefault(h => world.Items.GetName(h.Item) == "chalcopyrite");

        Sim.Walk.To(world, patch.X, patch.Y);
        var built = world.TryBuild(buildables, data.Item("stm_miner"), patch.X, patch.Y);
        GD.Print($"miner placed    {built}");

        var ok = reached
                 && route.StuckOn is null
                 && benchRecipes >= 10
                 && minersInHand >= 1
                 && built == Sim.BuildResult.Ok;

        GD.Print(ok ? "--- opening route ok ---" : "opening route   FAILED");
    }

    /// Belts as a player lays them: a run of tiles built one at a time from the
    /// inventory, an inserter at the end of it, and ore actually arriving in a
    /// machine that had none.
    ///
    /// The unit tests cover the compiler; this covers the path through the
    /// build UI to it, and the thing a player would notice immediately if it
    /// broke -- that a line they laid moves anything at all.
    private void RunBeltFlow()
    {
        GD.Print("--- belts ---");

        var catalogue = Sim.Data.Catalogue.Instance;
        var buildables = new Sim.BuildCatalogue(catalogue);
        var world = GameSession.NewGame(seed: 20260907);

        var belt = buildables.Find("stm_transport_belt")!;
        var inserter = buildables.Find("stm_inserter")!;
        var furnace = buildables.Find("stm_furnace")!;

        // A recipe that eats what the belt will carry.
        var ore = catalogue.Item("chalcopyrite");
        var recipe = buildables.RecipesFor(furnace)
                               .FirstOrDefault(r => r.Inputs.Any(i => i.Item.Equals(ore)));
        if (recipe is null)
        {
            GD.Print("belts           FAILED: no furnace recipe takes the ore");
            return;
        }

        world.PlayerInventory.Add(belt.Item, 12);
        world.PlayerInventory.Add(inserter.Item, 1);
        world.PlayerInventory.Add(furnace.Item, 1);

        // Far enough out to be clear of the landing site's ore and water.
        const int y = 60;
        var laid = 0;
        for (var x = 0; x < 8; x++)
        {
            Sim.Walk.To(world, x, y, Sim.Player.BuildReachTiles);
            if (world.TryBuild(buildables, belt.Item, x, y, facing: Sim.Direction.East)
                == Sim.BuildResult.Ok) laid++;
        }

        // The arm stands at (7, y-1) facing north: it reaches back to the belt
        // tile at (7, y) and forward into the furnace at (7, y-2). Getting this
        // wrong is silent -- the inserter simply holds an item forever -- which
        // is why the check below is "did ore arrive" and not "did it build".
        Sim.Walk.To(world, 7, y - 2, Sim.Player.BuildReachTiles);
        var machineBuilt = world.TryBuild(buildables, furnace.Item, 7, y - 2, recipe);
        var armBuilt = world.TryBuild(buildables, inserter.Item, 7, y - 1,
                                      facing: Sim.Direction.North);

        world.SyncBelts();

        GD.Print($"laid            {laid} belt tiles -> {world.Belts.Segments.Count} segments");
        GD.Print($"inserter        {armBuilt} machine={machineBuilt}");

        // Ore onto the head of the line, the way a miner's inserter would.
        var head = world.BeltMap.SegmentAt(0, y);
        var placed = 0;
        for (var i = 0; i < 6 && head >= 0; i++)
        {
            if (world.Belts.Segments[head].LaneAt(0).TryInsertBack(ore)) placed++;
            world.Tick(20);
        }

        GD.Print($"loaded          {placed} onto the belt");

        world.Tick(1200);

        var arrived = world.MachineCount > 0
            ? world.Machines[0].GetInputCount(ore) + world.Machines[0].GetOutputCount(
                  world.Machines[0].Recipe.Outputs[0].Item)
            : 0;

        GD.Print($"reached machine {arrived > 0}");
        GD.Print($"spilled         {world.BeltMap.SpilledOnRemoval}");

        var ok = laid == 8 && world.Belts.Segments.Count >= 1
                 && armBuilt == Sim.BuildResult.Ok
                 && machineBuilt == Sim.BuildResult.Ok
                 && placed > 0 && arrived > 0
                 && world.BeltMap.SpilledOnRemoval == 0;

        GD.Print(ok ? "--- belts ok ---" : "belts           FAILED");
    }

    private static bool Free(Sim.World world, Sim.Buildable buildable, int x, int y)
    {
        var placement = buildable.PlacementAt(x, y);
        return world.CanPlace(placement) && !world.CoversFluidNode(placement);
    }

    /// The power loop, end to end: a machine that needs electricity sits dark
    /// until a pole reaches it and a fuelled generator feeds the network.
    private void RunPowerFlow(Sim.World world, int oreX, int oreY)
    {
        GD.Print("--- power ---");

        var catalogue = GameSession.Catalogue;
        var recipe = catalogue.Recipe("crush_chalcopyrite");

        var machine = world.TryPlaceMachine(recipe,
            new Sim.MachinePlacement(oreX + 10, oreY, 1, 3, 1), outputCapacityPerItem: 100);

        if (machine is null)
        {
            GD.Print("power           FAILED: could not place the machine");
            return;
        }

        machine.PushInput(catalogue.Item("chalcopyrite"), 100);
        world.Tick(120);
        GD.Print($"unpowered       state={machine.State} draw={machine.PowerDraw}/tick");

        world.Power.AddPole(new Sim.Pole(oreX + 10, oreY, supplyRadius: 6, wireRadius: 9));

        var generator = new Sim.Generator(catalogue.Item("coal_deposit"),
                                          outputPerTick: 20, ticksPerFuel: 400);
        generator.AddFuel(20);
        world.TryPlaceGenerator(generator, new Sim.MachinePlacement(oreX + 12, oreY, 1, 6, 1));

        // Storage on the same grid. Reported below so the smoke test says
        // whether energy actually reached it, rather than only that it exists.
        world.TryPlaceAccumulator(new Sim.Accumulator(capacity: 4000, ratePerTick: 40),
                                  new Sim.MachinePlacement(oreX + 11, oreY + 1, 1, 7, 1));

        world.Tick(120);
        var supply = 0;
        var demand = 0;
        foreach (var n in world.NetworkSupply) supply += n;
        foreach (var n in world.NetworkDemand) demand += n;

        GD.Print($"networks        {world.Power.NetworkCount}");
        GD.Print($"grid            supply={supply} demand={demand}");
        GD.Print($"powered         state={machine.State}");
        GD.Print($"stored          {world.StoredEnergy}/{world.StorageCapacity}");
        GD.Print($"produced        {machine.GetOutputCount(catalogue.Item("chalcopyrite_crushed"))}");
        GD.Print($"burning         {generator.IsBurning}");
        GD.Print("--- power ok ---");
    }

    /// The fluid loop end to end: find water, stand a pump in it, run pipe to a
    /// machine that needs water, and check the machine runs on what the pump
    /// pulled. Nothing here is hand-fed.
    private void RunFluidFlow(Sim.World world)
    {
        GD.Print("--- fluids ---");

        var catalogue = GameSession.Catalogue;
        var water = catalogue.Item("water");

        // Walk out from spawn until we hit open water.
        var (wx, wy) = (0, 0);
        var found = false;
        for (var r = 1; r < 400 && !found; r++)
            for (var a = -r; a <= r && !found; a++)
                foreach (var (x, y) in new[] { (a, -r), (a, r), (-r, a), (r, a) })
                    if (world.Ground.Gen.IsWater(x, y)) { (wx, wy) = (x, y); found = true; break; }

        if (!found)
        {
            GD.Print("fluids          FAILED: no water within 400 tiles");
            return;
        }

        GD.Print($"water found     {wx},{wy}");

        // Pipe from the shore to a washer sitting a few tiles inland.
        for (var i = 1; i <= 6; i++)
            world.Fluids.AddPipe(wx + i, wy, Sim.FluidNetwork.ThroughputLarge);

        var pump = world.TryPlaceExtractor(new Sim.MachinePlacement(wx, wy, 1, 7, 1), water,
                                           cycleTicks: 10);
        GD.Print($"pump built      {pump is not null} ambient={pump?.Ambient}");

        var recipe = catalogue.Recipe("wash_chalcopyrite");
        var washer = world.TryPlaceMachine(recipe,
            new Sim.MachinePlacement(wx + 3, wy + 1, 1, 4, 1), outputCapacityPerItem: 500);

        if (washer is null || pump is null)
        {
            GD.Print("fluids          FAILED: could not place the pump or the washer");
            return;
        }

        washer.PushInput(catalogue.Item("chalcopyrite_crushed"), 500);

        // The washer is an electric machine, so it needs a grid out here too --
        // without one this proves the pipe delivered and nothing more.
        world.Power.AddPole(new Sim.Pole(wx + 3, wy + 2, supplyRadius: 6, wireRadius: 9));
        var generator = new Sim.Generator(catalogue.Item("coal_deposit"),
                                          outputPerTick: 40, ticksPerFuel: 2000);
        generator.AddFuel(20);
        world.TryPlaceGenerator(generator, new Sim.MachinePlacement(wx + 5, wy + 2, 1, 6, 1));

        world.Tick(600);

        var network = world.Fluids.NetworkAt(wx + 1, wy);
        var held = network >= 0 ? world.Fluids.Network(network).Amount : -1;
        var rate = network >= 0 ? world.Fluids.Network(network).ThroughputPerTick : -1;

        GD.Print($"networks        {world.Fluids.NetworkCount}");
        GD.Print($"pipe rate       {rate}/tick");
        GD.Print($"pipe holds      {held}");
        GD.Print($"washer state    {washer.State}");
        GD.Print($"washed          {washer.GetOutputCount(catalogue.Item("chalcopyrite_purified"))}");
        GD.Print($"voided          {world.Fluids.VoidedByMixing}");
        GD.Print("--- fluids ok ---");
    }


    // ------------------------------------------------------------ multiplayer

    private NetSession? _net;
    private MultiplayerSetup? _setup;
    private Lobby? _lobby;
    private LockstepDriver? _driver;
    private int _lobbySeed;

    /// The transport, as a node, because it has to be polled by the tree and
    /// has to die with it. One per process: hosting while joining is not a
    /// thing, and two would silently fight over the default multiplayer.
    private NetSession Net()
    {
        if (_net is not null) return _net;

        _net = new NetSession { Name = "NetSession" };
        AddChild(_net);
        return _net;
    }

    private void ShowMultiplayerSetup(MultiplayerSetup.Mode mode)
    {
        CloseMultiplayerSetup();

        _setup = GD.Load<PackedScene>("res://scenes/multiplayer_setup.tscn")
                   .Instantiate<MultiplayerSetup>();
        _setup.Open(mode);
        _setup.HostRequested += OnHostRequested;
        _setup.JoinRequested += OnJoinRequested;
        _setup.BackRequested += CloseMultiplayerSetup;

        var layer = new CanvasLayer { Name = "MultiplayerSetupLayer" };
        layer.AddChild(_setup);
        AddChild(layer);
    }

    private void CloseMultiplayerSetup()
    {
        _setup?.GetParent()?.QueueFree();
        _setup = null;
    }

    private void OnHostRequested(string playerName, int port, int seed, int maxPlayers)
    {
        var session = Net();
        var failure = session.Host(playerName, port, maxPlayers);

        if (failure != NetFailure.None)
        {
            // The screen stays open and says why. A port that will not open is
            // a sentence, never a dead button.
            _setup?.ShowError(NetSession.Describe(failure));
            return;
        }

        CloseMultiplayerSetup();
        ShowLobby(session, $"Seed {seed}. Waiting for players.", seed);
    }

    private void OnJoinRequested(string playerName, string address, int port)
    {
        var session = Net();

        void Failed(NetFailure failure, string sentence)
        {
            session.ConnectFailed -= Failed;
            session.Connected -= Arrived;
            _setup?.ShowError(sentence);
        }

        void Arrived()
        {
            session.ConnectFailed -= Failed;
            session.Connected -= Arrived;
            CloseMultiplayerSetup();
            ShowLobby(session, "Connected. Waiting for the host to start.");
        }

        session.ConnectFailed += Failed;
        session.Connected += Arrived;

        var immediate = session.Join(playerName, address, port);
        if (immediate != NetFailure.None) Failed(immediate, NetSession.Describe(immediate));
    }

    private void ShowLobby(NetSession session, string note, int seed = 0)
    {
        _lobby = GD.Load<PackedScene>("res://scenes/lobby.tscn").Instantiate<Lobby>();
        _lobby.Bind(session);
        _lobby.LeaveRequested += LeaveLobby;

        // The driver exists from the moment the lobby does, on both sides: a
        // client that only built one when Start was pressed would not be
        // listening when the host pressed it.
        _driver?.Dispose();
        _driver = new LockstepDriver(session, GameSession.Catalogue);
        _driver.Started += OnSharedWorldStarted;
        _driver.Stopped += why => _lobby?.ShowError(why);

        _lobbySeed = seed;
        _lobby.StartRequested += () =>
        {
            if (!session.IsHost) return;
            _lobby?.ShowNote("Starting...");
            _driver?.StartAsHost(_lobbySeed);
        };

        var layer = new CanvasLayer { Name = "LobbyLayer" };
        layer.AddChild(_lobby);
        AddChild(layer);

        _lobby.Refresh();
        _lobby.ShowNote(note);
    }

    /// Every peer generates the same world from the same seed and drops into
    /// it together. Nothing but the seed crossed the wire (ADR 0038).
    private void OnSharedWorldStarted(Sim.World world)
    {
        HideLobby();
        GameSession.Adopt(world);
        StartGame(world, disposeMenu: true, net: _driver);
    }

    private void HideLobby()
    {
        _lobby?.GetParent()?.QueueFree();
        _lobby = null;
    }

    private void LeaveLobby()
    {
        _driver?.Dispose();
        _driver = null;
        _net?.Close("");
        HideLobby();
    }

    /// Host and client in one process, connecting for real over a loopback
    /// socket. Two processes would be closer to the truth, but a headless CI
    /// step that has to orchestrate two engines and reconcile their exit codes
    /// tests the harness more than the transport. Each session gets its own
    /// `SceneMultiplayer` bound to its own node path, which is what makes one
    /// process legal at all.
    ///
    /// Everything printed here is a count. A roster that came back empty and a
    /// roster that was never asked for look identical in a pass line.
    private async void RunNetTest()
    {
        GD.Print("=== NET ===");
        GD.Print($"protocol        {NetSession.Protocol}");

        var port = Cli.ReadInt("--net-port", NetSession.DefaultPort);
        var failures = new List<string>();

        var host = new NetSession { Name = "NetHost" };
        var client = new NetSession { Name = "NetClient" };
        AddChild(host);
        AddChild(client);

        var opened = host.Host("Ada", port, maxPlayers: 4);
        GD.Print($"host            {opened} on port {port}, roster {host.Roster.Count}");
        if (opened != NetFailure.None)
        {
            GD.Print($"net             FAILED: could not host: {NetSession.Describe(opened)}");
            GetTree().Quit(1);
            return;
        }

        // --- a client joins ------------------------------------------------
        var joined = false;
        var joinFailure = "";
        client.Connected += () => joined = true;
        client.ConnectFailed += (_, why) => joinFailure = why;

        var arrivals = 0;
        var departures = 0;
        host.PeerArrived += _ => arrivals++;
        host.PeerDeparted += _ => departures++;

        var dialled = client.Join("Grace", "127.0.0.1", port);
        GD.Print($"client dial     {dialled}");

        await Frames(() => joined || joinFailure.Length > 0, 600);

        GD.Print($"handshake       joined={joined} failure={(joinFailure.Length == 0 ? "none" : joinFailure)}");
        GD.Print($"host roster     {host.Roster.Count}: {Names(host)}");
        GD.Print($"client roster   {client.Roster.Count}: {Names(client)}");
        GD.Print($"client id       {client.SelfId} (host is {host.SelfId})");
        GD.Print($"peer arrivals   {arrivals} seen by the host");

        if (!joined) failures.Add($"the client never connected ({joinFailure})");
        if (host.Roster.Count != 2) failures.Add($"host roster is {host.Roster.Count}, want 2");
        if (client.Roster.Count != 2) failures.Add($"client roster is {client.Roster.Count}, want 2");
        if (client.SelfId <= 1) failures.Add($"client has peer id {client.SelfId}");
        if (!host.Roster.Any(p => p.Name == "Grace" && !p.IsHost))
            failures.Add("the host does not know the client's name");
        if (!client.Roster.Any(p => p.Name == "Ada" && p.IsHost))
            failures.Add("the client does not know the host's name");

        // --- a message each way --------------------------------------------
        var atClient = new List<string>();
        var atHost = new List<string>();
        client.MessageReceived += (from, bytes) =>
            atClient.Add($"{from}:{System.Text.Encoding.UTF8.GetString(bytes)}");
        host.MessageReceived += (from, bytes) =>
            atHost.Add($"{from}:{System.Text.Encoding.UTF8.GetString(bytes)}");

        // Three each way, not one: an ordered channel that delivered exactly
        // one message would pass a single-message check while having lost the
        // property lockstep actually needs.
        for (var i = 1; i <= 3; i++) host.SendText($"host-{i}");
        for (var i = 1; i <= 3; i++) client.SendText($"client-{i}", 1);

        await Frames(() => atClient.Count >= 3 && atHost.Count >= 3, 600);

        GD.Print($"host -> client  sent {host.MessagesSent} received {atClient.Count}: " +
                 $"{string.Join(" ", atClient)}");
        GD.Print($"client -> host  sent {client.MessagesSent} received {atHost.Count}: " +
                 $"{string.Join(" ", atHost)}");

        if (atClient.Count != 3) failures.Add($"the client got {atClient.Count} of 3 messages");
        if (atHost.Count != 3) failures.Add($"the host got {atHost.Count} of 3 messages");
        if (string.Join(",", atClient) != $"1:host-1,1:host-2,1:host-3")
            failures.Add("host messages arrived out of order or from the wrong peer");
        if (string.Join(",", atHost) != $"{client.SelfId}:client-1,{client.SelfId}:client-2," +
                                        $"{client.SelfId}:client-3")
            failures.Add("client messages arrived out of order or from the wrong peer");

        // --- a clean disconnect ---------------------------------------------
        client.Close("");
        await Frames(() => host.Roster.Count == 1, 600);

        GD.Print($"after leaving   host roster {host.Roster.Count}: {Names(host)}, " +
                 $"departures {departures}");
        if (host.Roster.Count != 1) failures.Add($"host roster is {host.Roster.Count} after a leave");
        if (departures != 1) failures.Add($"the host saw {departures} departures, want 1");

        // --- a client on the wrong protocol ---------------------------------
        var stale = new NetSession { Name = "NetStale", SpokenProtocol = "0.2.1" };
        AddChild(stale);
        var staleReason = "";
        stale.ConnectFailed += (failure, why) => staleReason = $"{failure}|{why}";
        stale.Join("Charles", "127.0.0.1", port);
        await Frames(() => staleReason.Length > 0, 600);

        GD.Print($"version 0.2.1   {(staleReason.Length == 0 ? "NOT REFUSED" : staleReason)}");
        GD.Print($"host roster     {host.Roster.Count} after the refusal");
        if (!staleReason.StartsWith("VersionMismatch|"))
            failures.Add($"a 0.2.1 client was not told about the version ({staleReason})");
        if (!staleReason.Contains("0.2.1") || !staleReason.Contains(NetSession.Protocol))
            failures.Add("the version refusal does not name both versions");
        if (host.Roster.Count != 1) failures.Add("a refused client stayed on the roster");

        // --- a host that is full ---------------------------------------------
        var small = new NetSession { Name = "NetSmall" };
        AddChild(small);
        small.Host("Solo", port + 1, maxPlayers: 1);

        var turnedAway = new NetSession { Name = "NetTurnedAway" };
        AddChild(turnedAway);
        var fullReason = "";
        turnedAway.ConnectFailed += (failure, why) => fullReason = $"{failure}|{why}";
        turnedAway.Join("Late", "127.0.0.1", port + 1);
        await Frames(() => fullReason.Length > 0, 600);

        GD.Print($"full host       {(fullReason.Length == 0 ? "NOT REFUSED" : fullReason)}");
        if (!fullReason.StartsWith("HostFull|")) failures.Add($"a full host let someone in ({fullReason})");

        // --- nobody listening --------------------------------------------------
        var lonely = new NetSession { Name = "NetLonely", ConnectTimeoutMs = 1200 };
        AddChild(lonely);
        var lonelyReason = "";
        lonely.ConnectFailed += (failure, why) => lonelyReason = $"{failure}|{why}";
        lonely.Join("Nobody", "127.0.0.1", port + 2);
        await Frames(() => lonelyReason.Length > 0, 2000);

        GD.Print($"dead port       {(lonelyReason.Length == 0 ? "NO ANSWER AND NO REASON" : lonelyReason)}");
        if (!lonelyReason.StartsWith("NoAnswer|")) failures.Add($"a dead port gave no reason ({lonelyReason})");

        var bad = new NetSession { Name = "NetBad" };
        AddChild(bad);
        var badReason = bad.Join("Typo", "not a host name at all", port);
        GD.Print($"bad address     {badReason}");
        if (badReason != NetFailure.BadAddress) failures.Add($"a nonsense address gave {badReason}");

        var badPort = bad.Join("Typo", "127.0.0.1", 99999);
        GD.Print($"bad port        {badPort}");
        if (badPort != NetFailure.BadPort) failures.Add($"port 99999 gave {badPort}");

        // --- and the lobby draws what the roster says ---------------------------
        var lobby = GD.Load<PackedScene>("res://scenes/lobby.tscn").Instantiate<Lobby>();
        var lobbyLayer = new CanvasLayer { Name = "NetTestLobby" };
        lobbyLayer.AddChild(lobby);
        AddChild(lobbyLayer);
        lobby.Bind(host);
        lobby.Refresh();
        GD.Print($"lobby rows      {lobby.RowCount} for {host.Roster.Count} on the roster");
        if (lobby.RowCount != host.Roster.Count)
            failures.Add($"the lobby drew {lobby.RowCount} rows for {host.Roster.Count} peers");

        host.Close("");
        small.Close("");

        if (failures.Count > 0)
        {
            foreach (var problem in failures) GD.Print($"net             FAILED: {problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("=== NET OK ===");
        GetTree().Quit();
    }

    private static string Names(NetSession session) =>
        session.Roster.Count == 0
            ? "(empty)"
            : string.Join(", ", session.Roster.Select(p => p.ToString()));

    /// Waits for a condition, one frame at a time, up to a bound. Everything in
    /// the net test is asynchronous by nature -- the sockets are only serviced
    /// when the tree polls them -- and a fixed sleep would either be flaky or
    /// slow.
    private async System.Threading.Tasks.Task Frames(System.Func<bool> until, int limit)
    {
        for (var i = 0; i < limit && !until(); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// One player's script of actions, issued through the driver at ticks of
    /// its own choosing.
    ///
    /// Two peers on the *same* schedule would be a tidy world, and this
    /// repository's history is a list of tests that passed on tidy worlds. So
    /// the two are offset, act at co-prime intervals, and do things that fail
    /// as well as things that work: a second Uplink with none carried, a dig on
    /// bare ground, a removal of something that was never built. A refusal
    /// costs nothing and is therefore invisible unless the digest is watching,
    /// which is exactly why the stream has to contain some.
    private sealed class NetScript
    {
        private static readonly (int X, int Y)[] Ways =
            { (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1) };

        private readonly LockstepDriver _driver;
        private long _nextMove, _nextDig, _nextBuild, _nextRemove;
        private int _way;
        private int _builtX, _builtY;

        public int Moves, Digs, Builds, Removes;
        public int Issued => Moves + Digs + Builds + Removes;

        public NetScript(LockstepDriver driver, int offset)
        {
            _driver = driver;
            _nextMove = 11 + offset;
            _nextDig = 23 + offset;
            _nextBuild = 137 + offset;
            _nextRemove = 401 + offset;
        }

        public void Pump(long lastIssuingTick)
        {
            var world = _driver.World;
            if (world is null || _driver.Phase != NetPhase.Running) return;

            var tick = world.TickCount;
            if (tick > lastIssuingTick) return;

            var me = world.Players[_driver.LocalPlayer];

            while (tick >= _nextMove)
            {
                if (_driver.SetIntent(new Sim.MoveIntent(Ways[_way % Ways.Length].X,
                                                         Ways[_way % Ways.Length].Y)) >= 0)
                    Moves++;
                _way++;
                _nextMove += 29;
            }

            while (tick >= _nextDig)
            {
                _driver.Issue(Sim.PlayerCommand.Dig(0, 0, 0, me.TileX, me.TileY, 5));
                Digs++;
                _nextDig += 41;
            }

            while (tick >= _nextBuild)
            {
                _builtX = me.TileX + 2;
                _builtY = me.TileY;
                _driver.Issue(Sim.PlayerCommand.Build(0, 0, 0, _builtX, _builtY,
                                                      Sim.Research.UplinkItem,
                                                      Sim.Research.UplinkRecipe));
                Builds++;
                _nextBuild += 211;
            }

            while (tick >= _nextRemove)
            {
                _driver.Issue(Sim.PlayerCommand.Remove(0, 0, 0, _builtX, _builtY));
                Removes++;
                _nextRemove += 223;
            }
        }
    }

    /// The run that proves slice 3b: a host and a client in one process, one
    /// seed, real commands from both sides for a few thousand ticks, hashes
    /// compared the whole way, saves compared at the end -- and then a
    /// deliberate corruption, because a detector that has never been seen to
    /// fire is not a detector.
    private async void RunNetLockstepTest()
    {
        GD.Print("=== NET LOCKSTEP ===");
        GD.Print($"protocol        {NetSession.Protocol}  input delay " +
                 $"{LockstepDriver.InputDelay} ticks ({LockstepDriver.InputDelay * 1000 / 60} ms)" +
                 $"  hash every {LockstepDriver.HashEvery}");

        var port = Cli.ReadInt("--net-port", NetSession.DefaultPort + 20);
        var ticks = Cli.ReadInt("--net-ticks", 3000);
        var seed = Cli.ReadInt("--seed", 20260908);
        var failures = new List<string>();
        var catalogue = GameSession.Catalogue;

        var host = new NetSession { Name = "LockHost" };
        var client = new NetSession { Name = "LockClient" };
        AddChild(host);
        AddChild(client);

        var opened = host.Host("Ada", port, maxPlayers: 4);
        if (opened != NetFailure.None)
        {
            GD.Print($"lockstep        FAILED: could not host: {NetSession.Describe(opened)}");
            GetTree().Quit(1);
            return;
        }

        var joined = false;
        var joinFailure = "";
        client.Connected += () => joined = true;
        client.ConnectFailed += (_, why) => joinFailure = why;
        client.Join("Grace", "127.0.0.1", port);
        await Frames(() => joined || joinFailure.Length > 0, 900);

        GD.Print($"lobby           joined={joined} roster={host.Roster.Count} " +
                 $"({Names(host)})");
        if (!joined)
        {
            GD.Print($"lockstep        FAILED: the client never connected ({joinFailure})");
            GetTree().Quit(1);
            return;
        }

        using var hostDriver = new LockstepDriver(host, catalogue);
        using var clientDriver = new LockstepDriver(client, catalogue);

        Sim.World? hw = null;
        Sim.World? cw = null;
        hostDriver.Started += w => hw = w;
        clientDriver.Started += w => cw = w;

        var stops = new List<string>();
        hostDriver.Stopped += why => stops.Add($"host: {why}");
        clientDriver.Stopped += why => stops.Add($"client: {why}");

        hostDriver.StartAsHost(seed);
        await Frames(() => hw is not null && cw is not null, 600);

        if (hw is null || cw is null)
        {
            GD.Print($"lockstep        FAILED: worlds host={hw is not null} client={cw is not null}");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"world           seed {seed}  players {hw.Players.Count}  " +
                 $"teams {hw.Teams.Count}  host is player {hostDriver.LocalPlayer}, " +
                 $"client is player {clientDriver.LocalPlayer}");
        GD.Print($"start hash      {hw.StateHash():X16} / {cw.StateHash():X16}");

        if (hw.Players.Count != 2) failures.Add($"{hw.Players.Count} players, want 2");
        if (cw.Players.Count != 2) failures.Add($"{cw.Players.Count} players on the client");
        if (hostDriver.LocalPlayer != 0 || clientDriver.LocalPlayer != 1)
            failures.Add($"player indices are {hostDriver.LocalPlayer}/{clientDriver.LocalPlayer}");
        if (hw.LocalIndex != 0 || cw.LocalIndex != 1)
            failures.Add($"local indices are {hw.LocalIndex}/{cw.LocalIndex}");
        if (hw.StateHash() != cw.StateHash())
            failures.Add("two worlds from one seed did not start on one hash");

        // ---- the input delay, observed one tick at a time --------------------
        //
        // The single property this whole slice rests on: a local command is not
        // applied when it is issued. Advancing one tick at a time is slow and is
        // the only way to see the tick it lands on.
        var issuedAt = hw.TickCount;
        var appliesAt = hostDriver.Issue(Sim.PlayerCommand.Move(0, 0, 0, 1, 0));
        var seenAt = -1L;

        for (var i = 0; i < 200 && seenAt < 0; i++)
        {
            hostDriver.Advance(1);
            clientDriver.Advance(1);
            if (hw.Players[0].Intent.X == 1) seenAt = hw.TickCount - 1;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        GD.Print($"input delay     issued during tick {issuedAt}, scheduled for {appliesAt}, " +
                 $"took effect on tick {seenAt}");
        if (appliesAt != issuedAt + LockstepDriver.InputDelay)
            failures.Add($"a command issued at {issuedAt} was scheduled for {appliesAt}, " +
                         $"want {issuedAt + LockstepDriver.InputDelay}");
        if (seenAt != appliesAt)
            failures.Add($"a command scheduled for {appliesAt} took effect on tick {seenAt} " +
                         "-- a local command must never be applied early");

        // ---- a few thousand ticks with both peers acting ---------------------
        var hostScript = new NetScript(hostDriver, 0);
        var clientScript = new NetScript(clientDriver, 17);
        var lastIssuingTick = ticks - 200;

        var mismatches = new List<string>();
        var compared = 0;
        var hostHashes = new Dictionary<long, ulong>();
        var clientHashes = new Dictionary<long, ulong>();
        var frames = 0;

        while ((hw.TickCount < ticks || cw.TickCount < ticks) && frames < 20000)
        {
            frames++;
            hostScript.Pump(lastIssuingTick);
            clientScript.Pump(lastIssuingTick);

            // Clamped so neither peer can step over a hash boundary: a
            // comparison that is skipped because the loop ran eight ticks at
            // once is a comparison that never happened, and this run would
            // still print a number.
            hostDriver.Advance(Room(hw.TickCount, ticks));
            clientDriver.Advance(Room(cw.TickCount, ticks));

            if (hw.TickCount % LockstepDriver.HashEvery == 0) hostHashes[hw.TickCount] = hw.StateHash();
            if (cw.TickCount % LockstepDriver.HashEvery == 0) clientHashes[cw.TickCount] = cw.StateHash();

            if (hostDriver.Phase != NetPhase.Running || clientDriver.Phase != NetPhase.Running)
                break;

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        foreach (var (tick, hash) in hostHashes)
        {
            if (!clientHashes.TryGetValue(tick, out var theirs)) continue;
            compared++;
            if (hash != theirs) mismatches.Add($"tick {tick}: {hash:X16} != {theirs:X16}");
        }

        GD.Print($"ran             {frames} frames, host tick {hw.TickCount}, " +
                 $"client tick {cw.TickCount}");
        GD.Print($"commands        host issued {hostDriver.CommandsIssued} " +
                 $"(moves {hostScript.Moves} digs {hostScript.Digs} builds {hostScript.Builds} " +
                 $"removes {hostScript.Removes}), client issued {clientDriver.CommandsIssued} " +
                 $"(moves {clientScript.Moves} digs {clientScript.Digs} " +
                 $"builds {clientScript.Builds} removes {clientScript.Removes})");
        GD.Print($"applied         host {hw.CommandsApplied} applied / {hw.CommandsRefused} " +
                 $"refused, client {cw.CommandsApplied} / {cw.CommandsRefused}");
        GD.Print($"batches         sequencer sent {hostDriver.BatchesSent}, host applied " +
                 $"{hostDriver.BatchesApplied}, client applied {clientDriver.BatchesApplied}");
        GD.Print($"inputs          host sent {hostDriver.InputsSent}, client sent " +
                 $"{clientDriver.InputsSent}, sequencer received {hostDriver.InputsReceived}, " +
                 $"forged {hostDriver.Forged}");
        GD.Print($"hashes          exchanged {hostDriver.HashesSent}/{clientDriver.HashesSent}, " +
                 $"compared over the wire {hostDriver.HashesCompared}/" +
                 $"{clientDriver.HashesCompared}, compared here {compared}, " +
                 $"mismatches {mismatches.Count}");
        GD.Print($"stalls          host {hostDriver.StallTicks} tick-waits, client " +
                 $"{clientDriver.StallTicks}");
        GD.Print($"digest          host {hw.CommandDigest:X16} client {cw.CommandDigest:X16}");

        foreach (var line in mismatches.Take(4)) GD.Print($"MISMATCH        {line}");

        if (hw.TickCount != ticks) failures.Add($"the host stopped at tick {hw.TickCount}");
        if (cw.TickCount != ticks) failures.Add($"the client stopped at tick {cw.TickCount}");
        if (mismatches.Count > 0)
            failures.Add($"{mismatches.Count} of {compared} hash comparisons disagreed");
        if (compared < ticks / LockstepDriver.HashEvery - 2)
            failures.Add($"only {compared} hashes were compared over {ticks} ticks");
        if (hostDriver.HashesCompared < 10 || clientDriver.HashesCompared < 10)
            failures.Add($"the peers only compared {hostDriver.HashesCompared}/" +
                         $"{clientDriver.HashesCompared} hashes with each other over the wire");
        if (hw.CommandDigest != cw.CommandDigest)
            failures.Add("the two command digests differ");
        if (stops.Count > 0) failures.Add($"the session stopped: {string.Join("; ", stops)}");

        // Nobody's commands may go missing. A driver that dropped one peer's
        // input would keep both peers identical and lose the click -- the one
        // failure a hash comparison cannot see.
        var issued = hostDriver.CommandsIssued + clientDriver.CommandsIssued;
        var seen = hw.CommandsApplied + hw.CommandsRefused;
        GD.Print($"accounting      {issued} issued, {seen} reached the simulation " +
                 $"({hw.CommandsApplied} applied, {hw.CommandsRefused} refused)");
        if (seen != issued)
            failures.Add($"{issued} commands were issued and {seen} reached the world");
        if (hw.CommandsApplied == 0 || hw.CommandsRefused == 0)
            failures.Add($"a stream of {hw.CommandsApplied} applied and {hw.CommandsRefused} " +
                         "refused proves nothing about refusals");

        // ---- and the two saves, byte for byte --------------------------------
        //
        // With one honest exception, named here rather than papered over: a
        // save records *which* player is the local one, and that is genuinely
        // different on the two peers -- host is player 0, client is player 1.
        // Everything the simulation owns is byte-identical; the one field that
        // is not is the one field that describes the peer rather than the
        // world. Both halves are asserted, so the day somebody makes
        // LocalPlayer shared this run says so instead of quietly passing.
        var hostModel = Sim.Save.SaveGame.Capture(hw);
        var clientModel = Sim.Save.SaveGame.Capture(cw);
        var rawHost = Sim.Save.SaveGame.ToJson(hostModel);
        var rawClient = Sim.Save.SaveGame.ToJson(clientModel);
        var rawDiffers = string.CompareOrdinal(rawHost, rawClient) != 0;

        hostModel.LocalPlayer = 0;
        clientModel.LocalPlayer = 0;
        var hostSave = Sim.Save.SaveGame.ToJson(hostModel);
        var clientSave = Sim.Save.SaveGame.ToJson(clientModel);
        var identical = string.CompareOrdinal(hostSave, clientSave) == 0;

        GD.Print($"saves           {hostSave.Length} chars vs {clientSave.Length}, identical " +
                 $"except for LocalPlayer={identical}; differ as written={rawDiffers} " +
                 $"(host is player {hw.LocalIndex}, client is player {cw.LocalIndex})");
        if (!identical) failures.Add("the two peers saved different bytes");
        if (!rawDiffers)
            failures.Add("the two saves no longer differ in LocalPlayer -- the exemption " +
                         "this check makes is stale");

        // ---- and the game itself, driven by a driver -------------------------
        await RunSharedInputCheck(port + 2, seed, catalogue, failures);

        // ---- the detector, deliberately fired --------------------------------
        var caught = await RunDesyncCheck(port + 1, seed, catalogue, failures);
        GD.Print($"desync check    {caught}");

        host.Close("");
        client.Close("");

        if (failures.Count > 0)
        {
            foreach (var problem in failures) GD.Print($"lockstep        FAILED: {problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("=== NET LOCKSTEP OK ===");
        GetTree().Quit();
    }

    /// The real path, not the harness one: **two** `GameRoot`s, one per peer,
    /// each with a driver attached, each acting through the methods a click
    /// calls.
    ///
    /// Everything above this drives `LockstepDriver.Issue` directly, which
    /// proves the driver and proves nothing about the game. This drives
    /// `GameRoot.PlaceHeld`, `GameRoot.DigOrClose`, `GameRoot.RemoveAt` and
    /// `GameRoot.Actions` -- the exact entry points the mouse reaches -- on both
    /// peers at once, and then insists the two worlds are still the same world.
    ///
    /// Three things it is built to catch, because each is a plausible way to
    /// "finish" this slice wrongly:
    ///
    /// * a build applied locally instead of being sent as a command (the
    ///   machine count moves before the batch lands, and the peers diverge);
    /// * a refusal that is dropped rather than shown (no sentence is spoken for
    ///   an action that was refused, and `Lost` counts what was never answered);
    /// * one peer acting outside the driver (the hashes and the saves part).
    private async System.Threading.Tasks.Task RunSharedInputCheck(
        int port, int seed, Sim.Data.Catalogue catalogue, List<string> failures)
    {
        var builds = new Sim.BuildCatalogue(catalogue);

        // A seed with something diggable inside arm's length of spawn, so the
        // run exercises a dig that *works* rather than only a dig that is
        // refused. Chosen by looking rather than asserted, and printed: a run
        // that quietly stopped finding one would otherwise still pass while
        // testing half of what it says it does.
        var chosen = seed;
        var nearOre = (X: 0, Y: 0);
        var farOre = (X: 0, Y: 0);
        var found = false;

        var digItem = "";

        // Two passes. The first insists on ore the opening objective *accepts*,
        // so the dig feeds a delivery that is applied rather than refused; the
        // second settles for anything diggable. Worldgen deals the guaranteed
        // patch 12-40 tiles out (ADR 0026), so a wanted ore inside six tiles of
        // spawn is luck, and a run that failed when it did not get lucky would
        // be a flaky test rather than a check.
        for (var pass = 0; pass < 2 && !found; pass++)
        for (var attempt = 0; attempt < 60 && !found; attempt++)
        {
            var probe = Sim.NewGame.Create(seed + attempt, catalogue);
            var px = probe.Player.TileX;
            var py = probe.Player.TileY;

            // And not just anything: something the *opening objective* accepts,
            // so the dig feeds a delivery that is then applied rather than
            // refused. A run in which every delivery is `NothingWanted`
            // exercises the refusal and never proves a delivery lands.
            var wanted = probe.Research!.Objectives
                .SelectMany(o => o.Needs)
                .SelectMany(n => n.Accepts)
                .ToHashSet();

            for (var dy = -3; dy <= 3 && !found; dy++)
                for (var dx = -3; dx <= 3 && !found; dx++)
                    if (probe.Ground.TryResourceAt(px + dx, py + dy, out var item, out var left)
                        && left > 0
                        && (pass == 1 || wanted.Contains(catalogue.Items.GetName(item))))
                    {
                        chosen = seed + attempt;
                        nearOre = (px + dx, py + dy);
                        digItem = catalogue.Items.GetName(item);
                        found = true;
                    }

            if (!found) continue;

            for (var r = 30; r < 90 && farOre.X == 0; r += 5)
                for (var dy = -r; dy <= r && farOre.X == 0; dy += 5)
                    for (var dx = -r; dx <= r && farOre.X == 0; dx += 5)
                        if (probe.Ground.TryResourceAt(px + dx, py + dy, out _, out var n) && n > 0
                            && System.Math.Abs(dx) + System.Math.Abs(dy) > 30)
                            farOre = (px + dx, py + dy);
        }

        GD.Print($"input seed      {chosen} ({digItem} in reach at {nearOre.X},{nearOre.Y}" +
                 $"={found}, ore out of reach at {farOre.X},{farOre.Y})");
        if (!found)
            failures.Add("no seed in 60 put anything diggable within reach of spawn");

        var host = new NetSession { Name = "GameHost" };
        var client = new NetSession { Name = "GameClient" };
        AddChild(host);
        AddChild(client);

        if (host.Host("Ada", port, maxPlayers: 4) != NetFailure.None)
        {
            failures.Add("could not host the shared-input check");
            return;
        }

        var joined = false;
        client.Connected += () => joined = true;
        client.Join("Grace", "127.0.0.1", port);
        await Frames(() => joined, 900);
        if (!joined)
        {
            failures.Add("the shared-input check's client never connected");
            return;
        }

        using var hostDriver = new LockstepDriver(host, catalogue);
        using var clientDriver = new LockstepDriver(client, catalogue);

        Sim.World? hw = null;
        Sim.World? cw = null;
        hostDriver.Started += w => hw = w;
        clientDriver.Started += w => cw = w;
        hostDriver.StartAsHost(chosen);
        await Frames(() => hw is not null && cw is not null, 600);

        if (hw is null || cw is null)
        {
            failures.Add("the shared-input check never got two worlds");
            return;
        }

        // Two roots, each looking through its own peer's eyes. Both tick their
        // own driver in _Process, exactly as a running game does.
        var hostRoot = new GameRoot { Name = "HostRoot", InitialWorld = hw, Net = hostDriver };
        var clientRoot = new GameRoot { Name = "ClientRoot", InitialWorld = cw, Net = clientDriver };
        AddChild(hostRoot);
        AddChild(clientRoot);
        await Frames(() => false, 4);

        var said = new Dictionary<string, List<string>>
        {
            ["host"] = new(), ["client"] = new(),
        };
        Listen(hostRoot, said["host"]);
        Listen(clientRoot, said["client"]);

        var bench = builds.Find("man_manual_crafting");
        var uplink = builds.Find(Sim.Research.UplinkItem);
        if (bench is null || uplink is null)
        {
            failures.Add("the shared-input check could not find a bench and an Uplink to place");
            return;
        }

        var benchRecipes = builds.RecipesFor(bench, hw.Research);
        var uplinkRecipe = builds.RecipesFor(uplink, hw.Research).FirstOrDefault();
        if (benchRecipes.Count < 2 || uplinkRecipe is null)
        {
            failures.Add($"the bench offers {benchRecipes.Count} recipes and the Uplink " +
                         $"{(uplinkRecipe is null ? 0 : 1)}; the check needs two and one");
            return;
        }

        var spawnX = hw.Player.TileX;
        var spawnY = hw.Player.TileY;

        // Each peer builds on its own tile: two players standing on one spawn
        // clicking one tile is a different test (ADR 0037's total order), and
        // this one is about routing.
        var plots = new (GameRoot Root, string Peer, int Dx)[]
        {
            (hostRoot, "host", 2), (clientRoot, "client", 4),
        };

        // ---- one build, watched across the delay it is supposed to have -----
        var before = (Host: hw.MachineCount, Client: cw.MachineCount);
        foreach (var (root, _, dx) in plots)
        {
            root.Hold(bench, benchRecipes[0]);
            root.PlaceHeld(spawnX + dx, spawnY);
        }

        var immediately = (Host: hw.MachineCount, Client: cw.MachineCount);
        GD.Print($"no prediction   machines {before} before the click, {immediately} in the " +
                 $"same frame, host in flight {hostRoot.Actions.InFlight.Count}");
        if (immediately != before)
            failures.Add($"a build changed the world in the frame it was clicked " +
                         $"({before} -> {immediately}) -- the local peer acted on its own guess");
        if (hostRoot.Actions.InFlight.Count != 1 || clientRoot.Actions.InFlight.Count != 1)
            failures.Add($"a click left {hostRoot.Actions.InFlight.Count}/" +
                         $"{clientRoot.Actions.InFlight.Count} actions in flight, want 1 each");

        await Step(20);

        GD.Print($"after the wait  machines host {hw.MachineCount} client {cw.MachineCount}, " +
                 $"in flight {hostRoot.Actions.InFlight.Count}/" +
                 $"{clientRoot.Actions.InFlight.Count}");
        if (hw.MachineCount != before.Host + 2)
            failures.Add($"two builds through the input path produced " +
                         $"{hw.MachineCount - before.Host} machines");

        // ---- and then the rest of the actions a player can take -------------
        //
        // The Uplink onto the bench's tile, not a second bench: a new game
        // carries exactly one bench, so a second bench click is refused
        // `NoneCarried` before the tile is ever considered and proves nothing
        // about occupancy.
        foreach (var (root, _, dx) in plots)
        {
            root.Hold(uplink, uplinkRecipe);
            root.PlaceHeld(spawnX + dx, spawnY);
        }
        await Step(16);

        foreach (var (root, _, dx) in plots)
            root.Actions.Retask(spawnX + dx, spawnY, benchRecipes[1]);
        await Step(16);

        foreach (var (root, _, dx) in plots)
            root.Actions.Retask(spawnX + dx, spawnY + 40, benchRecipes[1]);
        await Step(16);

        foreach (var (root, _, _) in plots) root.DigOrClose(nearOre.X, nearOre.Y);
        await Step(16);

        if (farOre.X != 0)
        {
            foreach (var (root, _, _) in plots) root.DigOrClose(farOre.X, farOre.Y);
            await Step(16);
        }

        foreach (var (root, _, dx) in plots) root.PlaceHeld(spawnX + dx, spawnY + 2);
        await Step(16);

        // What the ladder actually wants and the player actually has, resolved
        // the way `MachinePanel` resolves it: a need accepts a *set* of items
        // and its own key is not an item id (ADR 0030). Hardcoding one item id
        // here made this step a permanent `NothingWanted`, which exercised the
        // refusal and never once proved a delivery lands.
        var wantedItem = digItem.Length > 0 ? digItem : "stone_deposit";
        GD.Print($"delivering      {wantedItem}, held {Held(hw, wantedItem)}, " +
                 $"wanted by the ladder={Wanted(hw, wantedItem)}");
        foreach (var (root, _, _) in plots)
            root.Actions.Deliver(wantedItem, 3, $"Handing over 3 {wantedItem}");
        await Step(16);

        // ---- the opening loop, by hand: load a bench, take what it made -----
        //
        // ADR 0040, and the whole point of this slice: until now both of these
        // were refused in a shared world with a sentence, which meant two
        // people could share a world and not play its first hour.
        var affordable = benchRecipes.FirstOrDefault(
            r => r.Inputs.All(i => hw.Player.Inventory.Count(i.Item) >= i.Count));

        if (affordable is null)
        {
            failures.Add("nobody can afford anything the bench offers, so the hand-feed " +
                         "section would prove nothing");
        }
        else
        {
            foreach (var (root, _, dx) in plots)
                root.Actions.Retask(spawnX + dx, spawnY, affordable);
            await Step(16);

            // Take before anything has run: the refusal for clicking too early.
            foreach (var (root, _, dx) in plots)
                root.Actions.Take(spawnX + dx, spawnY, $"The early bench at {spawnX + dx}");
            await Step(16);

            // **The contested case.** Both peers reach into the *host's* bench
            // in the same frame. One fills the cycle and the other finds a
            // machine that wants nothing; which is which is ADR 0037's total
            // order and nothing else, and both peers must agree about it.
            // Same *tick*, not merely the same frame: two roots tick off their
            // own wall clocks, so a click in one frame can be stamped a tick
            // apart, and a tick apart is not a contest -- the first load's
            // cycle has already started and eaten the inputs by the time the
            // second lands, which is a legal outcome that proves nothing about
            // the total order.
            // The client runs a few ticks behind the host by construction (it
            // is the sequencer's follower), so both peers clicking in one
            // frame stamp their commands ticks apart. The host clicks, and the
            // client clicks on the frame where *its* stamp lands on the same
            // tick.
            const string contested = "The contested bench";
            hostRoot.Actions.Load(spawnX + plots[0].Dx, spawnY, 1, contested);
            var target = hostDriver.LastIssued.Tick;

            for (var i = 0; i < 900 && cw.TickCount + LockstepDriver.InputDelay < target; i++)
                await Step(1);

            clientRoot.Actions.Load(spawnX + plots[0].Dx, spawnY, 1, contested);

            var stamped = (Host: hostDriver.LastIssued.Tick, Client: clientDriver.LastIssued.Tick);
            if (stamped.Host != stamped.Client)
                failures.Add($"the contested loads were stamped for ticks {stamped.Host} and " +
                             $"{stamped.Client}, so this run never contested anything");
            await Step(24);

            var won = said.Where(kv => kv.Value.Any(l => l.StartsWith("Loaded")
                                                         && l.Contains(contested)))
                          .Select(kv => kv.Key).ToList();
            var lost = said.Where(kv => kv.Value.Any(l => l.Contains(contested)
                                                          && l.Contains("wants nothing")))
                           .Select(kv => kv.Key).ToList();
            GD.Print($"contested       both stamped for tick {stamped.Host}/{stamped.Client}");
            GD.Print($"contested load  won by [{string.Join(",", won)}], " +
                     $"refused for [{string.Join(",", lost)}] " +
                     $"(both peers, one furnace, same tick)");

            if (won.Count != 1 || lost.Count != 1 || won[0] == lost[0])
                failures.Add($"two peers loading one machine resolved to {won.Count} winner(s) " +
                             $"and {lost.Count} refusal(s); the total order says exactly one each");

            // The other peer's own bench, so both are working and both peers
            // have something to take.
            clientRoot.Actions.Load(spawnX + plots[1].Dx, spawnY, 1, "The client bench");
            await Step(24);

            // "Is there anything in the hopper" is the wrong question: a full
            // cycle's worth is consumed the moment the cycle starts, so a
            // successful load can read as an empty machine one tick later. What
            // a load actually causes is a bench that is *working*.
            var working = (Host: Busy(hw, spawnX + plots[0].Dx, spawnY),
                           Client: Busy(hw, spawnX + plots[1].Dx, spawnY));
            GD.Print($"hand-fed        host bench busy={working.Host} " +
                     $"(holding {InputsOf(hw, spawnX + plots[0].Dx, spawnY)}), " +
                     $"client bench busy={working.Client} " +
                     $"(holding {InputsOf(hw, spawnX + plots[1].Dx, spawnY)}), " +
                     $"stone left host {Stone(hw)} client {Stone(cw)}");
            if (!working.Host && !working.Client)
                failures.Add("two hand loads left both benches idle: nothing was fed");

            // Wait out the cycle. `Take` before it finishes is a refusal, not a
            // wait, so the run has to actually let the machine work.
            var deadline = hw.TickCount + affordable.DurationTicks + 90;
            for (var i = 0; i < 6000 && hw.TickCount < deadline
                                     && !Finished(hw, spawnX + plots[0].Dx, spawnY)
                                     && !Finished(hw, spawnX + plots[1].Dx, spawnY); i++)
                await Step(1);

            GD.Print($"cycle done      host tick {hw.TickCount}, " +
                     $"host bench out {OutputsOf(hw, spawnX + plots[0].Dx, spawnY)}, " +
                     $"client bench out {OutputsOf(hw, spawnX + plots[1].Dx, spawnY)}");

            var carriedBefore = (Host: Carried(hw), Client: Carried(cw));
            foreach (var (root, _, dx) in plots)
                root.Actions.Take(spawnX + dx, spawnY, $"The bench at {spawnX + dx},{spawnY}");
            await Step(24);

            var carriedAfter = (Host: Carried(hw), Client: Carried(cw));
            GD.Print($"took the output carried {carriedBefore} -> {carriedAfter}, " +
                     $"host bench out {OutputsOf(hw, spawnX + plots[0].Dx, spawnY)}");

            if (carriedAfter.Host <= carriedBefore.Host)
                failures.Add("taking a finished bench's output put nothing in anybody's pockets");

            // The two peers are *not* compared here: they stand a few ticks
            // apart while both are running, so a difference at this moment is
            // latency rather than disagreement. They are levelled and compared
            // below, on one tick, which is the only comparison that means
            // anything.
        }

        foreach (var (root, _, dx) in plots) root.RemoveAt(spawnX + dx, spawnY);
        await Step(16);

        foreach (var (root, _, dx) in plots) root.RemoveAt(spawnX + dx, spawnY + 9);
        await Step(24);

        // ---- what each peer did, and what the world said back ---------------
        foreach (var (root, who, _) in plots)
        {
            var a = root.Actions;
            GD.Print($"{who,-6} issued   builds {a.IssuedOf(Sim.CommandKind.Build)} " +
                     $"digs {a.IssuedOf(Sim.CommandKind.Dig)} " +
                     $"removes {a.IssuedOf(Sim.CommandKind.Remove)} " +
                     $"retasks {a.IssuedOf(Sim.CommandKind.ChangeRecipe)} " +
                     $"delivers {a.IssuedOf(Sim.CommandKind.Deliver)} " +
                     $"loads {a.IssuedOf(Sim.CommandKind.Load)} " +
                     $"takes {a.IssuedOf(Sim.CommandKind.Take)} " +
                     $"= {a.Issued} total");
            GD.Print($"{who,-6} answered {a.Applied} applied, {a.Refused} refused, " +
                     $"{a.Lost} lost, {a.InFlight.Count} still in flight");
            GD.Print($"{who,-6} reasons  {a.Tally()}");

            if (a.Issued < 10) failures.Add($"the {who} only issued {a.Issued} actions");
            if (a.Applied == 0) failures.Add($"the {who} had nothing applied");
            if (a.Refused == 0)
                failures.Add($"the {who} was refused nothing, so this run proves " +
                             "nothing about refusals arriving 100 ms late");
            if (a.Lost != 0)
                failures.Add($"{a.Lost} of the {who}'s actions were never answered");
            if (a.InFlight.Count != 0)
                failures.Add($"{a.InFlight.Count} of the {who}'s actions never came back");
            if (a.Applied + a.Refused != a.Issued)
                failures.Add($"the {who} issued {a.Issued} and heard about " +
                             $"{a.Applied + a.Refused}");

            // The refusals this run deliberately provokes. Each is a different
            // sentence in front of a player, and a routing that dropped them
            // would still leave every hash identical.
            foreach (var wanted in new[]
                     {
                         Sim.CommandOutcome.Blocked,
                         Sim.CommandOutcome.NothingThere,
                         Sim.CommandOutcome.NoMachine,
                         Sim.CommandOutcome.NothingToTake,
                     })
                if (a.CountOf(wanted) == 0)
                    failures.Add($"the {who} never saw {wanted}, which this run provokes " +
                                 "on purpose");
        }

        foreach (var (who, lines) in said)
        {
            GD.Print($"{who,-6} said     {lines.Count} sentences, last: " +
                     $"\"{(lines.Count > 0 ? Trim(lines[^1]) : "")}\"");
            foreach (var line in lines.Where(l => l.Contains("already there")).Take(1))
                GD.Print($"{who,-6} refusal  \"{Trim(line)}\"");

            if (lines.Count < 8)
                failures.Add($"the {who} spoke {lines.Count} sentences for its actions");
            if (!lines.Any(l => l.Contains("already there")))
                failures.Add($"the {who} never told the player why a build was refused");
        }

        var card = hostRoot.GetNodeOrNull<NetStatusPanel>("NetStatusLayer/NetStatus");
        var markers = hostRoot.GetNodeOrNull<PendingActionsView>("PendingActions");
        GD.Print($"in game         host tick {hw.TickCount}, client {cw.TickCount}, " +
                 $"card=\"{card?.TitleText}\" showing={card?.IsShowing} " +
                 $"body={card?.BodyText.Length ?? -1} chars, " +
                 $"in-flight markers ever drawn={_markersSeen}");

        if (hw.TickCount < 60) failures.Add($"the host root only reached tick {hw.TickCount}");
        if (cw.TickCount < 60) failures.Add($"the client root only reached tick {cw.TickCount}");
        if (card is null || !card.IsShowing || card.BodyText.Length == 0)
            failures.Add("the shared world drew no net status card");
        if (markers is null)
            failures.Add("the shared world has no in-flight marker node");
        if (_markersSeen == 0)
            failures.Add("no in-flight marker was ever drawn, so the 100 ms is invisible");

        // ---- and the two worlds, still one world ---------------------------
        //
        // Levelled first, with the roots detached. Two roots tick off their own
        // wall clocks, so one is a few ticks ahead of the other, and a hash
        // taken at tick 78 against one taken at tick 74 disagrees for a reason
        // that has nothing to do with this slice. Nothing is *simulated*
        // differently here -- the laggard is run forward on batches the
        // sequencer has already broadcast, which is the same catch-up a slow
        // frame does in play.
        hostRoot.QueueFree();
        clientRoot.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        for (var i = 0; i < 600 && hw.TickCount != cw.TickCount; i++)
        {
            if (cw.TickCount < hw.TickCount)
                clientDriver.Advance((int)(hw.TickCount - cw.TickCount));
            else
                hostDriver.Advance((int)(cw.TickCount - hw.TickCount));

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (hw.TickCount != cw.TickCount)
            failures.Add($"the two peers never met on one tick ({hw.TickCount} vs " +
                         $"{cw.TickCount}), so nothing below compares like with like");

        var hostHash = hw.StateHash();
        var clientHash = cw.StateHash();
        var hostModel = Sim.Save.SaveGame.Capture(hw);
        var clientModel = Sim.Save.SaveGame.Capture(cw);
        hostModel.LocalPlayer = 0;
        clientModel.LocalPlayer = 0;
        var identical = string.CompareOrdinal(Sim.Save.SaveGame.ToJson(hostModel),
                                              Sim.Save.SaveGame.ToJson(clientModel)) == 0;

        GD.Print($"after play      hash {hostHash:X16} / {clientHash:X16}, " +
                 $"digest {hw.CommandDigest:X16} / {cw.CommandDigest:X16}, " +
                 $"saves identical except for LocalPlayer={identical}");
        GD.Print($"machines        host {hw.MachineCount} client {cw.MachineCount}, " +
                 $"carrying stone host {Stone(hw)} client {Stone(cw)}");

        if (hostHash != clientHash)
            failures.Add("two peers played through the input path and ended on different hashes");
        if (hw.CommandDigest != cw.CommandDigest)
            failures.Add("the two peers' command digests differ after playing");
        if (!identical) failures.Add("the two peers saved different bytes after playing");

        host.Close("");
        client.Close("");

        async System.Threading.Tasks.Task Step(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var view = hostRoot.GetNodeOrNull<PendingActionsView>("PendingActions");
                if (view is not null) _markersSeen = System.Math.Max(_markersSeen, view.Drawn);
            }
        }

        static void Listen(GameRoot root, List<string> into)
        {
            var prior = root.Actions.Speak;
            root.Actions.Speak = message =>
            {
                prior?.Invoke(message);
                into.Add(message);
            };
        }

        /// Units sitting in the input buffer of the machine on a tile.
        static int InputsOf(Sim.World world, int x, int y)
            => world.TryMachineAt(x, y, out var machine, out _)
                ? machine.InputContents.Values.Sum()
                : 0;

        static int OutputsOf(Sim.World world, int x, int y)
            => world.TryMachineAt(x, y, out var machine, out _)
                ? machine.OutputContents.Values.Sum()
                : 0;

        static bool Finished(Sim.World world, int x, int y) => OutputsOf(world, x, y) > 0;

        /// A machine with a cycle in flight, which is what a hand load causes
        /// and what an idle one looks nothing like.
        static bool Busy(Sim.World world, int x, int y)
            => world.TryMachineAt(x, y, out var machine, out _)
               && (machine.State == Sim.MachineState.Working || machine.TicksRemaining > 0
                   || machine.InputContents.Values.Sum() > 0
                   || machine.OutputContents.Values.Sum() > 0);

        /// Everything *everybody* in the world is carrying, which is what a
        /// hand take moves into and a hand load moves out of.
        ///
        /// Every player, not the local one: each peer looks through a different
        /// pair of eyes, so two peers agreeing about the local player's pockets
        /// would be two peers comparing two different people.
        static int Carried(Sim.World world)
            => world.Players.Sum(p => p.Inventory.Contents.Values.Sum());

        static bool Wanted(Sim.World world, string item)
            => world.Research is not null
               && world.Research.Objectives.SelectMany(o => o.Needs)
                        .Any(n => !n.Met && n.Accepts.Contains(item));

        static int Held(Sim.World world, string item)
            => world.Items.TryGetId(item, out var id) ? world.PlayerInventory.Count(id) : -1;

        static int Stone(Sim.World world)
            => world.Items.TryGetId("stone_deposit", out var id)
                ? world.PlayerInventory.Count(id)
                : -1;
    }

    /// The most markers the in-flight view has had on screen at once during the
    /// input check. Zero means the 100 ms was never drawn, which is half of
    /// what this slice is.
    private int _markersSeen;

    /// Corrupts one peer by one milli-tile and insists the session notices.
    ///
    /// One milli-tile is a thousandth of a tile -- invisible on a screen, and
    /// exactly the size of divergence a floating-point drift would produce.
    /// If the detector catches this it catches anything.
    private async System.Threading.Tasks.Task<string> RunDesyncCheck(
        int port, int seed, Sim.Data.Catalogue catalogue, List<string> failures)
    {
        var host = new NetSession { Name = "DesyncHost" };
        var client = new NetSession { Name = "DesyncClient" };
        AddChild(host);
        AddChild(client);

        if (host.Host("Ada", port, maxPlayers: 4) != NetFailure.None)
        {
            failures.Add("could not host the desync check");
            return "not run";
        }

        var joined = false;
        client.Connected += () => joined = true;
        client.Join("Grace", "127.0.0.1", port);
        await Frames(() => joined, 900);
        if (!joined)
        {
            failures.Add("the desync check's client never connected");
            return "not run";
        }

        using var hostDriver = new LockstepDriver(host, catalogue);
        using var clientDriver = new LockstepDriver(client, catalogue);

        Sim.World? hw = null;
        Sim.World? cw = null;
        hostDriver.Started += w => hw = w;
        clientDriver.Started += w => cw = w;
        hostDriver.StartAsHost(seed);
        await Frames(() => hw is not null && cw is not null, 600);

        if (hw is null || cw is null)
        {
            failures.Add("the desync check never got two worlds");
            return "not run";
        }

        var perturbed = false;
        var corruptedAt = -1L;

        for (var frame = 0; frame < 4000; frame++)
        {
            if (!perturbed && cw.TickCount >= 100)
            {
                // One thousandth of a tile, on one peer, silently.
                var victim = cw.Players[1];
                victim.Restore(victim.X + 1, victim.Y, victim.FacingX, victim.FacingY);
                perturbed = true;
                corruptedAt = cw.TickCount;
            }

            hostDriver.Advance(4);
            clientDriver.Advance(4);

            if (hostDriver.Phase == NetPhase.Stopped && clientDriver.Phase == NetPhase.Stopped)
                break;

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var reason = hostDriver.StopReason.Length > 0
            ? hostDriver.StopReason
            : clientDriver.StopReason;

        GD.Print($"corrupted       client player by 1 milli-tile at tick {corruptedAt}");
        GD.Print($"host stopped    {hostDriver.Phase} at tick {hw.TickCount}: " +
                 $"{Trim(hostDriver.StopReason)}");
        GD.Print($"client stopped  {clientDriver.Phase} at tick {cw.TickCount}: " +
                 $"{Trim(clientDriver.StopReason)}");

        var namedTick = corruptedAt < 0
            ? -1
            : ((corruptedAt / LockstepDriver.HashEvery) + 1) * LockstepDriver.HashEvery;

        if (hostDriver.Phase != NetPhase.Stopped)
            failures.Add("a peer was corrupted and the host never stopped");
        if (clientDriver.Phase != NetPhase.Stopped)
            failures.Add("a peer was corrupted and the client never stopped");
        if (!reason.Contains($"tick {namedTick}"))
            failures.Add($"the desync was not reported against tick {namedTick}: {reason}");
        if (!reason.Contains("Desync"))
            failures.Add($"the stop did not say what happened: {reason}");
        if (hw.TickCount > namedTick + LockstepDriver.HashEvery)
            failures.Add($"the host ran on to tick {hw.TickCount} after a desync at {namedTick}");

        // Both peers compare, so both must stop on their own reckoning. Without
        // this, deleting the local Stop() leaves the run green: each peer is
        // still stopped by the *other* peer's Stop message, and "both peers
        // stopped" cannot tell the two apart. A peer that only ever stops on
        // the message runs on with a world it knows is wrong the moment that
        // message cannot arrive.
        if (!hostDriver.StoppedOnOwnComparison)
            failures.Add("the host stopped only because it was told to, not on its own hash comparison");
        if (!clientDriver.StoppedOnOwnComparison)
            failures.Add("the client stopped only because it was told to, not on its own hash comparison");

        host.Close("");
        client.Close("");

        return $"caught at tick {namedTick}, both peers stopped on their own " +
               $"comparison (host {hostDriver.StoppedOnOwnComparison}, client " +
               $"{clientDriver.StoppedOnOwnComparison}), " +
               $"host at {hw.TickCount} and client at {cw.TickCount}";
    }

    /// How many ticks a peer may run before it must stop and be looked at:
    /// never past the next hash boundary and never past the end of the run.
    private static int Room(long tick, long limit)
    {
        var toBoundary = LockstepDriver.HashEvery - (int)(tick % LockstepDriver.HashEvery);
        return (int)Math.Max(0, Math.Min(Math.Min(8, toBoundary), limit - tick));
    }

    private static string Trim(string text) =>
        text.Length <= 96 ? text : text[..96] + "...";

    /// Captures the three multiplayer screens, each with a deliberately long
    /// refusal in it: the failure sentences are the longest strings any of
    /// these screens will ever hold, and a card that a long one pushes past the
    /// viewport is a defect only a capture shows (docs/0025, docs/0032).
    private async void RunNetShot()
    {
        GD.Print("=== NET SHOT ===");
        ShowMenu();
        await Frames(() => false, 8);

        await Capture("menu");

        ShowMultiplayerSetup(MultiplayerSetup.Mode.Host);
        _setup!.ShowError(NetSession.Describe(NetFailure.SocketUnavailable, "ERR_CANT_CREATE"));
        await Frames(() => false, 8);
        await Capture("host");

        ShowMultiplayerSetup(MultiplayerSetup.Mode.Join);
        // The longest sentence any of these screens can hold. If the card
        // survives this one it survives all of them.
        _setup!.ShowError(NetSession.Describe(NetFailure.NoAnswer));
        await Frames(() => false, 8);
        await Capture("join");

        CloseMultiplayerSetup();

        var session = Net();
        session.Host("Ada Lovelace with a very long name indeed", 7801, maxPlayers: 4);
        ShowLobby(session, "");
        _lobby!.ShowError(NetSession.Describe(NetFailure.VersionMismatch,
            "this host is on 0.3.0, you are on 0.2.1."));
        await Frames(() => false, 8);
        await Capture("lobby");

        session.Close("");
        HideLobby();

        // The two states of the in-game overlay, staged rather than induced: a
        // real stall is a race and a real desync needs a corrupted peer, and
        // neither is a thing to point a camera at and hope. The strings are the
        // longest the panel can hold -- three peers being waited for, and a
        // desync sentence with a tick and two hashes in it.
        var statusLayer = new CanvasLayer { Name = "NetStatusShot" };
        var status = new NetStatusPanel { Name = "NetStatus" };
        statusLayer.AddChild(status);
        AddChild(statusLayer);
        await Frames(() => false, 4);

        status.Show(new NetStatus(NetPhase.Running, 128_400, true,
                                  "Grace Hopper, Charles Babbage, Ada", 214, ""));
        await Frames(() => false, 8);
        GD.Print($"stall caption   \"{status.TitleText}\" + {status.BodyText.Length} chars, " +
                 $"showing={status.IsShowing}");
        await Capture("stall");

        status.Show(new NetStatus(NetPhase.Stopped, 128_460, false, "", 0,
            "Desync at tick 128460: this peer computed 90107B8432E33FFD, Grace Hopper " +
            "computed 491987B30360C438. The two simulations no longer agree, so the " +
            "session has stopped. Nothing after that tick can be trusted."));
        await Frames(() => false, 8);
        GD.Print($"desync caption  \"{status.TitleText}\" + {status.BodyText.Length} chars, " +
                 $"showing={status.IsShowing}");
        await Capture("desync");

        // The in-flight state, in a real shared world rather than staged.
        //
        // Held open by *stalling*: the client's driver is simply not advanced,
        // so the host cannot seal the tick the click was addressed to and the
        // action stays in flight for as long as the camera needs. That is a
        // real state of the running game -- the amber card and the marker
        // belong on screen together -- and it is the only way to photograph a
        // window that is otherwise 100 ms wide.
        await CaptureInFlight(7899);

        GD.Print("=== NET SHOT OK ===");
        GetTree().Quit();
    }

    private async System.Threading.Tasks.Task CaptureInFlight(int port)
    {
        // The title screen and the staged status card were the subjects of the
        // captures above; here they are simply in the way.
        _menu?.GetParent()?.QueueFree();
        _menu = null;
        GetNodeOrNull<CanvasLayer>("NetStatusShot")?.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var catalogue = GameSession.Catalogue;
        var host = new NetSession { Name = "ShotHost" };
        var client = new NetSession { Name = "ShotClient" };
        AddChild(host);
        AddChild(client);

        if (host.Host("Ada", port, maxPlayers: 4) != NetFailure.None)
        {
            GD.Print("in flight       FAILED: could not host");
            return;
        }

        var joined = false;
        client.Connected += () => joined = true;
        client.Join("Grace Hopper", "127.0.0.1", port);
        await Frames(() => joined, 900);
        if (!joined)
        {
            GD.Print("in flight       FAILED: no client");
            return;
        }

        using var hostDriver = new LockstepDriver(host, catalogue);
        using var clientDriver = new LockstepDriver(client, catalogue);

        Sim.World? hw = null;
        hostDriver.Started += w => hw = w;
        hostDriver.StartAsHost(20260908);
        await Frames(() => hw is not null, 600);
        if (hw is null)
        {
            GD.Print("in flight       FAILED: no world");
            return;
        }

        var root = new GameRoot { Name = "ShotRoot", InitialWorld = hw, Net = hostDriver };
        AddChild(root);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        root.ClosePanelsForCapture();

        for (var i = 0; i < 60 && hw.TickCount < 40; i++)
        {
            clientDriver.Advance(4);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        // One refused action, all the way through: the sentence on screen is a
        // real refusal that arrived 100 ms after its click, not a caption.
        root.RemoveAt(hw.Player.TileX + 3, hw.Player.TileY + 3);
        for (var i = 0; i < 40 && root.Actions.Refused == 0; i++)
        {
            clientDriver.Advance(4);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        // And one that stays in flight, because the client stops speaking here.
        var builds = new Sim.BuildCatalogue(catalogue);
        var bench = builds.Find("man_manual_crafting");
        var recipe = bench is null
            ? null
            : builds.RecipesFor(bench, hw.Research).FirstOrDefault();
        if (bench is not null && recipe is not null)
        {
            root.Hold(bench, recipe);
            root.PlaceHeld(hw.Player.TileX + 6, hw.Player.TileY - 5);
        }

        // Terrain streams in over several frames and a capture returns the
        // frame that was already drawn (docs/0025), so this waits rather than
        // photographing an empty world with a caption on it.
        await Frames(() => false, 30);

        var markers = root.GetNodeOrNull<PendingActionsView>("PendingActions");
        var card = root.GetNodeOrNull<NetStatusPanel>("NetStatusLayer/NetStatus");
        GD.Print($"in flight       {root.Actions.InFlight.Count} queued, " +
                 $"{markers?.Drawn ?? -1} markers visible, " +
                 $"{root.Actions.Refused} refusals spoken, card=\"{card?.TitleText}\"");
        await Capture("inflight");

        root.QueueFree();
        host.Close("");
        client.Close("");
    }

    private async System.Threading.Tasks.Task Capture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng($"user://net-{name}.png");
        GD.Print($"shot {name,-8} {image.GetWidth()}x{image.GetHeight()} -> " +
                 ProjectSettings.GlobalizePath($"user://net-{name}.png"));
    }

    private void ShowMenu()
    {
        _game?.QueueFree();
        _game = null;

        _menu = GD.Load<PackedScene>("res://scenes/main_menu.tscn").Instantiate<MainMenu>();
        _menu.NewGameRequested += OnNewGame;
        _menu.LoadRequested += OnLoad;
        _menu.SaveSelectRequested += ShowSaveSelect;
        _menu.HostGameRequested += () => ShowMultiplayerSetup(MultiplayerSetup.Mode.Host);
        _menu.JoinGameRequested += () => ShowMultiplayerSetup(MultiplayerSetup.Mode.Join);

        var layer = new CanvasLayer { Name = "MenuLayer" };
        layer.AddChild(_menu);
        AddChild(layer);
    }

    private void OnNewGame(int seed) =>
        StartGame(GameSession.NewGame(seed), disposeMenu: true);

    /// The load screen, over the menu rather than instead of it: Back returns
    /// to a menu that never went away, so the card does not have to be rebuilt
    /// and the player does not lose their place.
    private SaveSelect? _saveSelect;

    private void ShowSaveSelect()
    {
        if (_saveSelect is not null) return;

        _saveSelect = GD.Load<PackedScene>("res://scenes/save_select.tscn")
                        .Instantiate<SaveSelect>();
        _saveSelect.LoadRequested += OnLoadFromSelect;
        _saveSelect.BackRequested += CloseSaveSelect;

        var layer = new CanvasLayer { Name = "SaveSelectLayer" };
        layer.AddChild(_saveSelect);
        AddChild(layer);
    }

    private void CloseSaveSelect()
    {
        _saveSelect?.GetParent()?.QueueFree();
        _saveSelect = null;
    }

    private void OnLoadFromSelect(string path)
    {
        try
        {
            StartGame(GameSession.Load(path), disposeMenu: true);
            CloseSaveSelect();
        }
        catch (System.Exception e)
        {
            // The screen stays open and says why. A failed load must never
            // drop the player into a half-built world or a blank one.
            GD.PushWarning($"load failed: {e.Message}");
            _saveSelect?.ShowError(e.Message);
        }
    }

    private void OnLoad(string path)
    {
        try
        {
            StartGame(GameSession.Load(path), disposeMenu: true);
        }
        catch (System.Exception e)
        {
            // A save that will not load is a message, never a broken world: the
            // player keeps their other saves and knows why this one failed.
            GD.PushWarning($"load failed: {e.Message}");
            _menu?.ShowError(e.Message);
        }
    }

    private void StartGame(Sim.World world, bool disposeMenu = false, LockstepDriver? net = null)
    {
        if (disposeMenu)
        {
            _menu?.GetParent()?.QueueFree();
            _menu = null;
        }

        _game = new GameRoot { Name = "GameRoot", InitialWorld = world, Net = net };
        _game.ReturnToMenuRequested += ShowMenu;
        AddChild(_game);
    }
}

/// Command-line arguments. Godot splits engine args from anything after "--",
/// so both halves have to be checked.
public static class Cli
{
    public static string[] All() =>
        OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();

    public static bool Has(string flag) => All().Any(a => a == flag || a.StartsWith(flag + "="));

    public static bool WantsHeadlessRun() =>
        Has("--smoke") || Has("--screenshot") || Has("--machines") || Has("--start-shot")
        || Has("--build-shot") || Has("--survey-shot") || Has("--dig-shot") || Has("--opening-shot") || Has("--pause-shot") || Has("--save-select-shot") || Has("--belt-shot") || Has("--uplink-shot")
        || Has("--shore-shot");

    public static int ReadInt(string name, int fallback)
    {
        foreach (var arg in All())
        {
            if (!arg.StartsWith(name + "=")) continue;
            if (int.TryParse(arg[(name.Length + 1)..], out var value) && value > 0)
                return value;
        }

        return fallback;
    }
}
