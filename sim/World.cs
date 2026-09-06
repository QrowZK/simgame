namespace Sim;

/// Deterministic simulation root. Owns all machines and advances them one fixed
/// 60 UPS tick at a time. No wall-clock time, no unseeded randomness.
public sealed class World
{
    private readonly List<Machine> _machines = new();
    public readonly Random Rng;

    public long TickCount { get; private set; }

    public World(int seed)
    {
        Rng = new Random(seed);
    }

    public Machine AddMachine(Recipe recipe, int outputCapacityPerItem = 100)
    {
        var machine = new Machine(recipe, outputCapacityPerItem);
        _machines.Add(machine);
        return machine;
    }

    public IReadOnlyList<Machine> Machines => _machines;

    public void Tick()
    {
        foreach (var machine in _machines)
            machine.Tick();

        TickCount++;
    }

    public void Tick(int count)
    {
        for (var i = 0; i < count; i++)
            Tick();
    }
}
