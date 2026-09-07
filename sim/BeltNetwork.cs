namespace Sim;

public enum EndpointKind
{
    None,
    Belt,
    Machine,
    Splitter,

    /// A miner's output buffer. Appended last so existing saves keep their
    /// numbering.
    Miner,
}

/// Where a lane hands its items on to.
public readonly struct Endpoint
{
    public readonly EndpointKind Kind;
    public readonly int Index;
    public readonly int Lane;

    private Endpoint(EndpointKind kind, int index, int lane)
    {
        Kind = kind;
        Index = index;
        Lane = lane;
    }

    public static readonly Endpoint None = new(EndpointKind.None, -1, 0);
    public static Endpoint Belt(int segment, int lane) => new(EndpointKind.Belt, segment, lane);
    public static Endpoint Machine(int machine) => new(EndpointKind.Machine, machine, 0);
    public static Endpoint Splitter(int splitter) => new(EndpointKind.Splitter, splitter, 0);
    public static Endpoint Miner(int miner) => new(EndpointKind.Miner, miner, 0);
}

/// Two lanes in, two lanes out, alternating. Round-robin rather than random, so
/// the balance is exact and the sim stays deterministic.
public sealed class Splitter
{
    public readonly Endpoint[] Outputs = new Endpoint[2];
    private readonly Queue<ItemId> _buffer = new();
    private int _next;

    /// One item per output side per tick; a splitter is not a free teleporter.
    public const int BufferLimit = 2;

    public bool CanAccept => _buffer.Count < BufferLimit;

    public void Accept(ItemId item) => _buffer.Enqueue(item);

    public int Buffered => _buffer.Count;

    /// Save surface. `Next` is the round-robin cursor: dropping it would nudge
    /// every balanced split off-balance on each load.
    public int Next => _next;

    public IEnumerable<ItemId> BufferedItems => _buffer;

    public void Restore(IReadOnlyList<ItemId> buffered, int next)
    {
        _buffer.Clear();
        foreach (var item in buffered) _buffer.Enqueue(item);
        _next = next;
    }

    /// Tries each output in turn starting from whichever is next in rotation, so
    /// a blocked side never starves the other.
    public void Push(Func<Endpoint, ItemId, bool> deliver)
    {
        while (_buffer.Count > 0)
        {
            var item = _buffer.Peek();
            var placed = false;

            for (var attempt = 0; attempt < 2 && !placed; attempt++)
            {
                var side = (_next + attempt) & 1;
                if (Outputs[side].Kind == EndpointKind.None)
                    continue;

                if (deliver(Outputs[side], item))
                {
                    _buffer.Dequeue();
                    _next = (side + 1) & 1;
                    placed = true;
                }
            }

            if (!placed)
                return;
        }
    }
}

/// The interface between belts and machines. Swings on a fixed cadence and
/// carries a stack, so throughput is a property of the inserter tier rather
/// than of how fast the belt happens to be running.
public sealed class Inserter
{
    public readonly Endpoint Source;
    public readonly Endpoint Target;
    public readonly int SwingTicks;
    public readonly int StackSize;

    private ItemId _heldItem;
    private int _held;
    private int _cooldown;

    public Inserter(Endpoint source, Endpoint target, int swingTicks = 20, int stackSize = 1)
    {
        if (swingTicks <= 0) throw new ArgumentOutOfRangeException(nameof(swingTicks));
        if (stackSize <= 0) throw new ArgumentOutOfRangeException(nameof(stackSize));

        Source = source;
        Target = target;
        SwingTicks = swingTicks;
        StackSize = stackSize;
    }

    public int Held => _held;
    public ItemId HeldItem => _heldItem;
    public int Cooldown => _cooldown;

    /// Save surface: what the arm is carrying and how far through its swing.
    public void Restore(ItemId heldItem, int held, int cooldown)
    {
        _heldItem = heldItem;
        _held = held;
        _cooldown = cooldown;
    }

    public void Tick(BeltNetwork network, IReadOnlyList<Machine> machines)
        => Tick(network, machines, Array.Empty<Miner>());

    public void Tick(BeltNetwork network, IReadOnlyList<Machine> machines,
                     IReadOnlyList<Miner> miners)
    {
        if (_cooldown > 0)
        {
            _cooldown--;
            return;
        }

        if (_held == 0)
        {
            if (network.TryTake(Source, machines, miners, StackSize, out _heldItem, out _held) && _held > 0)
                _cooldown = SwingTicks;
            return;
        }

        // Deliver what it can; a partially emptied hand keeps swinging next tick.
        while (_held > 0 && network.TryGive(Target, machines, _heldItem))
            _held--;

        if (_held == 0)
            _cooldown = SwingTicks;
    }
}

/// Owns belt topology and moves everything one tick at a time.
///
/// The tick is two-phase on purpose. Every lane advances first, then every
/// hand-off happens, both in a fixed order. Movement is therefore independent of
/// the order segments were built in, and hand-offs are deterministic. A knock-on
/// effect is that a freed slot propagates upstream one segment per tick, which is
/// invisible in practice: at basic belt speed an item takes eight ticks to cross
/// one item spacing anyway.
public sealed class BeltNetwork
{
    private readonly List<BeltSegment> _segments = new();
    private readonly List<Splitter> _splitters = new();
    private readonly List<Inserter> _inserters = new();
    private readonly List<Endpoint> _laneOutputs = new();

    public IReadOnlyList<BeltSegment> Segments => _segments;
    public IReadOnlyList<Splitter> Splitters => _splitters;
    public IReadOnlyList<Inserter> Inserters => _inserters;

    public int AddSegment(int tiles, int speed = BeltUnits.SpeedBasic)
    {
        var id = _segments.Count;
        _segments.Add(new BeltSegment(tiles, speed));
        for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            _laneOutputs.Add(Endpoint.None);
        return id;
    }

    public BeltSegment Segment(int id) => _segments[id];

    public void SetOutput(int segment, int lane, Endpoint target) =>
        _laneOutputs[segment * BeltSegment.LaneCount + lane] = target;

    /// Chains one belt into the next, lane for lane.
    public void LinkBelts(int from, int to)
    {
        for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            SetOutput(from, lane, Endpoint.Belt(to, lane));
    }

    public int AddSplitter(Endpoint outputA, Endpoint outputB)
    {
        var splitter = new Splitter();
        splitter.Outputs[0] = outputA;
        splitter.Outputs[1] = outputB;
        _splitters.Add(splitter);
        return _splitters.Count - 1;
    }

    public int AddInserter(Endpoint source, Endpoint target, int swingTicks = 20, int stackSize = 1)
    {
        _inserters.Add(new Inserter(source, target, swingTicks, stackSize));
        return _inserters.Count - 1;
    }

    /// Every lane output in index order, for saving. `OutputOf` answers one at
    /// a time; a save needs the whole table.
    public IReadOnlyList<Endpoint> LaneOutputs => _laneOutputs;

    public Endpoint OutputOf(int segment, int lane) =>
        _laneOutputs[segment * BeltSegment.LaneCount + lane];

    public void Tick(IReadOnlyList<Machine> machines) => Tick(machines, Array.Empty<Miner>());

    public void Tick(IReadOnlyList<Machine> machines, IReadOnlyList<Miner> miners)
    {
        // Phase 1 -- everything slides forward. O(1) per lane.
        for (var i = 0; i < _segments.Count; i++)
            _segments[i].Advance();

        // Phase 2 -- lane exits hand on.
        for (var i = 0; i < _segments.Count; i++)
        {
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var source = _segments[i].LaneAt(lane);
                if (!source.FrontReady)
                    continue;

                var target = _laneOutputs[i * BeltSegment.LaneCount + lane];
                if (target.Kind == EndpointKind.None)
                    continue;

                if (TryGive(target, machines, source.PeekFront()))
                    source.TakeFront();
            }
        }

        // Phase 3 -- splitters distribute what they took.
        for (var i = 0; i < _splitters.Count; i++)
            _splitters[i].Push((endpoint, item) => TryGive(endpoint, machines, item));

        // Phase 4 -- inserters.
        for (var i = 0; i < _inserters.Count; i++)
            _inserters[i].Tick(this, machines, miners);
    }

    /// Hands one item to an endpoint. Returns false when it will not fit, which
    /// is how backpressure travels back up the line.
    public bool TryGive(Endpoint endpoint, IReadOnlyList<Machine> machines, ItemId item)
    {
        // Nothing gives TO a miner: ore only ever leaves one.
        switch (endpoint.Kind)
        {
            case EndpointKind.Belt:
                return _segments[endpoint.Index].LaneAt(endpoint.Lane).TryInsertBack(item);

            case EndpointKind.Splitter:
                var splitter = _splitters[endpoint.Index];
                if (!splitter.CanAccept) return false;
                splitter.Accept(item);
                return true;

            case EndpointKind.Machine:
                machines[endpoint.Index].PushInput(item, 1);
                return true;

            default:
                return false;
        }
    }

    /// Takes up to `max` of a single item type from an endpoint.
    public bool TryTake(Endpoint endpoint, IReadOnlyList<Machine> machines, int max,
                        out ItemId item, out int taken)
        => TryTake(endpoint, machines, Array.Empty<Miner>(), max, out item, out taken);

    public bool TryTake(Endpoint endpoint, IReadOnlyList<Machine> machines,
                        IReadOnlyList<Miner> miners, int max, out ItemId item, out int taken)
    {
        item = default;
        taken = 0;

        switch (endpoint.Kind)
        {
            case EndpointKind.Belt:
            {
                var lane = _segments[endpoint.Index].LaneAt(endpoint.Lane);
                if (!lane.FrontReady) return false;
                item = lane.TakeFront();
                taken = 1;
                return true;
            }

            case EndpointKind.Machine:
            {
                var machine = machines[endpoint.Index];
                foreach (var output in machine.Recipe.Outputs)
                {
                    var got = machine.PullOutput(output.Item, max);
                    if (got <= 0) continue;
                    item = output.Item;
                    taken = got;
                    return true;
                }

                return false;
            }

            case EndpointKind.Miner:
            {
                var miner = miners[endpoint.Index];
                var got = miner.Pull(max);
                if (got <= 0) return false;
                item = miner.Item;
                taken = got;
                return true;
            }

            default:
                return false;
        }
    }

    /// Every item currently on a belt, in a splitter, or in an inserter's hand.
    /// Conservation tests compare this against what went in.
    public int ItemsInTransit()
    {
        var total = 0;
        foreach (var segment in _segments) total += segment.ItemCount;
        foreach (var splitter in _splitters) total += splitter.Buffered;
        foreach (var inserter in _inserters) total += inserter.Held;
        return total;
    }
}
