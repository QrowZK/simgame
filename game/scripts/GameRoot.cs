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

    /// The world this root is actually running, once _Ready has built it. Used
    /// by the headless menu test, which has to inspect what the New Game button
    /// produced rather than what it was handed.
    public World? World => _world;

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
    private Label _guide = null!;
    private MachinePanel _panel = null!;
    private PauseMenu _pause = null!;
    private BuildMenu _build = null!;
    private TechTreePanel _progression = null!;
    private SurveyPanel _survey = null!;
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

    /// Whether the next click takes something back rather than inspecting it.
    ///
    /// A mode rather than a modifier-click, and mutually exclusive with build
    /// mode, because both own the left button and a player needs to be able to
    /// see which one is armed before they click. X is next to the movement
    /// keys, which is where a key you press between placements has to be.
    private bool _removing;
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
        _guide = BuildGuideCard();
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
        _progression = BuildProgressionPanel();
        _survey = BuildSurveyPanel();
        _editor = BuildScriptEditor();

        // A brand new game, and only a brand new game: the premise is shown at
        // tick zero on an untouched world, so a loaded save never replays it.
        if (_world.TickCount == 0 && _world.MachineCount == 0 && _world.Research is not null)
            _progression.OpenWithPremise();

        // Frame whatever there is to look at. A new game has no factory, so the
        // camera sits on the landing site at a zoom where the ground around it
        // reads as terrain rather than as a texture.
        if (machineCount == 0)
        {
            // Off the wreck, not on it. The site is nearly fifteen tiles across
            // and it sits on the landing point, so a camera centred there fills
            // the screen with debris and the first thing the player builds gets
            // lost in it -- an Uplink four tiles out could not be picked out of
            // the wreckage at all. Looking a little past it puts the wreck in
            // the upper corner, where the eye still goes to it, and leaves the
            // ground the guide sends you to build on clear and in frame.
            _rig.Position = new Vector3(NewGame.SpawnX + 6f, 0f, NewGame.SpawnY + 6f);

            // Close enough that the first thing a player sees is a place.
            //
            // This was 78, chosen to show "the lie of the land and the nearest
            // ore". It showed neither: at 78 units of view a machine is eight
            // pixels, ground detail averages into a flat sheet, and the opening
            // frame of the game is an empty green field. The nearest ore is
            // 12-40 tiles away and is the prospector's job to find, not the
            // camera's -- the camera's job is to make the ground look like
            // somewhere you are standing.
            _rig.ZoomLevel = 30f;
        }
        else
        {
            var side = Mathf.CeilToInt(Mathf.Sqrt(machineCount));
            _rig.Position = new Vector3(side * 0.5f, 0f, side * 0.5f);
            _rig.ZoomLevel = Mathf.Clamp(side * 1.4f, 8f, 160f);
        }

        _rig.Apply();

        // The crash site. Scenery, not a machine: it is not in `World`, holds no
        // tiles, and a player can build straight through it. It exists because
        // the premise says a probe came apart here and, until now, the place it
        // came apart at was an empty patch of grass -- the most important moment
        // in the game had nothing on screen at all.
        //
        // Only in a world that is being played. The demo factory is a renderer
        // benchmark whose spawn is covered in machines, and a wreck under them
        // would be scenery in the middle of a measurement.
        if (_world.Research is not null)
        {
            AddChild(new LandingSite { Name = "LandingSite" });
        }

        _renderer.Sync(_world);
        _terrain.Sync(_world, _rig.Position);
        _poles.Sync(_world);
        _poles.SyncFluids(_world);
        _drones.Sync(_world);
        _belts.Sync(_world, _renderer.TileSize);

        if (AllArgs().Contains("--smoke"))
            CallDeferred(nameof(RunSmokeTest));

        if (AllArgs().Contains("--screenshot") || AllArgs().Contains("--build-shot")
            || AllArgs().Contains("--belt-shot") || AllArgs().Contains("--uplink-shot")
            || AllArgs().Contains("--survey-shot") || AllArgs().Contains("--shore-shot")
            || AllArgs().Contains("--dig-shot") || AllArgs().Contains("--opening-shot")
            || AllArgs().Contains("--pause-shot"))
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

            // A capture still has to finish while the world is paused: the
            // pause menu is itself a thing worth photographing, and arming a
            // shot of it used to hang forever because the countdown lived past
            // this return.
            if (_screenshotCountdown > 0 && --_screenshotCountdown == 0) Capture();
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

        // Open the inspection panel before the capture, so a screenshot shows
        // the GUI rather than only proving the world draws. It has to happen
        // after some ticks: nothing is Working before the first one.
        //
        // Four frames of margin, not one. `GetViewport().GetTexture()` returns
        // the frame that was *already drawn* when _Process runs, so anything
        // set up here is invisible to a capture taken in the same pass -- a
        // machine placed for the shot rendered as empty ground, and the shot
        // looked like a renderer bug for a while. The gap has to cover the
        // world change, the next Sync, and the draw that follows it.
        if (_screenshotCountdown == 4)
        {
            if (AllArgs().Contains("--build-shot")) OpenBuildForCapture();
            else if (AllArgs().Contains("--belt-shot")) FrameTheBelts();
            else if (AllArgs().Contains("--uplink-shot")) ShowTheUplink();
            else if (AllArgs().Contains("--survey-shot")) ShowTheSurvey();
            else if (AllArgs().Contains("--dig-shot")) DigByHandForCapture();
            else if (AllArgs().Contains("--opening-shot")) _progression.Close();
            else if (AllArgs().Contains("--pause-shot"))
            {
                _progression.Close();
                _pause.Open();
            }
            else if (AllArgs().Contains("--shore-shot")) FrameTheShore();
            else ShowAnyRunningMachine();
        }

        if (_screenshotCountdown > 0 && --_screenshotCountdown == 0) Capture();

        UpdateGhost();

        // The win, announced once. Nothing is taken away and nothing stops: a
        // factory game whose ending closes the factory has punished the player
        // for finishing it.
        if (!_seedAnnounced && _world.Research is { SeedDelivered: true })
        {
            _seedAnnounced = true;
            Say("The Seed is away. It will come apart on entry somewhere else, and start again.");
            _progression.Open();
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
                        power + stored + "\n" + Keys() +
                        (_toastFrames > 0 ? "\n" + _toast : "");

            RefreshGuide();
        }
    }


    private void Capture()
    {
        var image = GetViewport().GetTexture().GetImage();
        var path = "user://shot.png";
        image.SavePng(path);
        GD.Print($"screenshot {image.GetWidth()}x{image.GetHeight()} -> " +
                 ProjectSettings.GlobalizePath(path));
        GetTree().Quit();
    }

    /// The keys worth naming *right now*.
    ///
    /// This line used to list all twelve bindings in the game, always, whatever
    /// the player was doing. That is a reference card, and a reference card
    /// pinned to the top of the screen is read once and then becomes furniture
    /// -- while still costing a new player the width of the window to scan.
    ///
    /// A mode names its own exits and nothing else. Outside a mode the line
    /// stays short enough to actually read, and the rarely-used bindings live
    /// where they belong: on the pause menu, which is where a player goes when
    /// they want to know what a game can do.
    private string Keys()
    {
        if (_build.IsShowing)
            return _holding is null
                ? "pick something to build   ·   Esc  stop building"
                : $"click a tile to place {_holding.DisplayName}   ·   R  rotate   ·   " +
                  "Esc  stop building";

        if (_removing)
            return "click a building to take it back   ·   Esc  stop removing";

        if (_survey.IsShowing) return "P  close survey";
        if (_progression.IsShowing) return "T  close progression";
        if (_panel.IsShowing) return "click another machine to inspect it   ·   Esc  close";

        return "WASD  pan   ·   wheel  zoom   ·   Q/E  rotate   ·   B  build   ·   " +
               "P  survey   ·   T  progression   ·   Esc  menu";
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
        // Warm first, then measure a genuinely cold field.
        //
        // Timed naively this number says almost nothing: the first rebuild in a
        // process pays JIT for the whole describe-and-colour chain and measured
        // 95 ms, while the same code a moment later measured 31 ms. CI asserts a
        // ceiling on it, so the same commit went green on one runner and red on
        // another. A rebuild after a small camera move is no better -- it is
        // mostly cache hits, and measures the cache.
        //
        // So: rebuild twice to warm the path, drop the tile cache, and time the
        // full field. Warm code, cold cache, which is what "the cost of
        // describing the whole field" means.
        _terrain.Sync(_world, _rig.Position + new Vector3(1f, 0f, 1f));
        _terrain.Sync(_world, _rig.Position + new Vector3(2f, 0f, 2f));
        _terrain.ForgetCachedTiles();

        var terrainWatch = System.Diagnostics.Stopwatch.StartNew();
        _terrain.Sync(_world, _rig.Position + new Vector3(3f, 0f, 3f));
        terrainWatch.Stop();

        var field = _terrain.ViewRadius * 2 + 1;
        GD.Print($"terrain rebuild {field}x{field} tiles in " +
                 $"{terrainWatch.Elapsed.TotalMilliseconds:0.0} ms");

        // Water is recessed into real pools, and a still cannot prove that: a
        // dark blue plate and a hole in the ground look alike from above. The
        // count and the deepest recess say it in numbers instead -- measured
        // over the coast, because spawn is deliberately nowhere near it and a
        // field with no water in it reports nothing either way.
        var coast = TerrainRenderer.NearestWater(_world, _rig.Position);
        if (coast is { } shore) _terrain.Sync(_world, shore);
        GD.Print($"terrain water   tiles={_terrain.WaterTiles} " +
                 $"deepest={_terrain.DeepestWater:0.00} below land");
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
        _progression.Refresh();
        var site = new LandingSite { Name = "SmokeLandingSite" };
        AddChild(site);
        GD.Print($"landing site    pieces={site.PieceCount} tris={site.TriangleCount} " +
                 $"radius={site.Radius:0.0} drawn={site.IsDrawn}");

        GD.Print($"tech panel      {_progression.BodyText.Length} chars, " +
                 $"{_progression.BodyText.Split('\n').Length} lines, " +
                 $"ready={_progression.CountOf(TechTreePanel.NodeState.Ready)} " +
                 $"locked={_progression.CountOf(TechTreePanel.NodeState.Locked)}");

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
            else if (_removing) StopRemoving();
            else if (_progression.IsShowing) _progression.Close();
            else if (_survey.IsShowing) _survey.Close();
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
            if (_progression.IsShowing) _progression.Close();
            else _progression.Open();
            return;
        }

        // Surveyed from where the player is looking, not from the origin. The
        // device is carried, so walking somewhere and asking again is the whole
        // interaction -- a panel that always answered about spawn would be a
        // map, and a map is the thing this game deliberately does not hand out.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.P })
        {
            if (_survey.IsShowing) _survey.Close();
            else _survey.Open(Mathf.FloorToInt(_rig.Position.X / _renderer.TileSize),
                              Mathf.FloorToInt(_rig.Position.Z / _renderer.TileSize));
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.B })
        {
            if (_build.IsShowing) StopBuilding();
            else StartBuilding();
            return;
        }

        // X arms removal. Build mode is turned off rather than layered under
        // it: both modes own the left button, and a click that could either
        // place or destroy depending on state nobody can see is how a player
        // loses a machine they did not mean to touch.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.X })
        {
            if (_removing) StopRemoving();
            else StartRemoving();
            return;
        }

        // Right-click leaves removal mode too, for the same reason it leaves
        // build mode.
        if (_removing && @event is InputEventMouseButton
            { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            StopRemoving();
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

        if (_removing)
        {
            RemoveAt(tileX, tileY);
            return;
        }

        if (_world.TryMachineAt(tileX, tileY, out var machine, out var index))
            _panel.Show(machine, _world.PlacementOf(index), index);
        else if (_world.TryMinerAt(tileX, tileY, out var miner, out var minerIndex))
            _panel.Show(miner, _world.MinerPlacements[minerIndex],
                        _world.Ground.RemainingAt(tileX, tileY));
        else
            DigOrClose(tileX, tileY);
    }

    /// How much one click takes out of the ground by hand. Small on purpose:
    /// the first furnace wants 24 stone, so a bench costs a handful of clicks
    /// and a smelting run costs a few more. That is the ache the belt is the
    /// answer to, and removing it would remove the argument for the rest of
    /// the game.
    private const int HandMinePerClick = 5;

    /// Clicking bare ground.
    ///
    /// Hand mining existed in the simulation from the first week and was never
    /// bound to anything: the headless tests called `HandOps.Mine` directly and
    /// passed, the opening-route walker called it and passed, and a person
    /// sitting in front of the game had no way to dig at all. The whole
    /// documented opening -- walk to a patch, mine it by hand, craft a bench --
    /// was impossible to actually perform.
    ///
    /// So a click on a resource tile digs it. Refusals carry their reason, as
    /// everywhere else: a fluid deposit says the hands cannot lift it and names
    /// what can, and a worked-out patch says it is finished rather than doing
    /// nothing and looking broken.
    private void DigOrClose(int tileX, int tileY)
    {
        if (!_world.Ground.TryResourceAt(tileX, tileY, out var item, out var remaining))
        {
            _panel.Close();
            return;
        }

        var name = _world.Items.GetName(item);

        if (remaining <= 0)
        {
            Say($"This {name} patch is worked out. Press P to survey for another.");
            return;
        }

        if (_world.Ground.Gen.TryPatchAt(tileX, tileY, out var patch) && patch.IsFluid)
        {
            Say($"{name} is a fluid -- hands cannot lift it. It needs a derrick standing on it.");
            return;
        }

        var before = _world.PlayerInventory.Count(item);
        var dug = HandOps.Mine(_world.Ground, tileX, tileY, _world.PlayerInventory,
                               HandMinePerClick);

        Say(dug == 0
            ? $"Nothing came out of this {name}."
            : $"Dug {dug} {name}. Carrying {before + dug}. ({remaining - dug} left here.)");
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
        if (_removing)
        {
            UpdateRemovalGhost();
            return;
        }

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

    public void StartRemoving()
    {
        if (_build.IsShowing) StopBuilding();
        _panel.Close();
        _removing = true;
        Say("Removal: click a building to take it back. X or Esc to stop.");
    }

    public void StopRemoving()
    {
        _removing = false;
        _ghost.Hide();
        Say("Removal off.");
    }

    /// The ghost, standing on what the click would take rather than on what it
    /// would place.
    ///
    /// The same ghost, deliberately. It already draws a footprint in the mesh
    /// of the thing it represents, and the question a player asks before a
    /// removal click is the one it already answers: *which* building is under
    /// my cursor, and will this click do anything. Green still means the click
    /// works and red still means it will be refused, so nothing has to be
    /// relearned for the second mode.
    private void UpdateRemovalGhost()
    {
        if (!TileUnderCursor(out var tileX, out var tileY))
        {
            _ghost.Hide();
            return;
        }

        if (!_world.TryRemovableAt(tileX, tileY, out var item, out var anchorX, out var anchorY)
            || !_buildables.TryGet(item, out var buildable))
        {
            _ghost.Hide();
            return;
        }

        _ghost.Show(buildable, anchorX, anchorY, true, _renderer.TileSize);
    }

    /// Takes back what is under the cursor, and says what came with it.
    ///
    /// Every refusal gets its own sentence, as with building. The counts are
    /// said out loud rather than left to the player to notice: a removal that
    /// hands back eleven items and mentions none of them is indistinguishable
    /// from one that ate them.
    public void RemoveAt(int tileX, int tileY)
    {
        var report = _world.TryRemove(tileX, tileY);

        var name = report.Ok && _buildables.TryGet(report.Item, out var buildable)
            ? buildable.DisplayName
            : "building";

        Say(report.Result switch
        {
            RemoveResult.Ok => Removed(name, report),
            RemoveResult.NothingThere => "Nothing of yours is there.",
            RemoveResult.UnknownBuilding =>
                "That was not built from anything you carried, so there is nothing to give back.",
            _ => "That cannot be removed.",
        });

        // The panel may have been showing the machine that just went, or one
        // whose index moved when the arrays closed up behind it.
        _panel.Close();
        _build.Refresh();
    }

    private static string Removed(string name, RemovalReport report)
    {
        var text = $"Took back the {name}";
        if (report.Returned > 0) text += $" and {report.Returned} item(s) inside it";
        if (report.FluidVoided > 0) text += $"; {report.FluidVoided} fluid drained away";
        if (report.Spilled > 0) text += $"; {report.Spilled} item(s) fell off the belt";
        return text + ".";
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
        // One panel at a time: the objectives panel covers the build menu, and
        // a capture of two overlapping panels shows neither of them properly.
        _progression.Close();

        // Hand over one of everything placeable, so the menu has a real list
        // rather than the one bench a new game carries.
        foreach (var buildable in _buildables.Offerable)
            _world.PlayerInventory.Add(buildable.Item, 3);

        StartBuilding();

        if (_holding is null) return;

        // Close enough that the preview reads. At the default zoom the ghost
        // was four pixels of green and proved nothing.
        _rig.ZoomLevel = 16f;
        _rig.Apply();

        // The cursor drives the preview, so the capture moves the cursor rather
        // than placing a ghost directly: `UpdateGhost` re-derives it from the
        // mouse every frame and would drop anything shown here on the next one.
        //
        // Clear of the menu, deliberately. The ghost is hidden while the
        // pointer is over the build panel -- a preview of a click the panel
        // will eat -- so a cursor parked at screen centre, which is inside the
        // panel's rect, produced a capture with no ghost in it at all.
        Input.WarpMouse(new Vector2(GetViewport().GetVisibleRect().Size.X * 0.72f,
                                    GetViewport().GetVisibleRect().Size.Y * 0.22f));
    }

    /// Capture path for hand mining, driven through the real input handler.
    ///
    /// It synthesises an actual left click at an actual screen position rather
    /// than calling the dig directly, because calling the dig directly is
    /// exactly what every test did while the game had no way to dig at all.
    /// The thing worth proving is that a person clicking a patch gets ore.
    private void DigByHandForCapture()
    {
        _progression.Close();

        var usable = _world.Research?.ConsumableNow(_world.Items);
        var hits = new Prospector(radius: 400).Scan(_world.Ground.Gen,
                                                   NewGame.SpawnX, NewGame.SpawnY, usable);

        // A solid patch: hands cannot lift a fluid, and the refusal for that is
        // its own message rather than the thing being demonstrated here.
        var target = hits.FindIndex(h => !(_world.Ground.Gen.TryPatchAt(h.X, h.Y, out var p)
                                           && p.IsFluid));
        if (target < 0) return;

        var hit = hits[target];
        _rig.Position = new Vector3(hit.X * _renderer.TileSize, 0f, hit.Y * _renderer.TileSize);
        _rig.ZoomLevel = 18f;
        _rig.Apply();

        var screen = _rig.Camera.UnprojectPosition(
            new Vector3(hit.X * _renderer.TileSize, 0f, hit.Y * _renderer.TileSize));

        Input.WarpMouse(screen);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = screen,
            GlobalPosition = screen,
        });

        GD.Print($"dig target      {_world.Items.GetName(hit.Item)} at {hit.X},{hit.Y}");
    }

    /// Capture path for the survey device: open it where a new game starts, so
    /// the shot shows the list a player actually gets on their first minute --
    /// including whether the top of it is marked usable (ADR 0026).
    private void ShowTheSurvey()
    {
        _progression.Close();
        _survey.Open(NewGame.SpawnX, NewGame.SpawnY);
    }

    /// Capture path for the Uplink: put one down, give it something research
    /// wants and something it does not, and open the panel on it. Both halves
    /// matter -- the want list and the hopper of refused items are the two
    /// things this panel says that no other panel does.
    private void ShowTheUplink()
    {
        _progression.Close();

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
    /// Capture path for the coastline. Worldgen lifts the whole home region
    /// out of the sea so a new game is playable, so every other capture in this
    /// file is inland and the shoreline -- the one place this renderer puts
    /// relief -- appears in none of them. Walks out to the nearest water and
    /// frames it close enough that the bank and the pool floor are both legible.
    private void FrameTheShore()
    {
        _panel.Close();
        _progression.Close();

        var coast = TerrainRenderer.NearestWater(_world, _rig.Position);
        if (coast is not { } shore) return;

        _rig.Position = shore;
        _rig.ZoomLevel = 48f;
        _rig.Apply();
        _terrain.Sync(_world, _rig.Position);
    }

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

            // Zoom is the orthographic size, and nothing re-reads it until the
            // rig is applied -- setting it alone left the belt capture at the
            // default framing, too far out to see a tunnel end.
            _rig.Apply();
            return;
        }

        if (map.Belts.Count == 0) return;

        var belt = map.Belts[map.Belts.Count / 2];
        _rig.Position = new Vector3((belt.X + 0.5f) * _renderer.TileSize, 0f,
                                    (belt.Y + 0.5f) * _renderer.TileSize);
        _rig.ZoomLevel = 16f;
        _rig.Apply();
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
            _progression.Bind(_world);
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

    private SurveyPanel BuildSurveyPanel()
    {
        var layer = new CanvasLayer { Name = "SurveyUi" };
        var panel = GD.Load<PackedScene>("res://scenes/survey_panel.tscn")
                      .Instantiate<SurveyPanel>();
        panel.Bind(_world);
        layer.AddChild(panel);
        AddChild(layer);
        return panel;
    }

    private TechTreePanel BuildProgressionPanel()
    {
        var layer = new CanvasLayer { Name = "ProgressionUi" };
        var panel = GD.Load<PackedScene>("res://scenes/tech_tree.tscn")
                      .Instantiate<TechTreePanel>();
        panel.Bind(_world);
        layer.AddChild(panel);
        AddChild(layer);
        return panel;
    }

    /// The one thing to do next, on screen, always.
    ///
    /// Not a tutorial: it never disables a control, never waits for a keypress
    /// and never blocks. `Sim.Guide` reads the step out of world state, so a
    /// player who builds the furnace before delivering the ore, or lays a belt
    /// nobody asked for, is not corrected -- the card simply says the next
    /// thing that is still undone. Wube's post-mortem on the Factorio tutorial
    /// they deleted is the argument: constrain the player's actions and they
    /// learn to solve the tutorial rather than the game.
    ///
    /// It removes itself when the ladder is finished. From there the
    /// progression screen is the guidance, which is where a game this size
    /// keeps it.
    private Label BuildGuideCard()
    {
        var layer = new CanvasLayer { Name = "GuideUi" };
        var label = new Label
        {
            Name = "Guide",
            Position = new Vector2(12, 84),
            CustomMinimumSize = new Vector2(430, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        layer.AddChild(label);
        AddChild(layer);
        return label;
    }

    /// Repaints the guide card from the world. Cheap enough to do per frame --
    /// it is a handful of integer comparisons -- and doing it per frame is what
    /// makes it react the instant the player finishes a step, which is the only
    /// moment the card has to be right.
    private void RefreshGuide()
    {
        var step = Sim.Guide.Current(_world);
        if (step is not { } now)
        {
            _guide.Visible = false;
            return;
        }

        _guide.Visible = true;
        _guide.Text = string.IsNullOrEmpty(now.Key)
            ? $"NEXT   {now.Title}\n{now.Detail}"
            : $"NEXT   {now.Title}   [{now.Key}]\n{now.Detail}";
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
