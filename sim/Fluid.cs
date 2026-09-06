namespace Sim;

/// A connected run of pipe, simulated as a single reservoir.
///
/// This is deliberately not "belts, but liquid". Belts cost time proportional to
/// distance because each item is a discrete thing at a position; a pipe network
/// is one volume with a throughput limit, so moving fluid across a hundred tiles
/// costs exactly what moving it across one costs. That is both the cheaper
/// simulation -- O(networks) per tick rather than O(fluid units) -- and the more
/// truthful one, since a real pipeline's capacity is set by bore and pumping,
/// not by how far the far end happens to be.
///
/// The consequence for play is intended: once a fluid is involved, pipe it.
public sealed class FluidNetwork
{
    /// Capacity contributed by one tile of pipe.
    public const int CapacityPerTile = 100;

    /// Units per tick a network can move, by pipe tier. Even the basic tier
    /// carries far more than a belt's 7.5 items/s per lane.
    public const int ThroughputBasic = 200;
    public const int ThroughputLarge = 800;
    public const int ThroughputPumped = 3200;

    // Inflow and outflow are budgeted separately. Sharing one budget would let a
    // network's own inflow starve its outflow, which would make the delivered
    // rate depend on how much pipe was laid -- the exact coupling this model
    // exists to avoid.
    private int _inBudget;
    private int _outBudget;

    public int Capacity { get; private set; }
    public int ThroughputPerTick { get; private set; }

    /// What the network currently holds. A network carries one fluid at a time;
    /// mixing is refused rather than silently averaged.
    public ItemId Fluid { get; private set; }
    public int Amount { get; private set; }
    public bool IsEmpty => Amount == 0;

    public FluidNetwork(int tiles, int throughputPerTick = ThroughputBasic)
    {
        if (tiles <= 0) throw new ArgumentOutOfRangeException(nameof(tiles));
        if (throughputPerTick <= 0) throw new ArgumentOutOfRangeException(nameof(throughputPerTick));

        Capacity = tiles * CapacityPerTile;
        ThroughputPerTick = throughputPerTick;
        _inBudget = throughputPerTick;
        _outBudget = throughputPerTick;
    }

    /// Extending a network with more pipe adds volume but not flow rate.
    public void AddPipe(int tiles)
    {
        if (tiles <= 0) throw new ArgumentOutOfRangeException(nameof(tiles));
        Capacity += tiles * CapacityPerTile;
    }

    /// Pumps are what raise throughput, not more pipe.
    public void SetThroughput(int perTick)
    {
        if (perTick <= 0) throw new ArgumentOutOfRangeException(nameof(perTick));
        ThroughputPerTick = perTick;
    }

    public void BeginTick()
    {
        _inBudget = ThroughputPerTick;
        _outBudget = ThroughputPerTick;
    }

    public int RemainingInflow => _inBudget;
    public int RemainingOutflow => _outBudget;

    public bool Accepts(ItemId fluid) => Amount == 0 || Fluid.Equals(fluid);

    /// Returns how much actually went in: capacity and the per-tick throughput
    /// budget both clamp it, and a different fluid is refused outright.
    public int TryInsert(ItemId fluid, int amount)
    {
        if (amount <= 0 || !Accepts(fluid))
            return 0;

        var room = Capacity - Amount;
        var moved = Math.Min(Math.Min(amount, room), _inBudget);
        if (moved <= 0)
            return 0;

        Fluid = fluid;
        Amount += moved;
        _inBudget -= moved;
        return moved;
    }

    public int TryExtract(ItemId fluid, int amount)
    {
        if (amount <= 0 || Amount == 0 || !Fluid.Equals(fluid))
            return 0;

        var moved = Math.Min(Math.Min(amount, Amount), _outBudget);
        if (moved <= 0)
            return 0;

        Amount -= moved;
        _outBudget -= moved;
        if (Amount == 0)
            Fluid = default;

        return moved;
    }
}

/// Owns every pipe network and refreshes their throughput budgets each tick.
public sealed class FluidSystem
{
    private readonly List<FluidNetwork> _networks = new();

    public IReadOnlyList<FluidNetwork> Networks => _networks;

    public int AddNetwork(int tiles, int throughputPerTick = FluidNetwork.ThroughputBasic)
    {
        _networks.Add(new FluidNetwork(tiles, throughputPerTick));
        return _networks.Count - 1;
    }

    public FluidNetwork Network(int id) => _networks[id];

    /// The entire per-tick cost of fluid transport: one budget reset per network,
    /// however much fluid is in flight.
    public void Tick()
    {
        for (var i = 0; i < _networks.Count; i++)
            _networks[i].BeginTick();
    }

    public int TotalFluid()
    {
        var total = 0;
        foreach (var network in _networks) total += network.Amount;
        return total;
    }
}
