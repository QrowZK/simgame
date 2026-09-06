namespace Sim;

/// Deterministic simulation root. Owns all machines and advances them one fixed
/// 60 UPS tick at a time. No wall-clock time, no unseeded randomness.
public sealed class World
{
    private readonly List<Machine> _machines = new();
    private MachinePlacement[] _placements = new MachinePlacement[64];
    private MachineState[] _states = new MachineState[64];

    /// Tile -> machine index, for every tile a placed machine covers. A grid
    /// rather than a scan over placements: click-to-inspect and the placement
    /// check both want an O(1) answer, and at 100k machines a scan is neither.
    private readonly Dictionary<long, int> _occupancy = new();

    public readonly Random Rng;

    /// Item names for this world. Owned here because the GUI has to name what a
    /// machine is holding, and a name table that is not world state would let
    /// two parts of the game disagree about what item 47 is.
    public ItemDatabase Items { get; }

    /// What the player is carrying. Hand-loading a machine is a world state
    /// change, so it lives on the same side of the engine boundary as the tick.
    public Inventory PlayerInventory { get; } = new();

    /// Belts, splitters and inserters. Owned here so the whole world advances
    /// under one deterministic tick.
    public BeltNetwork Belts { get; } = new();

    /// Pipe networks. Fluids move by network flow rather than as discrete items,
    /// so this costs one budget reset per network per tick.
    public FluidSystem Fluids { get; } = new();

    public long TickCount { get; private set; }

    public World(int seed, ItemDatabase? items = null)
    {
        Rng = new Random(seed);
        Items = items ?? new ItemDatabase();
    }

    public int MachineCount => _machines.Count;

    public IReadOnlyList<Machine> Machines => _machines;

    /// Dense views for the renderer to copy into instance buffers in one pass.
    public ReadOnlySpan<MachinePlacement> Placements => _placements.AsSpan(0, _machines.Count);
    public ReadOnlySpan<MachineState> MachineStates => _states.AsSpan(0, _machines.Count);

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// Whether a footprint is clear. Every tile is checked, so a large machine
    /// cannot be slid over a small one by anchoring it one tile away.
    public bool CanPlace(in MachinePlacement placement)
    {
        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                if (_occupancy.ContainsKey(Key(placement.X + dx, placement.Y + dy)))
                    return false;
        return true;
    }

    /// The machine covering a tile, if any. This is what a click resolves to.
    public bool TryMachineAt(int x, int y, out Machine machine, out int index)
    {
        if (_occupancy.TryGetValue(Key(x, y), out index))
        {
            machine = _machines[index];
            return true;
        }

        machine = null!;
        index = -1;
        return false;
    }

    public MachinePlacement PlacementOf(int index) => _placements[index];

    /// Adds a machine with no position. Used by tests and by headless analysis,
    /// where a machine's throughput is the question and its tile is not.
    public Machine AddMachine(Recipe recipe, int outputCapacityPerItem = 100)
        => AddMachine(recipe, default, outputCapacityPerItem);

    /// Places a machine on the grid, or returns null if the footprint is taken.
    /// Parallelism comes from the footprint, so callers never set the two
    /// independently and cannot get them out of step.
    public Machine? TryPlaceMachine(Recipe recipe, in MachinePlacement placement,
                                    int outputCapacityPerItem = 100)
    {
        if (!CanPlace(placement))
            return null;

        var machine = AddMachine(recipe, placement, outputCapacityPerItem);
        var index = _machines.Count - 1;

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = index;

        return machine;
    }

    public Machine AddMachine(Recipe recipe, MachinePlacement placement, int outputCapacityPerItem = 100)
    {
        var machine = new Machine(recipe, outputCapacityPerItem, placement.Area);
        var index = _machines.Count;
        if (index == _placements.Length)
        {
            Array.Resize(ref _placements, _placements.Length * 2);
            Array.Resize(ref _states, _states.Length * 2);
        }

        _machines.Add(machine);
        _placements[index] = placement;
        _states[index] = machine.State;
        return machine;
    }

    public void Tick()
    {
        for (var i = 0; i < _machines.Count; i++)
        {
            var machine = _machines[i];
            machine.Tick();
            _states[i] = machine.State;
        }

        // Machines first, then transport: fixed order, so the tick is reproducible.
        Fluids.Tick();
        Belts.Tick(_machines);

        TickCount++;
    }

    public void Tick(int count)
    {
        for (var i = 0; i < count; i++)
            Tick();
    }
}
