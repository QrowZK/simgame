using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;

namespace Game;

/// The 100 ms, drawn.
///
/// In a shared world a click does not take effect for six ticks (ADR 0038). A
/// player who clicks and sees nothing clicks again, and then again, and ends up
/// with three benches or a confusing refusal. So every action that is in flight
/// gets a marker on the tile it was aimed at, from the moment of the click
/// until the world answers.
///
/// It is deliberately **not** the build ghost's colours. Green means "this will
/// work" and red means "this will be refused" -- both are claims about the
/// outcome, and the outcome is exactly what nobody knows yet. This is amber and
/// see-through: it says *asked for*, not *done*, which is the honest state.
/// Nothing here is a prediction and nothing here touches the world.
public sealed partial class PendingActionsView : Node3D
{
    private static readonly Color Waiting = new(0.98f, 0.74f, 0.18f, 0.55f);

    private readonly List<MeshInstance3D> _pool = new();
    private StandardMaterial3D _material = null!;
    private BoxMesh _tile = null!;

    /// How many markers are on screen -- counted from what is **visible**, not
    /// from what was asked for.
    ///
    /// It counted intent first, and a mutation that placed every marker
    /// correctly and then left it hidden went unnoticed: the number was still
    /// right and nothing was on screen. That is this project's oldest bug shape
    /// (a label that is set and invisible) wearing a mesh, and the counter that
    /// was supposed to catch it was reporting the wrong thing.
    public int Drawn => _pool.Count(node => node.Visible);

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Waiting,
        };

        // A low slab rather than a full hull for the non-build actions: a dig
        // and a removal are things happening *to* a tile, and a machine-shaped
        // marker over a tile you are digging would read as a machine.
        _tile = new BoxMesh { Size = new Vector3(1f, 0.12f, 1f) };
    }

    /// Redraws every marker. Called once a frame; cheap, and cheaper than the
    /// class of bug where a marker is drawn once and then describes an action
    /// that was answered a minute ago.
    public void Show(IReadOnlyList<PendingAction> pending, float tileSize)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            var node = At(i);
            var action = pending[i];

            if (action.Ghost is not null)
            {
                var placement = action.Ghost.PlacementAt(action.X, action.Y);
                var plan = placement.Size;
                var lift = 1f + (placement.Size - 1) * 0.5f;

                node.Mesh = MeshKit.Hull(action.Ghost.Tier);
                node.Position = new Vector3((placement.CentreX - 0.5f) * tileSize, 0f,
                                            (placement.CentreY - 0.5f) * tileSize);
                node.Scale = new Vector3(plan, lift, plan);
            }
            else
            {
                node.Mesh = _tile;
                node.Position = new Vector3((action.X + 0.5f) * tileSize, 0.06f,
                                            (action.Y + 0.5f) * tileSize);
                node.Scale = new Vector3(tileSize, 1f, tileSize);
            }

            node.Visible = true;
        }

        for (var i = pending.Count; i < _pool.Count; i++) _pool[i].Visible = false;
    }

    private MeshInstance3D At(int index)
    {
        while (_pool.Count <= index)
        {
            var node = new MeshInstance3D
            {
                Name = $"Pending{_pool.Count}",
                MaterialOverride = _material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(node);
            _pool.Add(node);
        }

        return _pool[index];
    }
}
