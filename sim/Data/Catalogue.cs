namespace Sim.Data;

/// The bridge from `/data` JSON to the sim's own types.
///
/// `GameData` is the file format; this is the runnable graph. It registers every
/// item in a single `ItemDatabase` and builds a `Recipe` for every recipe
/// definition, so the sim never parses JSON and the data files never know about
/// `ItemId`s.
///
/// Registration order is the file order, which makes ids stable for a given set
/// of data files -- but saves still store item names rather than ids, because
/// that order changes the moment the data changes.
public sealed class Catalogue
{
    public ItemDatabase Items { get; } = new();
    public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
    public GameData Data { get; }

    private readonly Dictionary<string, Recipe> _recipes = new();

    /// Everything worldgen has to bury somewhere: raw ores, and raw fluids that
    /// come out of the ground rather than out of the air. Adding a raw material
    /// to the data puts it in the world with no further wiring.
    public IReadOnlyList<ItemDef> RawSolids { get; }

    /// Raw fluids with no patch behind them -- water and air. A pump standing in
    /// a lake is not consuming a deposit, so these are excluded from worldgen
    /// and drawn from the terrain instead.
    public static readonly HashSet<string> AmbientFluids = new() { "water", "air" };

    public Catalogue(GameData data)
    {
        Data = data;

        foreach (var item in data.Items)
            Items.Register(item.Id);

        var fluids = data.Items.Where(i => i.Form == "fluid").Select(i => i.Id).ToHashSet();

        foreach (var def in data.Recipes)
        {
            // A recipe whose items are not all registered would be a data bug,
            // not a runtime condition -- Register above covers every item, so
            // an unknown id here means the files disagree with each other.
            var inputs = def.Inputs
                .Select(i => new RecipeInput(Id(i.Item, def.Id), i.Count, fluids.Contains(i.Item)))
                .ToArray();
            var outputs = def.Outputs
                .Select(o => new RecipeOutput(Id(o.Item, def.Id), o.Count, fluids.Contains(o.Item)))
                .ToArray();
            _recipes[def.Id] = new Recipe(def.Id, Math.Max(1, def.DurationTicks), inputs, outputs,
                                          def.PowerDraw);
        }

        RawSolids = data.Items
            .Where(i => i.Raw && i.Category != "machine" && !AmbientFluids.Contains(i.Id))
            .ToList();
    }

    private ItemId Id(string name, string recipeId) =>
        Items.TryGetId(name, out var id)
            ? id
            : throw new InvalidDataException(
                $"recipe '{recipeId}' uses item '{name}', which is not in items.json");

    public ItemId Item(string id) => Items.GetId(id);

    public Recipe Recipe(string id) =>
        _recipes.TryGetValue(id, out var recipe)
            ? recipe
            : throw new KeyNotFoundException($"no recipe '{id}'");

    /// The catalogue built from the repository's own data files. Cached, because
    /// building it parses five JSON files and registers 600 items.
    ///
    /// `Lazy` rather than `_instance ??= new(...)`, and that is not a style
    /// choice: `??=` on a static is a read, a construct and a write with no
    /// barrier, so two threads racing the first use each build a catalogue and
    /// the loser's is handed to whoever asked first. Two catalogues means two
    /// sets of `Recipe` objects, and `BuildCatalogue.CanRun` compares recipes by
    /// `ReferenceEquals` -- so a caller holding `Catalogue.Instance` and a
    /// `BuildCatalogue` built from `Catalogue.Instance` could find that its own
    /// machine could not run its own recipe. That reached CI as an opening-route
    /// test failing on one run and passing on the next from the same commit.
    ///
    /// `Lazy`'s default mode is `ExecutionAndPublication`: constructed once,
    /// published once, every caller sees the same object.
    private static readonly Lazy<Catalogue> Cached = new(() => new Catalogue(GameData.Instance));

    public static Catalogue Instance => Cached.Value;
}
