using System.Linq;
using Godot;
using Sim;

namespace Game;

/// Wires the engine layer to the sim. Owns no game state itself: it ticks the
/// World at a fixed rate and hands the renderer a read-only view each frame.
public sealed partial class GameRoot : Node3D
{
    /// Raised when the player leaves to the title screen. Boot owns the
    /// menu/game switch; GameRoot only says that it wants to go back.
    [Signal] public delegate void ReturnToMenuRequestedEventHandler();

    /// The world to run. Set by Boot before the node enters the tree -- a new
    /// game, or one restored from a save.
    public World? InitialWorld { get; set; }

    private const int TicksPerSecond = 60;
    private const double SecondsPerTick = 1.0 / TicksPerSecond;
    /// Never advance more than this many ticks in one frame -- a long stall must
    /// not turn into an unbounded catch-up loop that stalls even longer.
    private const int MaxCatchUpTicks = 8;

    private World _world = null!;
    private MachineRenderer _renderer = null!;
    private TerrainRenderer _terrain = null!;
    private PoleRenderer _poles = null!;
    private DroneRenderer _drones = null!;
    private BeltRenderer _belts = null!;
    private ScriptEditor _editor = null!;
    private CameraRig _rig = null!;
    private Label _hud = null!;
    private MachinePanel _panel = null!;
    private PauseMenu _pause = null!;
    private BuildMenu _build = null!;
    private QuestPanel _quests = null!;
    /// Whether the win has already been announced. The world stays playable
    /// after the Seed goes -- there is no reason to take a factory away from
    /// someone -- so the banner must not re-fire every frame.
    private bool _seedAnnounced;
    private BuildGhost _ghost = null!;
    private BuildCatalogue _buildables = null!;
    private Buildable? _holding;
    private Recipe? _holdingRecipe;

    /// Which way the next belt or inserter will face. Held across placements,
    /// because laying a run means placing the same direction many times.
    private Direction _facing = Direction.East;
    private double _accumulator;
    private int _screenshotCountdown = -1;
    private string _toast = "";
    private int _toastFrames;

    public override void _Ready()
    {
        _world = InitialWorld ?? DemoWorld.Build(ReadIntArg("--machines", 4096), seed: 1234);
        GameSession.Adopt(_world);
        var machineCount = _world.MachineCount;

        _rig = new CameraRig { Name = "CameraRig" };
        AddChild(_rig);

        _terrain = new TerrainRenderer { Name = "TerrainRenderer" };
        AddChild(_terrain);

        _renderer = new MachineRenderer { Name = "MachineRenderer" };
        AddChild(_renderer);

        _poles = new PoleRenderer { Name = "PoleRenderer" };
        AddChild(_poles);

        _drones = new DroneRenderer { Name = "DroneRenderer" };
        AddChild(_drones);

        AddChild(BuildLighting());
        _hud = BuildHud();
        // Before the panel: the panel binds to it, and a null catalogue there
        // silently costs the recipe picker.
        _buildables = new BuildCatalogue(Sim.Data.Catalogue.Instance);
        _panel = BuildPanel();
        _build = BuildBuildMenu();
        _ghost = new BuildGhost { Name = "BuildGhost" };
        AddChild(_ghost);
        _belts = new BeltRenderer { Name = "BeltRenderer" };
        AddChild(_belts);
        _pause = BuildPauseMenu();
        _quests = BuildQuestPanel();
        _editor = BuildScriptEditor();

        // A brand new game, and only a brand new game: the premise is shown at
        // tick zero on an untouched world, so a loaded save never replays it.
        if (_world.TickCount == 0 && _world.MachineCount == 0 && _world.Research is not null)
            _quests.OpenWithPremise();

        // Frame whatever there is to look at. A new game has no factory, so the
        // camera sits on the landing site at a zoom where the ground around it
        // reads as terrain rather than as a texture.
        if (machineCount == 0)
        {
            _rig.Position = new Vector3(NewGame.SpawnX, 0f, NewGame.SpawnY);

            // Wide enough to see the lie of the land and the nearest ore --
            // the first decision a new game asks for -- but inside the drawn
            // tile field, so the player never sees its edge.
            _rig.ZoomLevel = 78f;
        }
        else
        {
            var side = Mathf.CeilToInt(Mathf.Sqrt(machineCount));
            _rig.Position = new Vector3(side * 0.5f, 0f, side * 0.5f);
            _rig.ZoomLevel = Mathf.Clamp(side * 1.4f, 8f, 160f);
        }

        _rig.Apply();

        _renderer.Sync(_world);
        _terrain.Sync(_world, _rig.Position);
        _poles.Sync(_world);
        _poles.SyncFluids(_world);
        _drones.Sync(_world);
        _belts.Sync(_world, _renderer.TileSize);

        if (AllArgs().Contains("--smoke"))
            CallDeferred(nameof(RunSmokeTest));

        if (AllArgs().Contains("--screenshot") || AllArgs().Contains("--build-shot")
            || AllArgs().Contains("--belt-shot") || AllArgs().Contains("--uplink-shot"))
        {
            _screenshotCountdown = 12;      // let a few frames draw first

            // Capturing the editor rather than the world, when asked. Its
            // layout is the part most likely to be wrong in a way that reading
            // the scene file will not show.
            if (AllArgs().Contains("--editor-shot"))
                CallDeferred(nameof(OpenEditor));
        }
    }

    public override void _Process(double delta)
    {
        // A paused world must not tick. Saving a world mid-tick would capture a
        // state no single tick ever produced, and the save format's whole claim
        // is that it round-trips exactly.
        if (_pause is { Visible: true })
        {
            _renderer.Sync(_world);
            _terrain.Sync(_world, _rig.Position);
        _poles.Sync(_world);
        _poles.SyncFluids(_world);
        _drones.Sync(_world);
        _belts.Sync(_world, _renderer.TileSize);
            return;
        }

        _accumulator += delta;

        var ticks = 0;
        while (_accumulator >= SecondsPerTick && ticks < MaxCatchUpTicks)
        {
            _world.Tick();
            _accumulator -= SecondsPerTick;
            ticks++;
        }

        if (_accumulator > SecondsPerTick * MaxCatchUpTicks)
            _accumulator = 0;

        _renderer.Sync(_world);
        _terrain.Sync(_world, _rig.Position);
        _poles.Sync(_world);
        _poles.SyncFluids(_world);
        _drones.Sync(_world);
        _belts.Sync(_world, _renderer.TileSize);

        // Open the inspection panel just before the capture, so a screenshot
        // shows the GUI rather than only proving the world draws. It has to
        // happen after some ticks: nothing is Working before the first one.
        if (_screenshotCountdown == 2)
        {
            if (AllArgs().Contains("--build-shot")) OpenBuildForCapture();
            else if (AllArgs().Contains("--belt-shot")) FrameTheBelts();
            else if (AllArgs().Contains("--uplink-shot")) ShowTheUplink();
            else ShowAnyRunningMachine();
        }

        if (_screenshotCountdown > 0 && --_screenshotCountdown == 0)
        {
            var image = GetViewport().GetTexture().GetImage();
            var path = "user://shot.png";
            image.SavePng(path);
            GD.Print($"screenshot {image.GetWidth()}x{image.GetHeight()} -> {ProjectSettings.GlobalizePath(path)}");
            GetTree().Quit();
        }

        UpdateGhost();

        // The win, announced once. Nothing is taken away and nothing stops: a
        // factory game whose ending closes the factory has punished the player
        // for finishing it.
        if (!_seedAnnounced && _world.Research is { SeedDelivered: true })
        {
            _seedAnnounced = true;
            Say("The Seed is away. It will come apart on entry somewhere else, and start again.");
            _quests.Open();
        }

        if (_toastFrames > 0) _toastFrames--;

        if (_hud is not null)
        {
            var supply = 0;
            var demand = 0;
            foreach (var n in _world.NetworkSupply) supply += n;
            foreach (var n in _world.NetworkDemand) demand += n;

            // Shown as supply/demand rather than a percentage: a player fixing
            // a brownout needs to know how much more generation to build, and
            // "68%" does not say that.
            var power = _world.Power.NetworkCount == 0
                ? ""
                : $"   power {supply}/{demand}" +
                  (demand > supply ? " BROWNOUT" : "");

            // Stored energy as a percentage, which is the one case where a
            // percentage is the right reading: a player watching a bank wants
            // to know how much buffer is left, not its absolute joules.
            var capacity = _world.StorageCapacity;
            var stored = capacity == 0
                ? ""
                : $"   stored {_world.StoredEnergy * 100 / capacity}%" +
                  $" ({_world.StoredEnergy}/{capacity})";

            _hud.Text = $"machines {_world.MachineCount}   tick {_world.TickCount}   " +
                        $"batches {_renderer.BatchCount}   fps {Engine.GetFramesPerSecond():0}" +
                        power + stored + "\n" +
                        "WASD pan   Q/E rotate   wheel zoom   click a machine to inspect   " +
                        "B build   T objectives   R rotate   F5 save   F9 load   F1 script   Esc menu" +
                        (_toastFrames > 0 ? "\n" + _toast : "");
        }
    }

    /// Headless verification: tick the sim, refill the instance buffers, and
    /// report what actually happened. Exercises the real render path rather than
    /// just proving the project opens.
    private void RunSmokeTest()
    {
        const int ticks = 600;
        var tickWatch = new System.Diagnostics.Stopwatch();
        var syncWatch = new System.Diagnostics.Stopwatch();
        for (var i = 0; i < ticks; i++)
        {
            tickWatch.Start();
            _world.Tick();
            tickWatch.Stop();

            syncWatch.Start();
            _renderer.Sync(_world);
            syncWatch.Stop();
        }

        var working = 0;
        var starved = 0;
        var blocked = 0;
        var idle = 0;
        foreach (var state in _world.MachineStates)
        {
            switch (state)
            {
                case MachineState.Working: working++; break;
                case MachineState.Starved: starved++; break;
                case MachineState.Blocked: blocked++; break;
                default: idle++; break;
            }
        }

        var instances = 0;
        foreach (var child in _renderer.GetChildren())
            if (child is MultiMeshInstance3D { Multimesh: not null } mmi)
                instances += mmi.Multimesh.InstanceCount;

        GD.Print("=== SMOKE ===");
        GD.Print($"machines        {_world.MachineCount}");
        GD.Print($"ticks           {_world.TickCount}");
        GD.Print($"draw batches    {_renderer.BatchCount}");
        GD.Print($"authored meshes {MeshKit.AuthoredMeshes.Count} of {MeshKit.TierCount + MeshKit.CategoryCount}");
        GD.Print($"live instances  {instances}");
        GD.Print($"states          working={working} starved={starved} blocked={blocked} idle={idle}");
        GD.Print($"sim tick        {tickWatch.Elapsed.TotalMilliseconds / ticks:0.000} ms/tick");
        GD.Print($"render sync     {syncWatch.Elapsed.TotalMilliseconds / ticks:0.000} ms/frame");
        GD.Print($"budget          16.667 ms/frame at 60 UPS");
        GD.Print($"camera          pitch={_rig.Pitch} yaw={_rig.Yaw} zoom={_rig.ZoomLevel:0.0} " +
                 $"ortho={_rig.Orthographic}");

        // Rotating the camera must not disturb sim state -- the renderer is a
        // reader, and this is the cheapest place to keep that honest.
        var before = _world.TickCount;
        _rig.Yaw += 137f;
        _rig.Apply();
        _renderer.Sync(_world);
        GD.Print($"after yaw+137   tick={_world.TickCount} (unchanged: {before == _world.TickCount})");
        // Picking, without a mouse: every tile of the largest machine must
        // resolve to that one machine, and the panel must accept it.
        var pickOk = true;
        var largest = 0;
        var largestIndex = -1;
        for (var i = 0; i < _world.Placements.Length; i++)
            if (_world.Placements[i].Size > largest)
            {
                largest = _world.Placements[i].Size;
                largestIndex = i;
            }

        if (largestIndex >= 0)
        {
            var placement = _world.PlacementOf(largestIndex);
            for (var dy = 0; dy < placement.Size; dy++)
                for (var dx = 0; dx < placement.Size; dx++)
                    if (!_world.TryMachineAt(placement.X + dx, placement.Y + dy, out _, out var hit)
                        || hit != largestIndex)
                        pickOk = false;
        }

        var droneInstances = 0;
        foreach (var child in _drones.GetChildren())
            if (child is MultiMeshInstance3D { Multimesh: not null } dmm)
                droneInstances += dmm.Multimesh.InstanceCount;

        GD.Print($"drones          {_world.Logistics.Drones.Count} " +
                 $"instances={droneInstances} idle={_world.Logistics.IdleDrones} " +
                 $"tasks={_world.Logistics.Tasks.Count}");
        if (_world.Logistics.Drones.Count > 0)
        {
            var d = _world.Logistics.Drones[0];
            GD.Print($"drone 0         at {d.X},{d.Y} cargo={d.CargoCount} task={d.Task}");
        }

        // Accumulators go through the machine renderer's hull pools, so the
        // instance count above already covers them. What that count cannot say
        // is whether they are on a grid and taking charge, so say it here.
        // Build mode, exercised rather than described: turn it on, take what
        // the menu offers, and report whether the ghost actually appeared. A
        // build UI that lists things and previews nothing looks fine in a
        // screenshot and is unusable.
        _world.PlayerInventory.Add(_buildables.Offerable.First().Item, 1);
        StartBuilding();
        var offered = _build.OfferedCount;
        var ghostShown = false;
        if (_holding is not null)
        {
            _ghost.Show(_holding, 0, -6, true, _renderer.TileSize);
            ghostShown = _ghost.Visible;
        }

        GD.Print($"item icons      known={ItemIcons.Known} of {_world.Items.Count}");

        // The ground is shaded from the worldgen's own noise, which means a
        // rebuild evaluates it once per visible tile. It only happens when the
        // camera crosses a tile boundary, but it happens inside a frame, so the
        // cost of the whole field is worth a number rather than a shrug.
        var terrainWatch = System.Diagnostics.Stopwatch.StartNew();
        _terrain.Sync(_world, _rig.Position + new Vector3(1f, 0f, 1f));
        terrainWatch.Stop();

        var field = _terrain.ViewRadius * 2 + 1;
        GD.Print($"terrain rebuild {field}x{field} tiles in " +
                 $"{terrainWatch.Elapsed.TotalMilliseconds:0.0} ms");
        GD.Print($"build menu      offered={offered} " +
                 $"holding={_holding?.DisplayName ?? "<none>"} ghost={ghostShown}");
        StopBuilding();
        GD.Print($"build closed    menu={_build.IsShowing} ghost={_ghost.Visible}");

        GD.Print($"belts           tiles={_world.BeltMap.Belts.Count} " +
                 $"segments={_world.Belts.Segments.Count} " +
                 $"inserters={_world.BeltMap.Inserters.Count} " +
                 $"items drawn={_belts.DrawnItems}");

        // Tunnels and splitters have no state a screenshot can confirm from
        // across the map: a placed-but-undrawn tunnel end and an empty tile are
        // the same picture. The counts say the buffers were actually filled.
        GD.Print($"belt parts      undergrounds={_world.BeltMap.Undergrounds.Count} " +
                 $"splitters={_world.BeltMap.Splitters.Count} " +
                 $"solids drawn={_belts.DrawnSolids} arrows drawn={_belts.DrawnArrows}");

        GD.Print($"accumulators    {_world.Power.Accumulators.Count} " +
                 $"stored={_world.StoredEnergy}/{_world.StorageCapacity}");

        GD.Print($"controllers     {_world.Controllers.Count} " +
                 $"error={(_world.Controllers.Count > 0 ? _world.Controllers[0].Error ?? "none" : "n/a")}");

        GD.Print($"picking         largest={largest}x{largest} " +
                 $"all tiles resolve to one machine: {pickOk}");
        // The recipe picker cannot be seen in the smoke run and photographs as
        // an empty box when it is broken -- a null catalogue at bind time cost
        // exactly that, and only a screenshot found it. So the panel is opened
        // on a real machine here and asked how many recipes it is offering.
        ShowAnyRunningMachine();
        GD.Print($"panel           showing={_panel.IsShowing}");
        GD.Print($"recipe picker   options={_panel.RecipeOptions} " +
                 $"current selected={_panel.CurrentRecipeIsSelected}");
        // Research, which gates the build menu and so decides what the two
        // lines above can ever say. Measured on a fresh state rather than on
        // this world's, because the demo world the smoke run uses has none and
        // the number that matters is the one a new game starts with: too few
        // and the opening is unplayable, all of them and nothing is gated.
        var fresh = new Research(Sim.Data.Catalogue.Instance);
        var openAtStart = _buildables.OfferableWith(fresh).Count();
        var openUngated = _buildables.Offerable.Count();
        GD.Print($"research        techs={fresh.UnlockedTechs.Count}/{fresh.AllTechs.Count} " +
                 $"objectives={fresh.Objectives.Count} " +
                 $"buildable={openAtStart}/{openUngated} at tick zero");

        // The demo world has no research, so the panel would photograph as an
        // empty box. Give it the fresh state built above -- the same one a new
        // game starts with -- so the line reports the text a player would read.
        _world.Research ??= fresh;
        _quests.Refresh();
        GD.Print($"quest panel     {_quests.BodyText.Length} chars, " +
                 $"{_quests.BodyText.Split('\n').Length} lines");

        GD.Print("=== SMOKE OK ===");

        GetTree().Quit();
    }

    private static Node BuildLighting()
    {
        var holder = new Node3D { Name = "Lighting" };

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            LightEnergy = 1.1f,
        };
        sun.RotationDegrees = new Vector3(-55f, -40f, 0f);
        holder.AddChild(sun);

        var environment = new WorldEnvironment
        {
            Name = "WorldEnvironment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.12f, 0.13f, 0.15f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.45f, 0.48f, 0.55f),
                AmbientLightEnergy = 0.6f,
            },
        };
        holder.AddChild(environment);

        return holder;
    }

    /// Click-to-inspect. Resolves the click to a ground tile and asks the sim
    /// which machine covers it -- the occupancy grid answers in O(1), and a 3x3
    /// answers the same machine from any of its nine tiles.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F1 })
        {
            if (_editor.IsOpen) CloseEditor();
            else OpenEditor();
            return;
        }

        // While the editor has the keyboard, nothing else may claim a keystroke
        // -- F5 in the middle of a line would quick-save instead of typing.
        if (_editor.IsOpen)
        {
            if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
                CloseEditor();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            // Escape backs out one level at a time: the inspection panel first,
            // then the pause menu. Jumping straight to a menu from an open panel
            // would feel like the game ignored the panel.
            if (_build.IsShowing) StopBuilding();
            else if (_quests.IsShowing) _quests.Close();
            else if (_panel.IsShowing) _panel.Close();
            else if (_pause.Visible) _pause.Close();
            else _pause.Open();
            return;
        }

        // R turns what you are holding. Only while building: R is a scarce key
        // and rotating nothing would be a silent no-op.
        if (_build.IsShowing && @event is InputEventKey { Pressed: true, Keycode: Key.R })
        {
            _facing = Directions.Rotate(_facing);
            Say($"Facing {_facing}.");
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.T })
        {
            if (_quests.IsShowing) _quests.Close();
            else _quests.Open();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.B })
        {
            if (_build.IsShowing) StopBuilding();
            else StartBuilding();
            return;
        }

        // Right-click leaves build mode, the same as Escape. A player holding a
        // machine wants out without moving their hand to the keyboard.
        if (_build.IsShowing && @event is InputEventMouseButton
            { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            StopBuilding();
            return;
        }

        // Quick save and quick load, the bindings a factory player expects.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F5 })
        {
            QuickSave();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.F9 })
        {
            QuickLoad();
            return;
        }

        // Clicks belong to the menu while it is open.
        if (_pause.Visible) return;

        if (@event is not InputEventMouseButton
            { Pressed: true, ButtonIndex: MouseButton.Left } click)
            return;

        if (!_rig.TryGroundPoint(click.Position, out var point))
            return;

        var tileX = Mathf.FloorToInt(point.X / _renderer.TileSize);
        var tileY = Mathf.FloorToInt(point.Z / _renderer.TileSize);

        if (_build.IsShowing)
        {
            PlaceHeld(tileX, tileY);
            return;
        }

        if (_world.TryMachineAt(tileX, tileY, out var machine, out var index))
            _panel.Show(machine, _world.PlacementOf(index), index);
        else if (_world.TryMinerAt(tileX, tileY, out var miner, out var minerIndex))
            _panel.Show(miner, _world.MinerPlacements[minerIndex],
                        _world.Ground.RemainingAt(tileX, tileY));
        else
            _panel.Close();
    }

    /// Opens the panel on a running machine, falling back to any machine at
    /// all. Used by the screenshot path to exercise the panel's live layout.
    private void ShowAnyRunningMachine()
    {
        for (var i = 0; i < _world.MachineCount; i++)
            if (_world.Machines[i].State == MachineState.Working)
            {
                _panel.Show(_world.Machines[i], _world.PlacementOf(i), i);
                return;
            }

        if (_world.MachineCount > 0)
            _panel.Show(_world.Machines[0], _world.PlacementOf(0), 0);
    }

    private BuildMenu BuildBuildMenu()
    {
        var layer = new CanvasLayer { Name = "BuildUi" };
        var menu = GD.Load<PackedScene>("res://scenes/build_menu.tscn").Instantiate<BuildMenu>();
        menu.Bind(_buildables, _world, _world.Items);
        menu.Selected += OnBuildSelected;
        menu.Closed += StopBuilding;
        layer.AddChild(menu);
        AddChild(layer);
        return menu;
    }

    public void StartBuilding()
    {
        // The inspection panel and build mode both own the left mouse button,
        // so only one may be up at a time.
        _panel.Close();
        _build.Open();
    }

    public void StopBuilding()
    {
        _build.Close();
        _ghost.Hide();
        _holding = null;
        _holdingRecipe = null;
    }

    private void OnBuildSelected(Buildable buildable, Recipe? recipe)
    {
        _holding = buildable;
        _holdingRecipe = recipe;
    }

    /// Draws the held machine where it would land, coloured by whether it could
    /// actually go there. Asking the sim the same question the click will ask
    /// means the ghost can never promise a placement the build then refuses.
    private void UpdateGhost()
    {
        if (!_build.IsShowing || _holding is null)
        {
            _ghost.Hide();
            return;
        }

        // Over the menu itself the ghost is a lie: the click will be eaten by
        // the panel, so previewing a placement it will never make is worse than
        // showing nothing.
        if (_build.GetGlobalRect().HasPoint(GetViewport().GetMousePosition()))
        {
            _ghost.Hide();
            return;
        }

        if (!TileUnderCursor(out var tileX, out var tileY))
        {
            _ghost.Hide();
            return;
        }

        var placement = _holding.PlacementAt(tileX, tileY);
        var allowed = _world.CanPlace(placement)
                      && !_world.CoversFluidNode(placement)
                      && !_world.CoversBeltTile(placement)
                      && (_holding.Kind != BuildKind.Miner
                          || _world.Ground.TryResourceAt(tileX, tileY, out _, out _));

        _ghost.Show(_holding, tileX, tileY, allowed, _renderer.TileSize);
    }

    private bool TileUnderCursor(out int tileX, out int tileY)
    {
        tileX = tileY = 0;
        var viewport = GetViewport();
        if (viewport is null) return false;

        if (!_rig.TryGroundPoint(viewport.GetMousePosition(), out var point))
            return false;

        tileX = Mathf.FloorToInt(point.X / _renderer.TileSize);
        tileY = Mathf.FloorToInt(point.Z / _renderer.TileSize);
        return true;
    }

    /// Places what the player is holding, and says what happened.
    ///
    /// Every refusal gets its own sentence. A build button that goes dead
    /// without saying why is the single most confusing thing a build UI can do,
    /// which is exactly why the sim returns a reason rather than a bool.
    public void PlaceHeld(int tileX, int tileY)
    {
        if (_holding is null)
        {
            Say("Pick something to build first.");
            return;
        }

        var result = _world.TryBuild(_buildables, _holding.Item, tileX, tileY,
                                     _holdingRecipe, _facing);

        Say(result switch
        {
            BuildResult.Ok => $"Built {_holding.DisplayName} at {tileX},{tileY}.",
            BuildResult.Blocked => "Something is already there.",
            BuildResult.NoneCarried => $"You have no {_holding.DisplayName} left.",
            BuildResult.NoResource => "A miner needs ore under it.",
            BuildResult.NoFluid => "A pump needs water or a fluid deposit under it.",
            BuildResult.TooFarToTunnel =>
                $"Too far: a {_holding.DisplayName} tunnels {_holding.UndergroundReach} tiles.",
            BuildResult.NeedsRecipe => $"Choose what the {_holding.DisplayName} should make.",
            BuildResult.NotResearched =>
                "Not researched yet -- deliver the tier's machine hulls to the Uplink.",
            BuildResult.NotPlaceableYet => $"Nothing places a {_holding.DisplayName} yet.",
            _ => $"Cannot build a {_holding.DisplayName}.",
        });

        // Refresh either way: a successful build changes the count beside the
        // entry, and a failed one may have been the last of its kind anyway.
        _build.Refresh();
    }

    /// Capture path: open build mode holding something, with the ghost parked
    /// on a tile, so a screenshot shows the menu and the preview together
    /// rather than proving only that the menu exists.
    private void OpenBuildForCapture()
    {
        // Hand over one of everything placeable, so the menu has a real list
        // rather than the one bench a new game carries.
        foreach (var buildable in _buildables.Offerable)
            _world.PlayerInventory.Add(buildable.Item, 3);

        StartBuilding();

        if (_holding is null) return;

        // Somewhere it would actually be allowed, so the capture shows the
        // ordinary case. The refusal colour is covered by the unit tests.
        for (var d = 0; d < 40; d++)
        {
            var placement = _holding.PlacementAt(d, -4);
            if (!_world.CanPlace(placement) || _world.CoversFluidNode(placement)) continue;

            _ghost.Show(_holding, d, -4, true, _renderer.TileSize);
            return;
        }
    }

    /// Capture path for the Uplink: put one down, give it something research
    /// wants and something it does not, and open the panel on it. Both halves
    /// matter -- the want list and the hopper of refused items are the two
    /// things this panel says that no other panel does.
    private void ShowTheUplink()
    {
        _quests.Close();

        var uplink = _buildables.Find(Research.UplinkItem);
        if (uplink is null || _world.Research is null) return;

        var recipe = _buildables.RecipesFor(uplink, _world.Research).FirstOrDefault();
        if (recipe is null) return;

        _world.PlayerInventory.Add(uplink.Item, 1);
        for (var d = 0; d < 40; d++)
            if (_world.TryBuild(_buildables, uplink.Item, d, -4, recipe) == BuildResult.Ok)
            {
                var index = _world.MachineCount - 1;
                var placement = _world.PlacementOf(index);

                if (_world.Items.TryGetId("iron_ingot", out var spare))
                    _world.Machines[index].PushInput(spare, 5);

                _rig.Position = new Vector3(placement.CentreX * _renderer.TileSize, 0f,
                                            placement.CentreY * _renderer.TileSize);
                _rig.ZoomLevel = 18f;
                _rig.Apply();
                _panel.Show(_world.Machines[index], placement, index);
                return;
            }
    }

    /// Points the camera at the belt line and closes the panel, so a capture
    /// shows the belts rather than a corner of them behind a GUI.
    private void FrameTheBelts()
    {
        _panel.Close();

        // Prefer a tunnel when there is one: an underground end is the piece
        // whose drawing is hardest to confirm, and a capture centred on the
        // middle of a plain run will not contain one.
        var map = _world.BeltMap;
        for (var i = 0; i < map.Undergrounds.Count; i++)
        {
            var partner = map.PartnerOf(i);
            if (partner < 0) continue;

            var a = map.Undergrounds[i];
            var b = map.Undergrounds[partner];
            _rig.Position = new Vector3((a.X + b.X + 1) * 0.5f * _renderer.TileSize, 0f,
                                        (a.Y + b.Y + 1) * 0.5f * _renderer.TileSize);
            _rig.ZoomLevel = 22f;
            return;
        }

        if (map.Belts.Count == 0) return;

        var belt = map.Belts[map.Belts.Count / 2];
        _rig.Position = new Vector3((belt.X + 0.5f) * _renderer.TileSize, 0f,
                                    (belt.Y + 0.5f) * _renderer.TileSize);
        _rig.ZoomLevel = 16f;
    }

    private void Say(string message)
    {
        _toast = message;
        _toastFrames = 180;
    }

    private void QuickSave()
    {
        try
        {
            GameSession.Save("quicksave");
            _toast = "Quick saved.";
        }
        catch (System.Exception e)
        {
            _toast = "Could not save: " + e.Message;
            GD.PushWarning($"quick save failed: {e.Message}");
        }

        _toastFrames = 180;
    }

    private void QuickLoad()
    {
        try
        {
            var path = GameSession.PathFor("quicksave");
            var world = GameSession.Load(path);

            // Swap the world under the renderer rather than rebuilding the
            // scene: the renderer holds no state of its own, so this is safe and
            // keeps the camera where the player left it.
            _world = world;
            _panel.Close();
            _panel.Bind(_world.Items, _world.PlayerInventory, _world, _buildables);
            _build.Bind(_buildables, _world, _world.Items);
            _quests.Bind(_world);
            _seedAnnounced = _world.Research?.SeedDelivered ?? false;
            _renderer.Sync(_world);
            _toast = "Quick loaded.";
        }
        catch (System.Exception e)
        {
            _toast = "Could not load: " + e.Message;
            GD.PushWarning($"quick load failed: {e.Message}");
        }

        _toastFrames = 180;
    }

    private PauseMenu BuildPauseMenu()
    {
        var layer = new CanvasLayer { Name = "PauseLayer" };
        var menu = GD.Load<PackedScene>("res://scenes/pause_menu.tscn").Instantiate<PauseMenu>();
        menu.ResumeRequested += () => menu.Close();
        menu.MainMenuRequested += () => EmitSignal(SignalName.ReturnToMenuRequested);
        layer.AddChild(menu);
        AddChild(layer);
        return menu;
    }

    private ScriptEditor BuildScriptEditor()
    {
        var layer = new CanvasLayer { Name = "EditorLayer" };
        var root = GD.Load<PackedScene>("res://scenes/script_editor.tscn").Instantiate<Control>();
        var editor = root.GetNode<ScriptEditor>("Card");
        editor.Closed += CloseEditor;
        layer.AddChild(root);
        AddChild(layer);
        return editor;
    }

    private void OpenEditor()
    {
        _panel.Close();
        _editor.Open(_world);

        // The camera reads the keyboard directly, so it has to be told to stop
        // while there is somewhere to type. Otherwise writing "was" pans the
        // map out from under the player.
        _rig.InputEnabled = false;
    }

    private void CloseEditor()
    {
        _editor.Close();
        _rig.InputEnabled = true;
    }

    private MachinePanel BuildPanel()
    {
        var layer = new CanvasLayer { Name = "Ui" };
        var panel = GD.Load<PackedScene>("res://scenes/machine_panel.tscn")
                      .Instantiate<MachinePanel>();
        layer.AddChild(panel);
        AddChild(layer);
        panel.Bind(_world.Items, _world.PlayerInventory, _world, _buildables);
        return panel;
    }

    private QuestPanel BuildQuestPanel()
    {
        var layer = new CanvasLayer { Name = "QuestUi" };
        var panel = GD.Load<PackedScene>("res://scenes/quest_panel.tscn").Instantiate<QuestPanel>();
        panel.Bind(_world);
        layer.AddChild(panel);
        AddChild(layer);
        return panel;
    }

    private Label BuildHud()
    {
        var layer = new CanvasLayer { Name = "Hud" };
        var label = new Label
        {
            Name = "Stats",
            Position = new Vector2(12, 8),
        };
        layer.AddChild(label);
        AddChild(layer);
        return label;
    }

    /// Godot splits engine args from anything after "--", so check both.
    private static string[] AllArgs() =>
        OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();

    private static int ReadIntArg(string name, int fallback)
    {
        foreach (var arg in AllArgs())
        {
            if (!arg.StartsWith(name + "="))
                continue;
            if (int.TryParse(arg[(name.Length + 1)..], out var value) && value > 0)
                return value;
        }

        return fallback;
    }
}
