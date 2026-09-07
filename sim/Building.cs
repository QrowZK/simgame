using Sim.Data;

namespace Sim;

/// What placing a buildable actually creates.
///
/// A machine item is not enough on its own: a furnace and a power pole are both
/// `<tier>_<machine>` items, but one runs a recipe and the other joins a grid.
/// The kind is what the placement code switches on.
public enum BuildKind
{
    /// Runs a recipe. Needs one chosen before it can be placed.
    Machine,

    /// Digs whatever ore is under it, so it can only go on a patch.
    Miner,

    Generator,
    Accumulator,
    Pole,
    Pipe,
    Tank,
    Pump,

    /// In the data and craftable, but nothing places it yet -- belts and
    /// inserters need routing, which is its own decision. Named rather than
    /// omitted so the UI can say "not yet" instead of silently not listing it.
    NotPlaceable,
}

/// One thing the player can build, resolved from the data files.
public sealed class Buildable
{
    public readonly ItemId Item;
    public readonly string ItemId;
    public readonly string MachineId;

    /// The machine's own name ("Furnace"), shared by every tier of it.
    public readonly string Name;

    /// What the player sees ("Steam Furnace"). The tier is half the identity:
    /// a list showing only Name puts three identical "Alloy Smelter" rows in
    /// front of someone carrying a Steam, an Arc and a Fusion one.
    public readonly string DisplayName;
    public readonly BuildKind Kind;

    /// Index into the tier ladder, which is also the hull mesh.
    public readonly byte Tier;

    /// The tier's data id ("ARC"), which is what recipes are keyed by.
    public readonly string TierId;

    /// Index of the function attachment mesh. Authored in the data.
    public readonly byte Category;

    public readonly byte Size;

    /// What this tier's grid runs at. Generator output, accumulator capacity
    /// and machine draw all scale off it, so one number in tiers.json moves
    /// the whole tier's power economy.
    public readonly int TierPower;

    public Buildable(ItemId item, string itemId, string machineId, string name,
                     string displayName, BuildKind kind, byte tier, string tierId,
                     byte category, byte size, int tierPower)
    {
        TierId = tierId;
        DisplayName = displayName;
        Item = item;
        ItemId = itemId;
        MachineId = machineId;
        Name = name;
        Kind = kind;
        Tier = tier;
        Category = category;
        Size = size;
        TierPower = tierPower;
    }

    public MachinePlacement PlacementAt(int x, int y) => new(x, y, Tier, Category, Size);

    /// A generator burns its fuel into this much power a tick.
    public int GeneratorOutput => Math.Max(1, TierPower);

    /// Ticks one unit of fuel lasts. Flat across tiers: a higher tier burns the
    /// same coal harder rather than longer, so fuel logistics stay a real
    /// problem instead of being solved by upgrading.
    public const int GeneratorTicksPerFuel = 400;

    /// Enough to run one machine of its own tier for about half a minute.
    public int AccumulatorCapacity => Math.Max(1, TierPower) * 1800;

    /// Charge and discharge cap. Four machines' worth, so a bank covers a spike
    /// rather than the whole factory.
    public int AccumulatorRate => Math.Max(1, TierPower) * 4;

    /// Poles reach further up the ladder, which is the upgrade a player feels:
    /// fewer poles for the same floor.
    public int PoleSupplyRadius => 4 + Tier;
    public int PoleWireRadius => 7 + Tier * 2;
}

/// Every buildable item in the data, keyed by the item the player carries.
///
/// Built once from the catalogue rather than parsed at each placement. The
/// mapping is item id -> machine, and it is done by stripping the tier prefix
/// rather than by matching the machine id as a suffix: `arc_arc_furnace` ends
/// with `furnace` as well as with `arc_furnace`, and suffix matching resolved
/// it to the wrong machine.
public sealed class BuildCatalogue
{
    private readonly Dictionary<ItemId, Buildable> _byItem = new();
    private readonly List<Buildable> _all = new();
    private readonly Dictionary<string, List<(int Tier, Recipe Recipe)>> _recipesByMachine = new();

    public IReadOnlyList<Buildable> All => _all;

    public BuildCatalogue(Catalogue catalogue)
    {
        var tierIndex = catalogue.Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var tierPower = catalogue.Data.Tiers.ToDictionary(t => t.Id, t => t.Power);
        var itemNames = catalogue.Data.Items.ToDictionary(i => i.Id, i => i.Name);

        foreach (var def in catalogue.Data.Recipes)
        {
            if (!_recipesByMachine.TryGetValue(def.Machine, out var list))
                _recipesByMachine[def.Machine] = list = new List<(int, Recipe)>();
            list.Add((tierIndex.GetValueOrDefault(def.Tier), catalogue.Recipe(def.Id)));
        }

        foreach (var machine in catalogue.Data.Machines)
        {
            var kind = KindOf(machine.Id);

            foreach (var tier in machine.Tiers)
            {
                var itemId = $"{tier.ToLowerInvariant()}_{machine.Id}";
                if (!catalogue.Items.TryGetId(itemId, out var item))
                    continue;

                var buildable = new Buildable(
                    item, itemId, machine.Id, machine.Name,
                    itemNames.GetValueOrDefault(itemId, machine.Name), kind,
                    (byte)Math.Clamp(tierIndex.GetValueOrDefault(tier), 0, 255), tier,
                    (byte)Math.Clamp(machine.Category, 0, 255),
                    (byte)Math.Max(1, machine.Size),
                    tierPower.GetValueOrDefault(tier));

                _byItem[item] = buildable;
                _all.Add(buildable);
            }
        }
    }

    /// Machine id -> what placing it makes. Everything not named here runs a
    /// recipe, which is the overwhelming majority.
    private static BuildKind KindOf(string machineId) => machineId switch
    {
        "generator" => BuildKind.Generator,
        "accumulator" => BuildKind.Accumulator,
        "pole" => BuildKind.Pole,
        "miner" or "oil_derrick" => BuildKind.Miner,
        "pipe" => BuildKind.Pipe,
        "storage_tank" => BuildKind.Tank,
        "inline_pump" or "pump_station" => BuildKind.Pump,

        // Craftable, but placing one is a routing decision nothing implements.
        "transport_belt" or "underground_belt" or "splitter"
            or "inserter" or "stack_inserter" => BuildKind.NotPlaceable,

        // Carried, not built. The manual crafting bench is NOT in this list:
        // it is the one machine the starter kit hands over, and placing it is
        // how the player crafts their first furnace. Marking it unplaceable
        // made the opening loop impossible -- you would carry a bench you
        // could never put down.
        "prospector" => BuildKind.NotPlaceable,

        _ => BuildKind.Machine,
    };

    /// The recipes this buildable can run: its machine, at its tier or any
    /// tier below it.
    ///
    /// The "or below" is the ladder's whole promise -- an Arc crusher does
    /// everything a Steam crusher did, faster. Restricting a machine to
    /// recipes authored at exactly its own tier left 63 of them, most of the
    /// upper half of the tree, able to be built and unable to run anything.
    ///
    /// The UI needs this to offer a choice and the build path needs it to
    /// refuse a furnace told to run an assembler's recipe, so both ask here.
    public IReadOnlyList<Recipe> RecipesFor(Buildable buildable)
        => _recipesByMachine.TryGetValue(buildable.MachineId, out var found)
            ? found.Where(r => r.Tier <= buildable.Tier).Select(r => r.Recipe).ToList()
            : Array.Empty<Recipe>();

    public bool CanRun(Buildable buildable, Recipe recipe)
    {
        foreach (var candidate in RecipesFor(buildable))
            if (ReferenceEquals(candidate, recipe)) return true;
        return false;
    }

    /// What a build UI should actually offer.
    ///
    /// Excludes the not-yet-placeable, and excludes a machine with no recipe it
    /// could run. The data has several machines with no recipes at all (the
    /// sifter, mixer, cutter and vacuum freezer are in the item list and the
    /// tech tree, and nothing in recipes.json uses them) and the spinneret is
    /// offered two tiers below its first recipe. Building one would produce a
    /// machine that can never do anything, so the honest thing is not to offer
    /// it. `All` still lists them, so this stays a display decision rather than
    /// a quiet deletion.
    public IEnumerable<Buildable> Offerable
        => _all.Where(b => b.Kind != BuildKind.NotPlaceable)
               .Where(b => b.Kind != BuildKind.Machine || RecipesFor(b).Count > 0);

    public bool TryGet(ItemId item, out Buildable buildable)
        => _byItem.TryGetValue(item, out buildable!);

    public Buildable? Find(string itemId)
        => _all.FirstOrDefault(b => b.ItemId == itemId);
}

/// Why a build did or did not happen.
///
/// An enum rather than a bool because every one of these needs a different
/// sentence in front of the player: "you have none" and "something is already
/// there" are not the same problem, and a build button that just goes dead is
/// the most confusing thing a build UI can do.
public enum BuildResult
{
    Ok,

    /// Not a buildable item at all.
    NotBuildable,

    /// A real buildable, but nothing places this kind yet.
    NotPlaceableYet,

    /// None in the player's inventory.
    NoneCarried,

    /// The footprint overlaps something already built.
    Blocked,

    /// A machine was asked for without a recipe, or with one it cannot run.
    NeedsRecipe,

    /// A miner was placed somewhere with no ore under it.
    NoResource,

    /// A pump or fluid extractor was placed away from the water it needs.
    NoFluid,
}
