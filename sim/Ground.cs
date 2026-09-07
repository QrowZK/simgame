namespace Sim;

/// The world's ore, and how much of it is left.
///
/// `WorldGen` is a pure function of the seed: it says where a patch is and how
/// much it started with, and it can never change. What a player has dug out of
/// it is simulation state, so it lives here as a sparse overlay -- only patches
/// that have actually been mined take any space, and an untouched world costs
/// nothing to store or to save.
///
/// This is the seam that makes worldgen part of the game rather than scenery:
/// ore leaves the ground here and enters the item graph.
public sealed class Ground
{
    private readonly WorldGen _gen;

    /// Patch centre -> units removed. Sparse on purpose: an unmined world has
    /// an empty dictionary, and a save writes nothing for it.
    private readonly Dictionary<long, int> _taken = new();

    public Ground(WorldGen gen) => _gen = gen;

    public WorldGen Gen => _gen;

    private static long Key(in OrePatch patch) => ((long)patch.X << 32) ^ (uint)patch.Y;

    /// The patch covering a tile, whether or not anything is left in it.
    public bool TryPatchAt(int x, int y, out OrePatch patch) => _gen.TryPatchAt(x, y, out patch);

    /// Units still in a patch.
    public int Remaining(in OrePatch patch) =>
        Math.Max(0, patch.Amount - _taken.GetValueOrDefault(Key(patch)));

    /// Units still under a tile, 0 if there is no patch or it is worked out.
    public int RemainingAt(int x, int y) =>
        TryPatchAt(x, y, out var patch) ? Remaining(patch) : 0;

    /// What a tile would yield, for a UI or a placement check.
    public bool TryResourceAt(int x, int y, out ItemId item, out int remaining)
    {
        if (TryPatchAt(x, y, out var patch))
        {
            item = patch.Item;
            remaining = Remaining(patch);
            return remaining > 0;
        }

        item = default;
        remaining = 0;
        return false;
    }

    /// Removes up to `amount` from the patch under a tile, returning how much
    /// actually came out. A patch that runs dry returns less than asked and then
    /// nothing -- callers must handle a short read rather than assuming the ask.
    public int Extract(int x, int y, int amount, out ItemId item)
    {
        item = default;
        if (amount <= 0 || !TryPatchAt(x, y, out var patch))
            return 0;

        var remaining = Remaining(patch);
        if (remaining <= 0)
            return 0;

        var taken = Math.Min(amount, remaining);
        _taken[Key(patch)] = _taken.GetValueOrDefault(Key(patch)) + taken;
        item = patch.Item;
        return taken;
    }

    /// How many patches have been dug into. A cheap change signal for anything
    /// that caches a view of the ground.
    public int DepletionCount => _taken.Count;

    /// Save surface: every patch that has been dug, as (x, y, units taken).
    public IEnumerable<(int X, int Y, int Taken)> Depletion =>
        _taken.OrderBy(kv => kv.Key)
              .Select(kv => ((int)(kv.Key >> 32), (int)(uint)kv.Key, kv.Value));

    public void Restore(IEnumerable<(int X, int Y, int Taken)> depletion)
    {
        _taken.Clear();
        foreach (var (x, y, count) in depletion)
            if (count > 0) _taken[((long)x << 32) ^ (uint)y] = count;
    }
}
