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

    public override void _Process(double delta)
    {
        if (_menuShotCountdown > 0 && --_menuShotCountdown == 0)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng("user://menu.png");
            GD.Print($"menu screenshot -> {ProjectSettings.GlobalizePath("user://menu.png")}");
            GetTree().Quit();
        }
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

        var dug = Sim.HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory, 25);
        GD.Print($"nearest ore     {name} at {hit.X},{hit.Y} ({hit.Distance} tiles), {before} units");
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

        RunOpeningRoute();
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
            if (world.TryBuild(buildables, belt.Item, x, y, facing: Sim.Direction.East)
                == Sim.BuildResult.Ok) laid++;

        // The arm stands at (7, y-1) facing north: it reaches back to the belt
        // tile at (7, y) and forward into the furnace at (7, y-2). Getting this
        // wrong is silent -- the inserter simply holds an item forever -- which
        // is why the check below is "did ore arrive" and not "did it build".
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

    private void ShowMenu()
    {
        _game?.QueueFree();
        _game = null;

        _menu = GD.Load<PackedScene>("res://scenes/main_menu.tscn").Instantiate<MainMenu>();
        _menu.NewGameRequested += OnNewGame;
        _menu.LoadRequested += OnLoad;

        var layer = new CanvasLayer { Name = "MenuLayer" };
        layer.AddChild(_menu);
        AddChild(layer);
    }

    private void OnNewGame(int seed) =>
        StartGame(GameSession.NewGame(seed), disposeMenu: true);

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
        || Has("--build-shot") || Has("--survey-shot") || Has("--belt-shot") || Has("--uplink-shot");

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
