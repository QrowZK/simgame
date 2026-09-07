namespace Sim;

public enum TerrainType
{
    DeepWater,
    Water,
    Sand,
    Grass,
    Rock,
    Mountain,
}

/// How one resource is distributed. `MinRing` is the first region ring out from
/// spawn where it can appear, which is how scarcity is expressed: the starter
/// metals are underfoot, xenite is a long way out.
public readonly struct OreSpec
{
    public readonly ItemId Item;
    public readonly int MinRing;
    public readonly int PatchRadius;

    /// Units in a typical patch. Deliberately a flat figure rather than a
    /// distribution: a patch you walk to should be worth roughly what the last
    /// one was, so deciding to haul from it is a judgement about distance rather
    /// than a bet on what you will find when you get there.
    public readonly int BaseAmount;

    /// Whether the deposit holds a fluid rather than a solid. Crude oil is
    /// buried like an ore because the derrick needs something to stand on, but
    /// it is not something a player can pick up -- see `HandOps.Mine`.
    public readonly bool IsFluid;

    /// Whether a new player can actually do something with this resource on the
    /// day they land: it is an input to a recipe that is unlocked at tick zero.
    /// Worldgen guarantees one of these near spawn, because a ranked list of
    /// nearby resources is worthless when the top of it is halite and nothing
    /// the player can build will touch halite (ADR 0026).
    ///
    /// Derived from the recipe and tech data in `NewGame.OreSpecs`, never listed
    /// here: a data change that gives the manual furnace another ore moves this
    /// with it.
    public readonly bool IsStarter;

    public OreSpec(ItemId item, int minRing, int patchRadius, int baseAmount,
                   bool isFluid = false, bool isStarter = false)
    {
        Item = item;
        MinRing = minRing;
        PatchRadius = patchRadius;
        BaseAmount = baseAmount;
        IsFluid = isFluid;
        IsStarter = isStarter;
    }
}

public readonly struct OrePatch
{
    public readonly ItemId Item;
    public readonly int X;
    public readonly int Y;
    public readonly int Radius;

    /// Total units this patch holds.
    public readonly int Amount;

    /// Carried down from the `OreSpec` so anything holding a patch can ask what
    /// form it is without a catalogue in hand.
    public readonly bool IsFluid;

    public OrePatch(ItemId item, int x, int y, int radius, int amount, bool isFluid = false)
    {
        Item = item;
        X = x;
        Y = y;
        Radius = radius;
        Amount = amount;
        IsFluid = isFluid;
    }

    public bool Contains(int x, int y)
    {
        var dx = x - X;
        var dy = y - Y;
        return dx * dx + dy * dy <= Radius * Radius;
    }
}

/// Procedural terrain and ore, on a tile grid.
///
/// Everything is a pure function of (seed, tile), so chunks can be generated in
/// any order, thrown away and regenerated, and come out identical. There is no
/// generation-order dependence and no global RNG.
///
/// Ore is dealt round-robin from the list of resources eligible at a region's
/// distance from spawn, rather than thresholded from noise. Noise thresholding
/// is the usual approach and it cannot promise anything: a rare ore can simply
/// fail to appear, and the player discovers this hours later when a recipe is
/// unbuildable. Dealing guarantees complete coverage within a bounded area,
/// which is a property worth having and worth testing.
public sealed class WorldGen
{
    public const int ChunkSize = 32;
    public const int RegionChunks = 8;
    public const int RegionSize = ChunkSize * RegionChunks;   // 256 tiles

    /// Patches dealt per region. Coverage of an eligible set of N resources is
    /// therefore complete within ceil(N / PatchesPerRegion) regions.
    public const int PatchesPerRegion = 6;

    /// Minimum tiles between patches of DIFFERENT resources. This is the setting
    /// that decides whether hauling is a real problem: without it, a region can
    /// deal copper, tin and coal on top of each other and the answer to every
    /// logistics question is "build it here". Patches of the SAME resource are
    /// free to sit close together, so a copper field still reads as a field.
    public const int MinCrossOreSeparation = 56;

    /// How much a patch's size and contents may vary from its resource's
    /// baseline. Kept tight on purpose: variance here is indistinguishable from
    /// luck, and a seed where the nearest iron holds a third of the usual is a
    /// seed the player did nothing to deserve.
    private const double AmountJitter = 0.10;

    /// How far from spawn the guaranteed starter patch may be dealt. Inside the
    /// prospector's 96-tile detection radius by a wide margin, so the device
    /// answers the opening question rather than reporting that everything it can
    /// see is useless.
    public const int StarterPatchRange = 40;

    private const int SeaLevel = 96;
    private const int ShoreLevel = 108;
    private const int RockLevel = 168;
    private const int MountainLevel = 205;

    private readonly OreSpec[] _ores;
    private readonly Dictionary<long, OrePatch[]> _regionCache = new();

    public int Seed { get; }

    public WorldGen(int seed, IReadOnlyList<OreSpec> ores)
    {
        Seed = seed;
        _ores = ores.ToArray();
    }

    public IReadOnlyList<OreSpec> Ores => _ores;

    /// Height in [0, 255].
    public int HeightAt(int x, int y)
    {
        var h = Noise.Fractal(Seed, x, y, cell: 192, octaves: 5);

        // Spawn sits on workable land: pull the first region up out of the sea
        // and down off the mountains so a new game never starts unplayable.
        var distance = Math.Max(Math.Abs(x), Math.Abs(y));
        if (distance < RegionSize)
        {
            var pull = 1.0 - distance / (double)RegionSize;
            h = h * (1 - 0.6 * pull) + 0.55 * 0.6 * pull;
        }

        return (int)(h * 255.0);
    }

    public TerrainType TerrainAt(int x, int y)
    {
        var h = HeightAt(x, y);
        if (h < SeaLevel - 24) return TerrainType.DeepWater;
        if (h < SeaLevel) return TerrainType.Water;
        if (h < ShoreLevel) return TerrainType.Sand;
        if (h < RockLevel) return TerrainType.Grass;
        if (h < MountainLevel) return TerrainType.Rock;
        return TerrainType.Mountain;
    }

    public bool IsWater(int x, int y) => TerrainAt(x, y) <= TerrainType.Water;

    public static int RegionOf(int tile) => (int)Math.Floor(tile / (double)RegionSize);

    /// Rings out from spawn: region (0,0) is ring 0.
    public static int RingOf(int rx, int ry) => Math.Max(Math.Abs(rx), Math.Abs(ry));

    /// Every patch in a region. Cached, but the cache is only a speed-up -- the
    /// result is a pure function of seed and region.
    public IReadOnlyList<OrePatch> PatchesInRegion(int rx, int ry)
    {
        var key = ((long)rx << 32) ^ (uint)ry;
        if (_regionCache.TryGetValue(key, out var cached))
            return cached;

        var ring = RingOf(rx, ry);
        var eligible = new List<OreSpec>();
        for (var i = 0; i < _ores.Length; i++)
            if (_ores[i].MinRing <= ring)
                eligible.Add(_ores[i]);

        var patches = new List<OrePatch>();

        // The home region is dealt one guaranteed starter patch before anything
        // else, so it is placed first and every later patch has to keep its
        // distance from it rather than the other way round.
        if (rx == 0 && ry == 0)
            AddStarterPatch(patches);

        if (eligible.Count > 0)
        {
            // Round-robin deal. The ordinal advances with the region, so walking
            // outward covers the whole eligible set rather than re-rolling it.
            var ordinal = rx + ry * 31;

            for (var i = 0; i < PatchesPerRegion; i++)
            {
                var index = (int)((uint)(ordinal * PatchesPerRegion + i) % (uint)eligible.Count);
                var spec = eligible[index];

                var salt = Noise.Hash(Seed ^ 0x51ED2701, rx * 977 + i, ry * 631 + i);
                var baseX = (int)(salt % RegionSize);
                var baseY = (int)((salt >> 12) % RegionSize);

                // Find a spot that is on land and clear of other resources.
                // Both constraints are searched together rather than one after
                // the other, so satisfying one cannot quietly break the other.
                var placed = false;
                var x = 0;
                var y = 0;

                for (var attempt = 0; attempt < 24 && !placed; attempt++)
                {
                    x = rx * RegionSize + (baseX + attempt * 37) % RegionSize;
                    y = ry * RegionSize + (baseY + attempt * 53) % RegionSize;

                    if (IsWater(x, y))
                        continue;

                    placed = true;
                    foreach (var other in patches)
                    {
                        if (other.Item.Equals(spec.Item))
                            continue;       // same resource may cluster

                        var dx = other.X - x;
                        var dy = other.Y - y;
                        if (dx * dx + dy * dy < MinCrossOreSeparation * MinCrossOreSeparation)
                        {
                            placed = false;
                            break;
                        }
                    }
                }

                if (!placed)
                    continue;

                // Predictable size and contents: a tenth either way, no more.
                var wobble = (salt >> 24) / 255.0 * 2.0 - 1.0;
                var amount = (int)(spec.BaseAmount * (1.0 + AmountJitter * wobble));
                var radius = Math.Max(2, spec.PatchRadius + (int)Math.Round(wobble));

                patches.Add(new OrePatch(spec.Item, x, y, radius, Math.Max(1, amount), spec.IsFluid));
            }
        }

        var result = patches.ToArray();
        _regionCache[key] = result;
        return result;
    }


    /// Deals the one patch a new game is guaranteed: a resource the player can
    /// use on the first day, within `StarterPatchRange` tiles of spawn.
    ///
    /// Measured before this existed: on 18 of the first 20 seeds the nearest
    /// resource to spawn was halite, coal, quartz, limestone, garnierite or
    /// crude oil, and the nearest one the manual furnace could smelt was between
    /// 56 and 184 tiles away -- often outside the prospector's range entirely.
    /// The opening was therefore a long walk decided by the seed, which is the
    /// "explore until lucky" the prospector exists to remove.
    ///
    /// Dealt rather than rolled, like the rest of worldgen: a threshold that
    /// usually works still strands the occasional player, and a stranded player
    /// cannot tell an unlucky seed from a broken game.
    ///
    /// It lands in the +x/+y quadrant because that is the part of the home
    /// region that spawn sits in the corner of. A patch 30 tiles north-west of
    /// the origin belongs to a different region, and dealing across a region
    /// boundary would make a region's contents depend on its neighbours.
    private void AddStarterPatch(List<OrePatch> patches)
    {
        var starters = new List<OreSpec>();
        for (var i = 0; i < _ores.Length; i++)
            if (_ores[i].IsStarter && _ores[i].MinRing <= 0)
                starters.Add(_ores[i]);

        if (starters.Count == 0)
            return;

        var pick = Noise.Hash(Seed ^ 0x1D57B0C1, 0, 0);
        var spec = starters[(int)(pick % (uint)starters.Count)];

        // Never right under the player's feet: the first minute is meant to be
        // a short walk with the survey device, not a patch you are standing on.
        const int minimum = 12;
        var span = StarterPatchRange - minimum;

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var salt = Noise.Hash(Seed ^ 0x6C1FA33B, attempt, 0);
            var x = minimum + (int)(salt % (uint)span);
            var y = minimum + (int)((salt >> 11) % (uint)span);

            if (IsWater(x, y))
                continue;

            // The range is a distance, not a bounding box. Drawing x and y
            // independently puts the corner of the square at 55 tiles, and a
            // guarantee that is really 55 tiles on the diagonal is not the
            // guarantee the prospector test pins.
            if (x * x + y * y > StarterPatchRange * StarterPatchRange)
                continue;

            var wobble = (salt >> 24) / 255.0 * 2.0 - 1.0;
            var amount = (int)(spec.BaseAmount * (1.0 + AmountJitter * wobble));
            var radius = Math.Max(2, spec.PatchRadius + (int)Math.Round(wobble));

            patches.Add(new OrePatch(spec.Item, x, y, radius, Math.Max(1, amount), spec.IsFluid));
            return;
        }
    }

    /// The patch covering a tile, if any. Neighbouring regions are checked too,
    /// since a patch near an edge overlaps into the next one.
    public bool TryPatchAt(int x, int y, out OrePatch patch)
    {
        var rx = RegionOf(x);
        var ry = RegionOf(y);

        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                foreach (var candidate in PatchesInRegion(rx + dx, ry + dy))
                    if (candidate.Contains(x, y))
                    {
                        patch = candidate;
                        return true;
                    }

        patch = default;
        return false;
    }

    /// Every patch whose centre falls within `radius` tiles.
    public List<OrePatch> PatchesNear(int x, int y, int radius)
    {
        var found = new List<OrePatch>();
        var minRx = RegionOf(x - radius);
        var maxRx = RegionOf(x + radius);
        var minRy = RegionOf(y - radius);
        var maxRy = RegionOf(y + radius);

        for (var ry = minRy; ry <= maxRy; ry++)
            for (var rx = minRx; rx <= maxRx; rx++)
                foreach (var patch in PatchesInRegion(rx, ry))
                {
                    var dx = patch.X - x;
                    var dy = patch.Y - y;
                    if (dx * dx + dy * dy <= radius * radius)
                        found.Add(patch);
                }

        return found;
    }
}
