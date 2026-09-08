namespace Sim;

/// A power pole. Two radii, because they do two different jobs: `WireRadius` is
/// how far it can reach another pole to form one network, and `SupplyRadius` is
/// the area it actually energises. Keeping them separate is what makes laying a
/// line across the map a distinct activity from covering a factory floor.
public readonly struct Pole
{
    public readonly int X;
    public readonly int Y;
    public readonly int SupplyRadius;
    public readonly int WireRadius;

    public Pole(int x, int y, int supplyRadius = 4, int wireRadius = 9)
    {
        if (supplyRadius < 1) throw new ArgumentOutOfRangeException(nameof(supplyRadius));
        if (wireRadius < 1) throw new ArgumentOutOfRangeException(nameof(wireRadius));
        X = x;
        Y = y;
        SupplyRadius = supplyRadius;
        WireRadius = wireRadius;
    }

    public bool Supplies(int x, int y)
    {
        var dx = x - X;
        var dy = y - Y;
        return dx * dx + dy * dy <= SupplyRadius * SupplyRadius;
    }

    /// Whether two poles are close enough to be wired together. The smaller of
    /// the two reaches decides, so a long-reach pylon cannot drag a short-reach
    /// pole into a network the short one could never have joined.
    public bool Reaches(in Pole other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        var reach = Math.Min(WireRadius, other.WireRadius);
        return dx * dx + dy * dy <= reach * reach;
    }
}

/// Burns fuel to make power.
///
/// A generator is not a machine: it consumes an item and produces energy, which
/// is not a recipe. Modelling it as one would mean inventing an "energy" item
/// that belts could carry and machines could stockpile.
public sealed class Generator
{
    public readonly ItemId Fuel;

    /// Energy produced on every tick it is burning.
    public readonly int OutputPerTick;

    /// How long one unit of fuel lasts.
    public readonly int TicksPerFuel;

    private int _fuelStock;
    private int _burnTicksLeft;

    public Generator(ItemId fuel, int outputPerTick, int ticksPerFuel)
    {
        if (outputPerTick < 1) throw new ArgumentOutOfRangeException(nameof(outputPerTick));
        if (ticksPerFuel < 1) throw new ArgumentOutOfRangeException(nameof(ticksPerFuel));
        Fuel = fuel;
        OutputPerTick = outputPerTick;
        TicksPerFuel = ticksPerFuel;
    }

    public int FuelStock => _fuelStock;
    public int BurnTicksLeft => _burnTicksLeft;
    public bool IsBurning => _burnTicksLeft > 0;

    /// Idle when out of fuel, Working while burning. Reusing MachineState means
    /// the status colour and the inspection panel need no special case.
    public MachineState State => _burnTicksLeft > 0 ? MachineState.Working : MachineState.Starved;

    public void AddFuel(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _fuelStock += count;
    }

    /// Empties the fuel hopper, returning what was in it. The unit already
    /// burning is not in it: `Tick` took that one out of stock when the burn
    /// started and it has been paying out power ever since, so handing it back
    /// on removal would mint energy from nothing.
    public int TakeFuel()
    {
        var stock = _fuelStock;
        _fuelStock = 0;
        return stock;
    }

    /// Advances one tick and returns the energy produced.
    ///
    /// Fuel is consumed at the moment a burn starts, not spread across it, so a
    /// generator picked up mid-burn has already paid for the coal in it -- the
    /// same rule the miner uses about ore halfway out of the hole.
    public int Tick()
    {
        if (_burnTicksLeft <= 0)
        {
            if (_fuelStock <= 0)
                return 0;

            _fuelStock--;
            _burnTicksLeft = TicksPerFuel;
        }

        _burnTicksLeft--;
        return OutputPerTick;
    }

    /// Save surface.
    public void Restore(int fuelStock, int burnTicksLeft)
    {
        _fuelStock = fuelStock;
        _burnTicksLeft = burnTicksLeft;
    }
}

/// Stores energy and gives it back.
///
/// Two jobs, and they are why a factory wants one. A burner generator makes
/// power in the lumps its fuel burns in, and machines draw it smoothly; an
/// accumulator flattens that. And demand is spiky -- a smelter bank all
/// starting a cycle at once -- so a store covers the peak without building
/// generation for the worst second of the day.
///
/// Charge and discharge are rate-limited separately from capacity. A large
/// slow store and a small fast one are different tools, and collapsing them
/// into one number would make them the same purchase.
public sealed class Accumulator
{
    public readonly int Capacity;

    /// The most that can move in or out in one tick, each way.
    public readonly int RatePerTick;

    public int Charge { get; private set; }

    public Accumulator(int capacity, int ratePerTick)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (ratePerTick < 1) throw new ArgumentOutOfRangeException(nameof(ratePerTick));
        Capacity = capacity;
        RatePerTick = ratePerTick;
    }

    public int Room => Capacity - Charge;

    /// Full, empty, or somewhere between. Reusing MachineState means the
    /// status colour and the inspection panel need no special case: a full
    /// store is Blocked (nothing more will fit) and an empty one is Starved.
    public MachineState State => Charge >= Capacity ? MachineState.Blocked
        : Charge <= 0 ? MachineState.Starved
        : MachineState.Working;

    /// Takes in up to `amount`, returning how much actually fit.
    public int Absorb(int amount)
    {
        var taken = Math.Min(Math.Min(Math.Max(0, amount), RatePerTick), Room);
        Charge += taken;
        return taken;
    }

    /// Gives out up to `amount`, returning how much was actually available.
    public int Release(int amount)
    {
        var given = Math.Min(Math.Min(Math.Max(0, amount), RatePerTick), Charge);
        Charge -= given;
        return given;
    }

    public void Restore(int charge) => Charge = Math.Clamp(charge, 0, Capacity);
}

/// Poles, generators, and who is connected to whom.
///
/// A network is a connected component of poles. Anything standing inside some
/// pole's supply radius is on that pole's network; anything else is unpowered
/// and, if it needs power, does not run.
///
/// Components are recomputed whenever something is built rather than maintained
/// incrementally. Building is rare and ticking is not, so the cost belongs on
/// the rare side -- and a rebuild cannot drift out of step with the truth the
/// way an incremental union can.
public sealed class PowerGrid
{
    private readonly List<Pole> _poles = new();
    private readonly List<Generator> _generators = new();
    private readonly List<Accumulator> _accumulators = new();
    private readonly List<int> _accumulatorNetwork = new();
    private readonly List<MachinePlacement> _accumulatorPlacements = new();

    /// Which network each pole and generator belongs to; -1 for a generator that
    /// no pole reaches, whose output therefore goes nowhere.
    private readonly List<int> _poleNetwork = new();
    private readonly List<int> _generatorNetwork = new();
    private readonly List<MachinePlacement> _generatorPlacements = new();

    private int _networkCount;
    private bool _dirty = true;

    /// Bumped whenever the layout changes. Callers that cache which network a
    /// thing is on watch this rather than re-asking every tick: `NetworkAt` is
    /// a scan over poles, and paying that per machine per tick cost more than
    /// the rest of the simulation put together.
    public int Version { get; private set; }

    public IReadOnlyList<Pole> Poles => _poles;
    public IReadOnlyList<Generator> Generators => _generators;
    public IReadOnlyList<MachinePlacement> GeneratorPlacements => _generatorPlacements;
    public IReadOnlyList<Accumulator> Accumulators => _accumulators;
    public IReadOnlyList<MachinePlacement> AccumulatorPlacements => _accumulatorPlacements;

    public int NetworkCount
    {
        get { Rebuild(); return _networkCount; }
    }

    public int AddPole(in Pole pole)
    {
        _poles.Add(pole);
        _poleNetwork.Add(-1);
        _dirty = true;
        Version++;
        return _poles.Count - 1;
    }

    public int AddGenerator(Generator generator, in MachinePlacement placement)
    {
        _generators.Add(generator);
        _generatorPlacements.Add(placement);
        _generatorNetwork.Add(-1);
        _dirty = true;
        Version++;
        return _generators.Count - 1;
    }

    /// Takes a pole out of the grid, moving the last one into its slot.
    ///
    /// Swap-remove rather than shifting: pole indices are held in the world's
    /// occupancy grid, and shifting would invalidate every index above the hole
    /// where swapping invalidates exactly one. Returns the index that moved, or
    /// -1 when the removed one was already the last, so the caller repoints one
    /// entry rather than rescanning the map.
    ///
    /// Networks are not repaired here: they are recomputed from scratch on the
    /// next `Rebuild`, the same path a newly built pole takes. Pulling the pole
    /// that joined two halves of a factory therefore splits them exactly as
    /// never having built it would have.
    public int RemovePole(int index)
    {
        if (index < 0 || index >= _poles.Count) return -1;
        return SwapRemove(_poles, _poleNetwork, index);
    }

    public int RemoveGenerator(int index)
    {
        if (index < 0 || index >= _generators.Count) return -1;
        _generatorPlacements[index] = _generatorPlacements[^1];
        _generatorPlacements.RemoveAt(_generatorPlacements.Count - 1);
        return SwapRemove(_generators, _generatorNetwork, index);
    }

    public int RemoveAccumulator(int index)
    {
        if (index < 0 || index >= _accumulators.Count) return -1;
        _accumulatorPlacements[index] = _accumulatorPlacements[^1];
        _accumulatorPlacements.RemoveAt(_accumulatorPlacements.Count - 1);
        return SwapRemove(_accumulators, _accumulatorNetwork, index);
    }

    private int SwapRemove<T>(List<T> items, List<int> networks, int index)
    {
        var moved = items.Count - 1;
        items[index] = items[moved];
        items.RemoveAt(moved);
        networks[index] = networks[moved];
        networks.RemoveAt(moved);
        _dirty = true;
        Version++;
        return index == moved ? -1 : moved;
    }

    /// The network energising a tile, or -1 if nothing does.
    public int NetworkAt(int x, int y)
    {
        Rebuild();

        for (var i = 0; i < _poles.Count; i++)
            if (_poles[i].Supplies(x, y))
                return _poleNetwork[i];

        return -1;
    }

    public int NetworkOfGenerator(int index)
    {
        Rebuild();
        return _generatorNetwork[index];
    }

    public int AddAccumulator(Accumulator accumulator, in MachinePlacement placement)
    {
        _accumulators.Add(accumulator);
        _accumulatorPlacements.Add(placement);
        _accumulatorNetwork.Add(-1);
        _dirty = true;
        Version++;
        return _accumulators.Count - 1;
    }

    public int NetworkOfAccumulator(int index)
    {
        Rebuild();
        return _accumulatorNetwork[index];
    }

    /// Connected components of poles, by wire reach.
    ///
    /// O(poles^2). Deliberately: a factory has tens of poles per network and
    /// this runs when something is built, not every tick. A spatial index here
    /// would be more code defending a cost nobody is paying.
    private void Rebuild()
    {
        if (!_dirty) return;
        _dirty = false;

        var parent = new int[_poles.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }

        for (var a = 0; a < _poles.Count; a++)
            for (var b = a + 1; b < _poles.Count; b++)
                if (_poles[a].Reaches(_poles[b]))
                {
                    var ra = Find(a);
                    var rb = Find(b);
                    if (ra != rb) parent[ra] = rb;
                }

        // Number the components in pole order, so network ids are stable for a
        // given layout rather than depending on union order.
        var ids = new Dictionary<int, int>();
        for (var i = 0; i < _poles.Count; i++)
        {
            var root = Find(i);
            if (!ids.TryGetValue(root, out var id))
            {
                id = ids.Count;
                ids[root] = id;
            }

            _poleNetwork[i] = id;
        }

        _networkCount = ids.Count;

        for (var i = 0; i < _generators.Count; i++)
            _generatorNetwork[i] = CoveringNetwork(_generatorPlacements[i]);

        for (var i = 0; i < _accumulators.Count; i++)
            _accumulatorNetwork[i] = CoveringNetwork(_accumulatorPlacements[i]);
    }

    private int CoveringNetwork(in MachinePlacement placement)
    {
        for (var p = 0; p < _poles.Count; p++)
            if (_poles[p].Supplies(placement.X, placement.Y))
                return _poleNetwork[p];

        return -1;
    }
}
