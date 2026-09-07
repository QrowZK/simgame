using Godot;
using Sim;

namespace Game;

/// Where the player writes the Lua that commands the drones.
///
/// Applying a program compiles it immediately, so a syntax error is a message
/// under the box while the player is still looking at the code -- not a silent
/// failure discovered later when the drones do nothing.
///
/// The editor shows the controller's `print` output, which is the only way a
/// player can debug a program that runs inside the tick. Without it, writing
/// anything non-trivial would be guesswork.
public sealed partial class ScriptEditor : PanelContainer
{
    /// What a new controller starts with. A working program rather than an
    /// empty box: the fastest way to learn this API is to edit something that
    /// already runs.
    public const string Template = @"-- Keep the smelter fed from the mine.
-- Runs forever; world.sleep gives the rest of the factory its turn.
local mineX, mineY = 0, 0
local smelterX, smelterY = 20, 6

while true do
  local waiting = inventory.output(mineX, mineY, 'magnetite')

  if waiting >= 20 and queue.pending() < 2 then
    queue.haul('magnetite', 20, mineX, mineY, smelterX, smelterY)
    print('queued a haul; ' .. waiting .. ' waiting at the mine')
  end

  world.sleep(30)
end
";

    [Signal] public delegate void ClosedEventHandler();

    private TextEdit _code = null!;
    private Label _status = null!;
    private RichTextLabel _log = null!;
    private Label _which = null!;

    private World? _world;
    private Controller? _controller;

    public override void _Ready()
    {
        _code = GetNode<TextEdit>("Rows/Code");
        _status = GetNode<Label>("Rows/Status");
        _log = GetNode<RichTextLabel>("Rows/Log");
        _which = GetNode<Label>("Rows/Buttons/Which");

        GetNode<Button>("Rows/Buttons/Apply").Pressed += Apply;
        GetNode<Button>("Rows/Buttons/Close").Pressed += () => EmitSignal(SignalName.Closed);

        GetParent<Control>().Visible = false;
    }

    /// Opens on a world's first controller, creating one from the template if
    /// there is none yet.
    public void Open(World world)
    {
        _world = world;
        _controller = world.Controllers.Count > 0 ? world.Controllers[0] : null;

        _code.Text = _controller?.Source ?? Template;
        _status.Text = _controller?.Error ?? "";
        _which.Text = _controller is null
            ? "no controller yet - Apply creates one"
            : $"controller 1 of {world.Controllers.Count}";

        GetParent<Control>().Visible = true;
        _code.GrabFocus();
        Refresh();
    }

    public void Close() => GetParent<Control>().Visible = false;

    public bool IsOpen => GetParent<Control>().Visible;

    public override void _Process(double delta)
    {
        if (IsOpen) Refresh();
    }

    private void Refresh()
    {
        if (_controller is null) return;

        _status.Text = _controller.Error ?? "";
        _log.Text = string.Join("\n", _controller.Log);
    }

    /// Compiles and installs the program.
    ///
    /// Replacing a running controller restarts it from the top, which is the
    /// same thing a save reload does -- so there is one rule to learn about
    /// when a program starts over, not two.
    private void Apply()
    {
        if (_world is null) return;

        var source = _code.Text;

        if (_controller is null)
        {
            _controller = _world.AddController(source);
            _which.Text = $"controller 1 of {_world.Controllers.Count}";
        }
        else
        {
            _controller.Replace(source, _world);
        }

        _status.Text = _controller.Error ?? "running";
        Refresh();
    }
}
