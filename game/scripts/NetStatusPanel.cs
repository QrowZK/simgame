using Godot;

namespace Game;

/// What the shared session is doing, on screen.
///
/// It exists because of one rule in the design: a stall must be visible. A
/// lockstep peer that is waiting for somebody else's commands looks exactly
/// like a peer that has crashed -- the world stops, the input does nothing, and
/// nothing on the screen says why. So while everything is in step this draws a
/// single quiet line, and the moment it is not, it says who is being waited for
/// and for how long.
///
/// The other thing it draws is the end of the world: a desync stops the session
/// and the reason is the most important sentence this feature will ever show.
/// It gets the whole card, in red, with the tick and both hashes in it, because
/// that is the bug report.
///
/// Two label traps have bitten this project twice each, and both are avoided
/// here deliberately: a `Label`'s minimum size is its full text unless it is
/// clipped, and a clipped label with no minimum size is handed none and draws
/// nothing at all.
public sealed partial class NetStatusPanel : Control
{
    private static readonly Color Calm = new(0.60f, 0.64f, 0.70f);
    private static readonly Color Waiting = new(0.90f, 0.75f, 0.25f);
    private static readonly Color Broken = new(0.90f, 0.35f, 0.30f);

    private PanelContainer _card = null!;
    private Label _title = null!;
    private Label _body = null!;

    /// What the panel last put on the screen. For the headless run: a caption
    /// that was computed and never set looks identical to one that was right.
    public string TitleText => _title?.Text ?? "";
    public string BodyText => _body?.Text ?? "";
    public bool IsShowing => Visible;

    public override void _Ready()
    {
        // Anchors *and offsets*: a Control added straight to a CanvasLayer is
        // handed no size at all, so a preset that only sets anchors leaves a
        // zero-width card in the top-left corner with its text clipped to
        // nothing. That is precisely what the first capture of this panel
        // showed -- the third time this project has been bitten by a label
        // that was set and invisible.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 18);
        AddChild(margin);

        // Wide enough for the desync sentence to wrap into three lines rather
        // than thirty, centred so it does not sit on top of the HUD.
        _card = new PanelContainer
        {
            Name = "Card",
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(640, 0),
        };
        _card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.067f, 0.086f, 0.92f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderColor = new Color(0.122f, 0.141f, 0.173f),
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        margin.AddChild(_card);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 4);
        _card.AddChild(rows);

        _title = new Label { Text = "Shared world", ClipText = true };
        _title.CustomMinimumSize = new Vector2(0, 22);
        _title.AddThemeFontSizeOverride("font_size", 18);
        rows.AddChild(_title);

        // Wrapped, clipped, and given a height of its own: the desync sentence
        // is three lines long and is the one string on this screen that must
        // never be the thing that pushes the card off the viewport.
        _body = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
        };
        // Five lines' worth. A clipped label truncates at its height rather
        // than growing, so this is sized for the longest sentence the panel can
        // hold and the sentence is bounded to match (`Fit`). The first capture
        // of the desync card had 54px here and lost the last line of the
        // message -- the half that says the session has stopped.
        _body.CustomMinimumSize = new Vector2(0, 130);
        _body.AddThemeColorOverride("font_color", Calm);
        rows.AddChild(_body);

        Visible = false;
    }

    /// The longest sentence the card can draw without losing a line. A refusal
    /// that runs off the bottom of its own card is worse than a short one:
    /// the part that gets cut is always the end, and the end is the part that
    /// says what to do about it.
    public const int MaxReason = 300;

    private static string Fit(string reason) =>
        reason.Length <= MaxReason ? reason : reason[..MaxReason] + "...";

    /// Draws one status. Called every frame while a session is running; cheap,
    /// and cheaper than the class of bug where a caption is set once and then
    /// describes a stall that ended a minute ago.
    public void Show(in NetStatus status)
    {
        if (_title is null) return;

        switch (status.Phase)
        {
            case NetPhase.Stopped:
                _title.Text = "The session has stopped";
                _title.AddThemeColorOverride("font_color", Broken);
                _body.AddThemeColorOverride("font_color", Broken);
                _body.Text = Fit(status.Reason);
                Visible = true;
                break;

            case NetPhase.Running when status.Stalled:
                _title.Text = $"Waiting for {status.WaitingFor}";
                _title.AddThemeColorOverride("font_color", Waiting);
                _body.AddThemeColorOverride("font_color", Waiting);
                _body.Text =
                    $"The world is paused at tick {status.Tick} until their commands arrive " +
                    $"-- {status.StalledTicks / 60.0:0.0} seconds so far. Everybody waits: " +
                    "under lockstep nobody may run a tick they do not have every player's " +
                    "instructions for.";
                Visible = true;
                break;

            case NetPhase.Running:
                _title.Text = "Shared world";
                _title.AddThemeColorOverride("font_color", Calm);
                _body.AddThemeColorOverride("font_color", Calm);
                _body.Text = $"In step at tick {status.Tick}.";
                Visible = true;
                break;

            default:
                Visible = false;
                break;
        }
    }
}
