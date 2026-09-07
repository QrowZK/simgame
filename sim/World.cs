namespace Sim;

/// Deterministic simulation root. Owns all machines and advances them one fixed
/// 60 UPS tick at a time. No wall-clock time, no unseeded randomness.
public sealed class World
{
    private readonly List<Machine> _machines = new();
    private readonly List<Miner> _miners = new();
    private readonly List<MachinePlacement> _minerPlacements = new();
    private readonly List<FluidExtractor> _extractors = new();
    private readonly List<MachinePlacement> _extractorPlacements = new();
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

    /// Belts and inserters as tiles on the map. `Belts` moves items; this says
    /// where they are.
    public BeltMap BeltMap { get; } = new();

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

    /// Drones and the jobs they are doing.
    public LogisticsSystem Logistics { get; } = new();

    private readonly List<Controller> _controllers = new();

    public IReadOnlyList<Controller> Controllers => _controllers;

    /// Adds a player program. Compiling here rather than on first tick means a
    /// syntax error is reported when the program is installed, which is when
    /// the player is looking at it.
    public Controller AddController(string source)
    {
        var controller = new Controller(source);
        controller.Compile(this);
        _controllers.Add(controller);
        return controller;
    }

    /// Poles, generators and networks. Power is a world-level concern because a
    /// network spans machines that know nothing about each other.
    public PowerGrid Power { get; } = new();

    private int[] _supply = Array.Empty<int>();
    private int[] _demand = Array.Empty<int>();
    private long[] _carry = Array.Empty<long>();
    private long[] _storeCarry = Array.Empty<long>();
    private int[] _storeTotal = Array.Empty<int>();
    private int[] _storeMove = Array.Empty<int>();

    /// Energy held across every accumulator, and the most they could hold.
    /// A player fixing a brownout wants both: a bank at zero and a bank at
    /// capacity are different problems.
    public int StoredEnergy
    {
        get
        {
            var total = 0;
            foreach (var accumulator in Power.Accumulators) total += accumulator.Charge;
            return total;
        }
    }

    public int StorageCapacity
    {
        get
        {
            var total = 0;
            foreach (var accumulator in Power.Accumulators) total += accumulator.Capacity;
            return total;
        }
    }

    /// Builds an accumulator on the grid.
    public Accumulator? TryPlaceAccumulator(Accumulator accumulator, in MachinePlacement placement)
    {
        if (!CanPlace(placement))
            return null;

        Power.AddAccumulator(accumulator, placement);

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = GeneratorIndexBase;

        return accumulator;
    }

    /// Which network each machine and miner sits on, cached.
    ///
    /// Resolving this means scanning the poles, which is fine when something is
    /// built and ruinous every tick: doing it live cost more per tick than the
    /// entire rest of the simulation. The cache is rebuilt when the grid
    /// changes or something is placed, both of which are rare.
    private int[] _machineNetwork = Array.Empty<int>();
    private int[] _minerNetwork = Array.Empty<int>();

    /// Which fluid network each machine is plumbed into. Cached for the same
    /// reason as power: finding it means scanning the machine's border, which
    /// is fine when something is built and ruinous every tick.
    private int[] _machineFluidNetwork = Array.Empty<int>();
    private int[] _extractorFluidNetwork = Array.Empty<int>();
    private int _cachedFluidVersion = -1;
    private int _cachedExtractors = -1;
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

    private void RefreshFluidCache()
    {
        if (_cachedFluidVersion == Fluids.Version &&
            _machineFluidNetwork.Length >= _machines.Count &&
            _extractorFluidNetwork.Length >= _extractors.Count &&
            _cachedExtractors == _extractors.Count)
            return;

        _cachedExtractors = _extractors.Count;

        _cachedFluidVersion = Fluids.Version;
        if (_machineFluidNetwork.Length < _machines.Count)
            Array.Resize(ref _machineFluidNetwork, Math.Max(64, _machines.Count));

        for (var i = 0; i < _machines.Count; i++)
            _machineFluidNetwork[i] = Fluids.NetworkAdjacentTo(_placements[i]);

        if (_extractorFluidNetwork.Length < _extractors.Count)
            Array.Resize(ref _extractorFluidNetwork, Math.Max(16, _extractors.Count));

        for (var i = 0; i < _extractors.Count; i++)
            _extractorFluidNetwork[i] = Fluids.NetworkAdjacentTo(_extractorPlacements[i]);
    }

    /// Runs the pumps and derricks, and empties them into whatever pipe they
    /// are standing next to. An extractor plumbed to nothing fills its buffer
    /// and stops, which is the same backpressure a miner with no belt gets.
    private void TickExtractors()
    {
        RefreshFluidCache();

        for (var i = 0; i < _extractors.Count; i++)
        {
            var placement = _extractorPlacements[i];
            var extractor = _extractors[i];
            extractor.Tick(Ground, placement.X, placement.Y);

            var network = _extractorFluidNetwork[i];
            if (network < 0 || extractor.Buffered <= 0) continue;

            var moved = Fluids.Network(network).TryInsert(extractor.Fluid, extractor.Buffered);
            if (moved > 0) extractor.Pull(moved);
        }
    }

    /// Moves fluid between machines and the pipe they are standing next to.
    ///
    /// Machines keep fluids in the same buffers as solids, so nothing in the
    /// machine itself knows a pipe exists -- the world fills the inputs before
    /// the machines run and drains the outputs afterwards, exactly as it hands
    /// out power. One transport concept per direction, not two.
    private void FillFluidInputs()
    {
        RefreshFluidCache();

        for (var i = 0; i < _machines.Count; i++)
        {
            var machine = _machines[i];
            if (!machine.Recipe.UsesFluids) continue;

            var network = _machineFluidNetwork[i];
            if (network < 0) continue;

            foreach (var input in machine.Recipe.Inputs)
            {
                if (!input.IsFluid) continue;

                // Top up to one cycle's worth. Buffering more would let a
                // machine hoard a scarce fluid its neighbours also need.
                var want = machine.InputPerCycle(input.Item) - machine.GetInputCount(input.Item);
                if (want <= 0) continue;

                var got = Fluids.Network(network).TryExtract(input.Item, want);
                if (got > 0) machine.PushInput(input.Item, got);
            }
        }
    }

    private void DrainFluidOutputs()
    {
        for (var i = 0; i < _machines.Count; i++)
        {
            var machine = _machines[i];
            if (!machine.Recipe.UsesFluids) continue;

            var network = _machineFluidNetwork[i];
            if (network < 0) continue;

            foreach (var output in machine.Recipe.Outputs)
            {
                if (!output.IsFluid) continue;

                var held = machine.GetOutputCount(output.Item);
                if (held <= 0) continue;

                // Insert first, then take out of the machine only what the pipe
                // actually accepted -- a full network has to back the machine up
                // rather than swallow the difference.
                var moved = Fluids.Network(network).TryInsert(output.Item, held);
                if (moved > 0) machine.PullOutput(output.Item, moved);
            }
        }
    }

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
            Array.Resize(ref _storeCarry, Math.Max(4, networks));
            Array.Resize(ref _storeTotal, Math.Max(4, networks));
            Array.Resize(ref _storeMove, Math.Max(4, networks));
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

        for (var i = 0; i < _extractors.Count; i++)
        {
            if (!_extractors[i].WantsPower) continue;
            var network = Power.NetworkAt(_extractorPlacements[i].X, _extractorPlacements[i].Y);
            if (network >= 0) _demand[network] += _extractors[i].PowerDraw;
        }

        // Storage covers the gap before anything is handed out, so a machine
        // on a network with a charged accumulator never sees the shortfall.
        DischargeStorage();

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

        for (var i = 0; i < _extractors.Count; i++)
        {
            if (!_extractors[i].WantsPower) continue;
            var network = Power.NetworkAt(_extractorPlacements[i].X, _extractorPlacements[i].Y);
            if (network >= 0)
                _extractors[i].SupplyEnergy(Share(network, _extractors[i].PowerDraw));
        }

        ChargeStorage();
    }

    /// Tops up the supply from stored energy, but only as far as demand.
    ///
    /// Accumulators are drawn down in proportion to what each is holding, so a
    /// bank empties evenly and reads as one number rather than as a row of
    /// stores at unrelated levels. The same exact-integer carry the consumer
    /// allocator uses, for the same reason: no floats, no drift.
    private void DischargeStorage()
    {
        var accumulators = Power.Accumulators;
        if (accumulators.Count == 0) return;

        Array.Clear(_storeCarry, 0, _storeCarry.Length);
        Array.Clear(_storeTotal, 0, _storeTotal.Length);
        Array.Clear(_storeMove, 0, _storeMove.Length);

        for (var i = 0; i < accumulators.Count; i++)
        {
            var network = Power.NetworkOfAccumulator(i);
            if (network < 0) continue;
            _storeTotal[network] += Math.Min(accumulators[i].Charge, accumulators[i].RatePerTick);
        }

        // Settle how much each network draws before moving any of it. Releasing
        // energy raises the supply, so a shortfall read inside the loop shrinks
        // as we go and the first accumulators would carry the whole load.
        for (var n = 0; n < _storeTotal.Length; n++)
        {
            var shortfall = _demand[n] - _supply[n];
            _storeMove[n] = shortfall <= 0 ? 0 : Math.Min(shortfall, _storeTotal[n]);
        }

        // Rotated by tick. The carry hands the division's remainder to whoever
        // is processed last, so a fixed order would give the same accumulator
        // the extra unit every tick and the bank would drift apart despite the
        // total being exact. TickCount keeps the rotation deterministic.
        var offset = (int)(TickCount % accumulators.Count);
        for (var k = 0; k < accumulators.Count; k++)
        {
            var i = (k + offset) % accumulators.Count;
            var network = Power.NetworkOfAccumulator(i);
            if (network < 0 || _storeMove[network] <= 0) continue;

            var available = Math.Min(accumulators[i].Charge, accumulators[i].RatePerTick);
            _storeCarry[network] += (long)_storeMove[network] * available;
            var want = (int)(_storeCarry[network] / _storeTotal[network]);
            _storeCarry[network] -= (long)want * _storeTotal[network];

            _supply[network] += accumulators[i].Release(want);
        }
    }

    /// Puts genuine surplus into storage.
    ///
    /// Machines are paid first and accumulators take what is left, which is why
    /// charging never competes with production: a factory does not brown out
    /// because its batteries were filling.
    private void ChargeStorage()
    {
        var accumulators = Power.Accumulators;
        if (accumulators.Count == 0) return;

        Array.Clear(_storeCarry, 0, _storeCarry.Length);
        Array.Clear(_storeTotal, 0, _storeTotal.Length);
        Array.Clear(_storeMove, 0, _storeMove.Length);

        for (var i = 0; i < accumulators.Count; i++)
        {
            var network = Power.NetworkOfAccumulator(i);
            if (network < 0) continue;
            _storeTotal[network] += Math.Min(accumulators[i].Room, accumulators[i].RatePerTick);
        }

        // As in DischargeStorage: fix the amount first, then share it out. The
        // surplus falls as each accumulator absorbs, so reading it per
        // accumulator would fill the first of a bank and starve the rest.
        for (var n = 0; n < _storeTotal.Length; n++)
        {
            var surplus = _supply[n] - _demand[n];
            _storeMove[n] = surplus <= 0 ? 0 : Math.Min(surplus, _storeTotal[n]);
        }

        // Rotated by tick. The carry hands the division's remainder to whoever
        // is processed last, so a fixed order would give the same accumulator
        // the extra unit every tick and the bank would drift apart despite the
        // total being exact. TickCount keeps the rotation deterministic.
        var offset = (int)(TickCount % accumulators.Count);
        for (var k = 0; k < accumulators.Count; k++)
        {
            var i = (k + offset) % accumulators.Count;
            var network = Power.NetworkOfAccumulator(i);
            if (network < 0 || _storeMove[network] <= 0) continue;

            var room = Math.Min(accumulators[i].Room, accumulators[i].RatePerTick);
            _storeCarry[network] += (long)_storeMove[network] * room;
            var want = (int)(_storeCarry[network] / _storeTotal[network]);
            _storeCarry[network] -= (long)want * _storeTotal[network];

            _supply[network] -= accumulators[i].Absorb(want);
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
    public IReadOnlyList<FluidExtractor> Extractors => _extractors;
    public IReadOnlyList<MachinePlacement> ExtractorPlacements => _extractorPlacements;

    /// Builds a pump or derrick. On water it draws water forever; on a patch of
    /// a raw fluid it draws that and depletes it. Anywhere else there is nothing
    /// to pump, and refusing is better than a building that never runs.
    public FluidExtractor? TryPlaceExtractor(in MachinePlacement placement, ItemId ambientWater,
                                             int cycleTicks = FluidExtractor.DefaultCycleTicks,
                                             int powerDraw = 0)
    {
        if (!CanPlace(placement))
            return null;

        FluidExtractor extractor;

        if (Ground.TryResourceAt(placement.X, placement.Y, out var item, out _))
            extractor = new FluidExtractor(item, ambient: false, placement.Area, cycleTicks, powerDraw);
        else if (Ground.Gen.IsWater(placement.X, placement.Y))
            extractor = new FluidExtractor(ambientWater, ambient: true, placement.Area, cycleTicks, powerDraw);
        else
            return null;

        return AddSavedExtractor(extractor, placement);
    }

    /// Restores an extractor without the "must be on something" check, so a
    /// derrick whose patch ran dry since the save still loads and reports
    /// itself depleted rather than disappearing.
    public FluidExtractor AddSavedExtractor(FluidExtractor extractor, in MachinePlacement placement)
    {
        var index = _extractors.Count;
        _extractors.Add(extractor);
        _extractorPlacements.Add(placement);

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = ExtractorIndexBase + index;

        return extractor;
    }

    /// Occupancy marker range for extractors, above miners and generators.
    public const int ExtractorIndexBase = 1 << 26;

    public bool TryExtractorAt(int x, int y, out FluidExtractor extractor, out int index)
    {
        if (_occupancy.TryGetValue(Key(x, y), out var slot) && slot >= ExtractorIndexBase)
        {
            index = slot - ExtractorIndexBase;
            extractor = _extractors[index];
            return true;
        }

        extractor = null!;
        index = -1;
        return false;
    }

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
        if (_occupancy.TryGetValue(Key(x, y), out var slot) &&
            slot >= MinerIndexBase && slot < GeneratorIndexBase)
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
    /// Builds one thing from the player's inventory.
    ///
    /// This is the whole build interaction in one call, deliberately: the item
    /// is only taken once the placement has succeeded, so a refused build can
    /// never cost the player the machine. Every refusal is a distinct reason,
    /// because the UI has to say which one.
    ///
    /// `recipe` is required for machines and ignored by everything else: a
    /// machine still has to know what it makes before it exists. It is no
    /// longer stuck with it -- `TryChangeRecipe` retasks a placed one (ADR
    /// 0021) -- but a build with no recipe is still a build with nothing
    /// chosen, and is still refused.
    public BuildResult TryBuild(BuildCatalogue catalogue, ItemId item, int x, int y,
                                Recipe? recipe = null, Direction facing = Direction.East)
    {
        if (!catalogue.TryGet(item, out var buildable))
            return BuildResult.NotBuildable;

        if (buildable.Kind == BuildKind.NotPlaceable)
            return BuildResult.NotPlaceableYet;

        if (PlayerInventory.Count(item) <= 0)
            return BuildResult.NoneCarried;

        var placement = buildable.PlacementAt(x, y);

        if (!CanPlace(placement) || CoversFluidNode(placement) || CoversBeltTile(placement))
            return BuildResult.Blocked;

        if (buildable.Kind == BuildKind.Machine && (recipe is null || !catalogue.CanRun(buildable, recipe)))
            return BuildResult.NeedsRecipe;

        var tunnel = TunnelRefusal.None;

        var built = buildable.Kind switch
        {
            BuildKind.Machine =>
                TryPlaceMachine(recipe!, placement, sourceItem: item) is not null,

            BuildKind.Miner =>
                TryPlaceMiner(placement, powerDraw: buildable.TierPower) is not null,

            BuildKind.Generator =>
                TryPlaceGenerator(
                    new Generator(FuelFor(), buildable.GeneratorOutput,
                                  Buildable.GeneratorTicksPerFuel),
                    placement) is not null,

            BuildKind.Accumulator =>
                TryPlaceAccumulator(
                    new Accumulator(buildable.AccumulatorCapacity, buildable.AccumulatorRate),
                    placement) is not null,

            BuildKind.Pole =>
                AddBuiltPole(buildable, placement),

            BuildKind.Pump =>
                TryPlaceExtractor(placement, WaterItem(), powerDraw: buildable.TierPower) is not null,

            BuildKind.Pipe => Fluids.AddPipe(x, y),
            BuildKind.Tank => Fluids.AddTank(x, y),

            BuildKind.Belt => BeltMap.PlaceBelt(x, y, facing, buildable.BeltSpeed),

            BuildKind.Inserter => BeltMap.PlaceInserter(
                x, y, facing, buildable.InserterSwingTicks, buildable.InserterStackSize),

            BuildKind.UndergroundBelt => BeltMap.PlaceUnderground(
                x, y, facing, buildable.BeltSpeed, buildable.UndergroundReach, out tunnel),

            BuildKind.Splitter => BeltMap.PlaceSplitter(x, y, facing),

            _ => false,
        };

        if (!built)
            return buildable.Kind switch
            {
                // The placement checks above already cleared the footprint, so
                // a refusal here is the thing under the tile, not the tile.
                BuildKind.Miner => BuildResult.NoResource,
                BuildKind.Pump => BuildResult.NoFluid,
                BuildKind.UndergroundBelt when tunnel == TunnelRefusal.TooFar
                    => BuildResult.TooFarToTunnel,
                _ => BuildResult.Blocked,
            };

        PlayerInventory.Take(item, 1);
        return BuildResult.Ok;
    }

    /// Belts and inserters keep their own tile map, so the occupancy grid does
    /// not know about them either. Same rule, same reason as fluid nodes.
    public bool CoversBeltTile(in MachinePlacement placement)
    {
        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                if (BeltMap.HasAnythingAt(placement.X + dx, placement.Y + dy))
                    return true;
        return false;
    }

    /// Pipes and machines are tracked by separate systems, so neither knows
    /// about the other's tiles. Nothing stops code layering them and existing
    /// worlds do; what a player builds by hand should not overlap, so the rule
    /// lives here at the build boundary rather than inside either system.
    public bool CoversFluidNode(in MachinePlacement placement)
    {
        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                if (Fluids.HasNodeAt(placement.X + dx, placement.Y + dy))
                    return true;
        return false;
    }

    private bool AddBuiltPole(Buildable buildable, in MachinePlacement placement)
    {
        Power.AddPole(new Pole(placement.X, placement.Y,
                               buildable.PoleSupplyRadius, buildable.PoleWireRadius));

        for (var dy = 0; dy < placement.Size; dy++)
            for (var dx = 0; dx < placement.Size; dx++)
                _occupancy[Key(placement.X + dx, placement.Y + dy)] = GeneratorIndexBase;

        return true;
    }

    /// What a newly built generator expects to burn. Coal if the data has it,
    /// which it does; a generator with no fuel item would simply sit idle
    /// rather than fail to build.
    private ItemId FuelFor()
        => Items.TryGetId("coal_deposit", out var coal) ? coal : default;

    private ItemId WaterItem()
        => Items.TryGetId("water", out var water) ? water : default;

    /// What a belt or an inserter finds on a tile that is not a belt.
    ///
    /// The belt map deliberately does not know about machines and miners -- it
    /// would have to depend on the whole world to place one tile -- so it asks
    /// this instead.
    public Endpoint EndpointAt(int x, int y)
    {
        if (TryMachineAt(x, y, out _, out var machine)) return Endpoint.Machine(machine);
        if (TryMinerAt(x, y, out _, out var miner)) return Endpoint.Miner(miner);
        return Endpoint.None;
    }

    /// Forces the belt map to recompile now rather than at the next tick. The
    /// build path calls this so a freshly placed belt reports its segment
    /// immediately, which is what a test -- and a renderer -- asks for.
    public void SyncBelts() => BeltMap.RebuildIfDirty(Belts, EndpointAt);

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

    /// Retasks a placed machine: it stops making one thing and starts making
    /// another, and everything it was holding goes back to the player.
    ///
    /// This is the reversal of ADR 0015's "recipes are chosen before placing,
    /// not after". The bill that ADR named is paid here: the eviction rule
    /// (`Machine.SetRecipe`, conservation), the check that the machine can
    /// actually run what it is being asked for, and a save version.
    ///
    /// `evicted` is the number of units handed back, and it is the number the
    /// UI reports. A retask that silently swallows a stack of ore is the kind
    /// of thing that loses a save's worth of trust, so the count is part of
    /// the result rather than something the caller has to work out.
    public RecipeChangeResult TryChangeRecipe(BuildCatalogue catalogue, int index,
                                              Recipe recipe, out int evicted)
    {
        evicted = 0;

        if (index < 0 || index >= _machines.Count)
            return RecipeChangeResult.NoMachine;

        var machine = _machines[index];

        // Re-picking what it already makes must cost nothing. A picker that
        // dumps the machine when a player clicks the highlighted row is a trap.
        if (ReferenceEquals(machine.Recipe, recipe))
            return RecipeChangeResult.AlreadyRunning;

        if (machine.SourceItem is not { } item || !catalogue.TryGet(item, out var buildable))
            return RecipeChangeResult.UnknownMachine;

        if (!catalogue.CanRun(buildable, recipe))
            return RecipeChangeResult.CannotRun;

        evicted = machine.SetRecipe(recipe, PlayerInventory);
        _states[index] = machine.State;
        return RecipeChangeResult.Ok;
    }

    /// Adds a machine with no position. Used by tests and by headless analysis,
    /// where a machine's throughput is the question and its tile is not.
    public Machine AddMachine(Recipe recipe, int outputCapacityPerItem = 100)
        => AddMachine(recipe, default, outputCapacityPerItem);

    /// Places a machine on the grid, or returns null if the footprint is taken.
    /// Parallelism comes from the footprint, so callers never set the two
    /// independently and cannot get them out of step.
    public Machine? TryPlaceMachine(Recipe recipe, in MachinePlacement placement,
                                    int outputCapacityPerItem = 100,
                                    ItemId? sourceItem = null)
    {
        if (!CanPlace(placement))
            return null;

        var machine = AddMachine(recipe, placement, outputCapacityPerItem);
        machine.SourceItem = sourceItem;
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

        // Pipe budgets are refreshed before anything draws on them, so a
        // machine's fluid intake this tick comes out of this tick's allowance.
        Fluids.Tick();
        TickExtractors();
        FillFluidInputs();

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
        DrainFluidOutputs();
        // Recompile before moving anything, so a belt placed this tick carries
        // items this tick rather than sitting inert until the next one.
        BeltMap.RebuildIfDirty(Belts, EndpointAt);
        Belts.Tick(_machines, _miners);
        // Controllers run last, on a world that has finished moving for this
        // tick. A program reading a machine's output sees a settled number
        // rather than one that depends on where in the tick it happened to ask.
        for (var i = 0; i < _controllers.Count; i++)
            _controllers[i].Tick();

        Logistics.Tick(this);

        TickCount++;
    }

    public void Tick(int count)
    {
        for (var i = 0; i < count; i++)
            Tick();
    }
}
