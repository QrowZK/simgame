using Godot;
using Sim;

namespace Game;

/// Draws the drone fleet.
///
/// Separate from the machine renderer because drones are the one thing on the
/// map that moves every tick: their buffer is rebuilt every frame, where the
/// machines' is rebuilt only when something is built. Mixing the two would mean
/// paying the moving cost for the static majority.
///
/// A drone is a body plus a cargo cube that only appears when it is carrying
/// something, so "is my fleet actually shifting anything" is answerable from
/// across the factory without opening a panel.
public sealed partial class DroneRenderer : Node3D
{
    private const int FloatsPerInstance = 16;

    /// How high drones fly. Above the tallest machine, so a drone crossing the
    /// factory never disappears behind one.
    private const float CruiseHeight = 3.2f;

    private MultiMeshInstance3D _bodies = null!;
    private MultiMeshInstance3D _cargo = null!;
    private float[] _bodyBuffer = System.Array.Empty<float>();
    private float[] _cargoBuffer = System.Array.Empty<float>();

    public override void _Ready()
    {
        _bodies = Pool(new BoxMesh { Size = new Vector3(0.72f, 0.26f, 0.72f) }, "Bodies");
        _cargo = Pool(new BoxMesh { Size = new Vector3(0.38f, 0.38f, 0.38f) }, "Cargo");
    }

    private MultiMeshInstance3D Pool(Mesh mesh, string name)
    {
        var node = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
            },
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.6f,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };

        AddChild(node);
        return node;
    }

    public void Sync(World world)
    {
        var drones = world.Logistics.Drones;

        if (_bodyBuffer.Length != drones.Count * FloatsPerInstance)
        {
            _bodyBuffer = new float[drones.Count * FloatsPerInstance];
            _cargoBuffer = new float[drones.Count * FloatsPerInstance];
        }

        var carrying = 0;

        for (var i = 0; i < drones.Count; i++)
        {
            var drone = drones[i];
            var x = drone.X + 0.5f;
            var z = drone.Y + 0.5f;

            // Idle drones sit lower than working ones. A fleet with nothing to
            // do reads as a fleet with nothing to do.
            var height = drone.IsIdle ? CruiseHeight - 0.8f : CruiseHeight;

            var colour = drone.IsIdle
                ? new Color(0.55f, 0.58f, 0.62f)
                : new Color(0.95f, 0.80f, 0.30f);

            Write(_bodyBuffer, i, x, height, z, colour);

            if (drone.CargoCount > 0)
                Write(_cargoBuffer, carrying++, x, height + 0.30f, z,
                      new Color(0.45f, 0.72f, 0.45f));
        }

        Upload(_bodies, _bodyBuffer, drones.Count);
        Upload(_cargo, _cargoBuffer, carrying);
    }

    private static void Write(float[] buffer, int index, float x, float y, float z, Color colour)
    {
        var o = index * FloatsPerInstance;
        buffer[o + 0] = 1f; buffer[o + 1] = 0f; buffer[o + 2] = 0f; buffer[o + 3] = x;
        buffer[o + 4] = 0f; buffer[o + 5] = 1f; buffer[o + 6] = 0f; buffer[o + 7] = y;
        buffer[o + 8] = 0f; buffer[o + 9] = 0f; buffer[o + 10] = 1f; buffer[o + 11] = z;
        buffer[o + 12] = colour.R;
        buffer[o + 13] = colour.G;
        buffer[o + 14] = colour.B;
        buffer[o + 15] = 1f;
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
