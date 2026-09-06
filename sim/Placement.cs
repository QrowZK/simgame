namespace Sim;

/// Where a machine sits and which meshes represent it. Deliberately a small
/// struct in a dense array: the renderer reads these in bulk to fill instance
/// buffers, and must never walk a list of objects to do it.
public readonly struct MachinePlacement
{
    /// Tile coordinate of the footprint's south-west corner, NOT its centre.
    /// A corner is the anchor a player's cursor actually holds when placing,
    /// and it keeps occupancy arithmetic in integers for even-sided machines.
    public readonly int X;
    public readonly int Y;

    /// Selects the hull mesh. Indexes the tier ladder.
    public readonly byte Tier;
    /// Selects the function attachment mesh. Indexes the machine category.
    public readonly byte Category;

    private readonly byte _size;

    /// Tiles per side. A machine is always square: a rectangle would double the
    /// placement rules (rotation, which way it faces) for no gain the recipe
    /// graph can express. A default-constructed placement reads as 1x1 rather
    /// than 0x0, so an unplaced machine still has a sane footprint.
    public byte Size => _size == 0 ? (byte)1 : _size;

    public MachinePlacement(int x, int y, byte tier, byte category, byte size = 1)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
        X = x;
        Y = y;
        Tier = tier;
        Category = category;
        _size = size;
    }

    /// Tiles covered. This is the number that scales a machine's effect, so it
    /// is also the number that scales its build cost.
    public int Area => Size * Size;

    /// Centre in tile space. Half-tile for even sizes, which is why the
    /// renderer works in floats and the occupancy grid does not.
    public float CentreX => X + Size / 2f;
    public float CentreY => Y + Size / 2f;

    public bool Covers(int x, int y)
        => x >= X && x < X + Size && y >= Y && y < Y + Size;

    public bool Overlaps(in MachinePlacement other)
        => X < other.X + other.Size && other.X < X + Size &&
           Y < other.Y + other.Size && other.Y < Y + Size;
}
