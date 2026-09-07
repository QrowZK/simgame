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

    /// Ore items in the order they appear in the data, with the tier that gates
    /// them. Worldgen deals patches from this, so a new raw material added to
    /// the data appears in the world without any further wiring.
    public IReadOnlyList<ItemDef> RawSolids { get; }

    public Catalogue(GameData data)
    {
        Data = data;

        foreach (var item in data.Items)
            Items.Register(item.Id);

        foreach (var def in data.Recipes)
        {
            // A recipe whose items are not all registered would be a data bug,
            // not a runtime condition -- Register above covers every item, so
            // an unknown id here means the files disagree with each other.
            var inputs = def.Inputs.Select(i => new RecipeInput(Id(i.Item, def.Id), i.Count)).ToArray();
            var outputs = def.Outputs.Select(o => new RecipeOutput(Id(o.Item, def.Id), o.Count)).ToArray();
            _recipes[def.Id] = new Recipe(def.Id, Math.Max(1, def.DurationTicks), inputs, outputs);
        }

        RawSolids = data.Items
            .Where(i => i.Raw && i.Form == "solid" && i.Category != "machine")
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

    private static Catalogue? _instance;

    /// The catalogue built from the repository's own data files. Cached, because
    /// building it parses five JSON files and registers 600 items.
    public static Catalogue Instance => _instance ??= new Catalogue(GameData.Instance);
}
