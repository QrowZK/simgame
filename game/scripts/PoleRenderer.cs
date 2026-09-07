using Godot;
using Sim;

namespace Game;

/// Draws power poles.
///
/// Separate from the machine renderer because a pole is not a machine: it is a
/// thin post, it has no tier or function attachment, and its interesting
/// property is the area it covers rather than what it looks like. That coverage
/// is drawn as a faint disc on the ground, because "is my machine in reach" is
/// the only question a player ever asks about a pole.
public sealed partial class PoleRenderer : Node3D
{
    private const int FloatsPerInstance = 16;

    private MultiMeshInstance3D _posts = null!;
    private MultiMeshInstance3D _coverage = null!;
    private float[] _postBuffer = System.Array.Empty<float>();
    private float[] _coverageBuffer = System.Array.Empty<float>();
    private int _built = -1;

    public override void _Ready()
    {
        _posts = Pool(new BoxMesh { Size = new Vector3(0.22f, 2.4f, 0.22f) }, shadows: true);
        _posts.Name = "Posts";

        // A flat disc, drawn just above the terrain tiles so it reads as a
        // marking on the ground rather than as an object standing on it.
        _coverage = Pool(new CylinderMesh
        {
            TopRadius = 1f,
            BottomRadius = 1f,
            Height = 0.02f,
            RadialSegments = 24,
        }, shadows: false);
        _coverage.Name = "Coverage";
    }

    private MultiMeshInstance3D Pool(Mesh mesh, bool shadows)
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
                Transparency = shadows
                    ? BaseMaterial3D.TransparencyEnum.Disabled
                    : BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.9f,
            },
            CastShadow = shadows
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(node);
        return node;
    }

    public void Sync(World world)
    {
        var poles = world.Power.Poles;

        // Poles only change when something is built, so the buffers are rebuilt
        // then rather than every frame.
        if (poles.Count == _built)
            return;

        _built = poles.Count;

        if (_postBuffer.Length != poles.Count * FloatsPerInstance)
        {
            _postBuffer = new float[poles.Count * FloatsPerInstance];
            _coverageBuffer = new float[poles.Count * FloatsPerInstance];
        }

        for (var i = 0; i < poles.Count; i++)
        {
            var pole = poles[i];
            var x = pole.X + 0.5f;
            var z = pole.Y + 0.5f;

            Write(_postBuffer, i, x, 1.2f, z, 1f, 1f, new Color(0.55f, 0.45f, 0.32f, 1f));
            Write(_coverageBuffer, i, x, 0.02f, z,
                  pole.SupplyRadius, 1f, new Color(0.95f, 0.85f, 0.35f, 0.10f));
        }

        Upload(_posts, _postBuffer, poles.Count);
        Upload(_coverage, _coverageBuffer, poles.Count);
    }

    private static void Write(float[] buffer, int index, float x, float y, float z,
                              float plan, float lift, Color colour)
    {
        var o = index * FloatsPerInstance;
        buffer[o + 0] = plan; buffer[o + 1] = 0f;   buffer[o + 2] = 0f;    buffer[o + 3] = x;
        buffer[o + 4] = 0f;   buffer[o + 5] = lift; buffer[o + 6] = 0f;    buffer[o + 7] = y;
        buffer[o + 8] = 0f;   buffer[o + 9] = 0f;   buffer[o + 10] = plan; buffer[o + 11] = z;
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
