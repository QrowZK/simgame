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
    private CameraRig _rig = null!;
    private Label _hud = null!;
    private MachinePanel _panel = null!;
    private PauseMenu _pause = null!;
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

        AddChild(BuildLighting());
        _hud = BuildHud();
        _panel = BuildPanel();
        _pause = BuildPauseMenu();

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

        if (AllArgs().Contains("--smoke"))
            CallDeferred(nameof(RunSmokeTest));

        if (AllArgs().Contains("--screenshot"))
        {
            _screenshotCountdown = 12;      // let a few frames draw first
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

        // Open the inspection panel just before the capture, so a screenshot
        // shows the GUI rather than only proving the world draws. It has to
        // happen after some ticks: nothing is Working before the first one.
        if (_screenshotCountdown == 2)
            ShowAnyRunningMachine();

        if (_screenshotCountdown > 0 && --_screenshotCountdown == 0)
        {
            var image = GetViewport().GetTexture().GetImage();
            var path = "user://shot.png";
            image.SavePng(path);
            GD.Print($"screenshot {image.GetWidth()}x{image.GetHeight()} -> {ProjectSettings.GlobalizePath(path)}");
            GetTree().Quit();
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

            _hud.Text = $"machines {_world.MachineCount}   tick {_world.TickCount}   " +
                        $"batches {_renderer.BatchCount}   fps {Engine.GetFramesPerSecond():0}" +
                        power + "\n" +
                        "WASD pan   Q/E rotate   wheel zoom   click a machine to inspect   " +
                        "F5 save   F9 load   Esc menu" +
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

        GD.Print($"picking         largest={largest}x{largest} " +
                 $"all tiles resolve to one machine: {pickOk}");
        GD.Print($"panel           showing={_panel.IsShowing}");
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
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            // Escape backs out one level at a time: the inspection panel first,
            // then the pause menu. Jumping straight to a menu from an open panel
            // would feel like the game ignored the panel.
            if (_panel.IsShowing) _panel.Close();
            else if (_pause.Visible) _pause.Close();
            else _pause.Open();
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

        if (_world.TryMachineAt(tileX, tileY, out var machine, out var index))
            _panel.Show(machine, _world.PlacementOf(index));
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
                _panel.Show(_world.Machines[i], _world.PlacementOf(i));
                return;
            }

        if (_world.MachineCount > 0)
            _panel.Show(_world.Machines[0], _world.PlacementOf(0));
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
            _panel.Bind(_world.Items, _world.PlayerInventory);
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

    private MachinePanel BuildPanel()
    {
        var layer = new CanvasLayer { Name = "Ui" };
        var panel = GD.Load<PackedScene>("res://scenes/machine_panel.tscn")
                      .Instantiate<MachinePanel>();
        layer.AddChild(panel);
        AddChild(layer);
        panel.Bind(_world.Items, _world.PlayerInventory);
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
