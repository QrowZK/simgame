using System.Collections.Generic;
using Godot;

namespace Game;

/// The crash site at spawn: scenery, not simulation.
///
/// A new game opens on an empty field. The premise -- a Von Neumann probe came
/// apart on entry and the fabricator survived -- is told in three sentences in
/// a panel and drawn nowhere, so the most important moment in the game is a
/// patch of grass. This draws it: a scorch scar dragged across the ground, the
/// broken hull at the end of it, a torn plate standing where it landed, a
/// snapped dish, and the debris in between.
///
/// Three constraints decide everything about the shape of this class.
///
/// **It is not a machine.** Nothing here is in `World`, occupies a tile, or is
/// asked about by placement. A player may build straight through the wreck. The
/// alternative -- scenery that blocks tiles -- would leave a player who wants
/// the spawn tile stuck forever with no way to clear it, because there is no
/// verb in the game for demolishing something that was never built.
///
/// **The ground stays flat.** Land tops are at y=0 and machines seat on that
/// plane, so the crater is a colour on the plane plus geometry standing on it,
/// never a hole in it. Everything here is drawn at y >= 0.
///
/// **It is one draw batch.** The whole site is built into a single `ArrayMesh`
/// with per-vertex colour and per-face normals, so it costs one instance and
/// one material however many pieces it is made of. A MultiMesh would have been
/// the wrong tool: there is exactly one landing site in a game, its pieces
/// share no mesh, and a pool of one instance per piece is a batch per piece.
///
/// Flat-shaded and colour-led, matching `MeshKit`: no smoothing, no textures,
/// silhouette and hue doing the work.
public sealed partial class LandingSite : Node3D
{
    /// Which way the probe was travelling when it came down, in the ground
    /// plane. The scar is dragged along this and the wreck sits at its end, so
    /// the site reads as an arrival with a direction rather than as a pile.
    private static readonly Vector2 Heading = new Vector2(1f, 0.42f).Normalized();

    private MeshInstance3D _body = null!;

    /// The burn is a second instance rather than a second surface of the first,
    /// for one reason: shadow casting is per instance, and a flat sheet lying on
    /// the ground that casts a shadow shadow-acnes itself into a black hole --
    /// which is what the second screenshot of this showed. Two instances, two
    /// batches, once, for the whole site.
    private MeshInstance3D _scar = null!;

    /// Tile the site is drawn on. Set by `Place`; defaults to the spawn tile,
    /// so adding this node to the scene with no arguments does the right thing.
    public int TileX { get; private set; } = Sim.NewGame.SpawnX;
    public int TileY { get; private set; } = Sim.NewGame.SpawnY;

    /// Distinct solids in the site -- boxes and ground patches. Reported rather
    /// than inferred because a screenshot cannot tell a wreck that failed to
    /// build from one that is behind the camera, and "the site is there" is a
    /// count, not a picture.
    public int PieceCount { get; private set; }

    public int TriangleCount { get; private set; }

    /// Whether the built mesh is actually in the tree and visible. A headless
    /// run can assert this; a still cannot distinguish it from bad framing.
    public bool IsDrawn => _body is not null && _body.Visible && _body.Mesh is not null;

    /// Widest extent of the site from its centre, in tiles. The opening frame
    /// shows 30 units of view, so this is the number that says whether the
    /// wreck is in it.
    public float Radius { get; private set; }

    public override void _Ready()
    {
        _body = new MeshInstance3D
        {
            Name = "Wreck",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        AddChild(_body);

        _scar = new MeshInstance3D
        {
            Name = "Scar",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_scar);
        Rebuild();
    }

    /// Draws the site on a different tile. The one entry point a caller needs:
    /// `AddChild(new LandingSite())` puts it at spawn, `Place(x, y)` moves it.
    public void Place(int tileX, int tileY)
    {
        TileX = tileX;
        TileY = tileY;
        if (_body is not null) Rebuild();
    }

    private void Rebuild()
    {
        var solids = new Builder();
        var ground = new Builder();
        Compose(solids, ground);

        var origin = new Vector3(TileX + 0.5f, 0f, TileY + 0.5f);
        _body.Mesh = solids.Commit();
        _body.Position = origin;
        _scar.Mesh = ground.Commit(metallic: 0f, roughness: 1f, twoSided: false);
        _scar.Position = origin;

        PieceCount = solids.Pieces + ground.Pieces;
        TriangleCount = solids.Triangles + ground.Triangles;
        Radius = Mathf.Max(solids.Radius, ground.Radius);
    }

    // ---- the site ----------------------------------------------------------

    private static readonly Color Scorch = new(0.17f, 0.14f, 0.12f);
    private static readonly Color Ash = new(0.30f, 0.26f, 0.19f);
    private static readonly Color Soil = new(0.34f, 0.26f, 0.17f);
    private static readonly Color Hull = new(0.52f, 0.55f, 0.60f);
    private static readonly Color HullDark = new(0.38f, 0.40f, 0.45f);
    private static readonly Color Burnt = new(0.42f, 0.26f, 0.18f);
    private static readonly Color Copper = new(0.72f, 0.45f, 0.22f);
    private static readonly Color Cavity = new(0.08f, 0.08f, 0.09f);

    /// Everything in the site, in world-ish local coordinates: +X along the
    /// heading is "onwards", and the impact is at the origin of this node.
    private static void Compose(Builder b, Builder g)
    {
        var d = Heading;
        var n = new Vector2(-d.Y, d.X);
        var yaw = Mathf.Atan2(d.Y, d.X);

        Vector3 At(float along, float across, float up = 0f)
            => new(d.X * along + n.X * across, up, d.Y * along + n.Y * across);

        // The scar. Two flat sheets on the ground plane, wider and paler
        // underneath, so the burn has an edge that fades instead of a hard
        // outline. Both are drawn just above y=0 -- the ground is not moved.
        g.Ground(At(-4.0f, 0f), yaw, length: 23f, width: 7.6f, y: 0.012f, Ash, taper: 0.16f);
        g.Ground(At(-3.0f, 0f), yaw, length: 19f, width: 3.4f, y: 0.026f, Scorch, taper: 0.14f);

        // Earth thrown clear of the furrow: two long low wedges either side of
        // the scar, the only geometry that says something ploughed through here
        // rather than a stain being painted on.
        for (var side = -1; side <= 1; side += 2)
            for (var i = 0; i < 4; i++)
            {
                var along = -11f + i * 3.6f;
                b.Box(At(along, side * (2.0f + i * 0.30f), 0.055f),
                      new Vector3(1.9f, 0.11f, 0.7f + i * 0.12f),
                      yaw, tilt: 0f, roll: 0f, Soil);
            }

        // The probe itself, nose down at the end of the furrow and broken open.
        // Big enough to read at 30 units of view: at that zoom a 1-tile object
        // is a smudge, and this is the thing the opening frame is about.
        b.Box(At(4.2f, 0.2f, 1.05f), new Vector3(5.4f, 1.9f, 2.6f),
              yaw, tilt: -0.30f, roll: 0.12f, Hull);

        // The torn end: a dark cavity facing back down the scar, so the hull
        // reads as broken open rather than as a crate someone left out.
        b.Box(At(1.55f, 0.15f, 0.95f), new Vector3(0.9f, 1.5f, 2.1f),
              yaw, tilt: -0.30f, roll: 0.12f, Cavity);

        // Ribs over the spine, and the scorched belly panel. Heat discolouration
        // is on the underside and the nose, which is where entry would put it.
        for (var i = 0; i < 4; i++)
            b.Box(At(3.0f + i * 0.95f, 0f, 1.9f + i * 0.28f), new Vector3(0.35f, 0.5f, 2.7f),
                  yaw, tilt: -0.30f, roll: 0.12f, HullDark);

        b.Box(At(5.9f, 0.2f, 0.55f), new Vector3(2.2f, 0.8f, 2.5f),
              yaw, tilt: -0.30f, roll: 0.12f, Burnt);

        // A torn hull plate, standing on its edge where it stopped. A vertical
        // in a field of horizontals: this is what carries the silhouette from
        // across the map, long after the scar has flattened into the grass.
        b.Box(At(-1.2f, 3.4f, 1.6f), new Vector3(3.4f, 3.2f, 0.24f),
              yaw + 0.5f, tilt: 0.05f, roll: 0.22f, Hull);
        b.Box(At(-1.35f, 3.4f, 0.30f), new Vector3(3.2f, 0.6f, 0.34f),
              yaw + 0.5f, tilt: 0.05f, roll: 0.22f, HullDark);

        // The dish, snapped off its mast and lying tipped against the ground.
        b.Disc(At(-5.6f, -3.6f, 0.85f), radius: 1.6f, thickness: 0.22f,
               yaw: yaw - 0.9f, tilt: 1.05f, Hull);
        b.Box(At(-4.4f, -2.7f, 0.5f), new Vector3(2.6f, 0.22f, 0.22f),
              yaw - 0.9f, tilt: 0.55f, roll: 0f, HullDark);

        // Struts driven into the ground on impact, leaning the way the probe
        // was going.
        b.Box(At(0.4f, -2.2f, 1.25f), new Vector3(0.26f, 2.6f, 0.26f), yaw, 0.42f, 0.2f, HullDark);
        b.Box(At(2.6f, 2.6f, 1.0f), new Vector3(0.24f, 2.1f, 0.24f), yaw + 1.1f, 0.55f, 0f, HullDark);
        b.Box(At(-2.6f, -1.1f, 0.75f), new Vector3(0.22f, 1.6f, 0.22f), yaw, -0.5f, 0f, Copper);

        // Debris, thinning towards the tail of the scar. Deterministic: the
        // same shards in the same places on every run and every machine, so a
        // screenshot is comparable with the last one.
        var rng = new Lcg(0x5EED);
        for (var i = 0; i < 16; i++)
        {
            var t = i / 21f;
            var along = Mathf.Lerp(6.5f, -12.5f, t) + rng.Range(-1.1f, 1.1f);
            var across = rng.Range(-1f, 1f) * (1.2f + 3.2f * t);
            var size = Mathf.Lerp(0.9f, 0.3f, t) * rng.Range(0.7f, 1.3f);
            var colour = rng.Next() % 3 == 0 ? Burnt : rng.Next() % 2 == 0 ? Hull : HullDark;

            b.Box(At(along, across, size * 0.45f),
                  new Vector3(size * 0.95f, size * 1.05f, size * 0.85f),
                  yaw + rng.Range(-3f, 3f), rng.Range(-0.6f, 0.6f), rng.Range(-0.5f, 0.5f),
                  colour);
        }
    }

    /// A tiny fixed-sequence generator. `System.Random` would do, but its
    /// sequence is not guaranteed stable across runtimes, and a wreck that
    /// rearranges itself between .NET versions makes every screenshot
    /// comparison a guess.
    private struct Lcg
    {
        private uint _state;
        public Lcg(uint seed) => _state = seed | 1u;
        public uint Next() => _state = _state * 1664525u + 1013904223u;
        public float Range(float lo, float hi) => lo + (Next() >> 8) / 16777216f * (hi - lo);
    }

    // ---- mesh building -----------------------------------------------------

    /// Accumulates flat-shaded, vertex-coloured triangles into one surface.
    ///
    /// Written out by hand rather than through `SurfaceTool` because every
    /// piece needs its own face normals with no smoothing or index sharing, and
    /// that is the entire job -- three arrays and a `Commit`.
    private sealed class Builder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color> _colours = new();

        public int Pieces { get; private set; }
        public int Triangles => _vertices.Count / 3;
        public float Radius { get; private set; }

        /// A box, placed by centre, then yawed about Y, tilted about its own
        /// length and rolled about its own axis. Enough freedom to make a hull
        /// look thrown rather than parked, and no more.
        public void Box(Vector3 centre, Vector3 size, float yaw, float tilt, float roll, Color colour)
        {
            var basis = new Basis(Vector3.Up, yaw)
                      * new Basis(Vector3.Forward, tilt)
                      * new Basis(Vector3.Right, roll);
            var h = size * 0.5f;

            // Corners in local space, then rotated into place once.
            var c = new Vector3[8];
            for (var i = 0; i < 8; i++)
            {
                var local = new Vector3(
                    (i & 1) == 0 ? -h.X : h.X,
                    (i & 2) == 0 ? -h.Y : h.Y,
                    (i & 4) == 0 ? -h.Z : h.Z);
                c[i] = centre + basis * local;
            }

            Quad(c[0], c[1], c[3], c[2], colour);   // -Z
            Quad(c[5], c[4], c[6], c[7], colour);   // +Z
            Quad(c[4], c[0], c[2], c[6], colour);   // -X
            Quad(c[1], c[5], c[7], c[3], colour);   // +X
            Quad(c[2], c[3], c[7], c[6], colour);   // +Y
            Quad(c[4], c[5], c[1], c[0], colour);   // -Y

            Pieces++;
        }

        /// A flat sheet lying on the ground plane: the burn. Tapered at the
        /// tail so the scar narrows away from the impact instead of ending in
        /// a rectangle, which reads as a painted stripe.
        public void Ground(Vector3 centre, float yaw, float length, float width, float y,
                           Color colour, float taper)
        {
            var basis = new Basis(Vector3.Up, yaw);
            var half = length * 0.5f;

            // Built as a strip so the width can vary along it, and so the burn
            // has an uneven edge rather than a machined one.
            const int steps = 14;
            var rng = new Lcg(0xB0FF);
            var left = new Vector3[steps + 1];
            var right = new Vector3[steps + 1];

            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                var along = Mathf.Lerp(-half, half, t);

                // Widest a third of the way from the leading end, where a
                // shallow impact digs hardest before it slides out.
                var shape = Mathf.Sin(Mathf.Pow(t, 0.75f) * Mathf.Pi);
                var w = width * 0.5f * Mathf.Lerp(taper, 1f, shape) * rng.Range(0.86f, 1.12f);

                left[i] = centre + basis * new Vector3(along, y - centre.Y, -w);
                right[i] = centre + basis * new Vector3(along, y - centre.Y, w);
            }

            // Forced up-facing, not derived: a sheet whose winding came out
            // the other way is culled away entirely and the scar simply does
            // not appear -- which is exactly what the first screenshot showed.
            for (var i = 0; i < steps; i++)
            {
                Flat(left[i], right[i], right[i + 1], colour);
                Flat(left[i], right[i + 1], left[i + 1], colour);
            }

            Pieces++;
        }

        /// A shallow disc: the antenna, approximated as a low prism. Eight
        /// sides, because at this size a rounder one is more triangles for a
        /// silhouette nobody can tell apart.
        public void Disc(Vector3 centre, float radius, float thickness, float yaw, float tilt,
                         Color colour)
        {
            var basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Forward, tilt);
            const int sides = 8;
            var top = new Vector3[sides];
            var bottom = new Vector3[sides];

            for (var i = 0; i < sides; i++)
            {
                var a = Mathf.Tau * i / sides;
                var p = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                top[i] = centre + basis * (p + new Vector3(0f, thickness * 0.5f, 0f));
                bottom[i] = centre + basis * (p - new Vector3(0f, thickness * 0.5f, 0f));
            }

            var topCentre = centre + basis * new Vector3(0f, thickness * 0.5f, 0f);
            var bottomCentre = centre + basis * new Vector3(0f, -thickness * 0.5f, 0f);

            for (var i = 0; i < sides; i++)
            {
                var j = (i + 1) % sides;
                Triangle(topCentre, top[i], top[j], colour);
                Triangle(bottomCentre, bottom[j], bottom[i], colour.Darkened(0.25f));
                Quad(bottom[i], bottom[j], top[j], top[i], colour.Darkened(0.12f));
            }

            Pieces++;
        }

        /// A triangle lying on the ground plane, lit from above whichever way
        /// it is wound. Culling is off for the whole mesh, so the sheet is
        /// visible from either side and never disappears on a winding mistake.
        private void Flat(Vector3 a, Vector3 b, Vector3 c, Color colour)
        {
            // Wound both ways, with back-face culling left on. A double-sided
            // material was the first attempt and it is why the scar rendered as
            // a near-black hole for two screenshots: Godot flips the shading
            // normal on a back face, so the sheet was lit from underneath and
            // showed ambient only. One of these two triangles is the front
            // face, whichever way the strip runs, and the other is culled.
            Add(a, Vector3.Up, colour);
            Add(b, Vector3.Up, colour);
            Add(c, Vector3.Up, colour);

            Add(a, Vector3.Up, colour);
            Add(c, Vector3.Up, colour);
            Add(b, Vector3.Up, colour);
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
        {
            Triangle(a, b, c, colour);
            Triangle(a, c, d, colour);
        }

        private void Triangle(Vector3 a, Vector3 b, Vector3 c, Color colour)
        {
            var normal = (c - a).Cross(b - a).Normalized();
            if (!normal.IsFinite() || normal == Vector3.Zero)
                normal = Vector3.Up;

            Add(a, normal, colour);
            Add(b, normal, colour);
            Add(c, normal, colour);
        }

        private void Add(Vector3 v, Vector3 normal, Color colour)
        {
            // Nothing may dip below the ground plane: land is flat at y=0 and a
            // piece poking through it would show the underside of the terrain
            // plates through the gaps between tiles.
            if (v.Y < 0f) v.Y = 0f;

            _vertices.Add(v);
            _normals.Add(normal);
            _colours.Add(colour);

            var reach = new Vector2(v.X, v.Z).Length();
            if (reach > Radius) Radius = reach;
        }

        public ArrayMesh Commit(float metallic = 0.20f, float roughness = 0.78f,
                                bool twoSided = true)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colours.ToArray();

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = roughness,
                Metallic = metallic,
                CullMode = twoSided
                    ? BaseMaterial3D.CullModeEnum.Disabled
                    : BaseMaterial3D.CullModeEnum.Back,
            });
            return mesh;
        }
    }
}
