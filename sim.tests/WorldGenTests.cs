using Sim;

namespace Sim.Tests;

public class WorldGenTests
{
    private static List<OreSpec> Ores(int count)
    {
        var ores = new List<OreSpec>();
        for (var i = 0; i < count; i++)
            ores.Add(new OreSpec(new ItemId(i), minRing: i / 6, patchRadius: 6, richness: 1000));
        return ores;
    }

    [Fact]
    public void SameSeed_GeneratesAnIdenticalWorld()
    {
        var a = new WorldGen(4242, Ores(20));
        var b = new WorldGen(4242, Ores(20));

        for (var y = -600; y < 600; y += 37)
            for (var x = -600; x < 600; x += 41)
            {
                Assert.Equal(a.HeightAt(x, y), b.HeightAt(x, y));
                Assert.Equal(a.TerrainAt(x, y), b.TerrainAt(x, y));
            }
    }

    [Fact]
    public void DifferentSeeds_GenerateDifferentWorlds()
    {
        var a = new WorldGen(1, Ores(20));
        var b = new WorldGen(2, Ores(20));

        var differences = 0;
        for (var y = -400; y < 400; y += 29)
            for (var x = -400; x < 400; x += 31)
                if (a.HeightAt(x, y) != b.HeightAt(x, y))
                    differences++;

        Assert.True(differences > 100, $"seeds should diverge, only {differences} tiles differed");
    }

    [Fact]
    public void RegionsGenerateIndependentlyOfVisitOrder()
    {
        // Chunks must be generatable in any order and come out the same, or the
        // world would depend on where the player happened to walk first.
        var forward = new WorldGen(77, Ores(18));
        var backward = new WorldGen(77, Ores(18));

        var a = new List<string>();
        for (var ry = -2; ry <= 2; ry++)
            for (var rx = -2; rx <= 2; rx++)
                foreach (var p in forward.PatchesInRegion(rx, ry))
                    a.Add($"{rx},{ry},{p.Item.Value},{p.X},{p.Y},{p.Radius}");

        var b = new List<string>();
        for (var ry = 2; ry >= -2; ry--)
            for (var rx = 2; rx >= -2; rx--)
                foreach (var p in backward.PatchesInRegion(rx, ry))
                    b.Add($"{rx},{ry},{p.Item.Value},{p.X},{p.Y},{p.Radius}");

        b.Reverse();
        // Same set regardless of the order regions were asked for.
        Assert.Equal(a.OrderBy(s => s).ToList(), b.OrderBy(s => s).ToList());
    }

    [Fact]
    public void SpawnIsHabitable_NotOceanAndNotMountain()
    {
        // A new game must not begin underwater or on a cliff.
        for (var seed = 1; seed <= 25; seed++)
        {
            var world = new WorldGen(seed, Ores(20));
            var buildable = 0;

            for (var y = -24; y <= 24; y++)
                for (var x = -24; x <= 24; x++)
                {
                    var terrain = world.TerrainAt(x, y);
                    if (terrain is TerrainType.Sand or TerrainType.Grass or TerrainType.Rock)
                        buildable++;
                }

            Assert.True(buildable > 1600,
                $"seed {seed}: only {buildable} of 2401 spawn tiles are buildable");
        }
    }

    [Fact]
    public void EveryStarterResource_AppearsWithinTheFirstRegions()
    {
        // Ring 0 resources are what the first hour depends on, so they cannot be
        // left to chance.
        var ores = Ores(24);
        var starters = ores.Where(o => o.MinRing == 0).Select(o => o.Item.Value).ToHashSet();

        for (var seed = 1; seed <= 20; seed++)
        {
            var world = new WorldGen(seed, ores);
            var found = new HashSet<int>();

            for (var ry = -1; ry <= 1; ry++)
                for (var rx = -1; rx <= 1; rx++)
                    foreach (var patch in world.PatchesInRegion(rx, ry))
                        found.Add(patch.Item.Value);

            var missing = starters.Except(found).ToList();
            Assert.True(missing.Count == 0,
                $"seed {seed}: starter resources missing near spawn: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void EveryResource_AppearsSomewhereWithinAKnownDistance()
    {
        // The guarantee that dealing buys over noise thresholding: nothing can
        // simply fail to exist.
        var ores = Ores(26);

        for (var seed = 1; seed <= 10; seed++)
        {
            var world = new WorldGen(seed, ores);
            var found = new HashSet<int>();

            for (var ry = -6; ry <= 6; ry++)
                for (var rx = -6; rx <= 6; rx++)
                    foreach (var patch in world.PatchesInRegion(rx, ry))
                        found.Add(patch.Item.Value);

            var missing = ores.Select(o => o.Item.Value).Except(found).ToList();
            Assert.True(missing.Count == 0,
                $"seed {seed}: never generated: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void RareResources_DoNotAppearNextToSpawn()
    {
        var ores = Ores(30);
        var rare = ores.Where(o => o.MinRing >= 3).Select(o => o.Item.Value).ToHashSet();

        var world = new WorldGen(9, ores);
        foreach (var patch in world.PatchesInRegion(0, 0))
            Assert.DoesNotContain(patch.Item.Value, rare);
    }

    [Fact]
    public void OreNeverGeneratesInOpenWater()
    {
        var world = new WorldGen(31, Ores(20));

        for (var ry = -3; ry <= 3; ry++)
            for (var rx = -3; rx <= 3; rx++)
                foreach (var patch in world.PatchesInRegion(rx, ry))
                    Assert.False(world.IsWater(patch.X, patch.Y),
                        $"patch at {patch.X},{patch.Y} is in water");
    }
}

public class ProspectorTests
{
    private static List<OreSpec> Ores(int count)
    {
        var ores = new List<OreSpec>();
        for (var i = 0; i < count; i++)
            ores.Add(new OreSpec(new ItemId(i), minRing: i / 6, patchRadius: 6, richness: 500 + i));
        return ores;
    }

    [Fact]
    public void Scan_FindsNearbyDepositsNearestFirst()
    {
        var world = new WorldGen(5, Ores(20));
        var hits = new Prospector(radius: 200).Scan(world, 0, 0);

        Assert.NotEmpty(hits);
        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Distance <= hits[i].Distance, "hits should be nearest first");

        // One entry per resource, not one per patch.
        Assert.Equal(hits.Select(h => h.Item.Value).Distinct().Count(), hits.Count);
    }

    [Fact]
    public void Scan_ReportsNothingBeyondItsRadius()
    {
        var world = new WorldGen(5, Ores(20));
        var near = new Prospector(radius: 48).Scan(world, 0, 0);

        foreach (var hit in near)
            Assert.True(hit.Distance <= 48, $"reported a hit {hit.Distance} tiles away");
    }

    [Fact]
    public void SampleAt_IdentifiesWhatIsUnderfoot()
    {
        var world = new WorldGen(11, Ores(20));
        var prospector = new Prospector();
        var patch = world.PatchesInRegion(0, 0).First();

        Assert.True(prospector.TrySampleAt(world, patch.X, patch.Y, out var hit));
        Assert.Equal(patch.Item, hit.Item);
        Assert.Equal(0, hit.Distance);
    }

    [Fact]
    public void Bearing_PointsAtAResourceFarOutsideDetectionRange()
    {
        // The device that turns "explore until lucky" into a decision.
        var ores = Ores(30);
        var world = new WorldGen(13, ores);
        var prospector = new Prospector(radius: 64);

        var distant = ores.Last().Item;
        Assert.Empty(prospector.Scan(world, 0, 0).Where(h => h.Item.Equals(distant)));

        Assert.True(prospector.TryBearing(world, 0, 0, distant, searchRadius: 4000, out var hit),
            "a bearing should be available even when the deposit is out of scan range");
        Assert.True(hit.Distance > 64, "the deposit should genuinely be far away");

        var heading = Prospector.HeadingTo(0, 0, hit);
        Assert.InRange(heading, 0, 359);
    }

    [Fact]
    public void Heading_IsMeasuredClockwiseFromNorth()
    {
        var north = new ProspectHit(new ItemId(0), 0, -100, 100, 1, 4);
        var east = new ProspectHit(new ItemId(0), 100, 0, 100, 1, 4);
        var south = new ProspectHit(new ItemId(0), 0, 100, 100, 1, 4);
        var west = new ProspectHit(new ItemId(0), -100, 0, 100, 1, 4);

        Assert.Equal(0, Prospector.HeadingTo(0, 0, north));
        Assert.Equal(90, Prospector.HeadingTo(0, 0, east));
        Assert.Equal(180, Prospector.HeadingTo(0, 0, south));
        Assert.Equal(270, Prospector.HeadingTo(0, 0, west));
    }
}
