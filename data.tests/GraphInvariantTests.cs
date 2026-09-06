namespace Data.Tests;

public class GraphInvariantTests
{
    private static readonly GameData Data = GameData.Load(DataPaths.DataDirectory);

    [Fact]
    public void EveryRecipeItem_ExistsInItemsJson()
    {
        var itemIds = Data.Items.Select(i => i.Id).ToHashSet();
        var missing = new List<string>();

        foreach (var recipe in Data.Recipes)
        {
            foreach (var input in recipe.Inputs)
                if (!itemIds.Contains(input.Item))
                    missing.Add($"{recipe.Id}: input '{input.Item}'");

            foreach (var output in recipe.Outputs)
                if (!itemIds.Contains(output.Item))
                    missing.Add($"{recipe.Id}: output '{output.Item}'");
        }

        Assert.True(missing.Count == 0, "Dangling item references:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryRecipeMachine_ExistsInMachinesJson()
    {
        var machineIds = Data.Machines.Select(m => m.Id).ToHashSet();
        var missing = Data.Recipes
            .Where(r => !machineIds.Contains(r.Machine))
            .Select(r => $"{r.Id}: machine '{r.Machine}'")
            .ToList();

        Assert.True(missing.Count == 0, "Dangling machine references:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryRecipeUnlock_ExistsInTiersJson()
    {
        var techIds = Data.Techs.Select(t => t.Id).ToHashSet();
        var missing = Data.Recipes
            .Where(r => !techIds.Contains(r.UnlockedBy))
            .Select(r => $"{r.Id}: unlocked_by '{r.UnlockedBy}'")
            .ToList();

        Assert.True(missing.Count == 0, "Dangling unlocked_by references:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryTechRequirement_ExistsInTiersJson()
    {
        var techIds = Data.Techs.Select(t => t.Id).ToHashSet();
        var missing = new List<string>();

        foreach (var tech in Data.Techs)
            foreach (var req in tech.Requires)
                if (!techIds.Contains(req))
                    missing.Add($"{tech.Id}: requires '{req}'");

        Assert.True(missing.Count == 0, "Dangling tech requirements:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TechGraph_HasNoCircularDependencies()
    {
        var byId = Data.Techs.ToDictionary(t => t.Id);
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        var cycle = new List<string>();

        bool Visit(string id, List<string> path)
        {
            if (visited.Contains(id)) return true;
            if (visiting.Contains(id))
            {
                cycle.AddRange(path.SkipWhile(p => p != id));
                cycle.Add(id);
                return false;
            }

            visiting.Add(id);
            path.Add(id);
            if (byId.TryGetValue(id, out var tech))
            {
                foreach (var req in tech.Requires)
                    if (!Visit(req, path))
                        return false;
            }
            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        foreach (var tech in Data.Techs)
        {
            if (!Visit(tech.Id, new List<string>()))
            {
                Assert.Fail("Circular tech dependency: " + string.Join(" -> ", cycle));
            }
        }
    }

    [Fact]
    public void EveryItem_IsReachableFromRawResourcesViaUnlockedRecipes()
    {
        // An item is reachable if it is raw, or produced by some recipe whose inputs
        // are all already reachable. Iterate to a fixed point (topological closure).
        var reachable = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var recipe in Data.Recipes)
            {
                if (recipe.Inputs.All(i => reachable.Contains(i.Item)))
                {
                    foreach (var output in recipe.Outputs)
                        if (reachable.Add(output.Item))
                            changed = true;
                }
            }
        } while (changed);

        var unreachable = Data.Items.Select(i => i.Id).Where(id => !reachable.Contains(id)).ToList();

        Assert.True(unreachable.Count == 0, "Unreachable items:\n" + string.Join("\n", unreachable));
    }

    [Fact]
    public void EveryTiersEntryComponentChain_TerminatesInRawResources()
    {
        // For each tier's recipes, walking the input chain backwards must bottom out
        // at raw items rather than looping or dead-ending on an unproduced intermediate.
        var rawIds = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        var recipesByOutput = Data.Recipes
            .SelectMany(r => r.Outputs.Select(o => (o.Item, Recipe: r)))
            .GroupBy(x => x.Item)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Recipe).ToList());

        var unresolved = new List<string>();

        bool CanTerminate(string itemId, HashSet<string> visiting)
        {
            if (rawIds.Contains(itemId)) return true;
            if (!visiting.Add(itemId)) return false; // cycle
            if (!recipesByOutput.TryGetValue(itemId, out var recipes))
                return false;

            var result = recipes.Any(r => r.Inputs.All(i => CanTerminate(i.Item, visiting)));
            visiting.Remove(itemId);
            return result;
        }

        foreach (var item in Data.Items.Where(i => !i.Raw))
        {
            if (!CanTerminate(item.Id, new HashSet<string>()))
                unresolved.Add(item.Id);
        }

        Assert.True(unresolved.Count == 0, "Items whose production chain does not terminate in raw resources:\n" + string.Join("\n", unresolved));
    }
}
