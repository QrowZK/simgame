using Sim;

using Sim.Data;

namespace Data.Tests;

/// Graph reachability says a chain *connects*. These tests say it actually
/// *runs*: they build the production chain for a target out of the real recipe
/// data, instantiate every step in the real sim, and tick until the target
/// appears. A recipe that is reachable on paper but cannot execute in the
/// machine model fails here and nowhere else.
///
/// Executability and throughput are deliberately separate concerns. Produce()
/// answers "can each step run at all"; Balance() answers "do the ratios hold",
/// analytically. Conflating them made allocation policy decide whether chains
/// passed, which tested the harness rather than the data.
public class CraftingPathTests
{
    private static readonly GameData Data = GameData.Instance;

    /// Chooses producers by reachability order: run the whole recipe set to a
    /// fixed point from raw resources, and remember which recipe first made each
    /// item obtainable. That recipe is by construction non-circular and actually
    /// runnable, and it mirrors how a player unlocks the chain. Picking by
    /// "fewest inputs" instead selected the ingot -> dust -> ingot recycling loop
    /// as the way to make steel, which deadlocks.
    private static (Dictionary<string, RecipeDef> First, Dictionary<string, int> Order) Discovery()
    {
        var tierIndex = Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var ordered = Data.Recipes.OrderBy(r => tierIndex[r.Tier]).ThenBy(r => r.Id).ToList();

        var reachable = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        var first = new Dictionary<string, RecipeDef>();
        var order = new Dictionary<string, int>();
        var step = 0;

        bool grew;
        do
        {
            grew = false;
            foreach (var recipe in ordered)
            {
                if (!recipe.Inputs.All(i => reachable.Contains(i.Item)))
                    continue;

                foreach (var output in recipe.Outputs)
                {
                    if (!reachable.Add(output.Item)) continue;
                    first[output.Item] = recipe;
                    order[recipe.Id] = step++;
                    grew = true;
                }
            }
        } while (grew);

        return (first, order);
    }

    private static List<RecipeDef> Plan(string target)
    {
        var (first, order) = Discovery();
        var raw = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();

        var needed = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(target);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (raw.Contains(item) || !first.TryGetValue(item, out var recipe))
                continue;
            if (!needed.Add(recipe.Id))
                continue;

            foreach (var input in recipe.Inputs)
                queue.Enqueue(input.Item);
        }

        // Discovery order is already dependency order.
        return Data.Recipes.Where(r => needed.Contains(r.Id))
                           .OrderBy(r => order.TryGetValue(r.Id, out var i) ? i : int.MaxValue)
                           .ToList();
    }

    /// How many machines each step needs to sustain the target rate. One machine
    /// per recipe is not a factory: coal dust alone feeds steel, silicon, steam
    /// and chlorination, and a single macerator starves all of them. Demand is
    /// propagated backwards through the chain, which is also what verifies the
    /// ratios are sane rather than merely connected.
    private static Dictionary<string, int> Balance(List<RecipeDef> plan, string target,
                                                   double targetPerTick, int capPerRecipe)
    {
        var demand = new Dictionary<string, double> { [target] = targetPerTick };
        var counts = new Dictionary<string, int>();

        // Consumers before producers: the plan is in discovery order, so walk it back.
        for (var i = plan.Count - 1; i >= 0; i--)
        {
            var recipe = plan[i];
            var duration = Math.Max(1, recipe.DurationTicks);

            // Cycles per tick actually needed -- NOT the machine count. Deriving
            // upstream demand from machines instead meant every recipe's
            // one-machine minimum propagated a full duty cycle, compounding at
            // each step and inflating the endgame by three orders of magnitude.
            var cycles = 0.0;
            foreach (var output in recipe.Outputs)
            {
                if (!demand.TryGetValue(output.Item, out var needed) || needed <= 0) continue;
                cycles = Math.Max(cycles, needed / output.Count);
            }

            counts[recipe.Id] = Math.Min(capPerRecipe,
                                         Math.Max(1, (int)Math.Ceiling(cycles * duration)));

            foreach (var input in recipe.Inputs)
            {
                var rate = cycles * input.Count;
                demand[input.Item] = demand.TryGetValue(input.Item, out var d) ? d + rate : rate;
            }
        }

        return counts;
    }

    /// Runs every step of a plan in the real sim and reports which ones managed
    /// to complete a cycle.
    ///
    /// Anything that has been produced at least once is then treated as freely
    /// available, exactly like a raw resource. That is deliberate: the question
    /// here is whether each recipe can *execute* in the machine model, not
    /// whether a particular factory layout keeps it fed. Modelling scarce
    /// intermediates with a shared store turned the harness itself into the
    /// thing under test -- allocation policy decided whether chains passed.
    /// Throughput and ratios are checked separately and analytically, by Balance.
    private static bool Produce(string target, int maxTicks, out int steps, out string diagnosis)
    {
        var plan = Plan(target);
        steps = plan.Count;
        diagnosis = "";

        var db = new ItemDatabase();
        foreach (var item in Data.Items) db.Register(item.Id);

        var available = Data.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        var world = new World(1);
        var built = new List<(Sim.Machine Machine, RecipeDef Def)>();

        foreach (var def in plan)
        {
            var recipe = new Recipe(
                def.Id,
                Math.Max(1, def.DurationTicks),
                def.Inputs.Select(i => new RecipeInput(db.GetId(i.Item), i.Count)).ToArray(),
                def.Outputs.Select(o => new RecipeOutput(db.GetId(o.Item), o.Count)).ToArray());

            built.Add((world.AddMachine(recipe, outputCapacityPerItem: 1_000_000), def));
        }

        var ran = new HashSet<string>();

        for (var tick = 0; tick < maxTicks; tick++)
        {
            foreach (var (machine, def) in built)
            {
                foreach (var output in def.Outputs)
                    if (machine.GetOutputCount(db.GetId(output.Item)) > 0)
                    {
                        machine.PullOutput(db.GetId(output.Item), 100_000);
                        available.Add(output.Item);
                        ran.Add(def.Id);
                    }

                if (!def.Inputs.All(i => available.Contains(i.Item)))
                    continue;

                foreach (var input in def.Inputs)
                {
                    var id = db.GetId(input.Item);
                    var want = input.Count - machine.GetInputCount(id);
                    if (want > 0) machine.PushInput(id, want);
                }
            }

            world.Tick();

            if (available.Contains(target))
                return true;
        }

        var stuck = plan.Where(r => !ran.Contains(r.Id))
                        .Select(r => "  " + r.Id + " [" + r.Machine + "] never ran; missing: " +
                                     string.Join(", ", r.Inputs.Where(i => !available.Contains(i.Item))
                                                               .Select(i => i.Item)))
                        .Take(8)
                        .ToList();

        diagnosis = stuck.Count > 0
            ? "\nSteps that never completed:\n" + string.Join("\n", stuck)
            : "\nEvery step ran but the target never appeared.";
        return false;
    }

    public static IEnumerable<object[]> Targets() => new[]
    {
        new object[] { "iron_plate" },          // manual tier, two steps
        new object[] { "steel_ingot" },         // first real alloy
        new object[] { "bakelite_ingot" },      // coal chemistry, the pre-oil branch
        new object[] { "aluminum_ingot" },      // electrolysis gate
        new object[] { "pvc_ingot" },           // needs the refinery and a cracker
        new object[] { "titanium_ingot" },      // Kroll process
        new object[] { "vlt_machine_hull" },    // a tier entry component
        new object[] { "arc_circuit" },         // electronics
        new object[] { "ptfe_ingot" },          // deep fluorochemistry
        new object[] { "peek_ingot" },          // the longest polymer route
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void EveryFleshedOutChain_ActuallyProducesItsTarget(string target)
    {
        var made = Produce(target, maxTicks: 100_000, out var steps, out var why);

        Assert.True(made,
            $"'{target}' was never produced across {steps} steps. " +
            "The chain is reachable on paper but does not run in the machine model." + why);
    }

    [Fact]
    public void TheGoalItem_CanBeBuiltFromRawResourcesAlone()
    {
        // The whole game, start to finish, in one simulation.
        var made = Produce("von_neumann_seed", maxTicks: 300_000, out var steps, out var why);

        Assert.True(made, $"the goal was never produced across {steps} steps" + why);
        Assert.True(steps > 100, $"the goal chain should be deep; it planned only {steps} steps");
    }

    [Fact]
    public void GoalFactory_HasSaneRatios()
    {
        // Executability says every step CAN run; this says the numbers behind
        // them are not absurd. A ratio error shows up here as one step needing
        // orders of magnitude more machines than the rest of the factory.
        var plan = Plan("von_neumann_seed");
        var counts = Balance(plan, "von_neumann_seed", targetPerTick: 1.0 / 20_000,
                             capPerRecipe: 1_000_000);

        var total = counts.Values.Sum();
        var worst = counts.OrderByDescending(kv => kv.Value).First();

        Assert.True(total is > 50 and < 2000,
            $"a goal factory of {total} machines looks wrong for one seed per 20k ticks");
        Assert.True(worst.Value < 250,
            $"'{worst.Key}' needs {worst.Value} machines against {total} total -- ratio looks broken");
    }

    [Fact]
    public void EveryPlannedRecipe_IsUnlockedByAReachableTech()
    {
        // A chain that runs but is gated behind a tech the player cannot reach
        // is still unplayable.
        var techIds = Data.Techs.Select(t => t.Id).ToHashSet();
        var plan = Plan("von_neumann_seed");

        var ungated = plan.Where(r => !techIds.Contains(r.UnlockedBy))
                          .Select(r => $"{r.Id} -> {r.UnlockedBy}")
                          .ToList();

        Assert.True(ungated.Count == 0,
            "Recipes on the goal path with no valid tech:\n" + string.Join("\n", ungated));
    }

    [Fact]
    public void NoPlannedRecipe_ConsumesItsOwnOutput()
    {
        // A recipe that eats what it makes is a net-zero loop and will deadlock
        // a real factory even though the graph looks fine.
        var offenders = Data.Recipes
            .Where(r => r.Inputs.Any(i => r.Outputs.Any(o => o.Item == i.Item)))
            .Select(r => r.Id)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Recipes consuming their own output:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryRawResource_IsActuallyUsedBySomething()
    {
        // World generation has to place every raw resource, so a raw item
        // nothing consumes is either dead content or a missing recipe.
        var consumed = Data.Recipes.SelectMany(r => r.Inputs).Select(i => i.Item).ToHashSet();
        var unused = Data.Items
            .Where(i => i.Raw && i.Category != "machine" && !consumed.Contains(i.Id))
            .Select(i => i.Id)
            .ToList();

        Assert.True(unused.Count == 0,
            "Raw resources nothing consumes:\n" + string.Join("\n", unused));
    }
}
