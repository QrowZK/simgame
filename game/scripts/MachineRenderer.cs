using Godot;
using Sim;

namespace Game;

/// Draws every machine in the world with a handful of MultiMesh batches.
///
/// One MultiMeshInstance3D per hull mesh (tier) and per attachment mesh
/// (category) -- not one Node per entity. At the 100k-entity target a Node per
/// entity is fatal, so placements are read out of the sim as a dense span and
/// written straight into instance buffers.
///
/// Instances are uploaded via MultiMesh.Buffer rather than SetInstanceTransform/
/// SetInstanceColor. The per-instance setters cross the C#/engine boundary twice
/// per entity per frame, which measured at 3.3 ms for only 4096 machines -- most
/// of a 60 UPS frame budget spent on marshalling. Filling a managed float array
/// and handing it over in one call per pool moves that work to plain memory
/// writes and leaves 18 interop calls per frame in total.
public sealed partial class MachineRenderer : Node3D
{
    /// 12 floats of 3x4 transform, then 4 of colour. Matches MultiMesh's layout
    /// when TransformFormat is Transform3D and UseColors is on.
    private const int FloatsPerInstance = 16;

    private readonly MultiMeshInstance3D[] _hullPools = new MultiMeshInstance3D[MeshKit.TierCount];
    private readonly MultiMeshInstance3D[] _attachPools = new MultiMeshInstance3D[MeshKit.CategoryCount];
    private readonly float[]?[] _hullBuffers = new float[MeshKit.TierCount][];
    private readonly float[]?[] _attachBuffers = new float[MeshKit.CategoryCount][];
    private readonly int[] _hullCounts = new int[MeshKit.TierCount];
    private readonly int[] _attachCounts = new int[MeshKit.CategoryCount];
    private readonly int[] _hullCursor = new int[MeshKit.TierCount];
    private readonly int[] _attachCursor = new int[MeshKit.CategoryCount];

    public float TileSize { get; set; } = 1.0f;
    public float AttachmentHeight { get; set; } = 0.5f;

    public override void _Ready()
    {
        for (var tier = 0; tier < MeshKit.TierCount; tier++)
            _hullPools[tier] = CreatePool(MeshKit.Hull(tier), $"HullPool{tier}");

        for (var category = 0; category < MeshKit.CategoryCount; category++)
            _attachPools[category] = CreatePool(MeshKit.Attachment(category), $"AttachPool{category}");
    }

    private MultiMeshInstance3D CreatePool(Mesh mesh, string name)
    {
        // Order matters: format flags must be set before InstanceCount.
        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
        };

        var node = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multiMesh,
            MaterialOverride = MeshKit.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };

        AddChild(node);
        return node;
    }

    /// Refills every instance buffer from the sim's dense arrays. Called once per
    /// frame, not once per entity per frame.
    public void Sync(World world)
    {
        var placements = world.Placements;
        var states = world.MachineStates;

        System.Array.Clear(_hullCounts);
        System.Array.Clear(_attachCounts);
        for (var i = 0; i < placements.Length; i++)
        {
            _hullCounts[Clamp(placements[i].Tier, MeshKit.TierCount)]++;
            _attachCounts[Clamp(placements[i].Category, MeshKit.CategoryCount)]++;
        }

        // MultiMesh.Buffer must be exactly InstanceCount * stride long, so buffers
        // are resized only when a count actually changes -- normally never.
        for (var tier = 0; tier < MeshKit.TierCount; tier++)
            EnsureBuffer(ref _hullBuffers[tier], _hullCounts[tier]);
        for (var category = 0; category < MeshKit.CategoryCount; category++)
            EnsureBuffer(ref _attachBuffers[category], _attachCounts[category]);

        System.Array.Clear(_hullCursor);
        System.Array.Clear(_attachCursor);

        for (var i = 0; i < placements.Length; i++)
        {
            var placement = placements[i];
            var tier = Clamp(placement.Tier, MeshKit.TierCount);
            var category = Clamp(placement.Category, MeshKit.CategoryCount);

            var x = placement.X * TileSize;
            var z = placement.Y * TileSize;

            Write(_hullBuffers[tier]!, _hullCursor[tier]++, x, 0f, z, MeshKit.TierColor(tier));
            Write(_attachBuffers[category]!, _attachCursor[category]++,
                  x, AttachmentHeight, z, MeshKit.StateColor(states[i]));
        }

        for (var tier = 0; tier < MeshKit.TierCount; tier++)
            Upload(_hullPools[tier], _hullBuffers[tier]!, _hullCounts[tier]);
        for (var category = 0; category < MeshKit.CategoryCount; category++)
            Upload(_attachPools[category], _attachBuffers[category]!, _attachCounts[category]);
    }

    /// Writes one instance: an axis-aligned transform plus its colour. Machines
    /// are not rotated, so the basis is identity; a rotated instance would fill
    /// the same 3x4 slots with its basis rows instead.
    private static void Write(float[] buffer, int index, float x, float y, float z, Color color)
    {
        var o = index * FloatsPerInstance;
        buffer[o + 0] = 1f; buffer[o + 1] = 0f; buffer[o + 2] = 0f; buffer[o + 3] = x;
        buffer[o + 4] = 0f; buffer[o + 5] = 1f; buffer[o + 6] = 0f; buffer[o + 7] = y;
        buffer[o + 8] = 0f; buffer[o + 9] = 0f; buffer[o + 10] = 1f; buffer[o + 11] = z;
        buffer[o + 12] = color.R;
        buffer[o + 13] = color.G;
        buffer[o + 14] = color.B;
        buffer[o + 15] = color.A;
    }

    private static void EnsureBuffer(ref float[]? buffer, int count)
    {
        var needed = count * FloatsPerInstance;
        if (buffer is null || buffer.Length != needed)
            buffer = new float[needed];
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

    private static int Clamp(byte value, int exclusiveMax) =>
        value >= exclusiveMax ? exclusiveMax - 1 : value;

    public int BatchCount => MeshKit.TierCount + MeshKit.CategoryCount;
}
