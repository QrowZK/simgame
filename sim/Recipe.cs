namespace Sim;

public readonly struct RecipeInput
{
    public readonly ItemId Item;
    public readonly int Count;

    public RecipeInput(ItemId item, int count)
    {
        Item = item;
        Count = count;
    }
}

public readonly struct RecipeOutput
{
    public readonly ItemId Item;
    public readonly int Count;

    public RecipeOutput(ItemId item, int count)
    {
        Item = item;
        Count = count;
    }
}

public sealed class Recipe
{
    public readonly string Id;
    public readonly int DurationTicks;
    public readonly IReadOnlyList<RecipeInput> Inputs;
    public readonly IReadOnlyList<RecipeOutput> Outputs;

    public Recipe(string id, int durationTicks, IReadOnlyList<RecipeInput> inputs, IReadOnlyList<RecipeOutput> outputs)
    {
        if (durationTicks <= 0) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        Id = id;
        DurationTicks = durationTicks;
        Inputs = inputs;
        Outputs = outputs;
    }
}
