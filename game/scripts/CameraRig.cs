using Godot;

namespace Game;

/// Angled overhead camera: fixed pitch, freely rotatable yaw, zoomable, pannable.
/// Not free-look -- pitch is deliberately not exposed, so the world always reads
/// as a factory floor seen from above rather than a first-person scene.
///
/// Yaw rotates freely rather than snapping to 90 degrees. With 3D meshes there is
/// no sprite-facing constraint forcing snapped angles, and free rotation is what
/// makes 3D models worth having.
public sealed partial class CameraRig : Node3D
{
    [Export] public float Pitch { get; set; } = 50f;
    [Export] public float Yaw { get; set; } = 45f;
    [Export] public float ZoomLevel { get; set; } = 24f;
    [Export] public float MinZoom { get; set; } = 4f;
    [Export] public float MaxZoom { get; set; } = 160f;
    [Export] public float PanSpeed { get; set; } = 18f;
    [Export] public float RotateSpeed { get; set; } = 90f;
    [Export] public bool Orthographic { get; set; } = true;

    private Camera3D _camera = null!;

    public override void _Ready()
    {
        _camera = new Camera3D { Name = "Camera3D", Current = true };
        AddChild(_camera);
        Apply();
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var moved = false;

        // Yaw. Q/E, matching the usual factory-game binding.
        if (Input.IsKeyPressed(Key.Q)) { Yaw -= RotateSpeed * dt; moved = true; }
        if (Input.IsKeyPressed(Key.E)) { Yaw += RotateSpeed * dt; moved = true; }

        // Pan, relative to the current yaw so the controls stay intuitive after rotating.
        var pan = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) pan.Z -= 1f;
        if (Input.IsKeyPressed(Key.S)) pan.Z += 1f;
        if (Input.IsKeyPressed(Key.A)) pan.X -= 1f;
        if (Input.IsKeyPressed(Key.D)) pan.X += 1f;

        if (pan != Vector3.Zero)
        {
            pan = pan.Normalized().Rotated(Vector3.Up, Mathf.DegToRad(Yaw));
            // Pan faster when zoomed out, so traversing a large factory stays usable.
            Position += pan * PanSpeed * dt * (ZoomLevel / 24f);
            moved = true;
        }

        if (moved)
            Apply();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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
        _camera.LookAt(GlobalPosition, Vector3.Up);

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
