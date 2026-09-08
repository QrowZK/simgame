using Godot;
using Sim;

namespace Game;

/// Draws the ground.
///
/// Until now the renderer only drew machines, which was survivable when a new
/// game started with a factory and fatal the moment it started with nothing:
/// the player landed in an empty void with no way to see where the ore was.
///
/// One unit-box mesh, instanced per visible tile, scaled and seated per
/// instance and coloured by terrain. Same MultiMesh approach as the machines,
/// for the same reason -- a per-tile node graph would cost more than the whole
/// sim -- and one pool for land and water together, so the ground still costs
/// exactly one draw batch.
///
/// Two rules govern everything below, and they pull in opposite directions:
///
/// **Land is flat and must stay flat.** The sim is a plane. Machines are seated
/// at y=0, belts and inserters and the build ghost all assume a flat surface,
/// and `TryBuild` does not know what height is. Every land tile's *top face*
/// therefore sits at exactly y=0, always. What varies is how far down the tile
/// extends, which nothing can stand on and nothing has to agree with.
///
/// **Water is where relief belongs.** `TryBuild` refuses water outright, so
/// nothing will ever stand on a water tile. That makes the surface free to be
/// recessed by its real depth -- and once it is, the land tile beside it shows
/// its own side wall, which is a bank. The relief costs no geometry that was
/// not already being drawn: it is the same box, seated lower.
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

    /// Height at which the worldgen stops calling a tile water. Mirrored from
    /// `WorldGen`'s private `SeaLevel` rather than exported, because the
    /// renderer only uses it to decide *how deep* a tile the sim has already
    /// called water is drawn -- never to decide whether it is water. That
    /// question is always asked of `TerrainAt`, so a worldgen change moves the
    /// shoreline and the buildable area together and this constant can only
    /// ever be wrong about a shade of blue.
    private const int SeaLevel = 96;

    /// How thick a land tile is away from water. Nothing sees the underside,
    /// so this is only the depth of the shadow in the seam between two tiles --
    /// and a thick slab everywhere turned that seam into a black canyon and the
    /// map into graph paper, which is the thing being fixed here.
    private const float PlateThickness = 0.12f;

    /// How thick a land tile that touches water is. This one *is* seen: it is
    /// the bank, the wall between the land plane and the pool surface, and it
    /// has to be tall enough to read from the play camera. The tile behind it
    /// is thin again, and the seam between them is 0.015 wide, so the step is
    /// invisible from anywhere except underneath.
    private const float BankThickness = 1.0f;

    /// The shallowest water is still recessed this far, so that even a single
    /// puddle tile reads as a hole rather than as a blue plate.
    private const float MinDepth = 0.34f;

    /// How far the rim of a pool is recessed. Shallower than `MinDepth` by
    /// enough to be a visible step down into the body of the water.
    private const float ShallowDepth = 0.14f;

    /// Extra recess at the bottom of the deep-water ramp.
    private const float DepthRange = 0.60f;

    /// Height units below sea level over which water reaches full depth.
    private const float DepthFalloff = 42f;

    private MultiMeshInstance3D _pool = null!;
    private float[] _buffer = System.Array.Empty<float>();

    /// The tile the field was last built around. Rebuilding every frame would
    /// re-evaluate noise for 37,000 tiles 60 times a second; the field only has
    /// to move when the camera does.
    private int _builtX = int.MinValue;
    private int _builtY = int.MinValue;
    private int _builtDug = -1;

    /// Everything a tile needs, keyed by tile.
    ///
    /// A rebuild is triggered by the camera crossing a single tile boundary,
    /// and then re-evaluates the whole 193x193 field -- of which all but two
    /// strips were already on screen the frame before. Evaluating the
    /// worldgen's noise 37,000 times for that cost 24 ms before this renderer
    /// shaded anything, which is a stutter every time the view creeps sideways.
    ///
    /// Caching by tile makes the second and every later rebuild a dictionary
    /// probe. It is correct to cache because the inputs are pure functions of
    /// the seed and the tile: the only thing that can change a tile's
    /// appearance is mining it out, and that already forces a rebuild by its
    /// own counter, so the cache is dropped there.
    private readonly System.Collections.Generic.Dictionary<long, Tile> _tiles = new();

    /// Panning far enough, for long enough, would otherwise grow the cache
    /// without bound. Dropping the lot costs one expensive rebuild, which is
    /// what the player would have paid anyway had they never come back.
    private const int MaxCachedTiles = 400_000;

    /// One tile's drawn form. `Top` is the y of its upper face: 0 for every
    /// land tile without exception, negative for water.
    private readonly struct Tile
    {
        /// The ground, with no ore in it. Depends only on seed and position, so
        /// it is true for the life of the world.
        public readonly Color Colour;

        /// The ore tint, and which patch it came from. Kept apart from `Colour`
        /// because mining changes only how strongly this is mixed in -- a live
        /// patch at 0.65, a worked-out one at 0.18. Holding the two separately
        /// is what lets a depletion redraw without describing anything again.
        public readonly Color Ore;
        public readonly long Patch;

        public readonly float Top;
        public readonly float Bottom;
        public readonly float Width;

        public Tile(Color colour, float top, float bottom, float width,
                    Color ore = default, long patch = NoPatch)
        {
            Colour = colour;
            Top = top;
            Bottom = bottom;
            Width = width;
            Ore = ore;
            Patch = patch;
        }

        public bool IsWater => Top < 0f;
        public bool HasOre => Patch != NoPatch;
    }

    /// No patch under this tile. A real patch key is an anchor packed into a
    /// long, and anchors are signed, so the sentinel cannot be a coordinate.
    private const long NoPatch = long.MinValue;

    /// Which patches still hold something, for the rebuild in progress. Cleared
    /// every rebuild: it is the one thing mining actually changes.
    private readonly System.Collections.Generic.Dictionary<long, bool> _alive = new();

    /// Water tiles are drawn slightly wider than land ones.
    ///
    /// Land keeps the seam it always had, because the game is played on tile
    /// coordinates and a player placing a 3x3 wants to count squares. A pool is
    /// not a floor and has no squares to count, so its tiles butt together into
    /// one unbroken sheet -- which, at a glance and with no shader involved, is
    /// most of what makes it read as liquid rather than as blue plates.
    /// A hairline, not a grid line. At 0.94 the gap was six percent of a tile
    /// and the map read as graph paper at every zoom -- the exact complaint
    /// this change exists to answer. At 0.985 the seam is still there, and a
    /// player counting squares for a 3x3 can still count them, but it reads as
    /// grout between plates rather than as ruled paper.
    private const float LandWidth = 0.985f;
    private const float WaterWidth = 1.0f;

    public override void _Ready()
    {
        // A unit box, sized per instance. The mesh carried the tile's
        // dimensions before this; it cannot now, because land and water are
        // different shapes.
        var mesh = new BoxMesh { Size = new Vector3(1f, 1f, 1f) };

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

        // Mining does NOT drop the cache. It used to, and that was the single
        // most expensive thing the renderer did: every ore extraction threw
        // away 37,000 described tiles and rebuilt them from the worldgen, and a
        // running factory extracts constantly. Describing the ground again
        // cannot change it -- the ground is a pure function of seed and tile.
        // What mining changes is how strongly the ore tints, and that is
        // resolved below, per patch rather than per tile.
        if (_tiles.Count > MaxCachedTiles) _tiles.Clear();
        _builtDug = dug;
        _alive.Clear();

        var side = ViewRadius * 2 + 1;
        var count = side * side;

        if (_buffer.Length != count * FloatsPerInstance)
            _buffer = new float[count * FloatsPerInstance];

        var cursor = 0;
        var water = 0;
        var deepest = 0f;
        for (var dy = -ViewRadius; dy <= ViewRadius; dy++)
            for (var dx = -ViewRadius; dx <= ViewRadius; dx++)
            {
                var x = centreX + dx;
                var y = centreY + dy;
                var key = ((long)x << 32) ^ (uint)y;

                // By reference, not by value. A rebuild does this 37,000 times
                // inside one frame, and copying the tile out of the dictionary
                // and then into the buffer measured as the largest single cost
                // in the loop once the struct grew past a Color.
                ref var tile = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(_tiles, key, out var existed);
                if (!existed) tile = Describe(world, x, y);

                if (tile.IsWater)
                {
                    water++;
                    if (tile.Top < deepest) deepest = tile.Top;
                }

                // A patch is worked out or it is not; every tile of it draws
                // the same either way. Asked once per patch per rebuild rather
                // than once per tile, which is a few dozen questions instead of
                // thirty-seven thousand.
                var colour = tile.Colour;
                if (tile.HasOre)
                {
                    if (!_alive.TryGetValue(tile.Patch, out var alive))
                    {
                        alive = world.Ground.TryPatchAt(x, y, out var patch)
                                && world.Ground.Remaining(patch) > 0;
                        _alive[tile.Patch] = alive;
                    }

                    colour = colour.Lerp(tile.Ore, alive ? 0.65f : 0.18f);
                }

                Write(cursor++, x, y, in tile, colour);
            }

        WaterTiles = water;
        DeepestWater = deepest == 0f ? 0f : -deepest;

        var multiMesh = _pool.Multimesh!;
        if (multiMesh.InstanceCount != count)
            multiMesh.InstanceCount = count;
        multiMesh.Buffer = _buffer;
    }

    /// Drops the per-tile cache, so the next `Sync` describes every tile again.
    ///
    /// Only the measurement path uses this. Timing a rebuild that is mostly
    /// cache hits measures the cache; timing the very first rebuild in a
    /// process measures the JIT. Clearing deliberately, after the code is warm,
    /// is the only way to time the thing the number claims to be about -- the
    /// cost of describing a whole field.
    public void ForgetCachedTiles() => _tiles.Clear();

    /// How many of the drawn tiles are water. Appearance alone cannot settle
    /// whether a pool is recessed or merely painted blue -- a dark plate and a
    /// hole look alike in a still -- so the smoke run prints this alongside the
    /// deepest recess it found.
    public int WaterTiles { get; private set; }

    /// The lowest water surface in the last field, in world units below the
    /// land plane. Zero if no water was drawn.
    public float DeepestWater { get; private set; }

    /// The nearest water tile to `from`, or null within `limit` tiles.
    ///
    /// Worldgen pulls the whole home region up out of the sea so a new game is
    /// never unplayable, which means the coast is a long way from spawn and
    /// neither the default capture nor the smoke run's field contains a single
    /// water tile. Both of them need one: a shoreline is the thing this
    /// renderer draws that a screenshot of the factory cannot show, and a water
    /// count of zero proves nothing about pools.
    ///
    /// Coarse-stepped on purpose. Water bodies are far wider than eight tiles
    /// at this worldgen's scale, so stepping the search does not miss one, and
    /// searching every tile out to a thousand would cost more than the rebuild
    /// it is setting up.
    public static Vector3? NearestWater(World world, Vector3 from, int limit = 1200)
    {
        var gen = world.Ground.Gen;
        var cx = Mathf.FloorToInt(from.X);
        var cy = Mathf.FloorToInt(from.Z);

        const int Step = 8;
        for (var r = Step; r <= limit; r += Step)
            for (var i = -r; i <= r; i += Step)
                foreach (var (x, y) in new[]
                         {
                             (cx + i, cy - r), (cx + i, cy + r),
                             (cx - r, cy + i), (cx + r, cy + i),
                         })
                    if (gen.IsWater(x, y))
                        return new Vector3(x, 0f, y);

        return null;
    }

    /// Heights, keyed by tile, and never invalidated.
    ///
    /// Separate from the tile cache on purpose. `HeightAt` is a five-octave
    /// fractal and the tile now needs its four neighbours' heights as well as
    /// its own, to know whether it is a bank; five fractal evaluations per tile
    /// is the sort of cost that turns a rebuild into a stutter.
    ///
    /// It is safe to keep forever where the tile cache is not, because height
    /// is a pure function of seed and tile and *nothing in the game changes
    /// it*. Mining changes what a tile is worth and therefore its colour, which
    /// is why the tile cache is dropped on `DepletionCount`; it does not move
    /// the ground. Sharing one cache made every ore extraction re-run the
    /// worldgen for the whole visible field.
    private readonly System.Collections.Generic.Dictionary<long, int> _heights = new();

    private int Height(WorldGen gen, int x, int y)
    {
        var key = ((long)x << 32) ^ (uint)y;
        if (_heights.TryGetValue(key, out var h)) return h;
        if (_heights.Count > MaxCachedTiles) _heights.Clear();
        return _heights[key] = gen.HeightAt(x, y);
    }

    /// Everything about how one tile is drawn.
    private Tile Describe(World world, int x, int y)
    {
        var gen = world.Ground.Gen;
        var height = Height(gen, x, y);
        var water = height < SeaLevel;

        var colour = water ? WaterColour(gen, x, y, height) : LandColour(gen, x, y, height);

        // Ore is drawn as a tint on the ground rather than as its own object: a
        // patch is a region you stand on, and a player deciding where to build
        // needs to see its shape, not a marker at its centre. A worked-out
        // patch still reads as a patch, just a dead one.
        //
        // Recorded, not mixed in. How strongly it tints is the only thing
        // mining changes, and that is decided at write time.
        var ore = default(Color);
        var patchKey = NoPatch;
        if (world.Ground.TryPatchAt(x, y, out var patch))
        {
            ore = OreColour(patch.Item);
            patchKey = ((long)patch.X << 32) ^ (uint)patch.Y;
        }

        var touchesLand = Height(gen, x - 1, y) >= SeaLevel || Height(gen, x + 1, y) >= SeaLevel
                       || Height(gen, x, y - 1) >= SeaLevel || Height(gen, x, y + 1) >= SeaLevel;

        if (water)
        {
            // Depth from the same number that decided this was water at all, so
            // the pool is deep where the worldgen says the ground is low.
            // Nothing here is invented for the picture.
            var below = Mathf.Clamp((SeaLevel - height) / DepthFalloff, 0f, 1f);
            var top = -(MinDepth + DepthRange * below);

            // The rim of a pool -- a water tile with land next to it -- is
            // drawn a step shallower and paler than the rest. This is the one
            // thing that makes a bay read as a bowl rather than as a lake of
            // constant depth, and it is not decoration: a tile adjacent to land
            // *is* the shallow edge, and worldgen's height is too smooth at
            // this scale to say so on its own.
            if (touchesLand)
            {
                top = -ShallowDepth;
                colour = colour.Lerp(new Color(0.32f, 0.54f, 0.54f), 0.32f);
            }

            // Water columns run below the deepest bank, so a pool floor is
            // never a hole through which the background shows.
            return new Tile(colour, top, -(BankThickness + 0.3f), WaterWidth, ore, patchKey);
        }

        var bank = Height(gen, x - 1, y) < SeaLevel || Height(gen, x + 1, y) < SeaLevel
                || Height(gen, x, y - 1) < SeaLevel || Height(gen, x, y + 1) < SeaLevel;

        return new Tile(colour, 0f, bank ? -BankThickness : -PlateThickness,
                        bank ? WaterWidth : LandWidth, ore, patchKey);
    }

    /// Water, darkening and cooling with depth.
    ///
    /// The colour is carried onto the sides of the recessed box as well as its
    /// top, so the wall of a deep pool seen from the play camera is the same
    /// blue as its surface, only shaded by the sun. That is the whole trick:
    /// no shader, no transparency, and the depth is legible from a still.
    private static Color WaterColour(WorldGen gen, int x, int y, int height)
    {
        var below = Mathf.Clamp((SeaLevel - height) / DepthFalloff, 0f, 1f);

        var shallow = new Color(0.22f, 0.45f, 0.47f);
        var deep = new Color(0.04f, 0.10f, 0.26f);
        var colour = shallow.Lerp(deep, below * below * 0.55f + below * 0.45f);

        // Broad, slow ripple. Deliberately much weaker than the land grain: a
        // water surface that is speckled per tile reads as gravel.
        var ripple = (float)(Sim.Noise.Value(gen.Seed ^ 0x2F1B, x, y, cell: 4) - 0.5);
        var value = 1f + 0.07f * ripple;
        return new Color(
            Mathf.Clamp(colour.R * value, 0f, 1f),
            Mathf.Clamp(colour.G * value, 0f, 1f),
            Mathf.Clamp(colour.B * value, 0f, 1f));
    }

    /// The land ramp: a continuous colour for a continuous height, so biomes
    /// meet in a gradient instead of a step.
    ///
    /// `TerrainAt` sorts height into five buckets, and the renderer used to
    /// paint one flat colour per bucket. That is why the map read as blocks --
    /// every tile in a sixty-unit-wide band of height was the same green, and
    /// the boundary between two bands was a hard contour line that no ground
    /// has. The stops below are the same terrain in the same order, but read
    /// off with a lerp, so a field runs from damp low green to dry olive and
    /// into scree without a seam anywhere.
    ///
    /// The height used for the lookup is warped by a mid-scale noise first, so
    /// the boundaries wander instead of tracing the height contours exactly --
    /// a beach that is a smooth curve everywhere reads as a diagram. The warp
    /// is clamped so it can never push a land tile down into the water part of
    /// the ramp: the shoreline the player sees is the shoreline `TryBuild`
    /// enforces, and the two must not disagree by a single tile.
    ///
    /// Every input is a pure function of the world seed and the tile. That is
    /// not tidiness: screenshots are how rendering is verified here, and a
    /// renderer that rolled its own dice would make every capture a different
    /// picture and every visual regression unprovable.
    private static Color LandColour(WorldGen gen, int x, int y, int height)
    {
        var warp = (float)(Sim.Noise.Value(gen.Seed ^ 0x5EED, x, y, cell: 11) - 0.5) * 7f
                 + (float)(Sim.Noise.Value(gen.Seed ^ 0x11D7, x, y, cell: 37) - 0.5) * 9f;

        var h = Mathf.Clamp(height + warp, SeaLevel + 0.5f, 255f);
        var colour = Ramp(h);

        // Per-tile grain, kept small. It stops any two neighbours matching
        // exactly, which is what makes a zoomed floor look painted rather than
        // tiled -- but at the strength it used to have it dithered the whole
        // map into a dot screen at play distance.
        var grain = (Sim.Noise.Hash(gen.Seed + 7919, x, y) & 0xFF) / 127.5f - 1f;

        // A clumping term an order of magnitude larger than a tile, so a field
        // has somewhere to be greener. The ramp alone follows height, and large
        // flats are exactly where height stops varying.
        var clump = (float)(Sim.Noise.Value(gen.Seed ^ 0x77A3, x, y, cell: 8) * 2.0 - 1.0);

        // And a second, much larger one. The home region is *deliberately*
        // flattened by worldgen so a new game is playable, which means the
        // height ramp has nothing to say there and the spawn view was a single
        // sheet of green whatever the ramp did. A field-sized term is the only
        // honest thing left to vary by: it is still a pure function of the
        // seed, and it is what gives the opening view meadows and dry ground
        // instead of one colour to the horizon.
        var field = (float)(Sim.Noise.Value(gen.Seed ^ 0x3C19, x, y, cell: 26) * 2.0 - 1.0);

        var value = 1f + 0.070f * clump + 0.085f * field + 0.030f * grain;

        // Warmth as well as brightness: dry ground is not just paler grass, it
        // is browner, and value alone reads as a lighting artefact rather than
        // as terrain. Green moves against it, so the wet end of the range goes
        // lush rather than merely dark.
        var warm = 0.022f * clump + 0.030f * field;

        return new Color(
            Mathf.Clamp(colour.R * value + warm, 0f, 1f),
            Mathf.Clamp(colour.G * value - warm * 0.35f, 0f, 1f),
            Mathf.Clamp(colour.B * value - warm, 0f, 1f));
    }

    /// Colour stops up the land ramp, in worldgen height units.
    ///
    /// The heights are chosen against `WorldGen`'s own band edges -- sand to
    /// 108, grass to 168, rock to 205 -- with a stop a little inside each band
    /// and one a little past it, which is what turns the edge into a blend
    /// rather than moving it.
    private static readonly (float Height, Color Colour)[] Stops =
    {
        ( 96f, new Color(0.46f, 0.44f, 0.34f)),   // wet sand at the waterline
        (101f, new Color(0.70f, 0.64f, 0.46f)),   // dry beach
        (108f, new Color(0.53f, 0.55f, 0.33f)),   // scrub behind the beach
        (122f, new Color(0.30f, 0.46f, 0.25f)),   // meadow
        (150f, new Color(0.21f, 0.38f, 0.21f)),   // deep grass
        (172f, new Color(0.30f, 0.40f, 0.26f)),   // grass thinning over stone
        (190f, new Color(0.42f, 0.41f, 0.38f)),   // scree
        (208f, new Color(0.50f, 0.50f, 0.53f)),   // rock
        (232f, new Color(0.66f, 0.67f, 0.71f)),   // bare mountain
        (255f, new Color(0.82f, 0.84f, 0.88f)),   // the tops
    };

    private static Color Ramp(float h)
    {
        if (h <= Stops[0].Height) return Stops[0].Colour;

        for (var i = 1; i < Stops.Length; i++)
        {
            if (h > Stops[i].Height) continue;
            var t = (h - Stops[i - 1].Height) / (Stops[i].Height - Stops[i - 1].Height);
            return Stops[i - 1].Colour.Lerp(Stops[i].Colour, t);
        }

        return Stops[^1].Colour;
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

    /// Writes one instance: a box scaled to the tile's footprint and thickness,
    /// hung from its top face.
    ///
    /// Land tops are all at exactly y=0 -- the sim's plane -- and land is only
    /// ever thickened downwards, so no machine, belt or ghost can be lifted or
    /// buried by anything in here.
    private void Write(int index, float x, float z, in Tile tile, Color colour)
    {
        var thickness = tile.Top - tile.Bottom;
        var centre = tile.Top - thickness * 0.5f;

        var o = index * FloatsPerInstance;
        _buffer[o + 0] = tile.Width; _buffer[o + 1] = 0f; _buffer[o + 2] = 0f; _buffer[o + 3] = x;
        _buffer[o + 4] = 0f; _buffer[o + 5] = thickness; _buffer[o + 6] = 0f; _buffer[o + 7] = centre;
        _buffer[o + 8] = 0f; _buffer[o + 9] = 0f; _buffer[o + 10] = tile.Width; _buffer[o + 11] = z;
        _buffer[o + 12] = colour.R;
        _buffer[o + 13] = colour.G;
        _buffer[o + 14] = colour.B;
        _buffer[o + 15] = 1f;
    }
}
