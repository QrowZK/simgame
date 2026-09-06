using System.Text.Json;
using System.Text.Json.Serialization;

namespace Data.Tests;

public sealed class ItemDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("raw")] public bool Raw { get; set; }
}

public sealed class MachineDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
}

public sealed class TechDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new();
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
    public required List<ItemDef> Items { get; init; }
    public required List<MachineDef> Machines { get; init; }
    public required List<TechDef> Techs { get; init; }
    public required List<RecipeDef> Recipes { get; init; }

    public static GameData Load(string dataDirectory)
    {
        var options = new JsonSerializerOptions();
        return new GameData
        {
            Items = ReadJson<List<ItemDef>>(dataDirectory, "items.json", options),
            Machines = ReadJson<List<MachineDef>>(dataDirectory, "machines.json", options),
            Techs = ReadJson<List<TechDef>>(dataDirectory, "tiers.json", options),
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
