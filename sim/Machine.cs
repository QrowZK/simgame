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

    /// Built, fed, and connected to nothing -- or to a network that cannot
    /// supply it. Distinct from Starved for the same reason Depleted is: the
    /// fix is a generator or a pole, not a belt.
    Unpowered,
}

/// Why a recipe change did or did not happen. An enum rather than a bool for
/// the same reason `BuildResult` is one: "this machine cannot make that" and
/// "it already makes that" need different sentences in front of a player.
public enum RecipeChangeResult
{
    /// Changed. Everything the machine was holding went back to the player.
    Ok,

    /// It was already set to that recipe. Nothing was evicted -- re-picking the
    /// current recipe must never cost a player the stack sitting in the machine.
    AlreadyRunning,

    /// This machine cannot run that recipe: wrong machine, or a tier above it.
    CannotRun,

    /// The machine was not placed from a buildable, so there is nothing to
    /// check the recipe against. Only headless and demo worlds build these.
    UnknownMachine,

    /// There is no machine at that index.
    NoMachine,

    /// The machine could run it, but the tech that unlocks it has not been
    /// researched yet (ADR 0023).
    NotResearched,
}

public sealed class Machine
{
    /// What this machine makes. Settable, but only through `SetRecipe`, which
    /// hands the contents back first -- see ADR 0021.
    public Recipe Recipe { get; private set; }

    /// The item this machine was placed from, when it was placed by a player.
    /// A recipe change is checked against this: without it there is no way to
    /// ask the build catalogue what this machine is allowed to run. Machines
    /// built directly by tests and the demo world have none.
    public ItemId? SourceItem { get; set; }

    public readonly int OutputCapacityPerItem;

    /// How many batches of the recipe this machine runs per cycle. This is what
    /// a bigger footprint buys: a 3x3 consumes and produces nine times as much
    /// in the same duration as a 1x1. Effect scales with size by construction
    /// rather than by a balance table someone has to keep honest.
    public readonly int Parallelism;

    private readonly Dictionary<ItemId, int> _inputBuffer = new();
    private readonly Dictionary<ItemId, int> _outputBuffer = new();
    private int _ticksRemaining;
    private int _energy;

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
                        IReadOnlyList<(ItemId Item, int Count)> outputs, int energy = 0)
    {
        _energy = energy;
        _inputBuffer.Clear();
        _outputBuffer.Clear();
        foreach (var (item, count) in inputs) _inputBuffer[item] = count;
        foreach (var (item, count) in outputs) _outputBuffer[item] = count;

        State = state;

        // The timer, not the state, is what carries a cycle -- an Unpowered
        // machine is mid-cycle too, and zeroing it here would silently restart
        // its work on load and eat its inputs twice.
        _ticksRemaining = state is MachineState.Working or MachineState.Unpowered
            ? ticksRemaining
            : 0;
    }

    /// Ticks left in the cycle regardless of state, for saving. TicksRemaining
    /// reports 0 when not working, which is right for a progress bar and wrong
    /// for a save.
    public int RawTicksRemaining => _ticksRemaining;

    /// Retasks a placed machine, handing everything it was holding back to the
    /// player. Returns the number of units evicted.
    ///
    /// The eviction rule is conservation, and it is the whole of ADR 0021: an
    /// item that went into a machine comes back out. That covers three things a
    /// player would otherwise lose without being told:
    ///
    /// - the input buffer, which is theirs and was never consumed;
    /// - the output buffer, which is finished goods they have already paid for;
    /// - **the batch of a cycle in flight**, whose inputs were taken at the
    ///   start of the cycle and whose outputs are not written until the end.
    ///   Refunding it is exact, not generous: no output has been produced yet,
    ///   so returning the inputs creates nothing.
    ///
    /// The energy buffer is deliberately left alone. It is at most one tick's
    /// draw, it is not an item, and there is nothing to hand it back to.
    ///
    /// Callers are expected to have checked that the machine can run `recipe`;
    /// `World.TryChangeRecipe` is the checked entry point.
    public int SetRecipe(Recipe recipe, Inventory into)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(into);

        var evicted = 0;

        // The in-flight batch first: those inputs left the buffer when the
        // cycle started and exist nowhere else.
        if (_ticksRemaining > 0)
            foreach (var input in Recipe.Inputs)
            {
                into.Add(input.Item, input.Count * Parallelism);
                evicted += input.Count * Parallelism;
            }

        foreach (var (item, count) in _inputBuffer)
        {
            into.Add(item, count);
            evicted += count;
        }

        foreach (var (item, count) in _outputBuffer)
        {
            into.Add(item, count);
            evicted += count;
        }

        _inputBuffer.Clear();
        _outputBuffer.Clear();
        _ticksRemaining = 0;
        Recipe = recipe;
        State = MachineState.Idle;

        return evicted;
    }

    public int GetInputCount(ItemId item) => _inputBuffer.GetValueOrDefault(item);

    public int GetOutputCount(ItemId item) => _outputBuffer.GetValueOrDefault(item);

    /// Pushes items into the machine's input buffer (called by an inserter/belt in the full game).
    public void PushInput(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _inputBuffer[item] = _inputBuffer.GetValueOrDefault(item) + count;
    }

    /// Removes items from the input buffer without running a cycle. The Uplink
    /// is the only caller: what is pushed into it leaves as research rather
    /// than as product, and it must leave the buffer exactly once.
    public int TakeInput(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var available = _inputBuffer.GetValueOrDefault(item);
        var taken = Math.Min(available, count);

        // The key is dropped at zero rather than left holding one. An Uplink is
        // fed and emptied every tick, and a buffer that kept a zero entry for
        // every item ever delivered would grow forever and be written into
        // every save.
        if (taken > 0)
        {
            if (available == taken) _inputBuffer.Remove(item);
            else _inputBuffer[item] = available - taken;
        }

        return taken;
    }

    /// Parks the machine as Idle. Save surface and the Uplink, which never
    /// starts a cycle and would otherwise report whatever state it was left in.
    public void SetIdle() => State = MachineState.Idle;

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

    /// Energy on hand. A machine holds at most one tick's worth: hoarding would
    /// let a factory bank power overnight and run a burst it never generated.
    public int Energy => _energy;

    public int PowerDraw => Recipe.PowerDraw * Parallelism;

    /// Whether this machine is asking for power this tick.
    ///
    /// Starved, Blocked and Depleted machines are not: they cannot run for a
    /// reason power will not fix, and charging a factory for idle machines
    /// would make "why is my base browning out" unanswerable.
    public bool WantsPower => PowerDraw > 0 && State is
        MachineState.Idle or MachineState.Working or MachineState.Unpowered;

    /// Adds energy to the buffer.
    ///
    /// Deliberately uncapped. Capping at one tick's draw looks safe and quietly
    /// destroys power: a machine drawing 7 that receives 6 a tick can never
    /// reach 7 if the buffer is clipped to 7 every tick, so it runs at half
    /// speed on six-sevenths of the energy and the rest vanishes.
    ///
    /// It cannot run away either. The buffer only grows while the machine
    /// cannot afford a tick, and the allocator never hands out more than one
    /// tick's draw, so the buffer stays below twice the draw by construction.
    public void SupplyEnergy(int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        _energy += amount;
    }

    public void Tick()
    {
        // A cycle in progress is tracked by the timer, not by the state, so a
        // machine that loses power holds its work rather than restarting it.
        // Re-entering TryStart mid-cycle would consume the inputs a second time.
        if (_ticksRemaining <= 0)
            TryStart();

        if (_ticksRemaining <= 0)
            return;                     // starved or blocked; nothing to power

        if (PowerDraw > 0)
        {
            if (_energy < PowerDraw)
            {
                State = MachineState.Unpowered;
                return;
            }

            _energy -= PowerDraw;
        }

        State = MachineState.Working;
        _ticksRemaining--;

        if (_ticksRemaining <= 0)
        {
            foreach (var output in Recipe.Outputs)
                _outputBuffer[output.Item] =
                    _outputBuffer.GetValueOrDefault(output.Item) + output.Count * Parallelism;
            State = MachineState.Idle;
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
