using Godot;

namespace Game;

/// Host or join: one screen, two modes, because they are the same question
/// asked from two ends -- who am I, and where is the session.
///
/// Built like `SaveSelect` (ADR 0032): the same card, the same well, the same
/// tokens. The only thing here that is not styling is the refusal line, which
/// gets a whole row of its own and is the reason `NetSession` returns a
/// `NetFailure` rather than a bool.
public sealed partial class MultiplayerSetup : Control
{
    public enum Mode { Host, Join }

    /// Host: name, port, seed, max players. Join: name, address, port.
    [Signal] public delegate void HostRequestedEventHandler(
        string playerName, int port, int seed, int maxPlayers);

    [Signal] public delegate void JoinRequestedEventHandler(
        string playerName, string address, int port);

    [Signal] public delegate void BackRequestedEventHandler();

    private static readonly Color Muted = new(0.60f, 0.64f, 0.70f);
    private static readonly Color Starved = new(0.90f, 0.75f, 0.25f);

    private Label _title = null!;
    private Label _blurb = null!;
    private Label _status = null!;
    private Button _go = null!;
    private LineEdit _name = null!;
    private LineEdit _address = null!;
    private LineEdit _port = null!;
    private LineEdit _seed = null!;
    private LineEdit _players = null!;
    private HBoxContainer _addressRow = null!;
    private HBoxContainer _seedRow = null!;
    private HBoxContainer _playersRow = null!;

    private Mode _mode = Mode.Host;

    public override void _Ready()
    {
        const string rows = "Card/Margin/Rows";
        _title = GetNode<Label>($"{rows}/Head/Title");
        _blurb = GetNode<Label>($"{rows}/Blurb");
        _status = GetNode<Label>($"{rows}/Status");
        _go = GetNode<Button>($"{rows}/Foot/Go");
        _name = GetNode<LineEdit>($"{rows}/Fields/NameRow/Value");
        _addressRow = GetNode<HBoxContainer>($"{rows}/Fields/AddressRow");
        _address = GetNode<LineEdit>($"{rows}/Fields/AddressRow/Value");
        _port = GetNode<LineEdit>($"{rows}/Fields/PortRow/Value");
        _seedRow = GetNode<HBoxContainer>($"{rows}/Fields/SeedRow");
        _seed = GetNode<LineEdit>($"{rows}/Fields/SeedRow/Value");
        _playersRow = GetNode<HBoxContainer>($"{rows}/Fields/PlayersRow");
        _players = GetNode<LineEdit>($"{rows}/Fields/PlayersRow/Value");

        // A Label's minimum size is its full text, so an unbounded refusal
        // sentence pushes the card past the viewport. Clipped plus wrapped: it
        // gives way rather than growing. (The same trap as SaveSelect's parse
        // errors, found the same way -- by looking at a capture.)
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.ClipText = true;
        _status.CustomMinimumSize = new Vector2(0, 70);
        _blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _blurb.ClipText = true;
        _blurb.CustomMinimumSize = new Vector2(0, 48);

        _go.Pressed += Go;
        GetNode<Button>($"{rows}/Foot/Back").Pressed +=
            () => EmitSignal(SignalName.BackRequested);

        Apply();
    }

    /// Opens the screen in one of its two shapes. Safe before `_Ready`.
    public void Open(Mode mode)
    {
        _mode = mode;
        if (IsInsideTree() && _title is not null) Apply();
    }

    private void Apply()
    {
        var hosting = _mode == Mode.Host;

        _title.Text = hosting ? "Host a multiplayer world" : "Join a multiplayer world";
        _blurb.Text = hosting
            ? "Others join by typing this machine's address and the port below. " +
              $"Protocol {NetSession.Protocol}; everyone has to be on the same one."
            : $"You are on protocol {NetSession.Protocol}. A host on a different one " +
              "will say so rather than letting you in.";

        _addressRow.Visible = !hosting;
        _seedRow.Visible = hosting;
        _playersRow.Visible = hosting;
        _go.Text = hosting ? "Open the port" : "Connect";

        if (_port.Text.Length == 0) _port.Text = NetSession.DefaultPort.ToString();
        if (_players.Text.Length == 0) _players.Text = NetSession.DefaultMaxPlayers.ToString();
        if (!hosting && _address.Text.Length == 0) _address.Text = "127.0.0.1";

        _status.Text = "";
        _name.GrabFocus();
    }

    private void Go()
    {
        var name = NetSession.CleanName(_name.Text);

        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError(NetSession.Describe(NetFailure.BadPort));
            return;
        }

        if (_mode == Mode.Join)
        {
            var address = _address.Text.Trim();
            if (address.Length == 0)
            {
                ShowError(NetSession.Describe(NetFailure.BadAddress));
                return;
            }

            _status.AddThemeColorOverride("font_color", Muted);
            _status.Text = $"Connecting to {NetSession.Short(address)}:{port}...";
            EmitSignal(SignalName.JoinRequested, name, address, port);
            return;
        }

        // A blank seed means "surprise me", exactly as New Game does. Two ways
        // to spell the same idea would be a worse answer than one.
        var text = _seed.Text.Trim();
        int seed;
        if (text.Length == 0) seed = MainMenu.SurpriseMeSeed();
        else if (!int.TryParse(text, out seed)) seed = text.GetHashCode() & 0x7FFFFFFF;

        if (!int.TryParse(_players.Text.Trim(), out var players) || players < 2)
            players = NetSession.DefaultMaxPlayers;

        EmitSignal(SignalName.HostRequested, name, port, seed, players);
    }

    /// A refusal, in the amber the design system keeps for starved.
    public void ShowError(string message)
    {
        _status.AddThemeColorOverride("font_color", Starved);
        _status.Text = message;
    }

    /// What the screen is currently saying, for the headless run: a status line
    /// that came out empty looks identical to one that was never set.
    public string StatusText => _status?.Text ?? "";
}
