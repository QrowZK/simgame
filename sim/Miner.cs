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

    /// Energy per tick while digging. Zero is a hand-cranked or steam miner and
    /// is what a world with no grid yet can still build.
    public readonly int PowerDraw;

    private int _buffered;
    private int _ticksRemaining;
    private int _energy;

    public MachineState State { get; private set; } = MachineState.Idle;

    public Miner(ItemId item, int parallelism = 1, int cycleTicks = DefaultCycleTicks,
                 int outputCapacity = 100, int powerDraw = 0)
    {
        if (powerDraw < 0) throw new ArgumentOutOfRangeException(nameof(powerDraw));
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (cycleTicks < 1) throw new ArgumentOutOfRangeException(nameof(cycleTicks));

        Item = item;
        Parallelism = parallelism;
        CycleTicks = cycleTicks;
        OutputCapacity = outputCapacity * parallelism;
        PowerDraw = powerDraw * parallelism;
    }

    public int Energy => _energy;

    /// A depleted or backed-up miner is not asking for power: the fix is more
    /// ore or a belt, not a generator.
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
        // As with machines, the timer carries the cycle: a miner that loses
        // power holds its progress instead of starting over.
        if (_ticksRemaining <= 0)
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
        }

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
    public void Restore(MachineState state, int ticksRemaining, int buffered, int energy = 0)
    {
        State = state;
        _ticksRemaining = state is MachineState.Working or MachineState.Unpowered
            ? ticksRemaining
            : 0;
        _buffered = buffered;
        _energy = energy;
    }

    public int RawTicksRemaining => _ticksRemaining;
}
