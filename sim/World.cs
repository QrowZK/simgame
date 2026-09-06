namespace Sim;

/// Deterministic simulation root. Owns all machines and advances them one fixed
/// 60 UPS tick at a time. No wall-clock time, no unseeded randomness.
public sealed class World
{
    private readonly List<Machine> _machines = new();
    private MachinePlacement[] _placements = new MachinePlacement[64];
    private MachineState[] _states = new MachineState[64];

    public readonly Random Rng;

    public long TickCount { get; private set; }

    public World(int seed)
    {
        Rng = new Random(seed);
    }

    public int MachineCount => _machines.Count;

    public IReadOnlyList<Machine> Machines => _machines;

    /// Dense views for the renderer to copy into instance buffers in one pass.
    public ReadOnlySpan<MachinePlacement> Placements => _placements.AsSpan(0, _machines.Count);
    public ReadOnlySpan<MachineState> MachineStates => _states.AsSpan(0, _machines.Count);

    public Machine AddMachine(Recipe recipe, int outputCapacityPerItem = 100)
        => AddMachine(recipe, default, outputCapacityPerItem);

    public Machine AddMachine(Recipe recipe, MachinePlacement placement, int outputCapacityPerItem = 100)
    {
        var machine = new Machine(recipe, outputCapacityPerItem);
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

        TickCount++;
    }

    public void Tick(int count)
    {
        for (var i = 0; i < count; i++)
            Tick();
    }
}
