namespace Sim;

/// Where a machine sits and which meshes represent it. Deliberately a small
/// struct in a dense array: the renderer reads these in bulk to fill instance
/// buffers, and must never walk a list of objects to do it.
public readonly struct MachinePlacement
{
    public readonly int X;
    public readonly int Y;
    /// Selects the hull mesh. Indexes the tier ladder.
    public readonly byte Tier;
    /// Selects the function attachment mesh. Indexes the machine category.
    public readonly byte Category;

    public MachinePlacement(int x, int y, byte tier, byte category)
    {
        X = x;
        Y = y;
        Tier = tier;
        Category = category;
    }
}
