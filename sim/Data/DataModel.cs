using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Data;

public sealed class TierDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("power")] public int Power { get; set; }
    [JsonPropertyName("metal")] public string? Metal { get; set; }
    [JsonPropertyName("electronics")] public bool Electronics { get; set; }
}

public sealed class ItemDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("form")] public string Form { get; set; } = "solid";
    [JsonPropertyName("raw")] public bool Raw { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
}

public sealed class MachineDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// Which function attachment mesh represents this machine.
    [JsonPropertyName("category")] public int Category { get; set; }

    [JsonPropertyName("tiers")] public List<string> Tiers { get; set; } = new();
    [JsonPropertyName("min_tier")] public string MinTier { get; set; } = "";
    [JsonPropertyName("size")] public int Size { get; set; } = 1;
    [JsonPropertyName("parallelism")] public int Parallelism { get; set; } = 1;
}

public sealed class TechDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("line")] public string Line { get; set; } = "";
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new();
    [JsonPropertyName("requires_item")] public string? RequiresItem { get; set; }

    /// Every item that counts toward this tech. One hull, for the tier techs;
    /// for the opening rungs, every ore a Manual furnace will smelt -- because
    /// which ore is near spawn is a property of the seed, and a rung naming one
    /// ore would be uncompletable on the seeds that buried it (docs/0030).
    ///
    /// `RequiresItem` is the *key* the delivery is filed under and displayed
    /// as, and for a group it is a label ("any metal ingot") rather than an
    /// item id. Read this list, not that string, when you mean items.
    [JsonPropertyName("requires_items")] public List<string> RequiresItems { get; set; } = new();

    [JsonPropertyName("requires_count")] public int RequiresCount { get; set; }

    /// What completing this hands the player. Data rather than code so the UI
    /// can name it: "this gives you two Steam Inserters" is a reason to do
    /// something, and "this unlocks 26 recipes" is not.
    [JsonPropertyName("rewards")] public List<RewardDef> Rewards { get; set; } = new();
}

public sealed class RewardDef
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class RecipeItemRef
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class RecipeDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("duration_ticks")] public int DurationTicks { get; set; }
    [JsonPropertyName("power_draw")] public int PowerDraw { get; set; }
    [JsonPropertyName("inputs")] public List<RecipeItemRef> Inputs { get; set; } = new();
    [JsonPropertyName("outputs")] public List<RecipeItemRef> Outputs { get; set; } = new();
    [JsonPropertyName("unlocked_by")] public string UnlockedBy { get; set; } = "";
}

public sealed class GameData
{
    public required List<TierDef> Tiers { get; init; }
    public required List<ItemDef> Items { get; init; }
    public required List<MachineDef> Machines { get; init; }
    public required List<TechDef> Techs { get; init; }
    public required List<RecipeDef> Recipes { get; init; }

    /// The running game reads the copy compiled into this assembly, never the
    /// filesystem. A shipped build has no data directory beside it, and the
    /// version that went looking for one threw on New Game for every player
    /// outside a checkout (ADR 0027).
    private static readonly Lazy<GameData> Cached = new(LoadEmbedded);
    public static GameData Instance => Cached.Value;

    /// The embedded copy: the same five files, compiled in by Sim.csproj.
    public static GameData LoadEmbedded()
    {
        var options = new JsonSerializerOptions();
        return new GameData
        {
            Tiers = ReadEmbedded<List<TierDef>>("tiers.json", options),
            Items = ReadEmbedded<List<ItemDef>>("items.json", options),
            Machines = ReadEmbedded<List<MachineDef>>("machines.json", options),
            Techs = ReadEmbedded<List<TechDef>>("techs.json", options),
            Recipes = ReadEmbedded<List<RecipeDef>>("recipes.json", options),
        };
    }

    /// One embedded file, as text. Public so a test can compare it against the
    /// file on disk -- the two drifting apart would ship a game whose recipes
    /// are not the ones in the repository.
    public static string EmbeddedText(string fileName)
    {
        var name = $"Sim.data.{fileName}";
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException(
                $"{name} is not embedded in Sim.dll. Check the EmbeddedResource item in Sim.csproj: " +
                "without it a shipped build has no recipes at all.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static T ReadEmbedded<T>(string fileName, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(EmbeddedText(fileName), options)
        ?? throw new InvalidDataException($"Failed to parse embedded {fileName}");

    /// Reads the files on disk. For the generator and for tests that check the
    /// embedded copy is current -- not for the running game.
    public static GameData Load(string dataDirectory)
    {
        var options = new JsonSerializerOptions();
        return new GameData
        {
            Tiers = ReadJson<List<TierDef>>(dataDirectory, "tiers.json", options),
            Items = ReadJson<List<ItemDef>>(dataDirectory, "items.json", options),
            Machines = ReadJson<List<MachineDef>>(dataDirectory, "machines.json", options),
            Techs = ReadJson<List<TechDef>>(dataDirectory, "techs.json", options),
            Recipes = ReadJson<List<RecipeDef>>(dataDirectory, "recipes.json", options),
        };
    }

    private static T ReadJson<T>(string dataDirectory, string fileName, JsonSerializerOptions options)
    {
        var path = Path.Combine(dataDirectory, fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, options)
               ?? throw new InvalidDataException($"Failed to parse {path}");
    }
}
