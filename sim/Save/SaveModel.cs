namespace Sim.Save;

/// The on-disk shape of a save. Plain data with public setters, because
/// System.Text.Json has to round-trip it; the sim types themselves stay
/// encapsulated and expose only the narrow snapshot/restore members these are
/// built from.
///
/// Two rules run through the whole model.
///
/// **Nothing is keyed on a runtime integer id.** ItemIds are assigned in
/// registration order, so they shift the moment the data files change. A save
/// that stored them would silently turn iron into tin after a balance edit.
/// Items are stored by name and remapped on load; recipes are stored by id and
/// re-looked-up in the current recipe set, so a balance change reaches existing
/// saves instead of being frozen into them.
///
/// **Arrays are written in index order** and restored in the same order, so a
/// save round-trips to byte-identical state rather than merely equivalent state.
public sealed class SaveFile
{
    /// Bumped whenever the shape below changes incompatibly. A loader that does
    /// not recognise a version refuses the file rather than guessing at it: a
    /// half-understood save is worse than no save.
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int Seed { get; set; }
    public long Tick { get; set; }

    /// Item names in id order. This table is what makes the rest of the file
    /// portable: every item reference below is an index into it, and loading
    /// remaps those indices onto whatever ids the running game has assigned.
    public List<string> Items { get; set; } = new();

    public List<StackSave> Player { get; set; } = new();
    public List<MachineSave> Machines { get; set; } = new();
    public BeltNetworkSave Belts { get; set; } = new();
    public List<FluidNetworkSave> Fluids { get; set; } = new();
}

/// One item and how many of it. A list of these rather than a dictionary,
/// because JSON object keys are unordered and this file is compared byte for
/// byte in tests.
public sealed class StackSave
{
    public int Item { get; set; }
    public int Count { get; set; }
}

public sealed class MachineSave
{
    /// Recipe id, looked up in the running game's recipe set on load.
    public string Recipe { get; set; } = "";

    /// The capacity the machine was built with, before parallelism scaled it.
    /// Storing the scaled figure would multiply it again on every load.
    public int OutputCapacityPerItem { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;

    /// Whether this machine occupies tiles. Headless analysis builds machines
    /// with no position at all, and those must not be given one on load.
    public bool Placed { get; set; }

    public int TicksRemaining { get; set; }
    public MachineState State { get; set; }
    public List<StackSave> Inputs { get; set; } = new();
    public List<StackSave> Outputs { get; set; } = new();
}

public sealed class BeltNetworkSave
{
    public List<BeltSegmentSave> Segments { get; set; } = new();
    public List<EndpointSave> LaneOutputs { get; set; } = new();
    public List<SplitterSave> Splitters { get; set; } = new();
    public List<InserterSave> Inserters { get; set; } = new();
}

public sealed class BeltSegmentSave
{
    public int Tiles { get; set; }
    public int Speed { get; set; }
    public List<LaneSave> Lanes { get; set; } = new();
}

/// A lane, front item first. Gaps are relative -- each is the distance ahead of
/// its own item -- which is exactly how the lane stores them, so a restored lane
/// is the same lane rather than a re-packed approximation of one.
public sealed class LaneSave
{
    public List<int> Items { get; set; } = new();
    public List<int> Gaps { get; set; } = new();
}

public sealed class EndpointSave
{
    public EndpointKind Kind { get; set; }
    public int Index { get; set; }
    public int Lane { get; set; }
}

public sealed class SplitterSave
{
    public List<EndpointSave> Outputs { get; set; } = new();
    public List<int> Buffer { get; set; } = new();

    /// Which output the round-robin will feed next. Dropping this would make
    /// every load nudge a balanced split off-balance.
    public int Next { get; set; }
}

public sealed class InserterSave
{
    public EndpointSave Source { get; set; } = new();
    public EndpointSave Target { get; set; } = new();
    public int SwingTicks { get; set; }
    public int StackSize { get; set; }
    public int HeldItem { get; set; }
    public int Held { get; set; }
    public int Cooldown { get; set; }
}

public sealed class FluidNetworkSave
{
    public int Capacity { get; set; }
    public int ThroughputPerTick { get; set; }
    public int Fluid { get; set; }
    public int Amount { get; set; }
}
