using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Game;

/// Load a world: the saves, newest first, with a detail pane for the selection.
///
/// Implements `templates/save-select/SaveSelect.dc.html` from the Automation
/// design system. Two things in that design are behaviour rather than styling,
/// and both are the reason it is worth building:
///
/// A save whose header will not parse is a **row**, dimmed and marked in the
/// starved amber, rather than a file the list quietly skips. It used to be
/// skipped with a warning nobody reads, which is the worst of the options: a
/// save that vanishes looks like data loss, and the player cannot delete the
/// thing that is bothering them.
///
/// And the detail pane answers "which of these is the one I want" with the
/// facts that actually distinguish two saves of the same world -- machines,
/// belts, research, tier -- rather than with a filename and a date.
public sealed partial class SaveSelect : Control
{
    [Signal] public delegate void LoadRequestedEventHandler(string path);
    [Signal] public delegate void BackRequestedEventHandler();

    private VBoxContainer _slots = null!;
    private VBoxContainer _facts = null!;
    private Label _name = null!;
    private Label _seed = null!;
    private Label _status = null!;
    private Button _load = null!;
    private Button _delete = null!;

    private readonly List<GameSession.SaveSlot> _saves = new();
    private readonly List<PanelContainer> _rows = new();
    private int _selected = -1;

    // Straight from the design system's tokens.
    private static readonly Color Muted = new(0.60f, 0.64f, 0.70f);
    private static readonly Color Body = new(0.875f, 0.875f, 0.875f);
    private static readonly Color Starved = new(0.90f, 0.75f, 0.25f);
    private static readonly Color RowSelected = new(0.32f, 0.52f, 0.85f, 0.28f);
    private static readonly Color Divider = new(0.122f, 0.141f, 0.173f);

    public override void _Ready()
    {
        _slots = GetNode<VBoxContainer>("Card/Margin/Rows/Body/Well/Scroll/Slots");
        var detail = "Card/Margin/Rows/Body/Detail/DetailMargin/DetailRows";
        _facts = GetNode<VBoxContainer>($"{detail}/Facts");
        _name = GetNode<Label>($"{detail}/Name");
        _seed = GetNode<Label>($"{detail}/Seed");
        _load = GetNode<Button>($"{detail}/Actions/Load");
        _delete = GetNode<Button>($"{detail}/Actions/Delete");
        _status = GetNode<Label>("Card/Margin/Rows/Foot/Status");

        _load.Pressed += LoadSelected;
        _delete.Pressed += DeleteSelected;
        GetNode<Button>("Card/Margin/Rows/Foot/Back").Pressed +=
            () => EmitSignal(SignalName.BackRequested);

        Refresh();
    }

    /// Rebuilds the list from disk. Called on open and after a delete, so the
    /// screen never shows a save that is no longer there.
    public void Refresh()
    {
        _saves.Clear();
        _saves.AddRange(GameSession.List());

        foreach (var row in _rows) row.QueueFree();
        _rows.Clear();

        for (var i = 0; i < _saves.Count; i++)
            _rows.Add(BuildRow(_saves[i], i));

        _status.Text = _saves.Count == 0 ? "No saves yet." : "";
        Select(_saves.Count == 0 ? -1 : 0);
    }

    /// One row: name and what world it is on the left, how far and how recent
    /// on the right. An unreadable save keeps the left half and says why in
    /// place of the world, because that is the half that is still true.
    private PanelContainer BuildRow(GameSession.SaveSlot slot, int index)
    {
        var row = new PanelContainer { Name = $"Slot{index}" };
        row.AddThemeStyleboxOverride("panel", RowStyle(false));
        row.MouseFilter = MouseFilterEnum.Stop;
        row.GuiInput += @event => OnRowInput(@event, index);

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
        left.AddChild(Clipped(new Label { Text = slot.Name }));
        left.AddChild(Clipped(Small(slot.Readable
            ? $"seed {slot.Seed}   {Grouped(slot.Tick)} ticks"
            : slot.Problem, slot.Readable ? Muted : Starved)));

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        right.AddThemeConstantOverride("separation", 0);
        columns.AddChild(right);

        // No playing time on a save that will not open: the number would be
        // invented, and an invented number is worse than a gap.
        if (slot.Readable)
        {
            right.AddChild(new Label
            {
                Text = slot.Hours.ToString("0.0", CultureInfo.InvariantCulture) + "h",
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }

        right.AddChild(Small(slot.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Muted,
                             HorizontalAlignment.Right));

        // The whole row is dimmed rather than greyed piecemeal, so it reads as
        // one thing that is not available rather than as a styling accident.
        if (!slot.Readable) row.Modulate = new Color(1, 1, 1, 0.45f);

        _slots.AddChild(row);
        return row;
    }

    private void OnRowInput(InputEvent @event, int index)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            Select(index);

            // Double-click loads, the way the old list did. Single-click only
            // selects, because Delete sits next to Load and a list that acts on
            // one click is a list that deletes the wrong save eventually.
            if (click.DoubleClick) LoadSelected();
        }
    }

    private void Select(int index)
    {
        _selected = index;

        for (var i = 0; i < _rows.Count; i++)
            _rows[i].AddThemeStyleboxOverride("panel", RowStyle(i == index));

        foreach (var child in _facts.GetChildren()) child.QueueFree();

        if (index < 0 || index >= _saves.Count)
        {
            _name.Text = "";
            _seed.Text = "";
            _load.Disabled = true;
            _delete.Disabled = true;
            return;
        }

        var slot = _saves[index];
        _name.Text = slot.Name;
        _seed.Text = slot.Readable ? $"seed {slot.Seed}" : "unreadable";

        // A broken save can still be deleted. That is the only thing a player
        // can usefully do with one, so it is the one action left enabled.
        _load.Disabled = !slot.Readable;
        _delete.Disabled = false;

        if (!slot.Readable)
        {
            Fact("Problem", slot.Problem, Starved);
            Fact("File", slot.Path.Replace("user://saves/", ""));
            return;
        }

        Fact("Ticks", Grouped(slot.Tick));
        Fact("Machines", slot.Machines.ToString(CultureInfo.InvariantCulture));
        Fact("Belts", slot.Belts.ToString(CultureInfo.InvariantCulture));
        Fact("Researched", $"{slot.Researched} / {slot.Techs}");
        Fact("Tier", slot.Tier.Length == 0 ? "none yet" : slot.Tier);
        Fact("Save format", $"v{slot.Version}");
    }

    /// One label/value line in the detail pane: the label muted on the left,
    /// the value hard right, so the column of values reads as a column.
    private void Fact(string label, string value, Color? valueColour = null)
    {
        var line = new HBoxContainer();

        // The label gives way, the value never does. Clipping the value was the
        // first attempt and it rendered every one of them as nothing: the label
        // expands to fill the row, so a clipped value is handed zero width and
        // draws an empty string rather than an ellipsis.
        var left = Clipped(new Label
        {
            Text = label,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        left.AddThemeColorOverride("font_color", Muted);
        line.AddChild(left);

        var right = new Label
        {
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
        };
        right.AddThemeColorOverride("font_color", valueColour ?? Body);
        line.AddChild(right);

        _facts.AddChild(line);
    }

    private void LoadSelected()
    {
        if (_selected < 0 || _selected >= _saves.Count) return;

        var slot = _saves[_selected];
        if (!slot.Readable)
        {
            _status.Text = $"{slot.Name} will not open: {slot.Problem}";
            return;
        }

        EmitSignal(SignalName.LoadRequested, slot.Path);
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _saves.Count) return;

        var slot = _saves[_selected];
        string message;
        try
        {
            message = GameSession.Delete(slot.Path)
                ? $"Deleted {slot.Name}."
                : $"{slot.Name} was already gone.";
        }
        catch (System.Exception e)
        {
            message = e.Message;
        }

        // Refresh first, then say what happened. The other way round, the
        // rebuild clears the line it was just written to and the delete
        // confirms itself by the row silently vanishing.
        Refresh();
        _status.Text = message;
    }

    /// Shown when a load fails, rather than dropping the player into a broken
    /// world or silently doing nothing.
    public void ShowError(string message) => _status.Text = message;

    /// Text that gives way rather than growing.
    ///
    /// A Label's minimum size is its full text, and a PanelContainer sizes to
    /// its contents, so one long string anywhere inside pushes the whole card
    /// past the width it was designed at. The first capture of this screen had
    /// the card's left edge and half the detail pane off the viewport because a
    /// parse error ran to 90 characters.
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

    private static StyleBoxFlat RowStyle(bool selected)
    {
        var style = new StyleBoxFlat
        {
            BgColor = selected ? RowSelected : new Color(0, 0, 0, 0),
            BorderWidthBottom = 1,
            BorderColor = Divider,
        };
        return style;
    }

    /// Ticks with thin spaces between the thousands, as the design sets them:
    /// "2 678 400". Seven digits in a row is a number nobody reads.
    private static string Grouped(long value) =>
        value.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
}
