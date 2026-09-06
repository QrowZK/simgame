using Godot;

namespace Game;

/// Placeholder art, built procedurally so the render path can be exercised
/// before any real models exist.
///
/// The important part is the structure, not the shapes: a machine is drawn as a
/// shared HULL (chosen by tier) plus a function ATTACHMENT (chosen by category).
/// That mirrors how machines are actually crafted in data -- hull + function
/// part -- and it is what keeps ~130 machine variants down to ~18 meshes.
///
/// It also decides rendering cost. MultiMesh batches per unique mesh, so a
/// bespoke mesh per machine variant would mean ~130 draw batches where this
/// gives 18, with per-instance colour carrying the tier and status differences.
public static class MeshKit
{
    public const int TierCount = 8;
    public const int CategoryCount = 10;

    private static readonly Mesh[] Hulls = new Mesh[TierCount];
    private static readonly Mesh[] Attachments = new Mesh[CategoryCount];
    private static StandardMaterial3D? _material;

    /// Per-instance colour is used as albedo, so every tier and machine state
    /// shares one material and therefore one batch.
    public static StandardMaterial3D Material =>
        _material ??= new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.65f,
            Metallic = 0.35f,
        };

    /// Authored models take precedence over placeholders, so an art pipeline can
    /// land parts one at a time without any renderer change. Drop a mesh at
    /// res://models/hull_3.glb (or .tres) and tier 3 starts using it.
    private static Mesh? TryLoadAuthored(string baseName)
    {
        foreach (var ext in new[] { ".tres", ".res", ".glb", ".gltf" })
        {
            var path = $"res://models/{baseName}{ext}";
            if (!ResourceLoader.Exists(path))
                continue;

            var resource = ResourceLoader.Load(path);
            if (resource is Mesh mesh)
                return mesh;

            // .glb imports as a scene; lift the first mesh out of it.
            if (resource is PackedScene scene)
            {
                var root = scene.Instantiate();
                var found = FindFirstMesh(root);
                root.QueueFree();
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static Mesh? FindFirstMesh(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } instance)
            return instance.Mesh;

        foreach (var child in node.GetChildren())
        {
            var found = FindFirstMesh(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    public static Mesh Hull(int tier)
    {
        tier = Mathf.Clamp(tier, 0, TierCount - 1);
        if (Hulls[tier] is not null)
            return Hulls[tier];

        // Hulls grow squatter and wider up the ladder, so tier reads at a glance
        // even before real models land.
        var height = 0.45f + tier * 0.07f;
        var mesh = TryLoadAuthored($"hull_{tier}") ?? new BoxMesh
        {
            Size = new Vector3(0.9f, height, 0.9f),
            Material = Material,
        };
        Hulls[tier] = mesh;
        return mesh;
    }

    public static Mesh Attachment(int category)
    {
        category = Mathf.Clamp(category, 0, CategoryCount - 1);
        if (Attachments[category] is not null)
            return Attachments[category];

        var authored = TryLoadAuthored($"attach_{category}");
        if (authored is not null)
        {
            Attachments[category] = authored;
            return authored;
        }

        Mesh mesh = category switch
        {
            0 => new BoxMesh { Size = new Vector3(0.5f, 0.35f, 0.5f) },              // smelting
            1 => new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f, Height = 0.4f },  // crushing
            2 => new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.3f, Height = 0.45f },  // separating
            3 => new SphereMesh { Radius = 0.26f, Height = 0.52f },                  // chemistry
            4 => new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.34f },         // fluid handling
            5 => new PrismMesh { Size = new Vector3(0.55f, 0.4f, 0.55f) },           // shaping
            6 => new BoxMesh { Size = new Vector3(0.7f, 0.18f, 0.35f) },             // assembly
            7 => new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.2f, Height = 0.6f },    // power
            8 => new CapsuleMesh { Radius = 0.22f, Height = 0.6f },                  // extraction
            _ => new SphereMesh { Radius = 0.3f, Height = 0.6f },                    // exotic
        };

        if (mesh is PrimitiveMesh primitive)
            primitive.Material = Material;

        Attachments[category] = mesh;
        return mesh;
    }

    /// Tier tint, dark and industrial low down, brighter and more exotic high up.
    public static Color TierColor(int tier) => tier switch
    {
        0 => new Color(0.55f, 0.52f, 0.48f),
        1 => new Color(0.72f, 0.45f, 0.22f),
        2 => new Color(0.45f, 0.50f, 0.58f),
        3 => new Color(0.62f, 0.66f, 0.70f),
        4 => new Color(0.55f, 0.62f, 0.72f),
        5 => new Color(0.40f, 0.58f, 0.62f),
        6 => new Color(0.65f, 0.60f, 0.85f),
        7 => new Color(0.85f, 0.82f, 0.95f),
        _ => Colors.White,
    };

    /// Status tint for the attachment, so a stalled factory is visible at a glance.
    public static Color StateColor(Sim.MachineState state) => state switch
    {
        Sim.MachineState.Working => new Color(0.35f, 0.85f, 0.40f),
        Sim.MachineState.Starved => new Color(0.90f, 0.75f, 0.25f),
        Sim.MachineState.Blocked => new Color(0.90f, 0.35f, 0.30f),
        _ => new Color(0.55f, 0.55f, 0.58f),
    };
}
