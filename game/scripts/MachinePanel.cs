using System.Linq;
using Godot;
using Sim;

namespace Game;

/// The machine inspection panel: what is this machine doing, with what, and how
/// far along. It is also how the first hour of the game is played, before any
/// inserter exists -- you walk up to a furnace, load it, and take the plates out.
///
/// The panel owns no state. It reads a Machine each frame and calls the sim's
/// hand operations; everything it shows is a snapshot, so holding the panel open
/// can never change what the simulation does.
public sealed partial class MachinePanel : PanelContainer
{
    private Label _title = null!;
    private Label _subtitle = null!;
    private ProgressBar _progress = null!;
    private Label _status = null!;
    private Label _inputs = null!;
    private Label _outputs = null!;
    private Label _carrying = null!;

    private Machine? _machine;
    private MachinePlacement _placement;
    private ItemDatabase _names = null!;
    private Inventory _bag = null!;

    public override void _Ready()
    {
        _title = GetNode<Label>("Margin/Rows/Title");
        _subtitle = GetNode<Label>("Margin/Rows/Subtitle");
        _progress = GetNode<ProgressBar>("Margin/Rows/Progress");
        _status = GetNode<Label>("Margin/Rows/Status");
        _inputs = GetNode<Label>("Margin/Rows/Inputs");
        _outputs = GetNode<Label>("Margin/Rows/Outputs");
        _carrying = GetNode<Label>("Margin/Rows/Carrying");

        GetNode<Button>("Margin/Rows/Buttons/Load").Pressed += LoadOneCycle;
        GetNode<Button>("Margin/Rows/Buttons/Take").Pressed += TakeOutput;

        Visible = false;
    }

    /// Wires the panel to the world's item names and the player's inventory.
    public void Bind(ItemDatabase names, Inventory bag)
    {
        _names = names;
        _bag = bag;
    }

    public void Show(Machine machine, in MachinePlacement placement)
    {
        _machine = machine;
        _placement = placement;
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        _machine = null;
        Visible = false;
    }

    public bool IsShowing => _machine is not null;

    public override void _Process(double delta)
    {
        if (_machine is not null)
            Refresh();
    }

    private void Refresh()
    {
        var machine = _machine!;

        _title.Text = $"{machine.Recipe.Id}   [{_placement.Size}x{_placement.Size}]";

        // A parallel machine's real per-cycle amounts, not the recipe card's.
        // Showing the card would make the panel lie about the machine it is on.
        var consumes = string.Join(", ", machine.Recipe.Inputs.Select(
            i => $"{machine.InputPerCycle(i.Item)} {ItemName(i.Item)}"));
        var makes = string.Join(", ", machine.Recipe.Outputs.Select(
            o => $"{machine.OutputPerCycle(o.Item)} {ItemName(o.Item)}"));

        _subtitle.Text = $"{consumes}  ->  {makes}   ({machine.Recipe.DurationTicks} ticks)";

        _progress.Value = machine.Progress;
        _status.Text = machine.State switch
        {
            MachineState.Working => $"Working -- {machine.TicksRemaining} ticks left",
            // The two failure states are the whole reason this panel exists, so
            // they say what to do about it rather than naming themselves.
            MachineState.Starved => "Starved -- waiting on " + Missing(machine),
            MachineState.Blocked => "Blocked -- output full, nothing is taking it away",
            _ => "Idle",
        };

        _inputs.Text = "In:  " + Describe(machine.InputContents);
        _outputs.Text = "Out: " + Describe(machine.OutputContents);
        _carrying.Text = "Carrying: " + Describe(_bag.Contents);
    }

    private string Missing(Machine machine)
    {
        var short_ = machine.Recipe.Inputs
            .Where(i => machine.GetInputCount(i.Item) < machine.InputPerCycle(i.Item))
            .Select(i => $"{ItemName(i.Item)} " +
                         $"({machine.GetInputCount(i.Item)}/{machine.InputPerCycle(i.Item)})");
        var text = string.Join(", ", short_);
        return text.Length > 0 ? text : "space";
    }

    private string Describe(System.Collections.Generic.IReadOnlyDictionary<ItemId, int> contents)
    {
        if (contents.Count == 0) return "empty";
        return string.Join(", ", contents.OrderBy(kv => kv.Key.Value)
                                         .Select(kv => $"{kv.Value} {ItemName(kv.Key)}"));
    }

    private string ItemName(ItemId item) =>
        _names.Count > item.Value ? _names.GetName(item) : $"#{item.Value}";

    /// Loads exactly one cycle's worth from the player's inventory -- the
    /// smallest useful unit of hand-feeding, and the one that makes the progress
    /// bar move exactly once so the machine's behaviour is legible.
    private void LoadOneCycle()
    {
        if (_machine is null) return;

        foreach (var input in _machine.Recipe.Inputs)
        {
            var want = _machine.InputPerCycle(input.Item) - _machine.GetInputCount(input.Item);
            if (want > 0)
                HandOps.Insert(_bag, _machine, input.Item, want);
        }
    }

    private void TakeOutput()
    {
        if (_machine is not null)
            HandOps.ExtractAll(_machine, _bag);
    }
}
