using Godot;
using Sim;

namespace Game;

/// Belts, inserters, and the items riding on them.
///
/// The items are the point. A belt with nothing drawn on it is a grey strip,
/// and the single question a player has about a line -- is anything actually
/// moving, and where does it stop -- is exactly the one a static strip cannot
/// answer. Backed-up belts are visible as packed items, which is how you find a
/// bottleneck without opening a single panel.
public sealed partial class BeltRenderer : Node3D
{
    private const int FloatsPerInstance = 16;

    /// A cap, because a large factory can have hundreds of thousands of items in
    /// transit and drawing them all would cost more than simulating them. Beyond
    /// this the belts still run; they are just not all drawn.
    private const int MaxDrawnItems = 20_000;

    private MultiMeshInstance3D _decks = null!;
    private MultiMeshInstance3D _arrows = null!;
    private MultiMeshInstance3D _arms = null!;
    private MultiMeshInstance3D _items = null!;

    private float[] _deckBuffer = System.Array.Empty<float>();
    private float[] _arrowBuffer = System.Array.Empty<float>();
    private float[] _armBuffer = System.Array.Empty<float>();
    private float[] _itemBuffer = System.Array.Empty<float>();

    private int _builtVersion = -1;

    public int DrawnItems { get; private set; }

    public override void _Ready()
    {
        // Flat and wide: a belt is a floor, not furniture, and anything with
        // height would hide the items it is carrying.
        _decks = Pool(new BoxMesh { Size = new Vector3(0.92f, 0.06f, 0.92f) });
        _decks.Name = "Decks";

        // A wedge, pointed along the belt. Direction is the only thing a player
        // sets when placing one, so it has to be readable without clicking.
        // Short and set into the deck, so a run of them reads as chevrons
        // rather than as a continuous painted stripe -- which is what a
        // half-tile-long bright arrow on every tile actually looks like.
        _arrows = Pool(new PrismMesh { Size = new Vector3(0.26f, 0.04f, 0.22f) });
        _arrows.Name = "Arrows";

        _arms = Pool(new BoxMesh { Size = new Vector3(0.18f, 0.5f, 0.62f) });
        _arms.Name = "InserterArms";

        _items = Pool(new BoxMesh { Size = new Vector3(0.2f, 0.2f, 0.2f) });
        _items.Name = "Items";
    }

    private MultiMeshInstance3D Pool(Mesh mesh)
    {
        var node = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
            },
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.85f,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(node);
        return node;
    }

    /// Belts and inserters change only when something is built, so their buffers
    /// are rebuilt against the map's version rather than every frame. The items
    /// on them move every tick and are refilled every frame.
    public void Sync(World world, float tileSize)
    {
        var map = world.BeltMap;

        if (map.Version != _builtVersion)
        {
            _builtVersion = map.Version;
            BuildStatic(map, tileSize);
        }

        BuildItems(world, tileSize);
    }

    private void BuildStatic(BeltMap map, float tileSize)
    {
        var belts = map.Belts;
        var inserters = map.Inserters;

        Resize(ref _deckBuffer, belts.Count);
        Resize(ref _arrowBuffer, belts.Count);
        Resize(ref _armBuffer, inserters.Count);

        for (var i = 0; i < belts.Count; i++)
        {
            var belt = belts[i];
            var x = (belt.X + 0.5f) * tileSize;
            var z = (belt.Y + 0.5f) * tileSize;

            // Faster belts read brighter, so a mixed line shows where it
            // changes without measuring anything.
            var shade = belt.Speed switch
            {
                <= BeltUnits.SpeedBasic => new Color(0.34f, 0.36f, 0.40f),
                BeltUnits.SpeedFast => new Color(0.46f, 0.40f, 0.28f),
                BeltUnits.SpeedExpress => new Color(0.30f, 0.44f, 0.52f),
                _ => new Color(0.50f, 0.36f, 0.54f),
            };

            WriteRotated(_deckBuffer, i, x, 0.03f, z, belt.Facing, 1f, shade);
            WriteRotated(_arrowBuffer, i, x, 0.07f, z, belt.Facing, 1f,
                         new Color(0.62f, 0.64f, 0.68f));
        }

        for (var i = 0; i < inserters.Count; i++)
        {
            var inserter = inserters[i];
            WriteRotated(_armBuffer, i,
                         (inserter.X + 0.5f) * tileSize, 0.25f, (inserter.Y + 0.5f) * tileSize,
                         inserter.Facing, 1f, new Color(0.80f, 0.55f, 0.20f));
        }

        Upload(_decks, _deckBuffer, belts.Count);
        Upload(_arrows, _arrowBuffer, belts.Count);
        Upload(_arms, _armBuffer, inserters.Count);
    }

    /// Every item on every belt, placed where the sim says it is.
    ///
    /// A lane stores an item's distance from its segment's exit; the map says
    /// which tiles that segment crosses. Together they give a world position, so
    /// what is drawn is the simulation's own answer rather than an animation
    /// that happens to look similar.
    private void BuildItems(World world, float tileSize)
    {
        var map = world.BeltMap;
        var drawn = 0;

        Resize(ref _itemBuffer, System.Math.Min(CountItems(world), MaxDrawnItems));

        for (var segment = 0; segment < map.SegmentCount && drawn < MaxDrawnItems; segment++)
        {
            var tiles = map.TilesOfSegment(segment);
            if (tiles.Count == 0) continue;

            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var source = world.Belts.Segments[segment].LaneAt(lane);

                for (var i = 0; i < source.Count && drawn < MaxDrawnItems; i++)
                {
                    var fromExit = source.PositionOf(i);
                    var tileIndex = tiles.Count - 1 - fromExit / BeltUnits.Tile;
                    if (tileIndex < 0 || tileIndex >= tiles.Count) continue;

                    var tile = tiles[tileIndex];
                    var (dx, dy) = Directions.Delta(tile.Facing);
                    var within = (fromExit % BeltUnits.Tile) / (float)BeltUnits.Tile;

                    // `within` is measured back from the tile's exit edge, so an
                    // item at zero sits on the edge it is about to leave by.
                    var along = 0.5f - within;

                    // The two lanes sit either side of the belt's centre line.
                    var side = lane == 0 ? 0.22f : -0.22f;

                    var x = (tile.X + 0.5f + dx * along - dy * side) * tileSize;
                    var z = (tile.Y + 0.5f + dy * along + dx * side) * tileSize;

                    WriteRotated(_itemBuffer, drawn++, x, 0.16f, z, tile.Facing, 1f,
                                 ItemColour(world.Items.GetName(source.ItemAt(i))));
                }
            }
        }

        DrawnItems = drawn;
        Upload(_items, _itemBuffer, drawn);
    }

    private static int CountItems(World world)
    {
        var total = 0;
        foreach (var segment in world.Belts.Segments) total += segment.ItemCount;
        return total;
    }

    /// A stable colour per item name. Not a palette anyone designed -- it exists
    /// so two different ores on one belt are visibly two different ores, which
    /// is the question a mixed line raises.
    private static Color ItemColour(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = hash * 31 + c;
        var hue = System.Math.Abs(hash % 360) / 360f;
        return Color.FromHsv(hue, 0.55f, 0.92f);
    }

    private static void Resize(ref float[] buffer, int count)
    {
        var needed = System.Math.Max(count, 0) * FloatsPerInstance;
        if (buffer.Length != needed) buffer = new float[needed];
    }

    /// Writes one instance turned to face a direction. Belts, arrows, arms and
    /// items all need it: a belt that always pointed east would make direction --
    /// the only thing the player chose -- invisible.
    private static void WriteRotated(float[] buffer, int index, float x, float y, float z,
                                     Direction facing, float scale, Color colour)
    {
        var o = index * FloatsPerInstance;
        if (o + FloatsPerInstance > buffer.Length) return;

        // Rotation about Y, in quarter turns, so the numbers stay exact.
        var (cos, sin) = facing switch
        {
            Direction.East => (0f, 1f),
            Direction.South => (1f, 0f),
            Direction.West => (0f, -1f),
            _ => (-1f, 0f),
        };

        buffer[o + 0] = cos * scale;  buffer[o + 1] = 0f;    buffer[o + 2] = sin * scale;  buffer[o + 3] = x;
        buffer[o + 4] = 0f;           buffer[o + 5] = scale; buffer[o + 6] = 0f;           buffer[o + 7] = y;
        buffer[o + 8] = -sin * scale; buffer[o + 9] = 0f;    buffer[o + 10] = cos * scale; buffer[o + 11] = z;
        buffer[o + 12] = colour.R;
        buffer[o + 13] = colour.G;
        buffer[o + 14] = colour.B;
        buffer[o + 15] = colour.A;
    }

    private static void Upload(MultiMeshInstance3D pool, float[] buffer, int count)
    {
        var multiMesh = pool.Multimesh!;
        if (multiMesh.InstanceCount != count)
            multiMesh.InstanceCount = count;
        if (count > 0)
            multiMesh.Buffer = buffer;
        pool.Visible = count > 0;
    }
}
