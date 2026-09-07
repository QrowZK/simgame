using Sim.Data;

namespace Sim;

/// Builds the world a new game starts in.
///
/// The starting position is deliberately almost nothing: a prospector, a few
/// hand tools' worth of materials, and whatever is under your feet. The first
/// hour is meant to be walking to a patch, digging by hand, hand-crafting a
/// furnace, and only then building the first miner -- so the factory is
/// something the player caused rather than something they inherited.
public static class NewGame
{
    /// Where a new game begins. Worldgen already pulls the first region up out
    /// of the sea and down off the mountains, so the origin is always land.
    public const int SpawnX = 0;
    public const int SpawnY = 0;

    /// Ore specs derived from the data files: every raw solid becomes something
    /// worldgen can deal. Adding a raw material to `/data` puts it in the world
    /// with no further wiring, which is the property that keeps the two from
    /// drifting apart.
    public static List<OreSpec> OreSpecs(Catalogue catalogue)
    {
        var tierIndex = catalogue.Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var starters = StarterOres(catalogue);
        var specs = new List<OreSpec>();

        foreach (var item in catalogue.RawSolids)
        {
            // Tier decides how far out a resource starts appearing, so the
            // early metals are underfoot and the exotic ones are a journey.
            var ring = tierIndex.TryGetValue(item.Tier, out var index) ? Math.Max(0, index - 1) : 0;

            specs.Add(new OreSpec(catalogue.Item(item.Id), ring,
                                  patchRadius: 6 + Math.Min(5, ring),
                                  // Further out means a longer haul, so patches
                                  // out there hold more -- the trip has to be
                                  // worth making.
                                  baseAmount: 4000 + ring * 3000,
                                  // Oil is buried like an ore so the derrick has
                                  // something to stand on. Hands cannot pick it
                                  // up, and the deposit is what says so.
                                  isFluid: item.Form == "fluid",
                                  // Worldgen guarantees one of these near spawn
                                  // (ADR 0026), so the first thing the survey
                                  // device points at is a thing you can use.
                                  isStarter: starters.Contains(item.Id)));
        }

        return specs;
    }


    /// The raw solids a player can actually do something with on the first day.
    ///
    /// Derived, never listed: an ore qualifies when some recipe unlocked at tick
    /// zero consumes it. That is the same question the player is asking -- "can
    /// I turn this into anything yet" -- and it is answered from the recipe and
    /// tech graph, so widening the manual furnace in `progression.json` widens
    /// this with no code change.
    ///
    /// What the starter kit already grants is excluded. Stone qualifies on the
    /// letter of the rule -- three manual recipes consume it -- but a guaranteed
    /// patch of the one resource the player lands holding 24 of would satisfy
    /// the guarantee while fixing nothing.
    public static HashSet<string> StarterOres(Catalogue catalogue)
    {
        var granted = StarterKit.Select(k => k.Item).ToHashSet();
        var raw = catalogue.RawSolids
                           .Where(i => i.Form != "fluid")
                           .Select(i => i.Id)
                           .ToHashSet();

        var research = new Research(catalogue);
        var starters = new HashSet<string>();

        foreach (var recipe in catalogue.Data.Recipes)
        {
            if (!research.IsUnlocked(recipe.Id)) continue;

            foreach (var input in recipe.Inputs)
                if (raw.Contains(input.Item) && !granted.Contains(input.Item))
                    starters.Add(input.Item);
        }

        return starters;
    }

    /// What the player is carrying at minute zero.
    ///
    /// Enough to hand-craft the first furnace and not one thing more. Handing
    /// over a miner would skip the part of the game this whole change exists to
    /// create; handing over nothing would mean an unopenable first recipe.
    public static readonly (string Item, int Count)[] StarterKit =
    {
        // The survey device, which is what turns "wander until you trip over
        // bauxite" into a decision about how far you are willing to walk.
        ("man_prospector", 1),

        // Your own hands. Granted rather than crafted, since there is nothing
        // to craft it with.
        ("man_manual_crafting", 1),

        // Enough piled stone for the first furnace and crucible, and not one
        // thing more. A miner in the starter kit would skip the part of the
        // game this exists to create; no stone at all would mean the first
        // recipe cannot be opened.
        ("stone_deposit", 24),

        // The lander's fabricator, which is the one thing that survived entry
        // (ADR 0023). Granted rather than crafted because it is the premise:
        // research is delivered into it, and a player who had to build one
        // before they could unlock anything would be locked out by the very
        // system that is supposed to open the game up. More can be built from
        // stone -- `build_man_uplink` -- so a badly-sited one is not fatal.
        ("man_uplink", 1),
    };

    public static World Create(int seed, Catalogue catalogue)
    {
        var gen = new WorldGen(seed, OreSpecs(catalogue));
        var world = new World(seed, catalogue.Items, gen);

        foreach (var (item, count) in StarterKit)
            if (catalogue.Items.TryGetId(item, out var id))
                world.PlayerInventory.Add(id, count);

        // A played world is gated; the four Manual techs are open from here
        // (ADR 0023), which is exactly the opening route and nothing more.
        world.Research = new Research(catalogue);

        return world;
    }
}
