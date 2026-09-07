namespace Sim;

public readonly struct RecipeInput
{
    public readonly ItemId Item;
    public readonly int Count;

    /// Whether this arrives by pipe rather than by belt. The data files already
    /// mark which items are fluids; carrying that through to the recipe is what
    /// lets a machine be plumbed and belted at the same time without two
    /// separate recipe systems.
    public readonly bool IsFluid;

    public RecipeInput(ItemId item, int count, bool isFluid = false)
    {
        Item = item;
        Count = count;
        IsFluid = isFluid;
    }
}

public readonly struct RecipeOutput
{
    public readonly ItemId Item;
    public readonly int Count;
    public readonly bool IsFluid;

    public RecipeOutput(ItemId item, int count, bool isFluid = false)
    {
        Item = item;
        Count = count;
        IsFluid = isFluid;
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

    /// True when any input or output travels by pipe. Lets the world skip the
    /// plumbing pass entirely for the many recipes that are solids only.
    public readonly bool UsesFluids;

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

        foreach (var input in inputs) UsesFluids |= input.IsFluid;
        foreach (var output in outputs) UsesFluids |= output.IsFluid;
    }
}
