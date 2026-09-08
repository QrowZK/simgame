using System.Linq;
using Godot;
using Sim;

namespace Game;

/// A standalone capture harness for art work.
///
/// The game's capture flags live in `Boot`/`GameRoot`, and both are frequently
/// being edited by whoever is wiring gameplay. This scene exists so a renderer
/// or a panel can be shot on a real world without touching either file: it is
/// run directly and quits when it has written its png.
///
///     godot --path game --rendering-driver opengl3 res://scenes/art_preview.tscn \
///         -- --shot=landing --out=/tmp/landing.png
///
/// Shots:
///   `landing`  the opening frame of a new game -- spawn, 30 units of view.
///   `tech`     the tech tree panel over that frame, with a partial research
///              state so locked, open and done are all on screen at once.
///   `player`   the player figure on open ground, and again beside a machine,
///              so the size relationship can be judged rather than assumed.
///              `--facing=N` turns the figure; `--machines` puts the demo
///              factory in the frame next to it.
///
/// It builds the same world `NewGame.Create` gives a player, so what it shows
/// is the game, not a diorama. It draws no HUD of its own and simulates
/// nothing: this is a camera, not a second game loop.
public sealed partial class ArtPreview : Node3D
{
    private int _countdown = 14;
    private string _out = "user://art_preview.png";

    public override void _Ready()
    {
        var args = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();
        var shot = Arg(args, "--shot") ?? "landing";
        _out = Arg(args, "--out") ?? _out;
        var seed = int.TryParse(Arg(args, "--seed"), out var s) ? s : 1234;

        var world = Sim.NewGame.Create(seed, Sim.Data.Catalogue.Instance);
        GameSession.Adopt(world);

        var rig = new CameraRig
        {
            Name = "CameraRig",
            Position = new Vector3(NewGame.SpawnX, 0f, NewGame.SpawnY),
            ZoomLevel = float.TryParse(Arg(args, "--zoom"), out var z) ? z : 30f,
            InputEnabled = false,
        };
        AddChild(rig);
        rig.Apply();

        var terrain = new TerrainRenderer { Name = "TerrainRenderer" };
        AddChild(terrain);
        terrain.Sync(world, rig.Position);

        AddChild(Lighting());

        var site = new LandingSite { Name = "LandingSite" };
        AddChild(site);
        GD.Print($"landing site    pieces={site.PieceCount} tris={site.TriangleCount} " +
                 $"radius={site.Radius:0.0} tiles at {site.TileX},{site.TileY} drawn={site.IsDrawn}");

        if (shot == "player")
        {
            // Two frames answer two different questions, so this shot has two
            // modes. Open ground says "can I find the figure at all"; a corner
            // of the demo factory says "how big is it next to a machine", which
            // is the judgement no amount of describing a mesh can settle.
            var withMachines = args.Contains("--machines");
            var px = withMachines ? 7.5f : NewGame.SpawnX + 6.5f;
            var pz = withMachines ? 7.5f : NewGame.SpawnY + 5.5f;
            var facing = float.TryParse(Arg(args, "--facing"), out var f) ? f : 135f;

            if (withMachines)
            {
                var demo = DemoWorld.Build(24, seed);
                var machines = new MachineRenderer { Name = "MachineRenderer" };
                AddChild(machines);
                machines.Sync(demo);
                GD.Print($"machines        {demo.Placements.Length} drawn");
            }

            var player = args.Contains("--no-player")
                ? null
                : new PlayerRenderer { Name = "PlayerRenderer" };
            if (player is null)
            {
                GD.Print("player          not drawn (--no-player)");
                return;
            }

            AddChild(player);

            // Placed twice, a third of a tile apart, so the frame is taken
            // mid-stride: the bob is exercised rather than only its zero case.
            player.Place(px, pz, facing);
            player.Place(px + 0.31f, pz, facing);

            rig.Position = new Vector3(px, 0f, pz);
            rig.Apply();

            GD.Print($"player          pieces={player.PieceCount} tris={player.TriangleCount} " +
                     $"height={player.Height:0.00} facing={player.FacingDegrees:0} " +
                     $"walked={player.DistanceWalked:0.00} drawn={player.IsDrawn}");
        }

        if (shot == "tech")
        {
            var layer = new CanvasLayer { Name = "TechUi" };
            var panel = GD.Load<PackedScene>("res://scenes/tech_tree.tscn").Instantiate<TechTreePanel>();
            layer.AddChild(panel);
            AddChild(layer);

            // A partial state, because the interesting picture is the one with
            // researched, ready, open and locked all on screen at once. One ore
            // delivered finishes the first rung; the ingots in hand make the
            // second one deliverable without finishing it.
            Deliver(world, "cassiterite", 1);
            Hand(world, "copper_ingot", 4);

            panel.Bind(world);
            // `--premise` shoots the new-game opening of the same screen: the
            // premise pushes the graph down, and a layout that only survives
            // without it is not the layout a new player sees.
            if (args.Contains("--premise")) panel.OpenWithPremise();
            else panel.Open();
            GD.Print($"tech panel      {panel.BodyText.Length} chars, " +
                     $"{panel.BodyText.Split('\n').Length} lines");
        }
    }

    /// Delivers by hand, the way a player would through the Uplink, so the
    /// research state in the shot is one the sim actually produced.
    private static void Deliver(World world, string item, int count)
    {
        var report = world.Research?.Deliver(item, count);
        GD.Print($"delivered       {item} x{count} accepted={report?.Accepted ?? 0}");
    }

    /// Puts items in the player's hands, so a node can be shown in the state
    /// that says "you are carrying this, go and deliver it" -- which is the
    /// state a screenshot of the panel most needs to prove is distinguishable.
    private static void Hand(World world, string item, int count)
    {
        if (world.Items.TryGetId(item, out var id))
            world.PlayerInventory.Add(id, count);
        GD.Print($"handed          {item} x{count}");
    }

    public override void _Process(double delta)
    {
        if (--_countdown > 0) return;

        var probe = GetNodeOrNull<Control>("TechUi/TechTreePanel/Margin/Rows/Body/GraphHolder");
        if (probe is not null)
            GD.Print($"graph box       holder={probe.Size} graph={probe.GetChild<Control>(0).Size} " +
                     $"panel={probe.GetParent().GetParent().GetParent<Control>().Size}");

        // What a new pool actually costs, measured rather than argued: run the
        // same shot with and without `--player` and diff this number.
        GD.Print("draw batches    " +
                 RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));

        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(_out);
        GD.Print($"art preview     {image.GetWidth()}x{image.GetHeight()} -> " +
                 $"{ProjectSettings.GlobalizePath(_out)}");
        GetTree().Quit();
    }

    private static string? Arg(string[] args, string name)
    {
        foreach (var arg in args)
            if (arg.StartsWith(name + "="))
                return arg[(name.Length + 1)..];
        return null;
    }

    /// The same sun and ambient the game uses. A preview lit differently from
    /// the game is worse than no preview: it hides exactly the contrast
    /// problems it is meant to find.
    private static Node Lighting()
    {
        var holder = new Node3D { Name = "Lighting" };
        var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.1f };
        sun.RotationDegrees = new Vector3(-55f, -40f, 0f);
        holder.AddChild(sun);
        holder.AddChild(new WorldEnvironment
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
        });
        return holder;
    }
}
