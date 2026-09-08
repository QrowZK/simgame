using Godot;

namespace Game;

/// The in-game menu: resume, save, leave.
///
/// Opening it pauses the simulation. That is not just a courtesy -- saving a
/// world that is still ticking would capture a state no single tick ever
/// produced, and the whole point of the save format is that it round-trips
/// exactly.
public sealed partial class PauseMenu : Control
{
    [Signal] public delegate void ResumeRequestedEventHandler();
    [Signal] public delegate void MainMenuRequestedEventHandler();

    private LineEdit _name = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        _name = GetNode<LineEdit>("Card/Rows/NameRow/SaveName");
        _status = GetNode<Label>("Card/Rows/Status");

        // Every binding in the game, in the one place a player goes when they
        // want to know what a game can do.
        //
        // The HUD used to carry this list, always, whatever the player was
        // doing -- a reference card pinned across the top of the screen. Making
        // that line contextual was right, but it left nowhere to look up a key
        // you had forgotten. Pausing to check is the ordinary way to do that,
        // and it is the ordinary place to find it.
        GetNode<Label>("Card/Rows/Controls").Text =
            "WASD  walk\n" +
            "Q / E  rotate     ·  mouse wheel  zoom\n" +
            "click  inspect a machine, or dig a resource tile\n" +
            "B  build     ·  R  rotate what you are holding\n" +
            "X  remove a building, and get it back\n" +
            "P  survey what is nearby\n" +
            "T  progression: what to deliver, and why things are locked\n" +
            "F1  script editor for drone controllers\n" +
            "F5  quick save     ·  F9  quick load\n" +
            "Esc  close what is open, or pause";

        GetNode<Button>("Card/Rows/Resume").Pressed += () => EmitSignal(SignalName.ResumeRequested);
        GetNode<Button>("Card/Rows/Save").Pressed += OnSave;
        GetNode<Button>("Card/Rows/MainMenu").Pressed += () => EmitSignal(SignalName.MainMenuRequested);
        GetNode<Button>("Card/Rows/Quit").Pressed += () => GetTree().Quit();

        Visible = false;
    }

    public void Open()
    {
        Visible = true;
        _status.Text = "";
        GetNode<Button>("Card/Rows/Resume").GrabFocus();
    }

    public void Close() => Visible = false;

    private void OnSave()
    {
        try
        {
            GameSession.Save(_name.Text);
            _status.Text = $"Saved as \"{_name.Text}\".";
        }
        catch (System.Exception e)
        {
            // Say what went wrong. A save button that silently does nothing is
            // how people lose factories.
            _status.Text = "Could not save: " + e.Message;
            GD.PushWarning($"save failed: {e.Message}");
        }
    }
}
