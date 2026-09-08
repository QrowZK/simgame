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
        ShowLobby(session, $"Seed {seed}. Waiting for players.");
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

    private void ShowLobby(NetSession session, string note)
    {
        _lobby = GD.Load<PackedScene>("res://scenes/lobby.tscn").Instantiate<Lobby>();
        _lobby.Bind(session);
        _lobby.LeaveRequested += LeaveLobby;

        // Start cannot start anything yet, and says so. The world does not
        // travel over the wire until the command layer lands; a button that
        // silently did nothing would be the worse of the two lies.
        _lobby.StartRequested += () =>
            _lobby?.ShowNote("Nothing to start yet: shared worlds arrive with the command layer.");

        var layer = new CanvasLayer { Name = "LobbyLayer" };
        layer.AddChild(_lobby);
        AddChild(layer);

        _lobby.Refresh();
        _lobby.ShowNote(note);
    }

    private void LeaveLobby()
    {
        _net?.Close("");
        _lobby?.GetParent()?.QueueFree();
        _lobby = null;
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
        GD.Print("=== NET SHOT OK ===");
        GetTree().Quit();
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

    private void StartGame(Sim.World world, bool disposeMenu = false)
    {
        if (disposeMenu)
        {
            _menu?.GetParent()?.QueueFree();
            _menu = null;
        }

        _game = new GameRoot { Name = "GameRoot", InitialWorld = world };
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
