namespace Sim;

/// Which way a belt carries, or an inserter faces.
///
/// Only four, and no diagonals: the occupancy grid is square and a diagonal
/// belt would have to decide what "adjacent" means for every other system that
/// asks about tiles.
public enum Direction
{
    East,
    South,
    West,
    North,
}

public static class Directions
{
    /// East is +X and South is +Y, matching the tile grid the renderer draws
    /// (world Z is the sim's Y).
    public static (int Dx, int Dy) Delta(Direction facing) => facing switch
    {
        Direction.East => (1, 0),
        Direction.South => (0, 1),
        Direction.West => (-1, 0),
        _ => (0, -1),
    };

    public static Direction Opposite(Direction facing) => facing switch
    {
        Direction.East => Direction.West,
        Direction.South => Direction.North,
        Direction.West => Direction.East,
        _ => Direction.South,
    };

    /// Clockwise, which is what a rotate key does.
    public static Direction Rotate(Direction facing) => facing switch
    {
        Direction.East => Direction.South,
        Direction.South => Direction.West,
        Direction.West => Direction.North,
        _ => Direction.East,
    };
}

/// A belt the player placed: one tile, facing one way.
public readonly struct PlacedBelt
{
    public readonly int X;
    public readonly int Y;
    public readonly Direction Facing;
    public readonly int Speed;

    public PlacedBelt(int x, int y, Direction facing, int speed)
    {
        X = x;
        Y = y;
        Facing = facing;
        Speed = speed;
    }

    public (int X, int Y) Ahead
    {
        get
        {
            var (dx, dy) = Directions.Delta(Facing);
            return (X + dx, Y + dy);
        }
    }
}

/// An inserter the player placed. It takes from the tile behind it and gives to
/// the tile in front, which is the whole of what an inserter decides.
public readonly struct PlacedInserter
{
    public readonly int X;
    public readonly int Y;
    public readonly Direction Facing;
    public readonly int SwingTicks;
    public readonly int StackSize;

    public PlacedInserter(int x, int y, Direction facing, int swingTicks, int stackSize)
    {
        X = x;
        Y = y;
        Facing = facing;
        SwingTicks = swingTicks;
        StackSize = stackSize;
    }

    public (int X, int Y) Ahead
    {
        get
        {
            var (dx, dy) = Directions.Delta(Facing);
            return (X + dx, Y + dy);
        }
    }

    public (int X, int Y) Behind
    {
        get
        {
            var (dx, dy) = Directions.Delta(Directions.Opposite(Facing));
            return (X + dx, Y + dy);
        }
    }
}

/// One end of an underground belt: a single tile with a facing, and a role.
///
/// The role is decided when it is placed rather than derived at compile time.
/// Deriving it needs a rule for "which of these two holes is the input", and
/// every rule that reads the surrounding belts changes its mind when a
/// neighbouring belt is rotated -- a tunnel that reverses itself because
/// something two tiles away turned is not a system a player can reason about.
/// Placement order is a rule the player can see themselves obeying.
public readonly struct PlacedUnderground
{
    public readonly int X;
    public readonly int Y;
    public readonly Direction Facing;
    public readonly int Speed;

    /// How far this end can tunnel, in tiles. Carried per end rather than
    /// looked up, because it is a property of the tier that was placed and a
    /// save must not re-derive it from whatever the ladder says later.
    public readonly int Reach;

    /// Entrance takes items down; exit brings them back up.
    public readonly bool IsEntrance;

    public PlacedUnderground(int x, int y, Direction facing, int speed, int reach, bool isEntrance)
    {
        X = x;
        Y = y;
        Facing = facing;
        Speed = speed;
        Reach = reach;
        IsEntrance = isEntrance;
    }

    public (int X, int Y) Ahead
    {
        get
        {
            var (dx, dy) = Directions.Delta(Facing);
            return (X + dx, Y + dy);
        }
    }
}

/// A splitter: one tile, taking whatever is fed into it and alternating between
/// two outputs.
///
/// One tile rather than Factorio's two. A machine's footprint here is square
/// and its size is its throughput dial (ADR 0007); a 1x2 building would be the
/// only non-square thing on the map and would need its own occupancy, ghost and
/// rotation rules for no simulation benefit. See ADR 0018 for what that costs.
public readonly struct PlacedSplitter
{
    public readonly int X;
    public readonly int Y;
    public readonly Direction Facing;

    public PlacedSplitter(int x, int y, Direction facing)
    {
        X = x;
        Y = y;
        Facing = facing;
    }

    /// The two tiles it feeds: straight on, and to its right. Rotating reaches
    /// every pair of adjacent directions, so "which side does the branch leave
    /// by" is a real choice made by the facing rather than a fixed handedness.
    public (int X, int Y) Straight
    {
        get
        {
            var (dx, dy) = Directions.Delta(Facing);
            return (X + dx, Y + dy);
        }
    }

    public (int X, int Y) Branch
    {
        get
        {
            var (dx, dy) = Directions.Delta(Directions.Rotate(Facing));
            return (X + dx, Y + dy);
        }
    }
}

/// Why an underground belt could not be placed.
public enum TunnelRefusal
{
    None,
    Blocked,

    /// There is an unpaired entrance behind it, aligned and facing the same
    /// way, but further than this tier can tunnel. Refused rather than quietly
    /// placed as a second entrance: a player who has just walked out from an
    /// entrance is completing a tunnel, and silently giving them a broken line
    /// plus a spare hole teaches them nothing about the span.
    TooFar,
}

/// Belts and inserters as things on the map.
///
/// `BeltNetwork` moves items beautifully and knows nothing about where anything
/// is: a segment has a tile *count*, not tile *positions*. That is the same gap
/// fluids had before ADR 0012 -- a system that works perfectly and that a player
/// cannot build. This is the tile layer over it: the player places single tiles,
/// and the runs those tiles form are compiled into segments.
///
/// Compiling rather than one-segment-per-tile is the point. A lane advances in
/// O(1) however long it is, so a 200-tile straight run must stay *one* segment;
/// a segment per tile would throw away the entire reason the lane is built the
/// way it is.
public sealed class BeltMap
{
    private readonly List<PlacedBelt> _belts = new();
    private readonly List<PlacedInserter> _inserters = new();
    private readonly List<PlacedUnderground> _undergrounds = new();
    private readonly List<PlacedSplitter> _splitters = new();
    private readonly Dictionary<long, int> _beltAt = new();
    private readonly Dictionary<long, int> _inserterAt = new();
    private readonly Dictionary<long, int> _undergroundAt = new();
    private readonly Dictionary<long, int> _splitterAt = new();

    /// Underground end index -> the end it is paired with, or -1. Rebuild
    /// output, like the segment list.
    private int[] _tunnelPartner = Array.Empty<int>();

    /// Tunnel tiles -> segment and distance from its exit. Kept apart from
    /// `_tileSegment` for two reasons: a buried tile can have a surface belt
    /// laid straight over it, and both need to resolve to their own segment;
    /// and an inserter must not be able to reach into a hole in the ground.
    private readonly Dictionary<long, (int Segment, int DistanceFromExit)> _tunnelSegment = new();

    /// Belt tile -> the segment it compiled into, and how far its exit edge is
    /// from that segment's exit. Both are rebuild output.
    private readonly Dictionary<long, (int Segment, int DistanceFromExit)> _tileSegment = new();

    /// Starts clean: a world with no placed belts must never touch the segment
    /// list, or every world that builds segments by hand -- the tests, the
    /// placeholder factory, an older save -- would have them wiped on its first
    /// tick.
    /// Each compiled segment's tiles, entry first and exit last. Rendering needs
    /// it to put an item riding at a given distance from a segment's exit onto
    /// the tile it is actually crossing.
    private readonly List<List<PlacedBelt>> _segmentTiles = new();

    private bool _dirty;

    public IReadOnlyList<PlacedBelt> Belts => _belts;
    public IReadOnlyList<PlacedInserter> Inserters => _inserters;
    public IReadOnlyList<PlacedUnderground> Undergrounds => _undergrounds;
    public IReadOnlyList<PlacedSplitter> Splitters => _splitters;

    /// The end this one tunnels to, or -1 when it has none. Only meaningful
    /// after a rebuild.
    public int PartnerOf(int end)
        => end >= 0 && end < _tunnelPartner.Length ? _tunnelPartner[end] : -1;

    /// The tiles a compiled segment runs across, entry first.
    public IReadOnlyList<PlacedBelt> TilesOfSegment(int segment)
        => segment >= 0 && segment < _segmentTiles.Count
            ? _segmentTiles[segment]
            : Array.Empty<PlacedBelt>();

    public int SegmentCount => _segmentTiles.Count;

    /// Bumped on every rebuild, so renderers and caches can tell when the
    /// compiled topology changed without diffing it.
    public int Version { get; private set; }

    /// Items destroyed by removing the belt they were riding.
    ///
    /// Counted rather than silently dropped, for the same reason fluids count
    /// what mixing voids: a player who cannot see the loss will never work out
    /// where their ore went.
    public int SpilledOnRemoval { get; private set; }

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    public bool HasBeltAt(int x, int y) => _beltAt.ContainsKey(Key(x, y));
    public bool HasInserterAt(int x, int y) => _inserterAt.ContainsKey(Key(x, y));
    public bool HasUndergroundAt(int x, int y) => _undergroundAt.ContainsKey(Key(x, y));
    public bool HasSplitterAt(int x, int y) => _splitterAt.ContainsKey(Key(x, y));

    public bool HasAnythingAt(int x, int y)
        => HasBeltAt(x, y) || HasInserterAt(x, y)
           || HasUndergroundAt(x, y) || HasSplitterAt(x, y);

    /// Whether a tunnel runs under a tile without surfacing on it. A machine,
    /// a pipe or another belt may sit on such a tile -- passing under things is
    /// the entire point -- so this is not part of `HasAnythingAt`. Rendering
    /// asks it so that items in transit underground are not drawn on top of
    /// whatever was built over them.
    public bool IsBuried(int x, int y)
        => !HasBeltAt(x, y) && _tunnelSegment.ContainsKey(Key(x, y))
           && !HasUndergroundAt(x, y);

    public bool PlaceBelt(int x, int y, Direction facing, int speed = BeltUnits.SpeedBasic)
    {
        if (HasAnythingAt(x, y)) return false;

        _beltAt[Key(x, y)] = _belts.Count;
        _belts.Add(new PlacedBelt(x, y, facing, speed));
        _dirty = true;
        return true;
    }

    /// Places one end of an underground belt.
    ///
    /// The role is placement order: an end placed within reach *behind* an
    /// unpaired entrance that faces the same way completes it as the exit, and
    /// anything else is a new entrance. That is the gesture -- place, walk,
    /// place -- and it needs no second key.
    public bool PlaceUnderground(int x, int y, Direction facing, int speed, int reach,
                                 out TunnelRefusal refusal)
    {
        refusal = TunnelRefusal.None;

        if (HasAnythingAt(x, y))
        {
            refusal = TunnelRefusal.Blocked;
            return false;
        }

        var (dx, dy) = Directions.Delta(facing);
        var isEntrance = true;

        // Look back along the line for the entrance this would complete. The
        // search runs past `reach` so that overshooting can be reported as
        // overshooting rather than silently becoming a second entrance.
        for (var d = 1; d <= reach * 2; d++)
        {
            if (!_undergroundAt.TryGetValue(Key(x - dx * d, y - dy * d), out var index))
                continue;

            var candidate = _undergrounds[index];
            if (candidate.Facing != facing || !candidate.IsEntrance) continue;
            if (!IsUnpaired(index)) continue;

            if (d > Math.Min(reach, candidate.Reach))
            {
                refusal = TunnelRefusal.TooFar;
                return false;
            }

            isEntrance = false;
            break;
        }

        _undergroundAt[Key(x, y)] = _undergrounds.Count;
        _undergrounds.Add(new PlacedUnderground(x, y, facing, speed, reach, isEntrance));
        _dirty = true;
        return true;
    }

    /// Whether an end has no partner *as the map currently stands*. Placement
    /// asks before the rebuild that would recompute pairing, so it pairs on the
    /// same rule the compiler uses rather than on stale rebuild output.
    private bool IsUnpaired(int end)
    {
        var entrance = _undergrounds[end];
        if (!entrance.IsEntrance) return false;

        var (dx, dy) = Directions.Delta(entrance.Facing);
        for (var d = 1; d <= entrance.Reach; d++)
        {
            if (!_undergroundAt.TryGetValue(
                    Key(entrance.X + dx * d, entrance.Y + dy * d), out var index))
                continue;

            var other = _undergrounds[index];
            if (other.Facing == entrance.Facing && !other.IsEntrance) return false;
        }

        return true;
    }

    /// Puts an end back with the role it was saved with, bypassing the
    /// order-based rule. Save surface only: see the comment on the save.
    public bool RestoreUnderground(int x, int y, Direction facing, int speed, int reach,
                                   bool isEntrance)
    {
        if (HasAnythingAt(x, y)) return false;

        _undergroundAt[Key(x, y)] = _undergrounds.Count;
        _undergrounds.Add(new PlacedUnderground(x, y, facing, speed, reach, isEntrance));
        _dirty = true;
        return true;
    }

    public bool PlaceSplitter(int x, int y, Direction facing)
    {
        if (HasAnythingAt(x, y)) return false;

        _splitterAt[Key(x, y)] = _splitters.Count;
        _splitters.Add(new PlacedSplitter(x, y, facing));
        _dirty = true;
        return true;
    }

    public bool PlaceInserter(int x, int y, Direction facing,
                              int swingTicks = 20, int stackSize = 1)
    {
        if (HasAnythingAt(x, y)) return false;

        _inserterAt[Key(x, y)] = _inserters.Count;
        _inserters.Add(new PlacedInserter(x, y, facing, swingTicks, stackSize));
        _dirty = true;
        return true;
    }

    /// Takes whatever the player placed on a tile off the map.
    ///
    /// One entry point for all four kinds rather than four: the caller has a
    /// tile and wants it clear, and a "which of these is it" switch at every
    /// call site is four chances to forget splitters. Returns false when the
    /// tile holds none of them.
    ///
    /// The topology is not repaired here. Marking the map dirty makes the next
    /// rebuild recompile every run and re-pair every tunnel from the tiles that
    /// are left, which is the same path placing a belt takes -- so a cut in the
    /// middle of a run splits it, and pulling one end of a tunnel frees its
    /// partner to be paired again.
    public bool Remove(int x, int y)
    {
        var key = Key(x, y);

        if (_beltAt.TryGetValue(key, out var belt))
            return SwapRemove(_belts, _beltAt, belt, b => Key(b.X, b.Y));
        if (_inserterAt.TryGetValue(key, out var inserter))
            return SwapRemove(_inserters, _inserterAt, inserter, i => Key(i.X, i.Y));
        if (_undergroundAt.TryGetValue(key, out var end))
            return SwapRemove(_undergrounds, _undergroundAt, end, u => Key(u.X, u.Y));
        if (_splitterAt.TryGetValue(key, out var splitter))
            return SwapRemove(_splitters, _splitterAt, splitter, p => Key(p.X, p.Y));

        return false;
    }

    private bool SwapRemove<T>(List<T> items, Dictionary<long, int> byTile, int index,
                               Func<T, long> keyOf)
    {
        var last = items.Count - 1;
        byTile.Remove(keyOf(items[index]));
        items[index] = items[last];
        items.RemoveAt(last);
        if (index != last) byTile[keyOf(items[index])] = index;
        _dirty = true;
        return true;
    }

    /// Every tile whose contents removing this one would strand: the tile
    /// itself, plus the buried span of a tunnel when one of its ends is being
    /// pulled up.
    ///
    /// The buried span is the case that is easy to miss. Items inside a tunnel
    /// are not standing on any tile the player can see or click, so a removal
    /// that only looked at the clicked tile would silently destroy everything
    /// in flight underground and count it as spillage.
    public List<(int X, int Y)> TilesEmptiedByRemoving(int x, int y)
    {
        var tiles = new List<(int X, int Y)> { (x, y) };

        if (!_undergroundAt.TryGetValue(Key(x, y), out var end)) return tiles;

        var partner = PartnerOf(end);
        if (partner < 0 || partner >= _undergrounds.Count) return tiles;

        var a = _undergrounds[end];
        var b = _undergrounds[partner];
        var (dx, dy) = Directions.Delta(a.Facing);
        var steps = Math.Abs((b.X - a.X) * dx + (b.Y - a.Y) * dy);

        // Between the ends, exclusive: the partner end is a tile of its own and
        // is not being removed, so what is standing on it stays where it is.
        var sign = (b.X - a.X) * dx + (b.Y - a.Y) * dy >= 0 ? 1 : -1;
        for (var d = 1; d < steps; d++)
            tiles.Add((a.X + dx * d * sign, a.Y + dy * d * sign));

        return tiles;
    }

    /// Lifts every item riding the given tiles off the belts and returns them,
    /// leaving everything else exactly where it was standing.
    ///
    /// Built on the same snapshot/restore the rebuild uses rather than a second
    /// path into the lanes: a lane stores gaps relative to the item ahead, so
    /// plucking one item out of the middle by hand means recomputing the gap of
    /// the one behind it, which is precisely what `Restore` already does.
    public List<ItemId> TakeItemsOn(BeltNetwork network, IReadOnlyList<(int X, int Y)> tiles)
    {
        var taken = new List<ItemId>();
        if (tiles.Count == 0) return taken;

        var wanted = new HashSet<long>();
        foreach (var (x, y) in tiles) wanted.Add(Key(x, y));

        var carried = Snapshot(network);
        var kept = new List<Riding>();
        foreach (var riding in carried)
        {
            if (wanted.Contains(Key(riding.X, riding.Y))) taken.Add(riding.Item);
            else kept.Add(riding);
        }

        if (taken.Count == 0) return taken;

        // Emptied first: `Restore` only writes the lanes it has items for, so a
        // lane whose every item was just picked up would otherwise keep them.
        foreach (var segment in network.Segments)
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                segment.LaneAt(lane).Restore(Array.Empty<ItemId>(), Array.Empty<int>());

        Restore(network, kept);

        // Deterministic order, so two identical worlds hand the two identical
        // players their items in the same order and the saves stay identical.
        taken.Sort((a, b) => a.Value.CompareTo(b.Value));
        return taken;
    }

    /// The compiled segment carrying a tile, or -1. Rendering and inserter
    /// wiring both ask this; it is only meaningful after a rebuild.
    public int SegmentAt(int x, int y)
    {
        if (_tileSegment.TryGetValue(Key(x, y), out var found)) return found.Segment;
        return _tunnelSegment.TryGetValue(Key(x, y), out var tunnel) ? tunnel.Segment : -1;
    }

    public void MarkDirty() => _dirty = true;

    /// Recompiles tiles into segments, if anything moved.
    ///
    /// `resolve` answers what is on a tile that is not a belt -- a machine, a
    /// miner, or nothing. The map does not know about those and must not: it
    /// would have to depend on the whole world to place a belt.
    public void RebuildIfDirty(BeltNetwork network, Func<int, int, Endpoint> resolve)
    {
        if (!_dirty) return;
        Rebuild(network, resolve);
    }

    private void Rebuild(BeltNetwork network, Func<int, int, Endpoint> resolve)
    {
        // Compiling replaces the whole segment list, so a hand-built segment
        // would disappear the moment the player placed a belt tile. Say so
        // rather than losing someone's factory quietly.
        if (network.HasHandBuiltSegments)
            throw new InvalidOperationException(
                "this world has hand-built belt segments, which placed belt tiles would replace");

        var carried = Snapshot(network);

        network.ClearSegments();
        _tileSegment.Clear();
        _tunnelSegment.Clear();
        _segmentTiles.Clear();
        PairTunnels();

        // Where segments must break. An inserter's tiles are boundaries so that
        // it takes from and gives to the exact tile it faces: an item handed to
        // the "back" of a long run would appear at the far end of the belt, and
        // one taken from its exit would come from wherever the run happened to
        // end rather than from the tile beside the arm.
        var takesFrom = new HashSet<long>();
        var givesTo = new HashSet<long>();
        foreach (var inserter in _inserters)
        {
            var behind = inserter.Behind;
            var ahead = inserter.Ahead;
            if (HasBeltAt(behind.X, behind.Y)) takesFrom.Add(Key(behind.X, behind.Y));
            if (HasBeltAt(ahead.X, ahead.Y)) givesTo.Add(Key(ahead.X, ahead.Y));
        }

        // Compile each run. A tile starts a segment when nothing flows into it
        // as part of the same straight line.
        var runs = new List<List<PlacedBelt>>();
        var claimed = new HashSet<long>();

        foreach (var belt in _belts)
        {
            if (claimed.Contains(Key(belt.X, belt.Y))) continue;
            if (!StartsARun(belt, takesFrom, givesTo)) continue;

            var run = new List<PlacedBelt>();
            var cursor = belt;

            while (true)
            {
                run.Add(cursor);
                claimed.Add(Key(cursor.X, cursor.Y));

                // The run ends here if an inserter reads this tile: its exit has
                // to be this tile's edge.
                if (takesFrom.Contains(Key(cursor.X, cursor.Y))) break;

                var (nx, ny) = cursor.Ahead;
                if (!_beltAt.TryGetValue(Key(nx, ny), out var nextIndex)) break;

                var next = _belts[nextIndex];
                if (next.Facing != cursor.Facing) break;          // a corner
                if (next.Speed != cursor.Speed) break;            // a speed change
                if (FeederCount(next.X, next.Y) != 1) break;      // a merge point
                if (givesTo.Contains(Key(nx, ny))) break;         // an inserter drops here
                if (claimed.Contains(Key(nx, ny))) break;         // a loop

                cursor = next;
            }

            runs.Add(run);
        }

        // A belt in a closed loop is reached by nothing that "starts" a run, so
        // it would compile to no segment at all and quietly stop working. Give
        // every unclaimed tile its own segment rather than dropping it.
        foreach (var belt in _belts)
            if (!claimed.Contains(Key(belt.X, belt.Y)))
            {
                claimed.Add(Key(belt.X, belt.Y));
                runs.Add(new List<PlacedBelt> { belt });
            }

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var segment = network.AddCompiledSegment(run.Count, run[0].Speed);
            _segmentTiles.Add(run);

            // The run is entry-first, so the last tile's exit edge is the
            // segment's exit: distance zero.
            for (var t = 0; t < run.Count; t++)
            {
                var fromExit = (run.Count - 1 - t) * BeltUnits.Tile;
                _tileSegment[Key(run[t].X, run[t].Y)] = (segment, fromExit);
            }
        }

        // Tunnels next. Each end is its own segment: an entrance has to end a
        // run anyway (what follows it is not on the surface), and an exit has
        // to start one. The entrance's segment is as long as the span it
        // covers -- entrance tile plus every buried tile -- so an item takes
        // exactly as long to cross a tunnel as it would to cross the same
        // number of surface tiles. A tunnel that skipped the travel would be a
        // free speed-up rather than a way past an obstacle.
        var endSegment = new int[_undergrounds.Count];

        for (var i = 0; i < _undergrounds.Count; i++)
        {
            var end = _undergrounds[i];
            var span = 1;

            if (end.IsEntrance && _tunnelPartner[i] >= 0)
            {
                var exit = _undergrounds[_tunnelPartner[i]];
                span = Math.Abs(exit.X - end.X) + Math.Abs(exit.Y - end.Y);
            }

            var segment = network.AddCompiledSegment(span, end.Speed);
            endSegment[i] = segment;

            var (dx, dy) = Directions.Delta(end.Facing);
            var tiles = new List<PlacedBelt>(span);
            for (var t = 0; t < span; t++)
            {
                var x = end.X + dx * t;
                var y = end.Y + dy * t;
                tiles.Add(new PlacedBelt(x, y, end.Facing, end.Speed));
                _tunnelSegment[Key(x, y)] = (segment, (span - 1 - t) * BeltUnits.Tile);
            }

            _segmentTiles.Add(tiles);
        }

        // Splitters are placed things, not compiled ones, so they are reused
        // across a rebuild rather than remade -- otherwise everything buffered
        // in one would vanish the moment the player extended a belt beside it.
        // Two per tile, one per lane: see WireSplitters.
        while (network.Splitters.Count < _splitters.Count * BeltSegment.LaneCount)
            network.AddSplitter(Endpoint.None, Endpoint.None);

        // Now that every tile knows its segment, wire each exit onward.
        foreach (var run in runs)
        {
            var last = run[^1];
            WireOnward(network, resolve, _tileSegment[Key(last.X, last.Y)].Segment, last.Ahead);
        }

        for (var i = 0; i < _undergrounds.Count; i++)
        {
            var end = _undergrounds[i];
            var partner = _tunnelPartner[i];

            // A paired entrance hands straight to its exit. An unpaired end --
            // one whose partner was never placed -- is simply a one-tile belt
            // that happens to look like a hole, which is the honest thing for
            // it to be rather than a line that silently eats items.
            if (end.IsEntrance && partner >= 0)
                network.LinkBelts(endSegment[i], endSegment[partner]);
            else
                WireOnward(network, resolve, endSegment[i], end.Ahead);
        }

        WireSplitters(network, resolve);
        WireInserters(network, resolve);
        Restore(network, carried);

        _dirty = false;
        Version++;
    }

    /// Whether a tile is the head of a run rather than the middle of one.
    private bool StartsARun(in PlacedBelt belt, HashSet<long> takesFrom, HashSet<long> givesTo)
    {
        if (givesTo.Contains(Key(belt.X, belt.Y))) return true;
        if (FeederCount(belt.X, belt.Y) != 1) return true;

        var feeder = SoleFeeder(belt);
        if (feeder is null) return true;
        if (feeder.Value.Facing != belt.Facing) return true;
        if (feeder.Value.Speed != belt.Speed) return true;

        // A tile whose feeder is read by an inserter cannot continue that run,
        // because the run has to end at the feeder.
        return takesFrom.Contains(Key(feeder.Value.X, feeder.Value.Y));
    }

    private static readonly Direction[] AllDirections =
        { Direction.East, Direction.South, Direction.West, Direction.North };

    /// How many things point into a tile. More than one is a merge, which has
    /// to start its own segment so both feeders can hand on independently.
    ///
    /// A tunnel exit and a splitter feed a tile just as a belt does, and both
    /// hand items to a segment's *entry*. Counting only belts here let a tile
    /// fed by a belt AND a tunnel exit continue the belt's run, which put the
    /// tunnel's items in at the far back of that run instead of at this tile.
    private int FeederCount(int x, int y)
    {
        var count = 0;
        foreach (var direction in AllDirections)
        {
            var (dx, dy) = Directions.Delta(direction);
            if (Feeds(x - dx, y - dy, x, y, direction)) count++;
        }

        return count;
    }

    /// Whether the thing on (fx,fy) hands items to (x,y). `direction` is the
    /// way from the feeder to the fed tile.
    private bool Feeds(int fx, int fy, int x, int y, Direction direction)
    {
        if (_beltAt.TryGetValue(Key(fx, fy), out var belt) && _belts[belt].Facing == direction)
            return true;

        if (_undergroundAt.TryGetValue(Key(fx, fy), out var end)
            && !_undergrounds[end].IsEntrance && _undergrounds[end].Facing == direction)
            return true;

        if (_splitterAt.TryGetValue(Key(fx, fy), out var splitter))
        {
            var placed = _splitters[splitter];
            if (placed.Straight == (x, y) || placed.Branch == (x, y)) return true;
        }

        return false;
    }

    /// The belt feeding a tile, when its one feeder is a belt. A tile fed by a
    /// tunnel exit or a splitter has no belt feeder and therefore starts a run.
    private PlacedBelt? SoleFeeder(in PlacedBelt belt)
    {
        foreach (var direction in AllDirections)
        {
            var (dx, dy) = Directions.Delta(direction);
            var (nx, ny) = (belt.X - dx, belt.Y - dy);
            if (_beltAt.TryGetValue(Key(nx, ny), out var index) && _belts[index].Facing == direction)
                return _belts[index];
        }

        return null;
    }

    /// Pairs every entrance with the nearest unclaimed exit ahead of it that
    /// faces the same way and is within both ends' reach.
    ///
    /// Nearest-first is what makes overlapping tunnels behave: an inner pair
    /// claims its own exit before an outer entrance can reach past it, so a
    /// tunnel never swallows another one's exit. Entrances are walked in
    /// placement order, which is stable across a save.
    private void PairTunnels()
    {
        if (_tunnelPartner.Length != _undergrounds.Count)
            _tunnelPartner = new int[_undergrounds.Count];
        Array.Fill(_tunnelPartner, -1);

        for (var i = 0; i < _undergrounds.Count; i++)
        {
            var entrance = _undergrounds[i];
            if (!entrance.IsEntrance || _tunnelPartner[i] >= 0) continue;

            var (dx, dy) = Directions.Delta(entrance.Facing);
            for (var d = 1; d <= entrance.Reach; d++)
            {
                if (!_undergroundAt.TryGetValue(
                        Key(entrance.X + dx * d, entrance.Y + dy * d), out var j))
                    continue;

                var exit = _undergrounds[j];
                if (exit.IsEntrance || exit.Facing != entrance.Facing) continue;
                if (_tunnelPartner[j] >= 0 || d > exit.Reach) continue;

                _tunnelPartner[i] = j;
                _tunnelPartner[j] = i;
                break;
            }
        }
    }

    /// Points every inserter at whatever is now beside it. Segment numbering
    /// changes on every rebuild, so an inserter that kept its old endpoints
    /// would quietly start feeding a different belt.
    private void WireInserters(BeltNetwork network, Func<int, int, Endpoint> resolve)
    {
        for (var i = 0; i < _inserters.Count; i++)
        {
            var inserter = _inserters[i];
            var behind = inserter.Behind;
            var ahead = inserter.Ahead;

            var source = EndpointAt(behind.X, behind.Y, resolve);
            var target = EndpointAt(ahead.X, ahead.Y, resolve);

            if (i < network.Inserters.Count)
                network.Inserters[i].Retarget(source, target);
            else
                network.AddInserter(source, target, inserter.SwingTicks, inserter.StackSize);
        }
    }

    /// Points a segment's exit at whatever is on the tile ahead of it.
    private void WireOnward(BeltNetwork network, Func<int, int, Endpoint> resolve,
                            int segment, (int X, int Y) ahead)
    {
        var (nx, ny) = ahead;

        if (_beltAt.ContainsKey(Key(nx, ny)))
        {
            var into = _tileSegment[Key(nx, ny)].Segment;
            if (into != segment) network.LinkBelts(segment, into);
            return;
        }

        // A tunnel entrance takes a belt's items down; a tunnel exit does not
        // accept anything from the surface, so a belt pointing at one backs up.
        if (_undergroundAt.TryGetValue(Key(nx, ny), out var end)
            && _undergrounds[end].IsEntrance)
        {
            var into = _tunnelSegment[Key(nx, ny)].Segment;
            if (into != segment) network.LinkBelts(segment, into);
            return;
        }

        if (_splitterAt.TryGetValue(Key(nx, ny), out var splitter))
        {
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                network.SetOutput(segment, lane,
                                  Endpoint.Splitter(SplitterIndex(splitter, lane)));
            return;
        }

        // Belts do not feed machines directly -- that is an inserter's job,
        // as in Factorio. A belt pointing at a machine simply backs up.
        var target = resolve(nx, ny);
        if (target.Kind == EndpointKind.Splitter)
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                network.SetOutput(segment, lane, target);
    }

    /// Which network splitter carries one lane of one placed splitter.
    ///
    /// Two per tile, because `Splitter` balances items and knows nothing about
    /// lanes. Merging both lanes into one buffer would halve a splitter's
    /// throughput; splitting each lane independently keeps the line full and
    /// keeps items on the side they were already riding. The cost is that this
    /// splitter does not lane-balance the way Factorio's does -- a line with
    /// one full lane and one empty one comes out of it just as lopsided.
    private static int SplitterIndex(int placed, int lane)
        => placed * BeltSegment.LaneCount + lane;

    /// Points every splitter at the two tiles it feeds, lane for lane.
    private void WireSplitters(BeltNetwork network, Func<int, int, Endpoint> resolve)
    {
        for (var i = 0; i < _splitters.Count; i++)
        {
            var placed = _splitters[i];

            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var splitter = network.Splitters[SplitterIndex(i, lane)];
                splitter.Outputs[0] = OutputTo(placed.Straight, lane, resolve);
                splitter.Outputs[1] = OutputTo(placed.Branch, lane, resolve);
            }
        }
    }

    /// What a splitter side hands to: the segment starting on that tile, or
    /// whatever the world says is there.
    private Endpoint OutputTo((int X, int Y) tile, int lane, Func<int, int, Endpoint> resolve)
    {
        if (_tileSegment.TryGetValue(Key(tile.X, tile.Y), out var belt))
            return Endpoint.Belt(belt.Segment, lane);

        if (_undergroundAt.TryGetValue(Key(tile.X, tile.Y), out var end)
            && _undergrounds[end].IsEntrance)
            return Endpoint.Belt(_tunnelSegment[Key(tile.X, tile.Y)].Segment, lane);

        return resolve(tile.X, tile.Y);
    }

    /// A belt tile resolves to its own segment, so an inserter reads and writes
    /// the exact tile it faces. Both lanes are not addressable from one tile, so
    /// lane 0 is the inserter's side by convention.
    private Endpoint EndpointAt(int x, int y, Func<int, int, Endpoint> resolve)
    {
        if (_tileSegment.TryGetValue(Key(x, y), out var found))
            return Endpoint.Belt(found.Segment, 0);

        // An inserter may load a splitter, on the same lane-0 convention. It
        // may NOT reach into a tunnel end: that is a hole in the ground rather
        // than a belt deck, and an arm reaching into an entrance would be
        // taking from the far side of the span it faces.
        if (_splitterAt.TryGetValue(Key(x, y), out var splitter))
            return Endpoint.Splitter(SplitterIndex(splitter, 0));

        return resolve(x, y);
    }

    /// `Tunnel` says which of the two tile maps this item was standing on. A
    /// buried tile and a surface belt can share coordinates -- a tunnel passing
    /// under a belt is the point of tunnels -- so the position alone does not
    /// say which segment an item should go back onto.
    private readonly record struct Riding(int X, int Y, int WithinTile, int Lane,
                                          ItemId Item, bool Tunnel);

    /// Where every item on every belt is, in world terms, before the segments
    /// are thrown away. Without this, extending a working belt would destroy
    /// everything already on it -- which is what a player does constantly.
    private List<Riding> Snapshot(BeltNetwork network)
    {
        var carried = new List<Riding>();
        if (_tileSegment.Count == 0 && _tunnelSegment.Count == 0) return carried;

        // Segment -> distance-from-exit -> the tile at that distance.
        var tiles = new Dictionary<(int Segment, int FromExit), (int X, int Y, bool Tunnel)>();
        foreach (var (key, value) in _tileSegment)
            tiles[(value.Segment, value.DistanceFromExit)] =
                ((int)(key >> 32), (int)(uint)key, false);
        foreach (var (key, value) in _tunnelSegment)
            tiles[(value.Segment, value.DistanceFromExit)] =
                ((int)(key >> 32), (int)(uint)key, true);

        for (var segment = 0; segment < network.Segments.Count; segment++)
        {
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var source = network.Segments[segment].LaneAt(lane);
                for (var i = 0; i < source.Count; i++)
                {
                    var fromExit = source.PositionOf(i);
                    var within = fromExit % BeltUnits.Tile;

                    if (!tiles.TryGetValue((segment, fromExit - within), out var tile))
                        continue;

                    carried.Add(new Riding(tile.X, tile.Y, within, lane,
                                           source.ItemAt(i), tile.Tunnel));
                }
            }
        }

        return carried;
    }

    /// Puts every carried item back where it was standing. An item whose tile no
    /// longer has a belt has nowhere to go: it is destroyed and counted.
    private void Restore(BeltNetwork network, List<Riding> carried)
    {
        if (carried.Count == 0) return;

        var byLane = new Dictionary<(int Segment, int Lane), List<(int Position, ItemId Item)>>();

        foreach (var riding in carried)
        {
            var map = riding.Tunnel ? _tunnelSegment : _tileSegment;
            if (!map.TryGetValue(Key(riding.X, riding.Y), out var found))
            {
                SpilledOnRemoval++;
                continue;
            }

            var key = (found.Segment, riding.Lane);
            if (!byLane.TryGetValue(key, out var list))
                byLane[key] = list = new List<(int, ItemId)>();

            list.Add((found.DistanceFromExit + riding.WithinTile, riding.Item));
        }

        foreach (var ((segment, lane), items) in byLane)
        {
            items.Sort((a, b) => a.Position.CompareTo(b.Position));

            var target = network.Segments[segment].LaneAt(lane);
            var restoredItems = new List<ItemId>();
            var gaps = new List<int>();
            var previous = 0;

            for (var i = 0; i < items.Count; i++)
            {
                if (restoredItems.Count == target.Capacity)
                {
                    // The run shrank under them. Better to lose the overflow
                    // visibly than to silently overlap items on a lane.
                    SpilledOnRemoval++;
                    continue;
                }

                var position = Math.Min(items[i].Position, target.Length - BeltUnits.ItemSpacing);
                var gap = i == 0 ? position : position - previous - BeltUnits.ItemSpacing;
                if (gap < 0) gap = 0;

                restoredItems.Add(items[i].Item);
                gaps.Add(gap);
                previous = i == 0 ? gap : previous + BeltUnits.ItemSpacing + gap;
            }

            target.Restore(restoredItems, gaps);
        }
    }
}
