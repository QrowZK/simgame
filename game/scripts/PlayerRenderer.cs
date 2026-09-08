using System.Collections.Generic;
using Godot;

namespace Game;

/// The player's body: one figure standing on the ground at the position the
/// simulation says the player is at.
///
/// Until now the camera *was* the player, so there was nothing to draw. With a
/// real position in the sim there has to be something at it, and the question
/// this class answers is not "what does a person look like" but the three a
/// player actually asks of it: **where am I**, **which way am I pointing**, and
/// **how big is that thing next to me**.
///
/// Four constraints decide the whole shape of this class.
///
/// **One instance, one batch.** The figure is a dozen boxes welded into a
/// single `ArrayMesh` with per-vertex colour, exactly as `LandingSite` builds
/// the wreck, so limbs cost triangles and not draw batches. A MultiMesh pool
/// would have been the wrong tool twice over: there is exactly one player, and
/// a pool per body part is a batch per body part. `--smoke` draw batches are
/// unchanged by this class existing until it is added to the scene, and go up
/// by exactly one when it is.
///
/// **Findable first.** The ground is greens and sand, machines are greys,
/// browns and tier colours, and green / amber / red / blue / violet are the
/// five machine states -- borrowing any of them would make the player read as a
/// machine that is working, starved, blocked, unpowered or worked out. So the
/// figure is bone white, the brightest and least saturated thing on the field,
/// with a magenta flash that nothing else in the palette uses.
///
/// **Facing is carried three ways**, because at the opening zoom the figure is
/// about fifty pixels tall and one cue is not enough: a dark visor and chest
/// panel on the front, a dark backpack on the back (so front and back differ
/// even when the head is a few pixels), and a magenta chevron on the ground
/// ahead of the feet, which survives any zoom the camera allows -- plus a
/// magenta band across the front half of the crown, which is the cue that
/// still works when the camera is looking almost straight down.
///
/// **It draws the simulation.** `Place` takes a position and a heading and
/// puts the figure there. Nothing here interpolates or eases towards a target.
/// The one motion this class has -- a small vertical bob -- is integrated from
/// the distance the sim actually moved the player, not from a clock, and is
/// exactly zero on a frame where the player did not move.
public sealed partial class PlayerRenderer : Node3D
{
    /// How far the player walks per bob cycle, in tiles. Set so a walk at
    /// normal pace bobs at roughly a footfall rate; it is a look, not a claim
    /// about stride length.
    private const float StrideLength = 0.62f;

    private const float BobHeight = 0.035f;

    private MeshInstance3D _body = null!;
    private bool _placed;
    private float _lastX;
    private float _lastZ;

    /// Distance the player has been *observed* to move, summed over every
    /// `Place`. The bob is a function of this and of nothing else, so the
    /// figure cannot appear to walk while the sim holds it still.
    public float DistanceWalked { get; private set; }

    public float FacingDegrees { get; private set; }

    /// Solids in the figure, and its triangles. Reported rather than inferred:
    /// a still cannot tell a body that failed to build from one behind the
    /// camera, so `--smoke` can assert these instead of a picture.
    public int PieceCount { get; private set; }

    public int TriangleCount { get; private set; }

    /// Top of the antenna, in world units. The size relationship with a machine
    /// is the thing most likely to go wrong when the kit is regenerated, and it
    /// is a number, so it is printable.
    public float Height { get; private set; }

    public bool IsDrawn => _body is not null && _body.Visible && _body.Mesh is not null;

    public override void _Ready()
    {
        _body = new MeshInstance3D
        {
            Name = "Figure",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        AddChild(_body);

        var builder = new Builder();
        Compose(builder);
        _body.Mesh = builder.Commit();
        PieceCount = builder.Pieces;
        TriangleCount = builder.Triangles;
        Height = builder.Top;

        // Safe to draw before anyone has called `Place`: the figure stands at
        // whatever position this node was added at, facing north, not at the
        // origin of the world and not hidden.
        _body.Position = Vector3.Zero;
    }

    /// Puts the figure at a world position with a heading.
    ///
    /// `x` and `z` are world units, one tile to 1.0, the same units
    /// `MachineRenderer` places machines in. `facingDegrees` is the direction
    /// of travel measured clockwise from north, and north is -Z / -Y-on-the-
    /// tile-grid, matching `Sim.Directions.Delta`. Cheap enough to call every
    /// frame -- it is two property writes and no allocation -- and safe to call
    /// before the player has ever moved.
    public void Place(float x, float z, float facingDegrees)
    {
        var step = _placed
            ? Mathf.Sqrt((x - _lastX) * (x - _lastX) + (z - _lastZ) * (z - _lastZ))
            : 0f;

        // A teleport -- a load, or a debug jump -- is not a walk, so it does
        // not advance the stride.
        if (step < 1f)
            DistanceWalked += step;

        _placed = true;
        _lastX = x;
        _lastZ = z;
        FacingDegrees = facingDegrees;

        // Standing still is flat on the ground, always: the bob is not allowed
        // to leave a stopped player frozen a few centimetres in the air.
        var bob = step > 0f
            ? BobHeight * Mathf.Abs(Mathf.Sin(Mathf.Pi * DistanceWalked / StrideLength))
            : 0f;

        Position = new Vector3(x, bob, z);

        // The figure is modelled facing -Z (north). Clockwise-from-north is the
        // opposite sense to Godot's Y rotation, hence the negation.
        Rotation = new Vector3(0f, -Mathf.DegToRad(facingDegrees), 0f);
    }

    // ---- the figure --------------------------------------------------------

    /// Bone white: the brightest thing on a field of greens and sand, and not
    /// one of the five machine-state colours.
    private static readonly Color Suit = new(0.94f, 0.93f, 0.87f);

    /// Near-black, for everything that has to read as a hole at fifty pixels:
    /// visor, pack, boots.
    private static readonly Color Dark = new(0.15f, 0.16f, 0.19f);

    /// Limbs and pack straps. Pale enough to stay on the light side of the
    /// machine kit's greys, so the figure never reads as a small machine.
    private static readonly Color Grey = new(0.60f, 0.60f, 0.62f);

    /// The one hue the palette does not already spend. Used sparingly -- the
    /// ground chevron, an antenna tip, two shoulder flashes -- because its job
    /// is to be found, not to be looked at.
    private static readonly Color Flash = new(0.93f, 0.22f, 0.52f);

    /// The same copper as the wreck at the landing site. The figure came off
    /// that probe and should look like it: salvaged webbing, not a uniform.
    private static readonly Color Copper = new(0.72f, 0.45f, 0.22f);

    /// Local space: +X is the figure's right, -Z is straight ahead, y=0 is the
    /// ground. Everything is a box, because at this size a bevel is a triangle
    /// nobody will ever see.
    private static void Compose(Builder b)
    {
        // Feet apart across the heading, so the stance has width from the side
        // as well as from the front. Boots dark, legs pale: the first draft had
        // both dark and the figure read as a small machine on legs, because
        // half its mass was the same value as the machine kit.
        for (var side = -1; side <= 1; side += 2)
        {
            b.Box(new Vector3(side * 0.105f, 0.05f, -0.02f), new Vector3(0.17f, 0.10f, 0.27f), Dark);
            b.Box(new Vector3(side * 0.105f, 0.28f, 0.01f), new Vector3(0.15f, 0.38f, 0.16f), Grey);
        }

        // Salvaged webbing at the waist, and the suit body above it. The torso
        // is deliberately the largest single solid in the figure and it is
        // white: at fifty pixels the silhouette is carried by one bright mass,
        // not by a count of parts.
        b.Box(new Vector3(0f, 0.52f, 0f), new Vector3(0.36f, 0.13f, 0.24f), Copper);
        b.Box(new Vector3(0f, 0.75f, -0.01f), new Vector3(0.41f, 0.42f, 0.27f), Suit);

        // Front: a dark chest panel. Back: a dark pack. Between them the torso
        // is light on one face and dark on the other from every camera yaw,
        // which is the facing cue that survives when the head is four pixels.
        b.Box(new Vector3(0f, 0.71f, -0.145f), new Vector3(0.15f, 0.13f, 0.04f), Dark);
        b.Box(new Vector3(0f, 0.76f, 0.19f), new Vector3(0.29f, 0.30f, 0.14f), Dark);
        b.Box(new Vector3(0f, 0.92f, 0.19f), new Vector3(0.31f, 0.05f, 0.16f), Copper);

        for (var side = -1; side <= 1; side += 2)
        {
            b.Box(new Vector3(side * 0.255f, 0.83f, -0.01f), new Vector3(0.13f, 0.17f, 0.23f), Suit);
            b.Box(new Vector3(side * 0.265f, 0.62f, -0.03f), new Vector3(0.10f, 0.27f, 0.13f), Grey);
        }

        b.Box(new Vector3(0f, 0.98f, 0f), new Vector3(0.14f, 0.07f, 0.14f), Dark);
        b.Box(new Vector3(0f, 1.10f, -0.01f), new Vector3(0.28f, 0.26f, 0.28f), Suit);
        b.Box(new Vector3(0f, 1.10f, -0.155f), new Vector3(0.22f, 0.13f, 0.04f), Dark);

        // A flash across the *front* half of the crown. The camera looks down
        // at 50 degrees, so the top faces are most of what is on screen -- this
        // is the one facing cue that still works from directly overhead.
        b.Box(new Vector3(0f, 1.235f, -0.075f), new Vector3(0.24f, 0.03f, 0.11f), Flash);

        // Antenna off the pack, offset to one side. A single tall thin vertical
        // is what makes a small figure findable in a wide shot; the magenta tip
        // is what makes it findable in a busy one.
        b.Box(new Vector3(0.11f, 1.22f, 0.16f), new Vector3(0.035f, 0.34f, 0.035f), Dark);
        b.Box(new Vector3(0.11f, 1.42f, 0.16f), new Vector3(0.09f, 0.09f, 0.09f), Flash);

        // The chevron: two flat bars lying on the ground ahead of the feet,
        // meeting in a point. It is drawn in the figure's own local space, so
        // it turns with the body and costs no batch of its own.
        //
        // A solid arrowhead was the first draft and reads as a painted patch --
        // the same mistake the belt arrows made before they became wedges. Two
        // bars with a gap between them stay a chevron at any zoom.
        b.Box(new Vector3(0.125f, 0.012f, -0.50f), new Vector3(0.36f, 0.022f, 0.10f),
              Mathf.DegToRad(-45f), Flash);
        b.Box(new Vector3(-0.125f, 0.012f, -0.50f), new Vector3(0.36f, 0.022f, 0.10f),
              Mathf.DegToRad(-135f), Flash);
    }

    // ---- mesh building -----------------------------------------------------

    /// Accumulates flat-shaded, vertex-coloured boxes into one surface.
    ///
    /// Deliberately a separate, smaller builder than `LandingSite`'s: that one
    /// carries ground strips, discs and a three-axis throw for debris, none of
    /// which a figure standing upright on flat ground has any use for. Sharing
    /// it would mean exporting a general mesh library to save fifty lines.
    private sealed class Builder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color> _colours = new();

        public int Pieces { get; private set; }
        public int Triangles => _vertices.Count / 3;
        public float Top { get; private set; }

        public void Box(Vector3 centre, Vector3 size, Color colour) =>
            Box(centre, size, 0f, colour);

        public void Box(Vector3 centre, Vector3 size, float yaw, Color colour)
        {
            var basis = new Basis(Vector3.Up, yaw);
            var h = size * 0.5f;
            var c = new Vector3[8];

            for (var i = 0; i < 8; i++)
            {
                var local = new Vector3(
                    (i & 1) == 0 ? -h.X : h.X,
                    (i & 2) == 0 ? -h.Y : h.Y,
                    (i & 4) == 0 ? -h.Z : h.Z);
                c[i] = centre + basis * local;
            }

            Quad(c[0], c[1], c[3], c[2], colour);   // -Z, the front
            Quad(c[5], c[4], c[6], c[7], colour);   // +Z
            Quad(c[4], c[0], c[2], c[6], colour);   // -X
            Quad(c[1], c[5], c[7], c[3], colour);   // +X
            Quad(c[2], c[3], c[7], c[6], colour);   // +Y
            Quad(c[4], c[5], c[1], c[0], colour);   // -Y

            Pieces++;
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
            // Land is flat at y=0 and the figure stands on that plane; nothing
            // may dip below it, or a boot shows through the gaps between the
            // terrain plates.
            if (v.Y < 0f) v.Y = 0f;

            _vertices.Add(v);
            _normals.Add(normal);
            _colours.Add(colour);

            if (v.Y > Top) Top = v.Y;
        }

        public ArrayMesh Commit()
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
                // Cloth and painted panel, not machinery: the figure should not
                // pick up the specular the machine kit does, or it stops
                // reading as a person among them.
                Roughness = 0.88f,
                Metallic = 0.05f,
            });
            return mesh;
        }
    }
}
