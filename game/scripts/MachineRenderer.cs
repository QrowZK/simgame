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
            // Placeholders share one material; authored meshes keep their own,
            // which is where the texture lives.
            MaterialOverride = MeshKit.IsAuthored(mesh) ? null : MeshKit.Material,
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
        var miners = world.MinerPlacements;
        var generators = world.Power.GeneratorPlacements;
        var accumulators = world.Power.AccumulatorPlacements;

        System.Array.Clear(_hullCounts);
        System.Array.Clear(_attachCounts);
        for (var i = 0; i < placements.Length; i++)
        {
            _hullCounts[Clamp(placements[i].Tier, MeshKit.TierCount)]++;
            _attachCounts[Clamp(placements[i].Category, MeshKit.CategoryCount)]++;
        }

        // Miners draw from the same mesh kit as machines -- they are machines as
        // far as the renderer is concerned, and giving them their own pools
        // would double the batch count for one more silhouette.
        for (var i = 0; i < miners.Count; i++)
        {
            _hullCounts[Clamp(miners[i].Tier, MeshKit.TierCount)]++;
            _attachCounts[Clamp(miners[i].Category, MeshKit.CategoryCount)]++;
        }

        for (var i = 0; i < generators.Count; i++)
        {
            _hullCounts[Clamp(generators[i].Tier, MeshKit.TierCount)]++;
            _attachCounts[Clamp(generators[i].Category, MeshKit.CategoryCount)]++;
        }

        // Accumulators too: same hull kit as machines and miners. Their own
        // pool would cost a draw batch for one more silhouette.
        for (var i = 0; i < accumulators.Count; i++)
        {
            _hullCounts[Clamp(accumulators[i].Tier, MeshKit.TierCount)]++;
            _attachCounts[Clamp(accumulators[i].Category, MeshKit.CategoryCount)]++;
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
            WriteOne(placements[i], states[i]);

        for (var i = 0; i < miners.Count; i++)
            WriteOne(miners[i], world.Miners[i].State);

        for (var i = 0; i < generators.Count; i++)
            WriteOne(generators[i], world.Power.Generators[i].State);

        // An accumulator is tinted by how full it is rather than by machine
        // state. Working/Blocked/Starved would only say "charging, full,
        // empty", and the question a player has standing in front of a bank is
        // how much is left in it -- so the cell reads as a gauge.
        for (var i = 0; i < accumulators.Count; i++)
        {
            var accumulator = world.Power.Accumulators[i];
            var level = accumulator.Capacity <= 0
                ? 0f
                : (float)accumulator.Charge / accumulator.Capacity;
            WriteOne(accumulators[i], ChargeColor(level));
        }

        for (var tier = 0; tier < MeshKit.TierCount; tier++)
            Upload(_hullPools[tier], _hullBuffers[tier]!, _hullCounts[tier]);
        for (var category = 0; category < MeshKit.CategoryCount; category++)
            Upload(_attachPools[category], _attachBuffers[category]!, _attachCounts[category]);
    }

    /// Places one hull and its attachment. Machines and miners both come
    /// through here, so their sizing and seating can never drift apart.
    /// A fuel gauge: red empty, amber half, green full.
    ///
    /// A single dark-to-bright ramp was tried first and could not be read at
    /// play distance -- a bank at 20% and one at 80% both looked green-ish on a
    /// cylinder that is a few pixels wide. Two segments through amber gives
    /// each third its own hue, which survives the zoom.
    private static Color ChargeColor(float level)
    {
        level = Mathf.Clamp(level, 0f, 1f);
        var empty = new Color(0.85f, 0.20f, 0.16f);
        var half = new Color(0.95f, 0.72f, 0.15f);
        var full = new Color(0.35f, 0.90f, 0.32f);
        return level < 0.5f
            ? empty.Lerp(half, level * 2f)
            : half.Lerp(full, (level - 0.5f) * 2f);
    }

    private void WriteOne(in MachinePlacement placement, MachineState state) =>
        WriteOne(placement, MeshKit.StateColor(state));

    private void WriteOne(in MachinePlacement placement, Color attachment)
    {
        var tier = Clamp(placement.Tier, MeshKit.TierCount);
        var category = Clamp(placement.Category, MeshKit.CategoryCount);

        // Placements anchor on the footprint's corner; meshes are centred on
        // theirs. The -0.5f keeps a 1x1 exactly where it always sat.
        var x = (placement.CentreX - 0.5f) * TileSize;
        var z = (placement.CentreY - 0.5f) * TileSize;

        // Footprint scales the plan fully; height grows at half that rate. A
        // 3x3 raised to three times the height reads as a tower rather than as
        // a bigger machine, and buries its neighbours in shadow.
        var plan = placement.Size;
        var lift = 1f + (placement.Size - 1) * 0.5f;

        Write(_hullBuffers[tier]!, _hullCursor[tier]++, x, 0f, z,
              MeshKit.TierColor(tier), plan, lift);

        // The attachment lifts with its hull, not with its plan: at full plan
        // scale a 3x3's drum is taller than the body it sits on and overhangs.
        Write(_attachBuffers[category]!, _attachCursor[category]++,
              x, MeshKit.DeckHeight(tier) * lift, z,
              attachment, plan, lift);
    }

    /// Writes one instance: an axis-aligned, axis-scaled transform plus colour.
    /// Machines are not rotated, so the basis is diagonal; a rotated instance
    /// would fill the same 3x4 slots with its basis rows instead.
    private static void Write(float[] buffer, int index, float x, float y, float z, Color color,
                              float plan = 1f, float lift = 1f)
    {
        var o = index * FloatsPerInstance;
        buffer[o + 0] = plan; buffer[o + 1] = 0f;   buffer[o + 2] = 0f;   buffer[o + 3] = x;
        buffer[o + 4] = 0f;   buffer[o + 5] = lift; buffer[o + 6] = 0f;   buffer[o + 7] = y;
        buffer[o + 8] = 0f;   buffer[o + 9] = 0f;   buffer[o + 10] = plan; buffer[o + 11] = z;
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
