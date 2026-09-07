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
    private Miner? _miner;
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
        _miner = null;
        _placement = placement;
        Visible = true;
        Refresh();
    }

    /// Miners get the same panel. A player should not have to learn two
    /// different readouts for "what is this building doing".
    public void Show(Miner miner, in MachinePlacement placement, int remainingInPatch)
    {
        _miner = miner;
        _machine = null;
        _placement = placement;
        _patchRemaining = remainingInPatch;
        Visible = true;
        Refresh();
    }

    private int _patchRemaining;

    public void Close()
    {
        _machine = null;
        _miner = null;
        Visible = false;
    }

    public bool IsShowing => _machine is not null || _miner is not null;

    public override void _Process(double delta)
    {
        if (IsShowing)
            Refresh();
    }

    private void Refresh()
    {
        if (_miner is not null)
        {
            RefreshMiner(_miner);
            return;
        }

        var machine = _machine!;

        _title.Text = $"{machine.Recipe.Id}   [{_placement.Size}x{_placement.Size}]";

        // A parallel machine's real per-cycle amounts, not the recipe card's.
        // Showing the card would make the panel lie about the machine it is on.
        // Fluids are marked, because "why is this starved" has a different
        // answer for a pipe than for a belt and the player needs to know which
        // they are looking at.
        var consumes = string.Join(", ", machine.Recipe.Inputs.Select(
            i => $"{machine.InputPerCycle(i.Item)} {ItemName(i.Item)}{(i.IsFluid ? " (piped)" : "")}"));
        var makes = string.Join(", ", machine.Recipe.Outputs.Select(
            o => $"{machine.OutputPerCycle(o.Item)} {ItemName(o.Item)}{(o.IsFluid ? " (piped)" : "")}"));

        _subtitle.Text = $"{consumes}  ->  {makes}   ({machine.Recipe.DurationTicks} ticks)";

        _progress.Value = machine.Progress;
        _status.Text = machine.State switch
        {
            MachineState.Working => $"Working -- {machine.TicksRemaining} ticks left",
            // The failure states are the whole reason this panel exists, so
            // they say what to do about it rather than naming themselves.
            MachineState.Starved => "Starved -- waiting on " + Missing(machine),
            MachineState.Blocked => "Blocked -- output full, nothing is taking it away",
            MachineState.Unpowered => machine.Energy > 0
                ? $"Browning out -- {machine.Energy}/{machine.PowerDraw} charged, needs more supply"
                : "No power -- not connected to a grid, or the grid has none spare",
            _ => "Idle",
        };

        if (machine.PowerDraw > 0)
            _subtitle.Text += $"   [{machine.PowerDraw}/tick]";

        _inputs.Text = "In:  " + Describe(machine.InputContents);
        _outputs.Text = "Out: " + Describe(machine.OutputContents);
        _carrying.Text = "Carrying: " + Describe(_bag.Contents);
    }

    /// A miner's readout. It reports what is left in the ground, because that is
    /// the number that decides whether to keep building here or move on -- and
    /// it is the only number the machine panel cannot infer.
    private void RefreshMiner(Miner miner)
    {
        _title.Text = $"Mining {ItemName(miner.Item)}   [{_placement.Size}x{_placement.Size}]";
        _subtitle.Text = $"{miner.YieldPerCycle} per {miner.CycleTicks} ticks   " +
                         $"({_patchRemaining} left in this patch)";

        _progress.Value = miner.Progress;
        _status.Text = miner.State switch
        {
            MachineState.Working => $"Mining -- {miner.TicksRemaining} ticks left",
            MachineState.Blocked => "Full -- nothing is taking the ore away",
            MachineState.Depleted => "Worked out -- this patch is finished",
            MachineState.Unpowered => miner.Energy > 0
                ? $"Browning out -- {miner.Energy}/{miner.PowerDraw} charged"
                : "No power -- not connected to a grid, or the grid has none spare",
            _ => "Idle",
        };

        _inputs.Text = "In:  the ground";
        _outputs.Text = $"Out: {miner.Buffered} {ItemName(miner.Item)}";
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
        if (_machine is null) return;      // nothing to hand-load into a miner

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

        // Emptying a miner by hand is how the first ore moves, before there is
        // an inserter to do it.
        if (_miner is not null)
        {
            var taken = _miner.Pull(int.MaxValue);
            if (taken > 0) _bag.Add(_miner.Item, taken);
        }
    }
}
