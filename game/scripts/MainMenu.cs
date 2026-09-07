using System;
using Godot;

namespace Game;

/// The title screen. Four things a player can do: carry on, start fresh, pick
/// an older save, or leave.
///
/// Continue is the important one and is deliberately first and default-focused:
/// on almost every launch after the first, resuming is what the player came to
/// do. It is disabled rather than hidden when there is nothing to continue, so
/// the menu does not change shape between the first launch and the second.
public sealed partial class MainMenu : Control
{
    [Signal] public delegate void NewGameRequestedEventHandler(int seed);
    [Signal] public delegate void LoadRequestedEventHandler(string path);

    private Button _continue = null!;
    private Button _newGame = null!;
    private Button _loadGame = null!;
    private LineEdit _seed = null!;
    private ItemList _saves = null!;
    private Label _status = null!;

    private bool _showingSaves;

    public override void _Ready()
    {
        _continue = GetNode<Button>("Card/Rows/Continue");
        _newGame = GetNode<Button>("Card/Rows/NewGame");
        _loadGame = GetNode<Button>("Card/Rows/LoadGame");
        _seed = GetNode<LineEdit>("Card/Rows/SeedRow/Seed");
        _saves = GetNode<ItemList>("Card/Rows/Saves");
        _status = GetNode<Label>("Card/Rows/Status");

        _continue.Pressed += OnContinue;
        _newGame.Pressed += OnNewGame;
        _loadGame.Pressed += ToggleSaveList;
        GetNode<Button>("Card/Rows/Quit").Pressed += () => GetTree().Quit();
        _saves.ItemActivated += index => LoadSlot((int)index);

        Refresh();
    }

    private void Refresh()
    {
        var hasSaves = GameSession.AnySaves();

        _continue.Disabled = !hasSaves;
        _loadGame.Disabled = !hasSaves;
        _saves.Visible = _showingSaves;

        if (hasSaves)
            _continue.GrabFocus();
        else
            _newGame.GrabFocus();
    }

    private void OnContinue()
    {
        var slot = GameSession.MostRecent();
        if (slot is null)
        {
            _status.Text = "There is no save to continue.";
            Refresh();
            return;
        }

        EmitSignal(SignalName.LoadRequested, slot.Path);
    }

    /// A blank seed field means "surprise me", which is what most players want
    /// and what a required field would get in the way of. A non-numeric seed is
    /// hashed rather than rejected, so "banana" is a valid world.
    private void OnNewGame()
    {
        var text = _seed.Text.Trim();
        int seed;

        if (text.Length == 0)
            seed = SurpriseMeSeed();
        else if (!int.TryParse(text, out seed))
            seed = text.GetHashCode() & 0x7FFFFFFF;

        EmitSignal(SignalName.NewGameRequested, seed);
    }

    /// The seed a blank field gets.
    ///
    /// Narrowed as a long first. Casting the milliseconds straight to int
    /// overflows -- the double is about 1.7e12 -- and the result saturates to
    /// int.MinValue, whose low 31 bits are zero. So every "surprise me" world
    /// was seed 0: the same world, every time, for every player.
    ///
    /// Public so the headless menu test presses the button the player presses,
    /// rather than a fixed seed that would have hidden this.
    public static int SurpriseMeSeed() =>
        (int)((long)(Time.GetUnixTimeFromSystem() * 1000) & 0x7FFFFFFF);

    private void ToggleSaveList()
    {
        _showingSaves = !_showingSaves;
        if (_showingSaves) PopulateSaves();
        _saves.Visible = _showingSaves;
        _status.Text = _showingSaves ? "Double-click a save to load it." : "";
    }

    private void PopulateSaves()
    {
        _saves.Clear();
        foreach (var slot in GameSession.List())
        {
            // Ticks, not wall-clock: the sim's clock is the one that means
            // something about how far a factory has come.
            var hours = slot.Tick / 60.0 / 60.0 / 60.0;
            _saves.AddItem($"{slot.Name}   seed {slot.Seed}   {hours:0.0}h   " +
                           $"{slot.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            _saves.SetItemMetadata(_saves.ItemCount - 1, slot.Path);
        }

        if (_saves.ItemCount == 0)
            _status.Text = "No saves yet.";
    }

    private void LoadSlot(int index)
    {
        var path = _saves.GetItemMetadata(index).AsString();
        EmitSignal(SignalName.LoadRequested, path);
    }

    /// Shown when a load fails, rather than dropping the player into a broken
    /// world or silently doing nothing.
    public void ShowError(string message)
    {
        _status.Text = message;
        _showingSaves = true;
        PopulateSaves();
        _saves.Visible = true;
    }
}
