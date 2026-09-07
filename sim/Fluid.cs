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
    ///
    /// A network's rate is set by its narrowest pipe, the way players already
    /// talk about these systems -- one bronze segment in a steel line throttles
    /// the whole run. Pumps are how you get past that.
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

    /// `byCapacity` takes the first argument as a volume rather than a tile
    /// count. The system builds networks from summed node capacities, where
    /// tanks contribute far more than a tile of pipe, so "tiles" stops meaning
    /// anything there.
    public FluidNetwork(int tiles, int throughputPerTick = ThroughputBasic, bool byCapacity = false)
    {
        if (tiles <= 0) throw new ArgumentOutOfRangeException(nameof(tiles));
        if (throughputPerTick <= 0) throw new ArgumentOutOfRangeException(nameof(throughputPerTick));

        Capacity = byCapacity ? tiles : tiles * CapacityPerTile;
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

    /// Save surface. Capacity and throughput are restored directly rather than
    /// replayed through AddPipe, so a network reloads as the network it was
    /// rather than as one assembled from a pipe count.
    public void Restore(int capacity, int throughputPerTick, ItemId fluid, int amount)
    {
        Capacity = capacity;
        ThroughputPerTick = throughputPerTick;
        Fluid = fluid;
        Amount = amount;
    }

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

/// What a piece of fluid infrastructure is.
///
/// Three kinds, because they do three different jobs: pipe carries, tank holds,
/// pump pushes. Collapsing them into one "pipe with properties" would hide that
/// a tank adds no throughput and a pump adds no volume.
public enum FluidNodeKind
{
    Pipe,
    Tank,
    Pump,
}

public readonly struct FluidNode
{
    public readonly int X;
    public readonly int Y;
    public readonly FluidNodeKind Kind;
    public readonly int Capacity;

    /// Units per tick. Meaningful for pipes (their bore) and pumps (their
    /// rate); a tank contributes none and is not a bottleneck either.
    public readonly int Throughput;

    public FluidNode(int x, int y, FluidNodeKind kind, int capacity, int throughput)
    {
        X = x;
        Y = y;
        Kind = kind;
        Capacity = capacity;
        Throughput = throughput;
    }
}

/// Every piece of pipe, tank and pump on the map, and the networks they form.
///
/// A network is a connected run of orthogonally adjacent nodes -- the same
/// component-finding the power grid does, for the same reason: the player builds
/// a shape and the shape decides what is connected, rather than the player
/// declaring networks.
///
/// Rate is the narrowest pipe in the run, raised by the best pump on it.
/// Capacity is everything summed. So more pipe buys volume, bigger pipe or a
/// pump buys flow, and a tank buys only buffer -- three distinguishable
/// decisions rather than one number that goes up.
public sealed class FluidSystem
{
    private readonly List<FluidNode> _nodes = new();
    private readonly Dictionary<long, int> _byTile = new();
    private readonly List<FluidNetwork> _networks = new();
    private int[] _nodeNetwork = Array.Empty<int>();
    private bool _dirty = true;

    /// Bumped whenever the layout changes, so callers can cache which network a
    /// machine is attached to instead of scanning for it every tick.
    public int Version { get; private set; }

    /// Fluid destroyed by connecting two runs carrying different things.
    ///
    /// Surfaced rather than swallowed: joining a water line to an oil line is a
    /// real mistake with a real cost, and a player who cannot see the loss will
    /// never work out where their oil went.
    public int VoidedByMixing { get; private set; }

    public IReadOnlyList<FluidNode> Nodes => _nodes;

    public IReadOnlyList<FluidNetwork> Networks
    {
        get { Rebuild(); return _networks; }
    }

    public FluidNetwork Network(int id)
    {
        Rebuild();
        return _networks[id];
    }

    public int NetworkCount
    {
        get { Rebuild(); return _networks.Count; }
    }

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    public bool HasNodeAt(int x, int y) => _byTile.ContainsKey(Key(x, y));

    /// Adds a length of pipe. Returns false if the tile already carries
    /// something -- two pipes on one tile would be a network that is its own
    /// neighbour.
    public bool AddPipe(int x, int y, int throughput = FluidNetwork.ThroughputBasic) =>
        Add(new FluidNode(x, y, FluidNodeKind.Pipe, FluidNetwork.CapacityPerTile, throughput));

    /// A tank: volume, no flow. Buffering a line is a different decision from
    /// widening it, and this is what makes it one.
    public bool AddTank(int x, int y, int capacity = 20_000) =>
        Add(new FluidNode(x, y, FluidNodeKind.Tank, capacity, 0));

    /// A pump: flow, no volume. This is how a run gets past its narrowest pipe.
    public bool AddPump(int x, int y, int throughput = FluidNetwork.ThroughputPumped) =>
        Add(new FluidNode(x, y, FluidNodeKind.Pump, 0, throughput));

    private bool Add(in FluidNode node)
    {
        var key = Key(node.X, node.Y);
        if (_byTile.ContainsKey(key))
            return false;

        _byTile[key] = _nodes.Count;
        _nodes.Add(node);
        _dirty = true;
        Version++;
        return true;
    }

    /// The network on a tile, or -1.
    public int NetworkAt(int x, int y)
    {
        Rebuild();
        return _byTile.TryGetValue(Key(x, y), out var node) ? _nodeNetwork[node] : -1;
    }

    /// The network a footprint can reach: a machine connects to pipe running
    /// alongside it, not underneath it. Scanning the border rather than the
    /// interior is what lets a 3x3 sit on bare ground and still be plumbed.
    public int NetworkAdjacentTo(in MachinePlacement placement)
    {
        Rebuild();

        for (var d = 0; d < placement.Size; d++)
        {
            var candidates = new[]
            {
                (placement.X + d, placement.Y - 1),
                (placement.X + d, placement.Y + placement.Size),
                (placement.X - 1, placement.Y + d),
                (placement.X + placement.Size, placement.Y + d),
            };

            foreach (var (x, y) in candidates)
                if (_byTile.TryGetValue(Key(x, y), out var node))
                    return _nodeNetwork[node];
        }

        return -1;
    }

    /// Recomputes components and moves the contents across.
    ///
    /// Contents are carried per node -- each node holds its share of its old
    /// network by capacity -- so splitting a run divides the fluid where the cut
    /// was, and joining two runs adds them together. Doing it per network
    /// instead would have to guess how to divide a split.
    private void Rebuild()
    {
        if (!_dirty) return;
        _dirty = false;

        // What each node was holding before the layout changed.
        var nodeFluid = new ItemId[_nodes.Count];
        var nodeAmount = new int[_nodes.Count];

        for (var i = 0; i < _nodes.Count && i < _nodeNetwork.Length; i++)
        {
            var old = _nodeNetwork[i];
            if (old < 0 || old >= _networks.Count) continue;

            var network = _networks[old];
            if (network.Amount <= 0 || network.Capacity <= 0) continue;

            nodeFluid[i] = network.Fluid;
            nodeAmount[i] = (int)((long)network.Amount * _nodes[i].Capacity / network.Capacity);
        }

        if (_nodeNetwork.Length < _nodes.Count)
            Array.Resize(ref _nodeNetwork, Math.Max(16, _nodes.Count));

        // Every slot, not just the used ones. The array is over-allocated, and
        // an unused slot defaults to 0 -- which reads as "network 0" on the next
        // rebuild and hands a brand new pipe a share of an old network's
        // contents. That invents fluid out of nothing.
        Array.Fill(_nodeNetwork, -1);

        // Flood fill in node order, so component ids are stable for a layout
        // rather than depending on which tile happened to be visited first.
        _networks.Clear();
        var queue = new Queue<int>();

        for (var start = 0; start < _nodes.Count; start++)
        {
            if (_nodeNetwork[start] >= 0) continue;

            var id = _networks.Count;
            var members = new List<int>();
            queue.Clear();
            queue.Enqueue(start);
            _nodeNetwork[start] = id;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                members.Add(current);
                var node = _nodes[current];

                foreach (var (nx, ny) in new[]
                         {
                             (node.X + 1, node.Y), (node.X - 1, node.Y),
                             (node.X, node.Y + 1), (node.X, node.Y - 1),
                         })
                {
                    if (!_byTile.TryGetValue(Key(nx, ny), out var neighbour)) continue;
                    if (_nodeNetwork[neighbour] >= 0) continue;
                    _nodeNetwork[neighbour] = id;
                    queue.Enqueue(neighbour);
                }
            }

            var capacity = 0;
            var narrowest = int.MaxValue;
            var bestPump = 0;

            foreach (var member in members)
            {
                var node = _nodes[member];
                capacity += node.Capacity;

                if (node.Kind == FluidNodeKind.Pipe)
                    narrowest = Math.Min(narrowest, node.Throughput);
                else if (node.Kind == FluidNodeKind.Pump)
                    bestPump = Math.Max(bestPump, node.Throughput);
            }

            // A run of tanks and pumps with no pipe still moves what the pump
            // moves; a run with no pipe and no pump moves nothing.
            var throughput = Math.Max(narrowest == int.MaxValue ? 0 : narrowest, bestPump);

            var network = new FluidNetwork(Math.Max(1, capacity), Math.Max(1, throughput),
                                           byCapacity: true);

            // Whichever fluid has the most volume in the new component wins it;
            // the rest is destroyed, and counted so it can be shown.
            var totals = new Dictionary<int, int>();
            foreach (var member in members)
            {
                if (nodeAmount[member] <= 0) continue;
                var key = nodeFluid[member].Value;
                totals[key] = totals.GetValueOrDefault(key) + nodeAmount[member];
            }

            if (totals.Count > 0)
            {
                var winner = totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
                foreach (var (fluid, amount) in totals)
                    if (fluid != winner.Key) VoidedByMixing += amount;

                network.Restore(network.Capacity, network.ThroughputPerTick,
                                new ItemId(winner.Key), Math.Min(winner.Value, network.Capacity));
            }

            _networks.Add(network);
        }
    }

    /// The entire per-tick cost of fluid transport: one budget reset per network,
    /// however much fluid is in flight.
    public void Tick()
    {
        Rebuild();
        for (var i = 0; i < _networks.Count; i++)
            _networks[i].BeginTick();
    }

    public int TotalFluid()
    {
        Rebuild();
        var total = 0;
        foreach (var network in _networks) total += network.Amount;
        return total;
    }
}

/// Draws fluid out of the world and into a pipe network.
///
/// The fluid counterpart of `Miner`, and deliberately the same shape: it fills
/// a small buffer on a cycle and the world moves that into whatever it is
/// plumbed to. An oil derrick standing on a crude patch depletes it exactly as
/// a miner depletes ore; a water pump standing on water does not, because a
/// lake is not a resource you can exhaust at this scale.
public sealed class FluidExtractor
{
    /// Units per cycle for a 1x1 extractor, before footprint scaling.
    public const int BaseYieldPerCycle = 50;

    public const int DefaultCycleTicks = 20;

    public readonly ItemId Fluid;
    public readonly int CycleTicks;
    public readonly int Parallelism;
    public readonly int PowerDraw;

    /// True for ambient sources such as water, which have no patch behind them
    /// and never run dry.
    public readonly bool Ambient;

    public readonly int BufferCapacity;

    private int _buffered;
    private int _ticksRemaining;
    private int _energy;

    public FluidExtractor(ItemId fluid, bool ambient, int parallelism = 1,
                          int cycleTicks = DefaultCycleTicks, int powerDraw = 0)
    {
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (cycleTicks < 1) throw new ArgumentOutOfRangeException(nameof(cycleTicks));

        Fluid = fluid;
        Ambient = ambient;
        Parallelism = parallelism;
        CycleTicks = cycleTicks;
        PowerDraw = powerDraw * parallelism;
        BufferCapacity = YieldPerCycle * 4;
    }

    public int YieldPerCycle => BaseYieldPerCycle * Parallelism;
    public int Buffered => _buffered;
    public int Energy => _energy;
    public int RawTicksRemaining => _ticksRemaining;

    public MachineState State { get; private set; } = MachineState.Idle;

    public float Progress => State == MachineState.Working
        ? 1f - _ticksRemaining / (float)CycleTicks
        : 0f;

    public bool WantsPower => PowerDraw > 0 && State is
        MachineState.Idle or MachineState.Working or MachineState.Unpowered;

    public void SupplyEnergy(int amount) => _energy += amount;

    public void Tick(Ground ground, int x, int y)
    {
        if (_ticksRemaining <= 0)
        {
            if (_buffered + YieldPerCycle > BufferCapacity)
            {
                State = MachineState.Blocked;
                return;
            }

            if (!Ambient && ground.RemainingAt(x, y) <= 0)
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

        var taken = Ambient
            ? YieldPerCycle
            : ground.Extract(x, y, YieldPerCycle, out _);

        _buffered += taken;
        State = taken > 0 ? MachineState.Idle : MachineState.Depleted;
    }

    /// Takes up to `max` out of the buffer, for the world to put in a pipe.
    public int Pull(int max)
    {
        var taken = Math.Min(_buffered, Math.Max(0, max));
        _buffered -= taken;
        return taken;
    }

    public void Restore(MachineState state, int ticksRemaining, int buffered, int energy)
    {
        State = state;
        _ticksRemaining = state is MachineState.Working or MachineState.Unpowered
            ? ticksRemaining
            : 0;
        _buffered = buffered;
        _energy = energy;
    }
}
