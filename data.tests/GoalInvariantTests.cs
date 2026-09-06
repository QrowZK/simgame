namespace Data.Tests;

/// The goal item is what ends the game, and the design requires that reaching it
/// exercises the whole petrochemical network rather than one convenient branch.
/// That is a graph property, so it is asserted rather than trusted.
public class GoalInvariantTests
{
    private static readonly GameData Data = GameData.Instance;
    private const string GoalId = "von_neumann_seed";

    /// Everything the goal transitively consumes.
    private static HashSet<string> GoalClosure()
    {
        var producers = new Dictionary<string, List<RecipeDef>>();
        foreach (var recipe in Data.Recipes)
            foreach (var output in recipe.Outputs)
                (producers.TryGetValue(output.Item, out var list)
                    ? list
                    : producers[output.Item] = new List<RecipeDef>()).Add(recipe);

        var closure = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(GoalId);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (!closure.Add(item)) continue;
            if (!producers.TryGetValue(item, out var recipes)) continue;

            foreach (var recipe in recipes)
                foreach (var input in recipe.Inputs)
                    if (!closure.Contains(input.Item))
                        queue.Enqueue(input.Item);
        }

        return closure;
    }

    [Fact]
    public void GoalItem_Exists()
    {
        Assert.Contains(Data.Items, i => i.Id == GoalId);
        Assert.Contains(Data.Recipes, r => r.Outputs.Any(o => o.Item == GoalId));
    }

    [Fact]
    public void GoalItem_RequiresEveryPolymer()
    {
        var closure = GoalClosure();

        // Grouped by material, not by form: the goal has to require the polymer,
        // not every shape it can be pressed into.
        var families = Data.Items
            .Where(i => i.Tags.Contains("polymer"))
            .GroupBy(i => i.Id[..i.Id.LastIndexOf('_')]);

        var skippable = families
            .Where(g => !g.Any(i => closure.Contains(i.Id)))
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            skippable.Count == 0,
            $"{skippable.Count} polymer families are skippable:\n" + string.Join("\n", skippable));
    }

    [Fact]
    public void GoalItem_RequiresTheWholePetrochemicalNetwork()
    {
        var closure = GoalClosure();
        var missing = Data.Items
            .Where(i => i.Tags.Contains("petrochem"))
            .Where(i => !closure.Contains(i.Id))
            .Select(i => $"{i.Id} ({i.Tier})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} petrochemical streams are not required to finish the game:\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void EveryPetrochemicalStream_IsConsumedBySomething()
    {
        var consumed = Data.Recipes.SelectMany(r => r.Inputs).Select(i => i.Item).ToHashSet();
        var deadEnds = Data.Items
            .Where(i => i.Tags.Contains("petrochem") && !consumed.Contains(i.Id))
            .Select(i => i.Id)
            .ToList();

        Assert.True(deadEnds.Count == 0,
            "Refinery streams nothing consumes:\n" + string.Join("\n", deadEnds));
    }
}
