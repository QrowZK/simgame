namespace Sim;

public readonly struct ProspectHit
{
    public readonly ItemId Item;
    public readonly int X;
    public readonly int Y;
    public readonly int Distance;
    public readonly int Amount;
    public readonly int Radius;

    public ProspectHit(ItemId item, int x, int y, int distance, int amount, int radius)
    {
        Item = item;
        X = x;
        Y = y;
        Distance = distance;
        Amount = amount;
        Radius = radius;
    }
}

/// The survey device the player carries from the first minute.
///
/// A factory game where you wander until you trip over bauxite is a game about
/// wandering. The prospector answers two questions instead: what is under me,
/// and which way is the thing I need. The second matters more -- it turns
/// "explore until lucky" into a decision about how far you are willing to go.
public sealed class Prospector
{
    /// Detection radius in tiles. Deliberately smaller than a region, so the
    /// device narrows the search rather than solving it.
    public int Radius { get; }

    public Prospector(int radius = 96)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
    }

    /// Everything detectable from here, nearest first, one entry per resource.
    public List<ProspectHit> Scan(WorldGen world, int x, int y)
    {
        var best = new Dictionary<int, ProspectHit>();

        foreach (var patch in world.PatchesNear(x, y, Radius))
        {
            var distance = Distance(patch.X - x, patch.Y - y);
            if (best.TryGetValue(patch.Item.Value, out var existing) && existing.Distance <= distance)
                continue;

            best[patch.Item.Value] =
                new ProspectHit(patch.Item, patch.X, patch.Y, distance, patch.Amount, patch.Radius);
        }

        var hits = best.Values.ToList();
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return hits;
    }

    /// The resource directly underfoot, if the tile is on a patch.
    public bool TrySampleAt(WorldGen world, int x, int y, out ProspectHit hit)
    {
        if (world.TryPatchAt(x, y, out var patch))
        {
            hit = new ProspectHit(patch.Item, patch.X, patch.Y, 0, patch.Amount, patch.Radius);
            return true;
        }

        hit = default;
        return false;
    }

    /// Where the nearest deposit of one resource is, searching well beyond the
    /// detection radius. This is the "it is that way, and it is far" answer that
    /// makes exploring a decision rather than a lottery.
    public bool TryBearing(WorldGen world, int x, int y, ItemId ore, int searchRadius,
                           out ProspectHit hit)
    {
        var found = false;
        hit = default;
        var bestDistance = int.MaxValue;

        foreach (var patch in world.PatchesNear(x, y, searchRadius))
        {
            if (!patch.Item.Equals(ore)) continue;

            var distance = Distance(patch.X - x, patch.Y - y);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            hit = new ProspectHit(patch.Item, patch.X, patch.Y, distance, patch.Amount, patch.Radius);
            found = true;
        }

        return found;
    }

    /// Compass heading to a hit, in degrees clockwise from north.
    public static int HeadingTo(int fromX, int fromY, in ProspectHit hit)
    {
        var angle = Math.Atan2(hit.X - fromX, -(hit.Y - fromY)) * (180.0 / Math.PI);
        var degrees = (int)Math.Round(angle);
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static int Distance(int dx, int dy) => (int)Math.Sqrt(dx * dx + dy * dy);
}
