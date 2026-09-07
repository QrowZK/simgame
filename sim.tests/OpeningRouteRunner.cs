using Sim;
using Sim.Data;

namespace Sim.Tests;

/// Plays the opening route in a real world, using only the operations a player
/// has: prospect, hand-mine, build, retask, hand-load, hand-empty.
///
/// This is a test helper rather than a sim feature. It exists so the route can
/// be asserted as a *route* -- every step using only what the previous ones
/// produced -- instead of as a set of independently-passing mechanisms, which
/// is exactly how the S1 blocker in `docs/0020` hid.
///
/// `game/scripts/OpeningRoute.cs` walks the same graph for `--session-test`.
/// The two are deliberately separate implementations: if the recipe data ever
/// grew a shape only one of them handled, the disagreement is the signal.
public sealed class OpeningRouteRunner
{
    private readonly World _world;
    private readonly Catalogue _data;
    private readonly BuildCatalogue _buildables;
    private readonly HashSet<string> _inProgress = new();

    /// Every recipe run, mapped to the index of the machine that ran it. The
    /// point of the fix is that one machine appears against many recipes.
    public Dictionary<string, int> RanOn { get; } = new();
    public string? StuckOn { get; private set; }
    public int Retasks { get; private set; }

    public OpeningRouteRunner(World world, Catalogue data, BuildCatalogue buildables)
    {
        _world = world;
        _data = data;
        _buildables = buildables;
    }

    public bool Reach(string item, int count) => Acquire(item, count);

    /// How many distinct recipes the busiest single machine ran. One before the
    /// fix, by construction: a machine's recipe was readonly.
    public int MostRecipesOnOneMachine =>
        RanOn.Count == 0 ? 0 : RanOn.GroupBy(kv => kv.Value).Max(g => g.Count());

    private int Have(string item) =>
        _data.Items.TryGetId(item, out var id) ? _world.PlayerInventory.Count(id) : 0;

    private bool Acquire(string item, int count)
    {
        if (Have(item) >= count) return true;
        if (!_inProgress.Add(item))
        {
            StuckOn ??= $"{item} (circular)";
            return false;
        }

        try
        {
            return _data.Data.Items.Any(i => i.Id == item && i.Raw)
                ? Mine(item, count)
                : Craft(item, count);
        }
        finally { _inProgress.Remove(item); }
    }

    private bool Mine(string item, int count)
    {
        var id = _data.Item(item);
        foreach (var hit in new Prospector(radius: 400)
                     .Scan(_world.Ground.Gen, NewGame.SpawnX, NewGame.SpawnY)
                     .Where(h => h.Item.Equals(id)))
        {
            while (Have(item) < count &&
                   HandOps.Mine(_world.Ground, hit.X, hit.Y, _world.PlayerInventory,
                                count - Have(item)) > 0) { }

            if (Have(item) >= count) return true;
        }

        StuckOn ??= $"{item} (nothing in reach yields it)";
        return false;
    }

    private bool Craft(string item, int count)
    {
        var def = _data.Data.Recipes
            .Where(r => r.Outputs.Any(o => o.Item == item))
            .OrderBy(r => _data.Data.Tiers.FirstOrDefault(t => t.Id == r.Tier)?.Index ?? int.MaxValue)
            .FirstOrDefault();

        if (def is null)
        {
            StuckOn ??= $"{item} (no recipe makes it)";
            return false;
        }

        var perRun = def.Outputs.Where(o => o.Item == item).Sum(o => o.Count);
        var runs = (count - Have(item) + perRun - 1) / perRun;

        if (!EnsureMachine(def.Machine, def.Tier, out var index)) return false;

        // Two branches of the tree can meet at the same ingot, so acquiring one
        // input can spend another. Re-check until they hold at once.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var short_ = def.Inputs.Where(i => Have(i.Item) < i.Count * runs).ToList();
            if (short_.Count == 0) break;
            foreach (var input in short_)
                if (!Acquire(input.Item, input.Count * runs)) return false;
        }

        var machine = _world.Machines[index];
        var recipe = _data.Recipe(def.Id);

        var change = _world.TryChangeRecipe(_buildables, index, recipe, out _);
        if (change == RecipeChangeResult.Ok) Retasks++;
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

            // Exactly one cycle. One tick more restarts it on an empty buffer.
            _world.Tick(recipe.DurationTicks);

            if (machine.OutputContents.Count == 0)
            {
                StuckOn ??= $"{def.Id} (a cycle produced nothing: {machine.State})";
                return false;
            }

            HandOps.ExtractAll(machine, _world.PlayerInventory);
        }

        RanOn[def.Id] = index;
        return true;
    }

    private bool EnsureMachine(string machineId, string tier, out int index)
    {
        var itemId = $"{tier.ToLowerInvariant()}_{machineId}";
        index = -1;

        var buildable = _buildables.Find(itemId);
        if (buildable is null)
        {
            StuckOn ??= $"{itemId} (not buildable)";
            return false;
        }

        for (var i = 0; i < _world.MachineCount; i++)
            if (_world.Machines[i].SourceItem is { } source &&
                _world.Items.GetName(source) == itemId)
            {
                index = i;
                return true;
            }

        if (!Acquire(itemId, 1)) return false;

        // Gated, like the build menu: a machine placed on a recipe the player
        // has not researched would be refused by `TryBuild` anyway, and picking
        // one here would report the refusal against the wrong step.
        var first = _buildables.RecipesFor(buildable, _world.Research).FirstOrDefault();
        if (first is null)
        {
            StuckOn ??= $"{itemId} (no recipe it can run)";
            return false;
        }

        for (var d = 1; d < 400; d++)
        {
            if (_world.TryBuild(_buildables, buildable.Item, d, -4, first) != BuildResult.Ok)
                continue;
            index = _world.MachineCount - 1;
            return true;
        }

        StuckOn ??= $"{itemId} (nowhere to place)";
        return false;
    }
}
