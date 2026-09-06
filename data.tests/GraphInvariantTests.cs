namespace Data.Tests;

public class GraphInvariantTests
{
    private static readonly GameData Data = GameData.Instance;

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
    public void EveryRecipeMachine_ExistsAndSupportsTheRecipeTier()
    {
        var machines = Data.Machines.ToDictionary(m => m.Id);
        var problems = new List<string>();

        foreach (var recipe in Data.Recipes)
        {
            if (!machines.TryGetValue(recipe.Machine, out var machine))
            {
                problems.Add($"{recipe.Id}: machine '{recipe.Machine}' does not exist");
                continue;
            }

            // A recipe must be runnable by a machine that actually exists at its tier.
            if (!machine.Tiers.Contains(recipe.Tier))
                problems.Add($"{recipe.Id}: needs '{recipe.Machine}' at tier {recipe.Tier}, " +
                             $"but that machine only exists at [{string.Join(", ", machine.Tiers)}]");
        }

        Assert.True(problems.Count == 0, "Machine/tier problems:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryRecipeTierAndItemTier_ExistsInTiersJson()
    {
        var tierIds = Data.Tiers.Select(t => t.Id).ToHashSet();
        var problems = new List<string>();

        foreach (var recipe in Data.Recipes.Where(r => !tierIds.Contains(r.Tier)))
            problems.Add($"recipe {recipe.Id}: tier '{recipe.Tier}'");
        foreach (var item in Data.Items.Where(i => !tierIds.Contains(i.Tier)))
            problems.Add($"item {item.Id}: tier '{item.Tier}'");
        foreach (var tech in Data.Techs.Where(t => !tierIds.Contains(t.Tier)))
            problems.Add($"tech {tech.Id}: tier '{tech.Tier}'");

        Assert.True(problems.Count == 0, "Dangling tier references:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryRecipeUnlock_ExistsInTechsJson()
    {
        var techIds = Data.Techs.Select(t => t.Id).ToHashSet();
        var missing = Data.Recipes
            .Where(r => !techIds.Contains(r.UnlockedBy))
            .Select(r => $"{r.Id}: unlocked_by '{r.UnlockedBy}'")
            .ToList();

        Assert.True(missing.Count == 0, "Dangling unlocked_by references:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryTechRequirement_ExistsAndResolves()
    {
        var techIds = Data.Techs.Select(t => t.Id).ToHashSet();
        var itemIds = Data.Items.Select(i => i.Id).ToHashSet();
        var missing = new List<string>();

        foreach (var tech in Data.Techs)
        {
            foreach (var req in tech.Requires)
                if (!techIds.Contains(req))
                    missing.Add($"{tech.Id}: requires tech '{req}'");

            if (tech.RequiresItem is not null && !itemIds.Contains(tech.RequiresItem))
                missing.Add($"{tech.Id}: requires_item '{tech.RequiresItem}'");
        }

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
            if (!Visit(tech.Id, new List<string>()))
                Assert.Fail("Circular tech dependency: " + string.Join(" -> ", cycle));
    }

    /// Walks the ladder tier by tier, accumulating everything craftable using only
    /// recipes available at or below that tier. This is the model the tier-gating
    /// invariants below are checked against.
    private static Dictionary<string, HashSet<string>> ReachableByTier()
    {
        var ordered = Data.Tiers.OrderBy(t => t.Index).ToList();
        var reachable = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        var tierIndex = Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var snapshots = new Dictionary<string, HashSet<string>>();

        foreach (var tier in ordered)
        {
            var usable = Data.Recipes.Where(r => tierIndex[r.Tier] <= tier.Index).ToList();
            bool changed;
            do
            {
                changed = false;
                foreach (var recipe in usable)
                {
                    if (!recipe.Inputs.All(i => reachable.Contains(i.Item)))
                        continue;
                    foreach (var output in recipe.Outputs)
                        if (reachable.Add(output.Item))
                            changed = true;
                }
            } while (changed);

            snapshots[tier.Id] = new HashSet<string>(reachable);
        }

        return snapshots;
    }

    [Fact]
    public void EveryItem_IsReachableFromRawResources()
    {
        var snapshots = ReachableByTier();
        var final = snapshots[Data.Tiers.OrderBy(t => t.Index).Last().Id];

        var unreachable = Data.Items
            .Where(i => !final.Contains(i.Id))
            .Select(i => $"{i.Id} ({i.Category})")
            .ToList();

        Assert.True(unreachable.Count == 0,
            $"{unreachable.Count} unreachable items:\n" + string.Join("\n", unreachable.Take(40)));
    }

    [Fact]
    public void EveryRecipe_HasInputsReachableAtItsOwnTier()
    {
        var snapshots = ReachableByTier();
        var dead = new List<string>();

        foreach (var recipe in Data.Recipes)
        {
            var available = snapshots[recipe.Tier];
            var blocked = recipe.Inputs.Where(i => !available.Contains(i.Item)).Select(i => i.Item).ToList();
            if (blocked.Count > 0)
                dead.Add($"{recipe.Id} (tier {recipe.Tier}) blocked on: {string.Join(", ", blocked)}");
        }

        Assert.True(dead.Count == 0,
            $"{dead.Count} recipes cannot run at the tier they are gated to:\n" + string.Join("\n", dead.Take(40)));
    }

    /// The invariant that keeps the ladder climbable: you must be able to build a
    /// tier's entry component using only the tier below it. Violating this is the
    /// silent-unplayability failure mode a deep tier ladder is most prone to --
    /// tier N's gate metal ending up only producible by a tier N machine.
    [Fact]
    public void EveryTierGateComponent_IsBuildableWithThePreviousTiersMachines()
    {
        var snapshots = ReachableByTier();
        var ordered = Data.Tiers.OrderBy(t => t.Index).ToList();
        var problems = new List<string>();

        foreach (var tier in ordered.Where(t => t.Index >= 1))
        {
            var previous = ordered[tier.Index - 1];
            var hull = $"{tier.Id.ToLowerInvariant()}_machine_hull";

            if (!snapshots[previous.Id].Contains(hull))
                problems.Add($"{tier.Id} ({tier.Name}): '{hull}' is not craftable using only " +
                             $"{previous.Id} and below -- the tier cannot be entered.");
        }

        Assert.True(problems.Count == 0,
            "Tier gating is circular:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryTierAboveManual_HasAGateHullAndPositivePower()
    {
        var itemIds = Data.Items.Select(i => i.Id).ToHashSet();
        var problems = new List<string>();

        foreach (var tier in Data.Tiers.Where(t => t.Index >= 1))
        {
            var hull = $"{tier.Id.ToLowerInvariant()}_machine_hull";
            if (!itemIds.Contains(hull))
                problems.Add($"{tier.Id}: missing gate component '{hull}'");
            if (tier.Power <= 0)
                problems.Add($"{tier.Id}: power must be positive, was {tier.Power}");
            if (string.IsNullOrEmpty(tier.Metal))
                problems.Add($"{tier.Id}: no structural metal declared");
        }

        Assert.True(problems.Count == 0, "Tier definition problems:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void RecipeIds_AreUnique()
    {
        var duplicates = Data.Recipes.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate recipe ids:\n" + string.Join("\n", duplicates));
    }

    [Fact]
    public void ItemIds_AreUnique()
    {
        var duplicates = Data.Items.GroupBy(i => i.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate item ids:\n" + string.Join("\n", duplicates));
    }

    [Fact]
    public void EveryRecipe_HasPositiveDurationAndNonNegativePower()
    {
        var problems = Data.Recipes
            .Where(r => r.DurationTicks <= 0 || r.PowerDraw < 0)
            .Select(r => $"{r.Id}: duration={r.DurationTicks} power={r.PowerDraw}")
            .ToList();

        Assert.True(problems.Count == 0, "Invalid recipe timings:\n" + string.Join("\n", problems));
    }
}
