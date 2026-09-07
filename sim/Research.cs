using Sim.Data;

namespace Sim;

/// One thing that has to be delivered before an objective completes.
public readonly struct ResearchNeed
{
    /// Item id as the data files name it ("stm_machine_hull"), not an `ItemId`.
    /// Research outlives any one `ItemDatabase`: a save rebuilds the item table
    /// in its own order, and a research state keyed on runtime ids would credit
    /// a reloaded factory's hulls against the wrong tech.
    public readonly string Item;
    public readonly int Required;
    public readonly int Delivered;

    public ResearchNeed(string item, int required, int delivered)
    {
        Item = item;
        Required = required;
        Delivered = delivered;
    }

    public int Outstanding => Math.Max(0, Required - Delivered);
    public bool Met => Delivered >= Required;
}

/// A quest: a tech waiting on its hull, or the Seed waiting on its assemblies.
public sealed class ResearchObjective
{
    /// The tech id, or `Research.SeedObjective` for the last one.
    public readonly string Id;
    public readonly string Name;
    public readonly IReadOnlyList<ResearchNeed> Needs;

    /// Recipe ids this objective opens. Empty for the Seed, which opens
    /// nothing because there is nothing after it.
    public readonly IReadOnlyList<string> Unlocks;

    public ResearchObjective(string id, string name, IReadOnlyList<ResearchNeed> needs,
                             IReadOnlyList<string> unlocks)
    {
        Id = id;
        Name = name;
        Needs = needs;
        Unlocks = unlocks;
    }

    public bool Complete
    {
        get
        {
            foreach (var need in Needs)
                if (!need.Met) return false;
            return true;
        }
    }
}

/// What a delivery did. Returned rather than raised as an event, because the
/// UI has to say "that completed Steam Metallurgy" in the same frame the player
/// clicked, and a headless test has to assert it without a subscription.
public sealed class DeliveryReport
{
    /// Units actually taken. A delivery of something nothing wants accepts
    /// nothing, and the caller must put it back -- an Uplink that swallows a
    /// belt of ore is the worst thing this system could do.
    public int Accepted { get; internal set; }

    /// Tech ids completed by this delivery, in the order they completed.
    public List<string> Completed { get; } = new();

    /// Whether this delivery finished the Seed.
    public bool SeedComplete { get; internal set; }
}

/// The tech tree, given a runtime (ADR 0023).
///
/// `data/techs.json` and every recipe's `unlocked_by` have existed since the
/// progression was written and nothing read either of them. This is what reads
/// them: which techs are unlocked, which recipes that makes buildable, and what
/// has to be *delivered* -- into the Uplink, by hand or by belt -- to unlock
/// the next one. The quest list is therefore not authored anywhere; it is the
/// tech graph, which is already balanced against the recipe graph.
///
/// Deterministic and integer throughout: deliveries are credited to objectives
/// in data order, so two worlds fed the same items in the same ticks hold the
/// same research.
public sealed class Research
{
    /// The id of the final objective. Not a tech -- it has no `unlocked_by`
    /// recipes behind it and no successor -- but it is an objective in every
    /// other respect, so it shares the type rather than being a second system.
    public const string SeedObjective = "goal_von_neumann_seed";

    /// The recipe the Uplink "runs". It has no inputs and no outputs; see
    /// `World.Tick`, which drains the Uplink's buffer here instead of cycling
    /// it. Named here because both the sim and the UI need to recognise one.
    public const string UplinkRecipe = "uplink_deliver";

    public const string UplinkItem = "man_uplink";

    private readonly GameData _data;
    private readonly HashSet<string> _unlocked = new();
    private readonly HashSet<string> _unlockedRecipes = new();
    private readonly Dictionary<string, Dictionary<string, int>> _progress = new();
    private readonly List<TechDef> _order;
    private readonly List<(string Item, int Count)> _seedNeeds;

    public Research(Catalogue catalogue) : this(catalogue.Data) { }

    public Research(GameData data)
    {
        _data = data;
        _order = data.Techs.ToList();

        // The Seed's bill of materials is the goal recipe's inputs. Read from
        // the data rather than restated here, so changing the goal in
        // `progression.json` moves the last quest with it.
        var goal = data.Recipes.FirstOrDefault(r => r.Id == "build_von_neumann_seed");
        _seedNeeds = goal is null
            ? new List<(string, int)>()
            : goal.Inputs.Select(i => (i.Item, i.Count)).ToList();

        // The four Manual techs have no `requires_item`, so they are unlocked
        // at tick zero. This is the single line that keeps a new game playable:
        // without it a player lands holding a bench they may not use.
        foreach (var tech in _order)
            if (tech.RequiresItem is null)
                Unlock(tech.Id);
    }

    /// The starting kit, granted once, when the first tech of a line completes.
    ///
    /// The only handout in the game, and it is a ramp smoother rather than a
    /// prize (ADR 0023): a player who has just unlocked Steam machines and owns
    /// no belts has to hand-craft their way to a first automated line, which is
    /// where people put a factory game down. Keyed by tech, so it is granted
    /// exactly once and a reload cannot grant it again -- the tech is already
    /// unlocked by then.
    public static readonly IReadOnlyDictionary<string, (string Item, int Count)[]> Kits =
        new Dictionary<string, (string, int)[]>
        {
            ["tech_stm_metallurgy"] = new[] { ("stm_transport_belt", 12) },
            ["tech_stm_processing"] = new[] { ("stm_inserter", 4) },
            ["tech_stm_chemistry"] = new[] { ("stm_pole", 2) },
            ["tech_stm_fabrication"] = new[] { ("stm_transport_belt", 12), ("stm_inserter", 4) },
        };

    public IReadOnlyCollection<string> UnlockedTechs => _unlocked;

    public bool IsTechUnlocked(string techId) => _unlocked.Contains(techId);

    /// Whether a recipe may be built or picked. An unknown recipe id is locked
    /// rather than open: a picker that fails open would quietly hand the whole
    /// tree to anyone who mistyped an id.
    public bool IsUnlocked(string recipeId) => _unlockedRecipes.Contains(recipeId);

    public bool IsUnlocked(Recipe recipe) => IsUnlocked(recipe.Id);

    public bool SeedDelivered { get; private set; }

    /// Every tech, unlocked or not, in data order. The quest panel shows the
    /// whole ladder so the player can see where the next hull leads.
    public IReadOnlyList<TechDef> AllTechs => _order;

    /// What can be worked on right now: every locked tech whose prerequisites
    /// are met, plus the Seed once the tree is finished. Recomputed rather than
    /// cached -- there are 32 techs and this is asked once a frame at most.
    public IReadOnlyList<ResearchObjective> Objectives
    {
        get
        {
            var open = new List<ResearchObjective>();

            foreach (var tech in _order)
            {
                if (_unlocked.Contains(tech.Id)) continue;
                if (tech.RequiresItem is null) continue;
                if (!tech.Requires.All(_unlocked.Contains)) continue;
                open.Add(Objective(tech));
            }

            if (!SeedDelivered && _order.All(t => _unlocked.Contains(t.Id)))
                open.Add(SeedObjectiveNow());

            return open;
        }
    }

    /// The objective for a tech whether or not it is currently open, so the
    /// panel can show a locked one with the progress already on it.
    public ResearchObjective Objective(TechDef tech)
    {
        var needs = tech.RequiresItem is null
            ? Array.Empty<ResearchNeed>()
            : new[] { new ResearchNeed(tech.RequiresItem, 1, Delivered(tech.Id, tech.RequiresItem)) };

        return new ResearchObjective(tech.Id, tech.Name, needs, RecipesOf(tech.Id));
    }

    public ResearchObjective SeedObjectiveNow()
        => new(SeedObjective, "Von Neumann Seed",
               _seedNeeds.Select(n => new ResearchNeed(n.Item, n.Count,
                                                       Delivered(SeedObjective, n.Item))).ToList(),
               Array.Empty<string>());

    private List<string> RecipesOf(string techId)
        => _data.Recipes.Where(r => r.UnlockedBy == techId).Select(r => r.Id).ToList();

    public int Delivered(string objectiveId, string item)
        => _progress.TryGetValue(objectiveId, out var byItem)
            ? byItem.GetValueOrDefault(item)
            : 0;

    /// Delivers items into the current objectives.
    ///
    /// Credited to open objectives in data order, one at a time, and never past
    /// what an objective still wants. Anything nothing wants is refused: the
    /// count in `Accepted` is what left the player's hands, and the caller keeps
    /// the rest. That refusal is the whole reason this returns a number instead
    /// of void -- an Uplink that eats a misrouted belt of iron would cost a
    /// player their factory's throughput with no message and no way back.
    public DeliveryReport Deliver(string item, int count)
    {
        if (count <= 0) return new DeliveryReport();

        var report = new DeliveryReport();
        var left = count;

        // A snapshot, because unlocking a tech mid-loop opens new objectives
        // and crediting the same delivery against a tech it has just unlocked
        // would let one hull buy two tiers.
        foreach (var objective in Objectives)
        {
            if (left <= 0) break;

            foreach (var need in objective.Needs)
            {
                if (need.Item != item) continue;
                var take = Math.Min(left, need.Outstanding);
                if (take <= 0) continue;

                var byItem = _progress.TryGetValue(objective.Id, out var found)
                    ? found
                    : _progress[objective.Id] = new Dictionary<string, int>();
                byItem[item] = byItem.GetValueOrDefault(item) + take;
                left -= take;
                report.Accepted += take;
            }

            // Re-read the objective: `Complete` is computed from stored
            // progress, and the copy in hand was built before this delivery.
            var settled = objective.Id == SeedObjective
                ? SeedObjectiveNow()
                : Objective(_order.First(t => t.Id == objective.Id));

            if (!settled.Complete) continue;

            if (settled.Id == SeedObjective)
            {
                SeedDelivered = true;
                report.SeedComplete = true;
            }
            else
            {
                Unlock(settled.Id);
                report.Completed.Add(settled.Id);
            }
        }

        return report;
    }

    /// Unlocks the whole tree at once.
    ///
    /// Not a player-facing operation and not reachable from the game: it exists
    /// for the worlds that are not being played -- headless throughput
    /// analysis, the demo world, and tests that hand themselves a machine
    /// rather than earning it. Those worlds want the whole recipe graph, and
    /// the alternative was for each of them to carry its own copy of the tech
    /// order.
    public void UnlockAll()
    {
        foreach (var tech in _order) Unlock(tech.Id);
    }

    private void Unlock(string techId)
    {
        if (!_unlocked.Add(techId)) return;
        foreach (var recipe in _data.Recipes)
            if (recipe.UnlockedBy == techId)
                _unlockedRecipes.Add(recipe.Id);
    }

    // ---- save surface ------------------------------------------------------

    /// Unlocked tech ids in data order. Ordered rather than as-added, so a save
    /// round-trips byte-identically however the player got there.
    public IEnumerable<string> UnlockedInOrder
        => _order.Where(t => _unlocked.Contains(t.Id)).Select(t => t.Id);

    /// Part-delivered objectives, in data order then item order.
    public IEnumerable<(string Objective, string Item, int Count)> Progress
    {
        get
        {
            foreach (var id in _order.Select(t => t.Id).Append(SeedObjective))
            {
                if (!_progress.TryGetValue(id, out var byItem)) continue;
                foreach (var (item, count) in byItem.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    if (count > 0)
                        yield return (id, item, count);
            }
        }
    }

    public void Restore(IEnumerable<string> unlocked,
                        IEnumerable<(string Objective, string Item, int Count)> progress,
                        bool seedDelivered)
    {
        _progress.Clear();
        foreach (var (objective, item, count) in progress)
        {
            if (!_progress.TryGetValue(objective, out var byItem))
                _progress[objective] = byItem = new Dictionary<string, int>();
            byItem[item] = count;
        }

        // Unlocks are replayed rather than trusted wholesale: the recipe set
        // belongs to the running build, so a save written before a recipe
        // existed still gets it if its tech is unlocked.
        foreach (var id in unlocked) Unlock(id);

        SeedDelivered = seedDelivered;
    }
}
