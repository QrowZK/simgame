namespace Sim;

/// What the player is carrying. Counted, not slotted: slots are an interface
/// idea, and making them a sim concept would put "did the stack split" into the
/// determinism contract for nothing.
public sealed class Inventory
{
    private readonly Dictionary<ItemId, int> _contents = new();

    public int Count(ItemId item) => _contents.GetValueOrDefault(item);

    public IReadOnlyDictionary<ItemId, int> Contents => new Dictionary<ItemId, int>(_contents);

    public void Add(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        _contents[item] = _contents.GetValueOrDefault(item) + count;
    }

    /// Replaces the whole inventory. Save surface only.
    public void Restore(IReadOnlyList<(ItemId Item, int Count)> contents)
    {
        _contents.Clear();
        foreach (var (item, count) in contents)
            if (count > 0) _contents[item] = count;
    }

    /// Removes up to `count`, returning how many were actually taken.
    public int Take(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var have = _contents.GetValueOrDefault(item);
        var taken = Math.Min(have, count);
        if (taken <= 0) return 0;

        if (have == taken) _contents.Remove(item);
        else _contents[item] = have - taken;
        return taken;
    }
}

/// Hand operations on a machine: the early game, before any inserter exists.
///
/// These live in the sim rather than in the UI layer because they change world
/// state, and every state change has to be inside the deterministic tick's
/// world. The GUI only calls them.
public static class HandOps
{
    /// Loads a machine by hand. Returns how many were actually moved, which is
    /// what the GUI shows: an insert that silently does nothing is the single
    /// most confusing thing a machine panel can do.
    public static int Insert(Inventory from, Machine machine, ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        // Refuse items the recipe has no use for. Otherwise a mistyped hand
        // load buries an item in a machine with no way to get it back.
        if (machine.InputPerCycle(item) == 0)
            return 0;

        var moved = from.Take(item, count);
        if (moved > 0) machine.PushInput(item, moved);
        return moved;
    }

    /// Digs by hand. The whole game before the first miner: you stand on ore
    /// and take it, slowly. Returns what actually came out, which is less than
    /// asked when the patch is nearly gone and zero when it is finished.
    ///
    /// Hand mining is deliberately not a miner with a different number on it --
    /// it takes no space, needs no power, and is the only way to bootstrap a
    /// world where you own nothing.
    public static int Mine(Ground ground, int x, int y, Inventory into, int amount)
    {
        // Hands cannot scoop a liquid. Crude oil is buried like an ore -- the
        // derrick has to stand on something -- and without this the first thing
        // a new player walks to on some seeds is oil, digging it appears to
        // work, and it is a dead end they cannot see the bottom of. The gate is
        // the derrick, and it belongs here rather than in `Ground.Extract`,
        // which the derrick itself goes through.
        if (ground.TryPatchAt(x, y, out var patch) && patch.IsFluid)
            return 0;

        var taken = ground.Extract(x, y, amount, out var item);
        if (taken > 0) into.Add(item, taken);
        return taken;
    }

    /// Empties a machine's output into the player's hands.
    public static int Extract(Machine machine, Inventory into, ItemId item, int count)
    {
        var moved = machine.PullOutput(item, count);
        if (moved > 0) into.Add(item, moved);
        return moved;
    }

    /// Takes everything a machine has finished. The button a player actually
    /// presses, rather than one item type at a time.
    public static int ExtractAll(Machine machine, Inventory into)
    {
        var total = 0;
        foreach (var (item, count) in machine.OutputContents)
            total += Extract(machine, into, item, count);
        return total;
    }
}
