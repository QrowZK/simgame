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
    private MultiMeshInstance3D _pipes = null!;
    private float[] _postBuffer = System.Array.Empty<float>();
    private float[] _coverageBuffer = System.Array.Empty<float>();
    private float[] _pipeBuffer = System.Array.Empty<float>();
    private int _built = -1;
    private int _builtPipes = -1;

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

        // Pipes are drawn low and thin, so a run reads as plumbing on the
        // ground rather than as a wall between machines.
        _pipes = Pool(new BoxMesh { Size = new Vector3(0.44f, 0.30f, 0.44f) }, shadows: true);
        _pipes.Name = "Pipes";
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

    /// Pipe, tanks and pumps. Colour says what a node is and, for pipe, what it
    /// is carrying -- an empty line and a full one should not look the same
    /// when the question a player has is "is my oil getting there".
    public void SyncFluids(World world)
    {
        var nodes = world.Fluids.Nodes;
        if (nodes.Count == _builtPipes)
            return;

        _builtPipes = nodes.Count;

        if (_pipeBuffer.Length != nodes.Count * FloatsPerInstance)
            _pipeBuffer = new float[nodes.Count * FloatsPerInstance];

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var network = world.Fluids.NetworkAt(node.X, node.Y);
            var carrying = network >= 0 && world.Fluids.Network(network).Amount > 0;

            var colour = node.Kind switch
            {
                FluidNodeKind.Tank => new Color(0.62f, 0.66f, 0.70f, 1f),
                FluidNodeKind.Pump => new Color(0.85f, 0.62f, 0.25f, 1f),
                _ => carrying
                    ? new Color(0.35f, 0.62f, 0.80f, 1f)
                    : new Color(0.48f, 0.50f, 0.54f, 1f),
            };

            // Tanks stand taller and wider than pipe; pumps sit between.
            var plan = node.Kind switch
            {
                FluidNodeKind.Tank => 1.7f,
                FluidNodeKind.Pump => 1.2f,
                _ => 1f,
            };
            var lift = node.Kind switch
            {
                FluidNodeKind.Tank => 3.2f,
                FluidNodeKind.Pump => 1.8f,
                _ => 1f,
            };

            Write(_pipeBuffer, i, node.X + 0.5f, 0.15f * lift, node.Y + 0.5f, plan, lift, colour);
        }

        Upload(_pipes, _pipeBuffer, nodes.Count);
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
