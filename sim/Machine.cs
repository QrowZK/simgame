namespace Sim;

public enum MachineState
{
    Idle,
    Working,
    Starved,
    Blocked,

    /// A miner whose patch is worked out. Distinct from Starved: no belt or
    /// inserter will ever fix this one, so the UI has to say something else.
    /// Appended last so existing save files keep their numbering.
    Depleted,
}

public sealed class Machine
{
    public readonly Recipe Recipe;
    public readonly int OutputCapacityPerItem;

    /// How many batches of the recipe this machine runs per cycle. This is what
    /// a bigger footprint buys: a 3x3 consumes and produces nine times as much
    /// in the same duration as a 1x1. Effect scales with size by construction
    /// rather than by a balance table someone has to keep honest.
    public readonly int Parallelism;

    private readonly Dictionary<ItemId, int> _inputBuffer = new();
    private readonly Dictionary<ItemId, int> _outputBuffer = new();
    private int _ticksRemaining;

    public MachineState State { get; private set; } = MachineState.Idle;

    public Machine(Recipe recipe, int outputCapacityPerItem = 100, int parallelism = 1)
    {
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        Recipe = recipe;
        Parallelism = parallelism;
        // Buffers scale with the batch, or a large machine would block on the
        // first cycle for holding more than a small one's shelf.
        OutputCapacityPerItem = outputCapacityPerItem * parallelism;
    }

    /// Units of `item` one cycle consumes, batch included.
    public int InputPerCycle(ItemId item)
    {
        foreach (var input in Recipe.Inputs)
            if (input.Item.Equals(item)) return input.Count * Parallelism;
        return 0;
    }

    /// Units of `item` one cycle yields, batch included.
    public int OutputPerCycle(ItemId item)
    {
        foreach (var output in Recipe.Outputs)
            if (output.Item.Equals(item)) return output.Count * Parallelism;
        return 0;
    }

    /// Ticks left in the current cycle, 0 when not working. The GUI's progress
    /// bar reads this; nothing in the sim branches on it.
    public int TicksRemaining => State == MachineState.Working ? _ticksRemaining : 0;

    /// Fraction of the current cycle completed, in [0, 1].
    public float Progress => State == MachineState.Working && Recipe.DurationTicks > 0
        ? 1f - _ticksRemaining / (float)Recipe.DurationTicks
        : 0f;

    /// What is actually sitting in the machine right now. Snapshots rather than
    /// live views: a GUI reads these once per frame and must not be able to
    /// mutate sim state by holding on to one.
    public IReadOnlyDictionary<ItemId, int> InputContents =>
        new Dictionary<ItemId, int>(_inputBuffer);

    public IReadOnlyDictionary<ItemId, int> OutputContents =>
        new Dictionary<ItemId, int>(_outputBuffer);

    /// Save surface. State, the cycle timer and both buffers are the whole of a
    /// machine's mutable state; everything else is rebuilt from its recipe and
    /// placement.
    public void Restore(MachineState state, int ticksRemaining,
                        IReadOnlyList<(ItemId Item, int Count)> inputs,
                        IReadOnlyList<(ItemId Item, int Count)> outputs)
    {
        _inputBuffer.Clear();
        _outputBuffer.Clear();
        foreach (var (item, count) in inputs) _inputBuffer[item] = count;
        foreach (var (item, count) in outputs) _outputBuffer[item] = count;

        State = state;
        _ticksRemaining = state == MachineState.Working ? ticksRemaining : 0;
    }

    /// Ticks left in the cycle regardless of state, for saving. TicksRemaining
    /// reports 0 when not working, which is right for a progress bar and wrong
    /// for a save.
    public int RawTicksRemaining => _ticksRemaining;

    public int GetInputCount(ItemId item) => _inputBuffer.GetValueOrDefault(item);

    public int GetOutputCount(ItemId item) => _outputBuffer.GetValueOrDefault(item);

    /// Pushes items into the machine's input buffer (called by an inserter/belt in the full game).
    public void PushInput(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _inputBuffer[item] = _inputBuffer.GetValueOrDefault(item) + count;
    }

    /// Pulls up to `count` items out of the output buffer. Returns the amount actually removed.
    public int PullOutput(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var available = _outputBuffer.GetValueOrDefault(item);
        var taken = Math.Min(available, count);
        if (taken > 0)
            _outputBuffer[item] = available - taken;
        return taken;
    }

    public void Tick()
    {
        if (State != MachineState.Working)
            TryStart();

        if (State == MachineState.Working)
        {
            _ticksRemaining--;
            if (_ticksRemaining <= 0)
            {
                foreach (var output in Recipe.Outputs)
                    _outputBuffer[output.Item] =
                        _outputBuffer.GetValueOrDefault(output.Item) + output.Count * Parallelism;
                State = MachineState.Idle;
            }
        }
    }

    private void TryStart()
    {
        foreach (var output in Recipe.Outputs)
        {
            var projected = _outputBuffer.GetValueOrDefault(output.Item) + output.Count * Parallelism;
            if (projected > OutputCapacityPerItem)
            {
                State = MachineState.Blocked;
                return;
            }
        }

        foreach (var input in Recipe.Inputs)
        {
            if (_inputBuffer.GetValueOrDefault(input.Item) < input.Count * Parallelism)
            {
                State = MachineState.Starved;
                return;
            }
        }

        foreach (var input in Recipe.Inputs)
            _inputBuffer[input.Item] -= input.Count * Parallelism;

        _ticksRemaining = Recipe.DurationTicks;
        State = MachineState.Working;
    }
}
