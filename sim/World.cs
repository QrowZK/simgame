namespace Sim;

/// Deterministic simulation root. Owns all machines and advances them one fixed
/// 60 UPS tick at a time. No wall-clock time, no unseeded randomness.
public sealed class World
{
    private readonly List<Machine> _machines = new();
    private readonly List<Miner> _miners = new();
    private readonly List<MachinePlacement> _minerPlacements = new();
    private MachinePlacement[] _placements = new MachinePlacement[64];
    private MachineState[] _states = new MachineState[64];
    private bool[] _placed = new bool[64];

    /// Tile -> machine index, for every tile a placed machine covers. A grid
    /// rather than a scan over placements: click-to-inspect and the placement
    /// check both want an O(1) answer, and at 100k machines a scan is neither.
    private readonly Dictionary<long, int> _occupancy = new();

    /// Item names for this world. Owned here because the GUI has to name what a
    /// machine is holding, and a name table that is not world state would let
    /// two parts of the game disagree about what item 47 is.
    public ItemDatabase Items { get; }

    /// What the player is carrying. Hand-loading a machine is a world state
    /// change, so it lives on the same side of the engine boundary as the tick.
    public Inventory PlayerInventory { get; } = new();

    /// Belts, splitters and inserters. Owned here so the whole world advances
    /// under one deterministic tick.
    public BeltNetwork Belts { get; } = new();

    /// Pipe networks. Fluids move by network flow rather than as discrete items,
    /// so this costs one budget reset per network per tick.
    public FluidSystem Fluids { get; } = new();

    public long TickCount { get; private set; }

    public World(int seed, ItemDatabase? items = null, WorldGen? gen = null)
    {
        Seed = seed;
        Items = items ?? new ItemDatabase();
        Ground = new Ground(gen ?? new WorldGen(seed, Array.Empty<OreSpec>()));
    }

    /// The terrain and its ore, with everything already dug out of it. A world
    /// built without a WorldGen gets an empty one rather than null, so nothing
    /// downstream has to null-check the map.
    public Ground Ground { get; }

    /// Poles, generators and networks. Power is a world-level concern because a
    /// network spans machines that know nothing about each other.
    public PowerGrid Power { get; } = new();

    private int[] _supply = Array.Empty<int>();
    private int[] _demand = Array.Empty<int>();
    private long[] _carry = Array.Empty<long>();

    /// Which network each machine and miner sits on, cached.
    ///
    /// Resolving this means scanning the poles, which is fine when something is
    /// built and ruinous every tick: doing it live cost more per tick than the
    /// entire rest of the simulation. The cache is rebuilt when the grid
    /// changes or something is placed, both of which are rare.
    private int[] _machineNetwork = Array.Empty<int>();
    private int[] _minerNetwork = Array.Empty<int>();
    private int _cachedGridVersion = -1;
    private int _cachedMachines = -1;
    private int _cachedMiners = -1;

    private void RefreshNetworkCache()
    {
        if (_cachedGridVersion == Power.Version &&
            _cachedMachines == _machines.Count &&
            _cachedMiners == _miners.Count)
            return;

        _cachedGridVersion = Power.Version;
        _cachedMachines = _machines.Count;
        _cachedMiners = _miners.Count;

        if (_machineNetwork.Length < _machines.Count)
            Array.Resize(ref _machineNetwork, Math.Max(64, _machines.Count));
        if (_minerNetwork.Length < _miners.Count)
            Array.Resize(ref _minerNetwork, Math.Max(64, _miners.Count));

        for (var i = 0; i < _machines.Count; i++)
            _machineNetwork[i] = Power.NetworkAt(_placements[i].X, _placements[i].Y);
        for (var i = 0; i < _miners.Count; i++)
            _minerNetwork[i] = Power.NetworkAt(_minerPlacements[i].X, _minerPlacements[i].Y);
    }

    /// Energy generated on each network last tick.
    public ReadOnlySpan<int> NetworkSupply => _supply.AsSpan(0, Power.NetworkCount);

    /// Energy asked for on each network last tick. Greater than supply means a
    /// brownout: everything on that network runs proportionally slower.
    public ReadOnlySpan<int> NetworkDemand => _demand.AsSpan(0, Power.NetworkCount);

    /// Builds a generator on the grid, refusing an occupied footprint the same
    /// way machines and miners do.
    public Generator? TryPlaceGenerator(Generator generator, in MachinePlacement placement)
    {
        if (!CanPlace(placement))
            return null;

        Power.AddGenerator(generator, placement);

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = GeneratorIndexBase;

        return generator;
    }

    /// Occupancy marker for generators. They are not addressed by index through
    /// the grid the way machines and miners are -- nothing clicks through to a
    /// generator yet -- so one marker is enough to keep their tiles reserved.
    public const int GeneratorIndexBase = 1 << 25;

    /// Distributes each network's generated energy across everything drawing on
    /// it.
    ///
    /// Shortfall is a proportional brownout rather than a cull: every machine on
    /// a half-fed network runs at half speed, instead of an arbitrary half of
    /// them stopping dead. Which half would be arbitrary is the problem --
    /// index order would mean the same machines always lose, for a reason
    /// invisible on screen.
    ///
    /// The split is exact integer arithmetic. Each consumer takes
    /// floor of its running share, and the remainder carries to the next one, so
    /// the shares sum to exactly the supply with no drift and no floats.
    private void TickPower()
    {
        var networks = Power.NetworkCount;
        if (_supply.Length < networks)
        {
            Array.Resize(ref _supply, Math.Max(4, networks));
            Array.Resize(ref _demand, Math.Max(4, networks));
            Array.Resize(ref _carry, Math.Max(4, networks));
        }

        Array.Clear(_supply);
        Array.Clear(_demand);
        Array.Clear(_carry);

        RefreshNetworkCache();

        for (var i = 0; i < Power.Generators.Count; i++)
        {
            var made = Power.Generators[i].Tick();
            var network = Power.NetworkOfGenerator(i);

            // A generator no pole reaches still burns its fuel. It is running;
            // it is just wired to nothing, which is a mistake the player should
            // see costing them coal.
            if (network >= 0 && made > 0)
                _supply[network] += made;
        }

        for (var i = 0; i < _machines.Count; i++)
        {
            var network = _machineNetwork[i];
            if (network >= 0 && _machines[i].WantsPower) _demand[network] += _machines[i].PowerDraw;
        }

        for (var i = 0; i < _miners.Count; i++)
        {
            var network = _minerNetwork[i];
            if (network >= 0 && _miners[i].WantsPower) _demand[network] += _miners[i].PowerDraw;
        }

        for (var i = 0; i < _machines.Count; i++)
        {
            var network = _machineNetwork[i];
            if (network >= 0 && _machines[i].WantsPower)
                _machines[i].SupplyEnergy(Share(network, _machines[i].PowerDraw));
        }

        for (var i = 0; i < _miners.Count; i++)
        {
            var network = _minerNetwork[i];
            if (network >= 0 && _miners[i].WantsPower)
                _miners[i].SupplyEnergy(Share(network, _miners[i].PowerDraw));
        }
    }

    private int Share(int network, int draw)
    {
        var demand = _demand[network];
        if (demand <= 0) return 0;

        _carry[network] += (long)_supply[network] * draw;
        var give = (int)Math.Min(int.MaxValue, _carry[network] / demand);
        _carry[network] -= (long)give * demand;

        // Never more than one tick's worth. A machine cannot spend faster than
        // that, and handing it more would let a surplus network bank energy in
        // machine buffers -- storage the game does not have yet.
        return Math.Min(draw, give);
    }

    public IReadOnlyList<Miner> Miners => _miners;
    public IReadOnlyList<MachinePlacement> MinerPlacements => _minerPlacements;

    /// Builds a miner on the patch under a tile. Returns null when the footprint
    /// is taken or there is nothing to mine -- placing a miner on bare rock is a
    /// mistake worth refusing rather than a machine that never runs.
    public Miner? TryPlaceMiner(in MachinePlacement placement,
                                int cycleTicks = Miner.DefaultCycleTicks, int powerDraw = 0)
    {
        if (!CanPlace(placement))
            return null;

        if (!Ground.TryResourceAt(placement.X, placement.Y, out var item, out _))
            return null;

        var miner = new Miner(item, placement.Area, cycleTicks, powerDraw: powerDraw);
        var index = _miners.Count;
        _miners.Add(miner);
        _minerPlacements.Add(placement);

        // Miners share the machine occupancy grid, so nothing can be built on
        // top of one. Their indices are offset past the machines, which is how
        // one grid addresses two lists.
        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = MinerIndexBase + index;

        return miner;
    }

    /// Occupancy values at or above this are miners, not machines. A single
    /// grid answers "is this tile free" for both without a second lookup.
    public const int MinerIndexBase = 1 << 24;

    /// The miner covering a tile, if any.
    public bool TryMinerAt(int x, int y, out Miner miner, out int index)
    {
        if (_occupancy.TryGetValue(Key(x, y), out var slot) && slot >= MinerIndexBase)
        {
            index = slot - MinerIndexBase;
            miner = _miners[index];
            return true;
        }

        miner = null!;
        index = -1;
        return false;
    }

    /// Restores a miner from a save, bypassing the "must be on ore" check --
    /// a patch that was worked out since the save must still load its miner,
    /// which then reports itself Depleted rather than vanishing.
    public Miner AddSavedMiner(ItemId item, in MachinePlacement placement, int cycleTicks,
                               int powerDraw = 0)
    {
        var miner = new Miner(item, placement.Area, cycleTicks, powerDraw: powerDraw);
        var index = _miners.Count;
        _miners.Add(miner);
        _minerPlacements.Add(placement);

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = MinerIndexBase + index;

        return miner;
    }

    /// The seed this world was generated from. Terrain and ore are pure
    /// functions of it, so a save stores the seed rather than the map.
    public int Seed { get; }

    public int MachineCount => _machines.Count;

    public IReadOnlyList<Machine> Machines => _machines;

    /// Dense views for the renderer to copy into instance buffers in one pass.
    public ReadOnlySpan<MachinePlacement> Placements => _placements.AsSpan(0, _machines.Count);
    public ReadOnlySpan<MachineState> MachineStates => _states.AsSpan(0, _machines.Count);

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// Whether a footprint is clear. Every tile is checked, so a large machine
    /// cannot be slid over a small one by anchoring it one tile away.
    public bool CanPlace(in MachinePlacement placement)
    {
        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                if (_occupancy.ContainsKey(Key(placement.X + dx, placement.Y + dy)))
                    return false;
        return true;
    }

    /// The machine covering a tile, if any. This is what a click resolves to.
    public bool TryMachineAt(int x, int y, out Machine machine, out int index)
    {
        if (_occupancy.TryGetValue(Key(x, y), out index) && index < MinerIndexBase)
        {
            machine = _machines[index];
            return true;
        }

        machine = null!;
        index = -1;
        return false;
    }

    public MachinePlacement PlacementOf(int index) => _placements[index];

    /// Whether a machine occupies tiles. Headless analysis builds machines with
    /// no position at all, and a save must not invent one for them.
    public bool IsPlaced(int index) => _placed[index];

    /// Restores the tick counter. Save surface only: nothing else may move the
    /// clock, or the tick would stop being the sim's single source of time.
    public void RestoreTick(long tick) => TickCount = tick;

    /// Adds a machine with no position. Used by tests and by headless analysis,
    /// where a machine's throughput is the question and its tile is not.
    public Machine AddMachine(Recipe recipe, int outputCapacityPerItem = 100)
        => AddMachine(recipe, default, outputCapacityPerItem);

    /// Places a machine on the grid, or returns null if the footprint is taken.
    /// Parallelism comes from the footprint, so callers never set the two
    /// independently and cannot get them out of step.
    public Machine? TryPlaceMachine(Recipe recipe, in MachinePlacement placement,
                                    int outputCapacityPerItem = 100)
    {
        if (!CanPlace(placement))
            return null;

        var machine = AddMachine(recipe, placement, outputCapacityPerItem);
        var index = _machines.Count - 1;
        _placed[index] = true;

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = index;

        return machine;
    }

    public Machine AddMachine(Recipe recipe, MachinePlacement placement, int outputCapacityPerItem = 100)
    {
        var machine = new Machine(recipe, outputCapacityPerItem, placement.Area);
        var index = _machines.Count;
        if (index == _placements.Length)
        {
            Array.Resize(ref _placements, _placements.Length * 2);
            Array.Resize(ref _states, _states.Length * 2);
            Array.Resize(ref _placed, _placed.Length * 2);
        }

        _machines.Add(machine);
        _placements[index] = placement;
        _states[index] = machine.State;
        return machine;
    }

    public void Tick()
    {
        // Power first: generators burn and the networks are shared out before
        // anything tries to run, so a machine's power state this tick reflects
        // this tick's generation rather than last tick's.
        TickPower();

        // Miners next: ore enters the world at the top of the tick, so the
        // transport phase below can already move what was just dug.
        for (var i = 0; i < _miners.Count; i++)
        {
            var placement = _minerPlacements[i];
            _miners[i].Tick(Ground, placement.X, placement.Y);
        }

        for (var i = 0; i < _machines.Count; i++)
        {
            var machine = _machines[i];
            machine.Tick();
            _states[i] = machine.State;
        }

        // Then transport. Fixed order, so the tick is reproducible.
        Fluids.Tick();
        Belts.Tick(_machines, _miners);

        TickCount++;
    }

    public void Tick(int count)
    {
        for (var i = 0; i < count; i++)
            Tick();
    }
}
