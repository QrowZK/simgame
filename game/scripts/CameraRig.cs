using Godot;

namespace Game;

/// Angled overhead camera: fixed pitch, freely rotatable yaw, zoomable, following.
/// Not free-look -- pitch is deliberately not exposed, so the world always reads
/// as a factory floor seen from above rather than a first-person scene.
///
/// Yaw rotates freely rather than snapping to 90 degrees. With 3D meshes there is
/// no sprite-facing constraint forcing snapped angles, and free rotation is what
/// makes 3D models worth having.
///
/// It no longer pans. WASD moves the *player* now (ADR 0033) and the rig
/// follows; the rig keeps yaw, zoom and the ground-picking ray, which are the
/// three things that are genuinely about looking rather than about being
/// somewhere.
public sealed partial class CameraRig : Node3D
{
    [Export] public float Pitch { get; set; } = 50f;
    [Export] public float Yaw { get; set; } = 45f;
    [Export] public float ZoomLevel { get; set; } = 24f;
    [Export] public float MinZoom { get; set; } = 4f;
    [Export] public float MaxZoom { get; set; } = 160f;
    [Export] public float RotateSpeed { get; set; } = 90f;

    /// How quickly the rig closes the gap to whoever it is following, as a
    /// fraction of the remaining distance per second.
    ///
    /// It trails rather than recentring instantly, and that is a decision, not
    /// an ease-in: a rig locked to the player makes the *world* the thing that
    /// slides while the player stays nailed to the middle of the screen, which
    /// reads as the ground moving under a static figure. Trailing lets the
    /// player pull ahead of centre in the direction they are walking, so the
    /// screen shows more of where they are going than where they have been --
    /// and at 8 per second the lag is about an eighth of a second, well under
    /// the point where the camera feels like it is being dragged.
    [Export] public float FollowRate { get; set; } = 8f;

    /// Past this many tiles the rig cuts rather than trails. A load, a
    /// teleport, or a capture path framing something across the map is not a
    /// walk, and smoothly flying two hundred tiles is a long look at nothing.
    [Export] public float SnapDistance { get; set; } = 40f;
    [Export] public bool Orthographic { get; set; } = true;

    private Camera3D _camera = null!;

    public override void _Ready()
    {
        _camera = new Camera3D { Name = "Camera3D", Current = true };
        AddChild(_camera);
        Apply();
    }

    /// Set false while a text field has the player's keystrokes.
    ///
    /// The camera polls the keyboard directly rather than consuming input
    /// events, which is right for a held key and wrong the moment there is
    /// somewhere to type: without this, writing "was" in the script editor
    /// spins the camera. `GameRoot` reads the same flag before it hands the
    /// player a walk intent, so typing does not walk you into the sea either.
    public bool InputEnabled { get; set; } = true;

    public override void _Process(double delta)
    {
        if (!InputEnabled)
            return;

        var dt = (float)delta;
        var moved = false;

        // Yaw. Q/E, matching the usual factory-game binding.
        if (Input.IsKeyPressed(Key.Q)) { Yaw -= RotateSpeed * dt; moved = true; }
        if (Input.IsKeyPressed(Key.E)) { Yaw += RotateSpeed * dt; moved = true; }

        if (moved)
            Apply();
    }

    /// Moves the rig towards a point on the ground. Called once a frame by
    /// `GameRoot` with the player's position.
    ///
    /// Frame-rate independent: the fraction closed is derived from the elapsed
    /// time rather than applied per frame, so a 30 fps machine and a 144 fps one
    /// trail by the same distance. This is float arithmetic and that is fine --
    /// it decides where a camera is, and nothing in the sim can see it.
    public void Follow(Vector3 target, float delta)
    {
        var offset = target - Position;

        if (offset.Length() > SnapDistance)
        {
            Position = target;
            Apply();
            return;
        }

        if (offset.LengthSquared() < 0.000001f)
            return;

        Position += offset * (1f - Mathf.Exp(-FollowRate * delta));
        Apply();
    }

    /// Puts the rig exactly on a point, with no trailing. For a new game, a
    /// load, and the capture paths that frame something deliberately.
    public void SnapTo(Vector3 target)
    {
        Position = target;
        Apply();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!InputEnabled)
            return;

        if (@event is not InputEventMouseButton { Pressed: true } button)
            return;

        var before = ZoomLevel;
        if (button.ButtonIndex == MouseButton.WheelUp) ZoomLevel *= 0.9f;
        if (button.ButtonIndex == MouseButton.WheelDown) ZoomLevel /= 0.9f;

        ZoomLevel = Mathf.Clamp(ZoomLevel, MinZoom, MaxZoom);
        if (!Mathf.IsEqualApprox(before, ZoomLevel))
            Apply();
    }

    /// Rebuilds the camera transform from pitch/yaw/zoom. Public so tests and the
    /// headless smoke run can drive it without an input device.
    public void Apply()
    {
        if (_camera is null)
            return;

        var pitchRad = Mathf.DegToRad(Pitch);
        var yawRad = Mathf.DegToRad(Yaw);

        // Orbit the rig origin at a fixed pitch.
        var offset = new Vector3(0f, Mathf.Sin(pitchRad), Mathf.Cos(pitchRad)) * ZoomLevel;
        offset = offset.Rotated(Vector3.Up, yawRad);

        _camera.Position = offset;

        // Aimed in rig-local space, not with LookAt. LookAt reads the camera's
        // *global* transform, which Godot has not yet recomputed when the rig
        // was moved earlier in the same frame -- so a camera pointed at a
        // freshly placed machine aimed at where the rig used to be, and the
        // machine fell outside the frame. The rig is never rotated (yaw is
        // baked into `offset`), so local and global orientation agree.
        _camera.Basis = Basis.LookingAt(-offset, Vector3.Up);

        if (Orthographic)
        {
            _camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _camera.Size = ZoomLevel;
        }
        else
        {
            // Long focal length keeps perspective distortion low, so the view still
            // reads as an angled overhead rather than a dramatic 3D shot.
            _camera.Projection = Camera3D.ProjectionType.Perspective;
            _camera.Fov = 25f;
        }

        _camera.Near = 0.1f;
        _camera.Far = ZoomLevel * 8f + 100f;
    }

    public Camera3D Camera => _camera;

    /// Where a screen position lands on the ground plane. Ray-plane rather than
    /// physics picking: machines are MultiMesh instances with no colliders, and
    /// giving 100k of them collision shapes to support a click would cost more
    /// than the entire sim tick.
    public bool TryGroundPoint(Vector2 screen, out Vector3 point)
    {
        point = Vector3.Zero;
        if (_camera is null) return false;

        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);

        // Parallel to the ground: no intersection to report.
        if (Mathf.Abs(direction.Y) < 0.0001f) return false;

        var distance = -origin.Y / direction.Y;
        if (distance < 0f) return false;        // the plane is behind the camera

        point = origin + direction * distance;
        return true;
    }
}
