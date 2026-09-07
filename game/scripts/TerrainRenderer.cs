using Godot;
using Sim;

namespace Game;

/// Draws the ground.
///
/// Until now the renderer only drew machines, which was survivable when a new
/// game started with a factory and fatal the moment it started with nothing:
/// the player landed in an empty void with no way to see where the ore was.
///
/// One flat tile mesh, instanced per visible tile, coloured by terrain and
/// tinted where there is ore. Same MultiMesh approach as the machines, for the
/// same reason -- a per-tile node graph would cost more than the whole sim.
public sealed partial class TerrainRenderer : Node3D
{
    /// Tiles drawn either side of the camera focus. 96 gives a 193x193 field --
    /// about 37,000 instances in one draw call.
    ///
    /// It has to exceed the default zoom by a wide margin, because the camera is
    /// rotated 45 degrees and a square field seen corner-on shows its edges long
    /// before its sides. Shrinking it to fit instead pulled the view in so far
    /// that the nearest ore fell off the screen, which defeats the point of
    /// drawing the ground at all.
    public int ViewRadius { get; set; } = 96;

    private const int FloatsPerInstance = 16;

    private MultiMeshInstance3D _pool = null!;
    private float[] _buffer = System.Array.Empty<float>();

    /// The tile the field was last built around. Rebuilding every frame would
    /// re-evaluate noise for 9,400 tiles 60 times a second; the field only has
    /// to move when the camera does.
    private int _builtX = int.MinValue;
    private int _builtY = int.MinValue;
    private int _builtDug = -1;

    /// Finished tile colours, keyed by tile.
    ///
    /// A rebuild is triggered by the camera crossing a single tile boundary,
    /// and then re-evaluates the whole 193x193 field -- of which all but two
    /// strips were already on screen the frame before. Evaluating the
    /// worldgen's noise 37,000 times for that cost 24 ms before this renderer
    /// shaded anything, which is a stutter every time the view creeps sideways.
    ///
    /// Caching by tile makes the second and every later rebuild a dictionary
    /// probe. It is correct to cache because the inputs are pure functions of
    /// the seed and the tile: the only thing that can change a tile's colour is
    /// mining it out, and that already forces a rebuild by its own counter, so
    /// the cache is dropped there.
    private readonly System.Collections.Generic.Dictionary<long, Color> _colours = new();

    /// Panning far enough, for long enough, would otherwise grow the cache
    /// without bound. Dropping the lot costs one expensive rebuild, which is
    /// what the player would have paid anyway had they never come back.
    private const int MaxCachedTiles = 400_000;

    public override void _Ready()
    {
        // Slightly under a tile, so the seams read as a grid. The game is
        // played on tile coordinates and hiding that helps nobody -- a player
        // placing a 3x3 wants to count squares.
        var mesh = new BoxMesh { Size = new Vector3(0.94f, 0.08f, 0.94f) };

        _pool = new MultiMeshInstance3D
        {
            Name = "Terrain",
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
            },
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.95f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(_pool);
    }

    /// Rebuilds the visible field if the view has moved or the ground changed.
    public void Sync(World world, Vector3 focus)
    {
        var centreX = Mathf.FloorToInt(focus.X);
        var centreY = Mathf.FloorToInt(focus.Z);

        // Mining changes tile colour, so the count of dug patches is enough of
        // a signal to redraw -- it only changes when ore actually comes out.
        var dug = world.Ground.DepletionCount;

        if (centreX == _builtX && centreY == _builtY && dug == _builtDug)
            return;

        _builtX = centreX;
        _builtY = centreY;
        if (dug != _builtDug || _colours.Count > MaxCachedTiles) _colours.Clear();
        _builtDug = dug;

        var side = ViewRadius * 2 + 1;
        var count = side * side;

        if (_buffer.Length != count * FloatsPerInstance)
            _buffer = new float[count * FloatsPerInstance];

        var cursor = 0;
        for (var dy = -ViewRadius; dy <= ViewRadius; dy++)
            for (var dx = -ViewRadius; dx <= ViewRadius; dx++)
            {
                var x = centreX + dx;
                var y = centreY + dy;
                var key = ((long)x << 32) ^ (uint)y;
                if (!_colours.TryGetValue(key, out var colour))
                    _colours[key] = colour = Colour(world, x, y);

                Write(cursor++, x, y, colour);
            }

        var multiMesh = _pool.Multimesh!;
        if (multiMesh.InstanceCount != count)
            multiMesh.InstanceCount = count;
        multiMesh.Buffer = _buffer;
    }

    /// Terrain colour, with ore showing through.
    ///
    /// Ore is drawn as a tint on the ground rather than as its own object: a
    /// patch is a region you stand on, and a player deciding where to build
    /// needs to see its shape, not a marker at its centre.
    private static Color Colour(World world, int x, int y)
    {
        var gen = world.Ground.Gen;
        var ground = gen.TerrainAt(x, y);

        var basecolour = Vary(gen, ground, x, y, ground switch
        {
            TerrainType.DeepWater => new Color(0.09f, 0.16f, 0.30f),
            TerrainType.Water => new Color(0.15f, 0.28f, 0.45f),
            TerrainType.Sand => new Color(0.62f, 0.57f, 0.40f),
            TerrainType.Grass => new Color(0.26f, 0.42f, 0.24f),
            TerrainType.Rock => new Color(0.40f, 0.40f, 0.42f),
            _ => new Color(0.62f, 0.62f, 0.66f),
        });

        if (!world.Ground.TryPatchAt(x, y, out var patch))
            return basecolour;

        // A worked-out patch still reads as a patch, just a dead one, so the
        // player can see where they have already been.
        var remaining = world.Ground.Remaining(patch);
        var ore = OreColour(patch.Item);
        return remaining > 0 ? basecolour.Lerp(ore, 0.65f) : basecolour.Lerp(ore, 0.18f);
    }

    /// Breaks one flat colour per terrain type into ground that reads as
    /// ground, at three scales at once.
    ///
    /// Every input is a pure function of the world seed and the tile, so the
    /// map looks identical between runs and between machines. That is not
    /// tidiness: screenshots are how rendering is verified here, and a renderer
    /// that rolled its own dice would make every capture a different picture.
    ///
    /// The scales answer different questions. **Height** is the one the
    /// worldgen already computed and the one the player is reading anyway --
    /// high ground dry and pale, low ground dark and damp -- so the shading
    /// follows the same contours that decide where the water is. **Patches**,
    /// about nine tiles across, give a field somewhere to be greener; a single
    /// global ramp leaves large flats as smooth as before. **Grain**, one hash
    /// per tile, stops any two neighbours matching exactly, which is what makes
    /// a zoomed-in floor look painted rather than tiled.
    ///
    /// Water is varied at about a third of the strength. It is a surface rather
    /// than a soil, and the shoreline was not the thing that needed fixing.
    private static Color Vary(WorldGen gen, TerrainType ground, int x, int y, Color colour)
    {
        var amount = ground switch
        {
            TerrainType.DeepWater or TerrainType.Water => 0.35f,
            TerrainType.Sand => 0.80f,
            TerrainType.Mountain => 0.90f,
            _ => 1.0f,
        };

        // [-1, 1] each.
        var lift = gen.HeightAt(x, y) / 127.5f - 1f;
        var patch = (float)(Sim.Noise.Value(gen.Seed ^ 0x5EED, x, y, cell: 9) * 2.0 - 1.0);
        var grain = (Sim.Noise.Hash(gen.Seed + 7919, x, y) & 0xFF) / 127.5f - 1f;

        var value = 1f + amount * (0.15f * lift + 0.13f * patch + 0.05f * grain);

        // A little warmth with the patch as well as brightness: dry ground is
        // not just paler grass, it is browner, and value alone reads as a
        // lighting artefact rather than as terrain.
        var warm = amount * 0.05f * patch;

        return new Color(
            Mathf.Clamp(colour.R * value + warm, 0f, 1f),
            Mathf.Clamp(colour.G * value, 0f, 1f),
            Mathf.Clamp(colour.B * value - warm, 0f, 1f));
    }

    /// A stable colour per resource. Derived from the item id rather than
    /// authored, so a resource added to `/data` is immediately distinguishable
    /// without anyone picking a swatch for it.
    private static Color OreColour(ItemId item)
    {
        var hash = (uint)(item.Value * 2654435761u);
        var hue = (hash % 360) / 360f;
        return Color.FromHsv(hue, 0.55f, 0.85f);
    }

    private void Write(int index, float x, float z, Color colour)
    {
        var o = index * FloatsPerInstance;
        _buffer[o + 0] = 1f; _buffer[o + 1] = 0f; _buffer[o + 2] = 0f; _buffer[o + 3] = x;
        _buffer[o + 4] = 0f; _buffer[o + 5] = 1f; _buffer[o + 6] = 0f; _buffer[o + 7] = -0.04f;
        _buffer[o + 8] = 0f; _buffer[o + 9] = 0f; _buffer[o + 10] = 1f; _buffer[o + 11] = z;
        _buffer[o + 12] = colour.R;
        _buffer[o + 13] = colour.G;
        _buffer[o + 14] = colour.B;
        _buffer[o + 15] = 1f;
    }
}
