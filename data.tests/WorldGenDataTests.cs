using Sim;

namespace Data.Tests;

/// World generation has to place the resources the recipe graph actually needs.
/// The synthetic tests in sim.tests prove the generator's guarantees hold; these
/// prove they hold for the real ore list.
public class WorldGenDataTests
{
    private static readonly GameData Data = GameData.Instance;

    /// Scarcity follows the tier ladder: a resource first needed at Plasma is
    /// further out than one needed at Steam.
    private static List<OreSpec> RealOres(out ItemDatabase db)
    {
        var database = new ItemDatabase();
        foreach (var item in Data.Items) database.Register(item.Id);
        db = database;

        var tierIndex = Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var specs = new List<OreSpec>();

        foreach (var item in Data.Items.Where(i => i.Raw && i.Category == "raw_ore"))
        {
            var ring = Math.Max(0, tierIndex[item.Tier] - 1);
            // Further out means a longer haul, so patches out there hold more --
            // the trip has to be worth making.
            specs.Add(new OreSpec(database.GetId(item.Id), ring,
                                  patchRadius: 6 + Math.Min(5, ring),
                                  baseAmount: 4000 + ring * 3000));
        }

        return specs;
    }

    [Fact]
    public void TheRealOreList_IsFullyPlacedWithinAKnowableArea()
    {
        var ores = RealOres(out var db);
        Assert.True(ores.Count >= 20, $"expected the full ore list, found {ores.Count}");

        for (var seed = 1; seed <= 8; seed++)
        {
            var world = new WorldGen(seed, ores);
            var found = new HashSet<int>();

            for (var ry = -7; ry <= 7; ry++)
                for (var rx = -7; rx <= 7; rx++)
                    foreach (var patch in world.PatchesInRegion(rx, ry))
                        found.Add(patch.Item.Value);

            var missing = ores.Where(o => !found.Contains(o.Item.Value))
                              .Select(o => db.GetName(o.Item))
                              .ToList();

            Assert.True(missing.Count == 0,
                $"seed {seed}: these resources never generate: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void TheFirstHoursResources_AreAllReachableFromSpawn()
    {
        // Copper, tin, iron, coal and stone are what the opening depends on.
        var ores = RealOres(out var db);
        var starters = new[]
        {
            "chalcopyrite", "cassiterite", "magnetite",
            "coal_deposit", "stone_deposit", "limestone_deposit", "quartz_deposit",
        };

        for (var seed = 1; seed <= 12; seed++)
        {
            var world = new WorldGen(seed, ores);
            var prospector = new Prospector(radius: 400);
            var reachable = prospector.Scan(world, 0, 0).Select(h => db.GetName(h.Item)).ToHashSet();

            var missing = starters.Where(s => !reachable.Contains(s)).ToList();
            Assert.True(missing.Count == 0,
                $"seed {seed}: not prospectable near spawn: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void EveryOreTheGoalNeeds_CanBeLocatedByTheProspector()
    {
        // Anything the win condition depends on must be findable, not merely
        // present somewhere in principle.
        var ores = RealOres(out var db);
        var world = new WorldGen(2024, ores);
        var prospector = new Prospector(radius: 96);

        var unfindable = new List<string>();
        foreach (var ore in ores)
            if (!prospector.TryBearing(world, 0, 0, ore.Item, searchRadius: 3000, out _))
                unfindable.Add(db.GetName(ore.Item));

        Assert.True(unfindable.Count == 0,
            "the prospector cannot locate: " + string.Join(", ", unfindable));
    }
}
