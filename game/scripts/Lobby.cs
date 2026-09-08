using System.Collections.Generic;
using Godot;

namespace Game;

/// Who is here, before anything starts.
///
/// The lobby exists because a shared world begins with a decision that is not
/// the game's: am I waiting for one more person, or are we all here. It is a
/// list and a Start button, and the honest thing about it today is that Start
/// cannot yet start anything -- the world does not travel over the wire until
/// the command layer lands. It says so on the screen rather than pretending.
///
/// Built like `SaveSelect`: same card, same well, same tokens.
public sealed partial class Lobby : Control
{
    [Signal] public delegate void StartRequestedEventHandler();

    /// Leave the session. Also fires when the host closes on you, so the screen
    /// is never left describing a session that has gone.
    [Signal] public delegate void LeaveRequestedEventHandler();

    private static readonly Color Muted = new(0.60f, 0.64f, 0.70f);
    private static readonly Color Body = new(0.875f, 0.875f, 0.875f);
    private static readonly Color Starved = new(0.90f, 0.75f, 0.25f);
    private static readonly Color Divider = new(0.122f, 0.141f, 0.173f);

    private VBoxContainer _slots = null!;
    private Label _title = null!;
    private Label _where = null!;
    private Label _count = null!;
    private Label _status = null!;
    private Button _start = null!;

    private readonly List<PanelContainer> _rows = new();
    private NetSession? _session;

    public override void _Ready()
    {
        const string rows = "Card/Margin/Rows";
        _slots = GetNode<VBoxContainer>($"{rows}/Body/Well/Scroll/Slots");
        _title = GetNode<Label>($"{rows}/Head/Title");
        _where = GetNode<Label>($"{rows}/Head/Where");
        _count = GetNode<Label>($"{rows}/Head/Count");
        _status = GetNode<Label>($"{rows}/Status");
        _start = GetNode<Button>($"{rows}/Foot/Start");

        // Wrapped so a long sentence stays inside the card, clipped so its
        // minimum width is zero rather than its full text, and given a height
        // of its own because a clipped label with no minimum size is handed
        // none and draws nothing. The first capture of this screen had exactly
        // that: an amber refusal that was set, and invisible.
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.ClipText = true;
        _status.CustomMinimumSize = new Vector2(0, 70);

        _start.Pressed += () => EmitSignal(SignalName.StartRequested);
        GetNode<Button>($"{rows}/Foot/Leave").Pressed +=
            () => EmitSignal(SignalName.LeaveRequested);

        if (_session is not null) Refresh();
    }

    /// The lobby watches one session and redraws whenever its roster changes.
    /// It never reaches into the transport for anything else.
    public void Bind(NetSession session)
    {
        _session = session;
        session.PeerArrived += _ => Refresh();
        session.PeerDeparted += _ => Refresh();
        session.Closed += reason =>
        {
            if (!IsInsideTree()) return;
            ShowError(reason.Length > 0 ? reason : "The session ended.");
            Refresh();
        };

        if (IsInsideTree() && _slots is not null) Refresh();
    }

    public void Refresh()
    {
        foreach (var row in _rows) row.QueueFree();
        _rows.Clear();

        var session = _session;
        var roster = session?.Roster ?? new List<NetPeer>();

        foreach (var peer in roster)
            _rows.Add(BuildRow(peer, session!.SelfId));

        _title.Text = session?.IsHost == true ? "Your session" : "Session";
        _where.Text = session is null
            ? ""
            : session.IsHost
                ? $"hosting on port {session.Port}"
                : $"connected on port {session.Port}";
        _count.Text = session is null
            ? ""
            : $"{roster.Count} of {session.MaxPlayers}";

        // Start belongs to the host and to nobody else. A client seeing a
        // disabled Start would read as a permission it might one day get.
        _start.Visible = session?.IsHost == true;
        _start.Disabled = roster.Count < 1;
    }

    private PanelContainer BuildRow(NetPeer peer, int selfId)
    {
        var row = new PanelContainer { Name = $"Peer{peer.Id}" };
        row.AddThemeStyleboxOverride("panel", RowStyle());

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        row.AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 12);
        margin.AddChild(columns);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 0);
        columns.AddChild(left);

        var name = Clipped(new Label
        {
            Text = peer.Id == selfId ? $"{peer.Name}  (you)" : peer.Name,
        });
        name.AddThemeColorOverride("font_color", Body);
        left.AddChild(name);
        left.AddChild(Clipped(Small($"peer #{peer.Id}", Muted)));

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        right.AddThemeConstantOverride("separation", 0);
        columns.AddChild(right);
        right.AddChild(Small(peer.IsHost ? "host" : "player", peer.IsHost ? Body : Muted,
                             HorizontalAlignment.Right));

        _slots.AddChild(row);
        return row;
    }

    public void ShowError(string message)
    {
        _status.AddThemeColorOverride("font_color", Starved);
        _status.Text = message;
    }

    public void ShowNote(string message)
    {
        _status.AddThemeColorOverride("font_color", Muted);
        _status.Text = message;
    }

    /// For the headless run: a roster drawn as zero rows looks exactly like a
    /// roster that was never drawn.
    public int RowCount => _rows.Count;

    public string StatusText => _status?.Text ?? "";

    private static Label Clipped(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.CustomMinimumSize = new Vector2(0, 0);
        return label;
    }

    private static Label Small(string text, Color colour,
                               HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text, HorizontalAlignment = align };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }

    private static StyleBoxFlat RowStyle() => new()
    {
        BgColor = new Color(0, 0, 0, 0),
        BorderWidthBottom = 1,
        BorderColor = Divider,
    };
}
