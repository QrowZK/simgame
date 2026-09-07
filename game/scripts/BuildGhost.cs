using Godot;
using Sim;

namespace Game;

/// The thing under the cursor while build mode is on: the machine you are
/// about to place, drawn where it would land.
///
/// It uses the same hull and attachment meshes the real machine will use, so
/// what you see before you click is what you get after. A generic box would be
/// a lie about size on every machine that is not 1x1, and footprint is exactly
/// what a player is judging when they move the cursor.
///
/// Colour carries the only other question worth answering before the click:
/// green means this will work, red means it will not.
public sealed partial class BuildGhost : Node3D
{
    private static readonly Color Allowed = new(0.35f, 0.95f, 0.45f, 0.55f);
    private static readonly Color Refused = new(0.95f, 0.30f, 0.25f, 0.55f);

    private MeshInstance3D _hull = null!;
    private MeshInstance3D _attachment = null!;
    private StandardMaterial3D _material = null!;

    private int _builtTier = -1;
    private int _builtCategory = -1;

    public override void _Ready()
    {
        // One shared transparent material: the ghost is never lit like a real
        // machine, or a valid placement in shadow would read as an invalid one.
        _material = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Allowed,
        };

        _hull = new MeshInstance3D
        {
            Name = "Hull",
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _attachment = new MeshInstance3D
        {
            Name = "Attachment",
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        AddChild(_hull);
        AddChild(_attachment);
        Visible = false;
    }

    /// Moves the ghost to a tile and says whether it would be allowed there.
    ///
    /// `tileSize` comes from the renderer rather than being assumed, so the
    /// ghost cannot drift from the machines it is standing among.
    public void Show(Buildable buildable, int tileX, int tileY, bool allowed, float tileSize)
    {
        if (buildable.Tier != _builtTier || buildable.Category != _builtCategory)
        {
            _hull.Mesh = MeshKit.Hull(buildable.Tier);
            _attachment.Mesh = MeshKit.Attachment(buildable.Category);
            _builtTier = buildable.Tier;
            _builtCategory = buildable.Category;
        }

        // Same seating arithmetic as MachineRenderer.WriteOne. A ghost that sat
        // differently from the machine would mislead about exactly the thing it
        // exists to show.
        var placement = buildable.PlacementAt(tileX, tileY);
        var plan = placement.Size;
        var lift = 1f + (placement.Size - 1) * 0.5f;

        var x = (placement.CentreX - 0.5f) * tileSize;
        var z = (placement.CentreY - 0.5f) * tileSize;

        _hull.Position = new Vector3(x, 0f, z);
        _hull.Scale = new Vector3(plan, lift, plan);

        _attachment.Position = new Vector3(x, MeshKit.DeckHeight(buildable.Tier) * lift, z);
        _attachment.Scale = new Vector3(plan, lift, plan);

        _material.AlbedoColor = allowed ? Allowed : Refused;
        Visible = true;
    }
}
