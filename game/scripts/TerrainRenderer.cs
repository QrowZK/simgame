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
                Write(cursor++, x, y, Colour(world, x, y));
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
        var ground = world.Ground.Gen.TerrainAt(x, y);

        var basecolour = ground switch
        {
            TerrainType.DeepWater => new Color(0.09f, 0.16f, 0.30f),
            TerrainType.Water => new Color(0.15f, 0.28f, 0.45f),
            TerrainType.Sand => new Color(0.62f, 0.57f, 0.40f),
            TerrainType.Grass => new Color(0.26f, 0.42f, 0.24f),
            TerrainType.Rock => new Color(0.40f, 0.40f, 0.42f),
            _ => new Color(0.62f, 0.62f, 0.66f),
        };

        if (!world.Ground.TryPatchAt(x, y, out var patch))
            return basecolour;

        // A worked-out patch still reads as a patch, just a dead one, so the
        // player can see where they have already been.
        var remaining = world.Ground.Remaining(patch);
        var ore = OreColour(patch.Item);
        return remaining > 0 ? basecolour.Lerp(ore, 0.65f) : basecolour.Lerp(ore, 0.18f);
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
