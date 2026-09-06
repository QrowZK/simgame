namespace Sim;

public enum MachineState
{
    Idle,
    Working,
    Starved,
    Blocked,
}

public sealed class Machine
{
    public readonly Recipe Recipe;
    public readonly int OutputCapacityPerItem;

    private readonly Dictionary<ItemId, int> _inputBuffer = new();
    private readonly Dictionary<ItemId, int> _outputBuffer = new();
    private int _ticksRemaining;

    public MachineState State { get; private set; } = MachineState.Idle;

    public Machine(Recipe recipe, int outputCapacityPerItem = 100)
    {
        Recipe = recipe;
        OutputCapacityPerItem = outputCapacityPerItem;
    }

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
                    _outputBuffer[output.Item] = _outputBuffer.GetValueOrDefault(output.Item) + output.Count;
                State = MachineState.Idle;
            }
        }
    }

    private void TryStart()
    {
        foreach (var output in Recipe.Outputs)
        {
            var projected = _outputBuffer.GetValueOrDefault(output.Item) + output.Count;
            if (projected > OutputCapacityPerItem)
            {
                State = MachineState.Blocked;
                return;
            }
        }

        foreach (var input in Recipe.Inputs)
        {
            if (_inputBuffer.GetValueOrDefault(input.Item) < input.Count)
            {
                State = MachineState.Starved;
                return;
            }
        }

        foreach (var input in Recipe.Inputs)
            _inputBuffer[input.Item] -= input.Count;

        _ticksRemaining = Recipe.DurationTicks;
        State = MachineState.Working;
    }
}
