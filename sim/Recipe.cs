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

    /// Energy per tick while running, straight from the data files. Zero means
    /// the recipe needs no power at all -- which is not a special case bolted
    /// on for convenience but what the Manual tier genuinely is: hand tools.
    /// It is also what keeps a new game playable before the first generator.
    public readonly int PowerDraw;

    public Recipe(string id, int durationTicks, IReadOnlyList<RecipeInput> inputs,
                  IReadOnlyList<RecipeOutput> outputs, int powerDraw = 0)
    {
        if (durationTicks <= 0) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (powerDraw < 0) throw new ArgumentOutOfRangeException(nameof(powerDraw));
        Id = id;
        DurationTicks = durationTicks;
        Inputs = inputs;
        Outputs = outputs;
        PowerDraw = powerDraw;
    }
}
