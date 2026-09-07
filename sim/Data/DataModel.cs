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

    private static readonly Lazy<GameData> Cached = new(() => Load(DataPaths.DataDirectory));
    public static GameData Instance => Cached.Value;

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
