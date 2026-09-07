namespace Sim;

/// Takes ore out of the ground and puts it into the item graph.
///
/// This is the only place raw material enters the world. Everything downstream
/// -- belts, furnaces, the whole recipe tree -- consumes what a miner produced,
/// so worldgen stops being scenery at exactly this class.
///
/// A miner mines whatever it was built on. Resolving the item once at build
/// time rather than each cycle means a miner cannot silently change what it
/// produces if patches ever overlap, and it gives the build a reason to fail
/// loudly when there is nothing underneath.
public sealed class Miner
{
    /// Units per cycle for a 1x1 miner. Multiplied by footprint area, so a
    /// bigger miner is proportionally faster in exactly the way a bigger
    /// machine is -- one rule for size across the whole game.
    public const int BaseYieldPerCycle = 1;

    public const int DefaultCycleTicks = 60;

    public readonly ItemId Item;
    public readonly int CycleTicks;
    public readonly int Parallelism;
    public readonly int OutputCapacity;

    private int _buffered;
    private int _ticksRemaining;

    public MachineState State { get; private set; } = MachineState.Idle;

    public Miner(ItemId item, int parallelism = 1, int cycleTicks = DefaultCycleTicks,
                 int outputCapacity = 100)
    {
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (cycleTicks < 1) throw new ArgumentOutOfRangeException(nameof(cycleTicks));

        Item = item;
        Parallelism = parallelism;
        CycleTicks = cycleTicks;
        OutputCapacity = outputCapacity * parallelism;
    }

    public int YieldPerCycle => BaseYieldPerCycle * Parallelism;

    public int Buffered => _buffered;

    public int TicksRemaining => State == MachineState.Working ? _ticksRemaining : 0;

    public float Progress => State == MachineState.Working
        ? 1f - _ticksRemaining / (float)CycleTicks
        : 0f;

    /// Advances one tick against the ground it stands on.
    ///
    /// The extraction happens when the cycle *completes*, not when it starts, so
    /// a miner destroyed mid-cycle has taken nothing -- the ground and the
    /// buffer never disagree about ore that is halfway out of the hole.
    public void Tick(Ground ground, int x, int y)
    {
        if (State != MachineState.Working)
        {
            if (_buffered + YieldPerCycle > OutputCapacity)
            {
                State = MachineState.Blocked;
                return;
            }

            if (ground.RemainingAt(x, y) <= 0)
            {
                State = MachineState.Depleted;
                return;
            }

            _ticksRemaining = CycleTicks;
            State = MachineState.Working;
        }

        if (--_ticksRemaining > 0)
            return;

        // A patch that runs dry mid-cycle yields what is left rather than
        // nothing: the last scrapings of a patch are still ore.
        var taken = ground.Extract(x, y, YieldPerCycle, out _);
        _buffered += taken;
        State = taken > 0 ? MachineState.Idle : MachineState.Depleted;
    }

    /// Takes up to `max` out of the miner's buffer.
    public int Pull(int max)
    {
        var taken = Math.Min(_buffered, Math.Max(0, max));
        _buffered -= taken;
        return taken;
    }

    /// Save surface.
    public void Restore(MachineState state, int ticksRemaining, int buffered)
    {
        State = state;
        _ticksRemaining = state == MachineState.Working ? ticksRemaining : 0;
        _buffered = buffered;
    }

    public int RawTicksRemaining => _ticksRemaining;
}
