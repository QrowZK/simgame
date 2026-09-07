using System;
using System.Collections.Generic;
using System.Linq;
using Sim;
using Sim.Data;

namespace Game;

/// The opening route, played rather than described: from a starter kit and bare
/// ground to a steam miner in the player's hands, using nothing the world did
/// not give.
///
/// This exists because `--session-test` used to print `--- building ok ---`
/// after placing the starter bench on `form_copper_plate` -- a recipe needing a
/// copper ingot, needing a furnace, needing the bench. It asserted that a build
/// succeeded, which was true, and implied that the opening loop worked, which
/// was not (`docs/0020`, F1). An assertion that cannot fail is worse than none,
/// because it is counted.
///
/// So this walks the whole thing: hand-mine what a recipe needs, craft what a
/// machine needs, place it, retask it, and run it -- recursively, from the goal
/// item backwards. Every step uses only the sim's public player-facing
/// operations: `TryBuild`, `TryChangeRecipe`, `HandOps`. If any link in the
/// chain breaks -- a machine that cannot be retasked, a recipe with no source,
/// an ore no hand can dig -- this stops and says which item it got stuck on.
public sealed class OpeningRoute
{
    private readonly World _world;
    private readonly Catalogue _data;
    private readonly BuildCatalogue _buildables;
    private readonly Action<string> _log;

    /// Item ids currently being acquired. A recipe graph with a cycle in it
    /// would otherwise recurse forever; this turns that into a named failure.
    private readonly HashSet<string> _inProgress = new();

    /// Distinct recipes actually run, and how many placed machines were
    /// retasked. These are the numbers that say the blocker is gone: one bench
    /// running eight recipes is the fix, and a report that only counted crafts
    /// could not tell that apart from eight benches.
    public HashSet<string> RecipesRun { get; } = new();
    public int Retasks { get; private set; }
    public string? StuckOn { get; private set; }

    public OpeningRoute(World world, Catalogue data, BuildCatalogue buildables, Action<string> log)
    {
        _world = world;
        _data = data;
        _buildables = buildables;
        _log = log;
    }

    /// Plays the route until the player is carrying `count` of `item`.
    public bool Reach(string item, int count) => Acquire(item, count);

    private int Have(string item) =>
        _data.Items.TryGetId(item, out var id) ? _world.PlayerInventory.Count(id) : 0;

    /// Gets `count` of an item into the player's hands, by whatever the game
    /// actually offers: digging it, or crafting it on a machine that has to be
    /// crafted and placed first.
    private bool Acquire(string item, int count)
    {
        if (Have(item) >= count) return true;

        if (!_inProgress.Add(item))
        {
            StuckOn ??= $"{item} (circular recipe)";
            return false;
        }

        try
        {
            return _data.Data.Items.Any(i => i.Id == item && i.Raw)
                ? Mine(item, count)
                : Craft(item, count);
        }
        finally
        {
            _inProgress.Remove(item);
        }
    }

    /// Digs a raw material out of the nearest patch of it the prospector can
    /// see. The prospector's range is the player's knowledge, so a material it
    /// cannot find is one the route genuinely cannot reach.
    private bool Mine(string item, int count)
    {
        var id = _data.Item(item);
        var hits = new Prospector(radius: 400).Scan(_world.Ground.Gen, NewGame.SpawnX, NewGame.SpawnY);

        foreach (var hit in hits.Where(h => h.Item.Equals(id)))
        {
            while (Have(item) < count)
            {
                var dug = HandOps.Mine(_world.Ground, hit.X, hit.Y, _world.PlayerInventory,
                                       count - Have(item));
                if (dug == 0) break;      // patch worked out; try the next one
            }

            if (Have(item) >= count) return true;
        }

        StuckOn ??= $"{item} (no patch within the prospector's range holds enough)";
        return false;
    }

    /// The lowest-tier recipe that makes an item. Lowest, because the route is
    /// walked from a starter kit: a recipe two tiers up is not a way to make
    /// your first miner, it is a way to make your second one.
    private RecipeDef? SourceOf(string item) => _data.Data.Recipes
        .Where(r => r.Outputs.Any(o => o.Item == item))
        .OrderBy(r => TierIndex(r.Tier))
        .FirstOrDefault();

    private int TierIndex(string tier) =>
        _data.Data.Tiers.FirstOrDefault(t => t.Id == tier)?.Index ?? int.MaxValue;

    private bool Craft(string item, int count)
    {
        var def = SourceOf(item);
        if (def is null)
        {
            StuckOn ??= $"{item} (nothing in the recipe graph makes it)";
            return false;
        }

        var perRun = def.Outputs.Where(o => o.Item == item).Sum(o => o.Count);
        var runs = (count - Have(item) + perRun - 1) / perRun;

        // The machine first: crafting on a bench you have not built yet is the
        // shape of the original blocker.
        if (!EnsureMachine(def.Machine, def.Tier, out var index))
            return false;

        // Inputs. Acquiring one can consume another -- two branches of the tree
        // can meet at the same ingot -- so the amounts are re-checked until they
        // all hold at once rather than only when they were fetched.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var short_ = def.Inputs.Where(i => Have(i.Item) < i.Count * runs).ToList();
            if (short_.Count == 0) break;

            foreach (var input in short_)
                if (!Acquire(input.Item, input.Count * runs))
                    return false;
        }

        return Run(index, def, runs);
    }

    /// Runs a placed machine by hand for `runs` cycles: retask it if it is
    /// making something else, load exactly one cycle, wait it out, take the
    /// output. This is the first hour of the game, which is why the panel has
    /// those two buttons.
    private bool Run(int index, RecipeDef def, int runs)
    {
        var machine = _world.Machines[index];
        var recipe = _data.Recipe(def.Id);

        var change = _world.TryChangeRecipe(_buildables, index, recipe, out var evicted);
        if (change == RecipeChangeResult.Ok)
        {
            Retasks++;
            if (evicted > 0) _log($"retasked        {def.Id} (+{evicted} back)");
        }
        else if (change != RecipeChangeResult.AlreadyRunning)
        {
            StuckOn ??= $"{def.Id} ({change})";
            return false;
        }

        for (var i = 0; i < runs; i++)
        {
            foreach (var input in machine.Recipe.Inputs)
                HandOps.Insert(_world.PlayerInventory, machine, input.Item,
                               machine.InputPerCycle(input.Item) - machine.GetInputCount(input.Item));

            // Exactly one cycle's worth of ticks. One more would restart the
            // machine on an empty buffer and read as starved.
            _world.Tick(recipe.DurationTicks);

            if (machine.OutputContents.Count == 0)
            {
                StuckOn ??= $"{def.Id} (a cycle produced nothing: {machine.State}, short of " +
                            string.Join(", ", machine.Recipe.Inputs.Select(
                                i => $"{machine.GetInputCount(i.Item)}/{machine.InputPerCycle(i.Item)} " +
                                     _world.Items.GetName(i.Item))) + ")";
                return false;
            }

            HandOps.ExtractAll(machine, _world.PlayerInventory);
        }

        RecipesRun.Add(def.Id);
        return true;
    }

    /// Finds a placed machine of this kind, crafting and placing one if there
    /// is none. The bench is the only one the starter kit provides; every other
    /// machine on the route is made on it.
    private bool EnsureMachine(string machineId, string tier, out int index)
    {
        var itemId = $"{tier.ToLowerInvariant()}_{machineId}";

        var buildable = _buildables.Find(itemId);
        if (buildable is null)
        {
            index = -1;
            StuckOn ??= $"{itemId} (not a buildable)";
            return false;
        }

        for (var i = 0; i < _world.MachineCount; i++)
            if (_world.Machines[i].SourceItem is { } source &&
                _world.Items.GetName(source) == itemId)
            {
                index = i;
                return true;
            }

        index = -1;
        if (!Acquire(itemId, 1)) return false;

        // Any free tile will do: this is a test of the recipe graph, not of
        // where a player chooses to stand things.
        for (var d = 1; d < 400; d++)
        {
            var (x, y) = (d, -4);
            var placement = buildable.PlacementAt(x, y);
            if (!_world.CanPlace(placement) || _world.CoversFluidNode(placement)) continue;

            // Gated, like the build menu: a machine placed on a recipe the player
        // has not researched would be refused by `TryBuild` anyway, and picking
        // one here would report the refusal against the wrong step.
        var first = _buildables.RecipesFor(buildable, _world.Research).FirstOrDefault();
            if (first is null)
            {
                StuckOn ??= $"{itemId} (no recipe it can run)";
                return false;
            }

            if (_world.TryBuild(_buildables, buildable.Item, x, y, first) != BuildResult.Ok)
                continue;

            _log($"placed          {buildable.DisplayName} at {x},{y}");
            index = _world.MachineCount - 1;
            return true;
        }

        StuckOn ??= $"{itemId} (nowhere to place it)";
        return false;
    }
}
