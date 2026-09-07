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
    private readonly Dictionary<long, int> _beltAt = new();
    private readonly Dictionary<long, int> _inserterAt = new();

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
    public bool HasAnythingAt(int x, int y) => HasBeltAt(x, y) || HasInserterAt(x, y);

    public bool PlaceBelt(int x, int y, Direction facing, int speed = BeltUnits.SpeedBasic)
    {
        if (HasAnythingAt(x, y)) return false;

        _beltAt[Key(x, y)] = _belts.Count;
        _belts.Add(new PlacedBelt(x, y, facing, speed));
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

    /// The compiled segment carrying a tile, or -1. Rendering and inserter
    /// wiring both ask this; it is only meaningful after a rebuild.
    public int SegmentAt(int x, int y)
        => _tileSegment.TryGetValue(Key(x, y), out var found) ? found.Segment : -1;

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
        _segmentTiles.Clear();

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
                if (FeederCount(next) != 1) break;                // a merge point
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

        // Now that every tile knows its segment, wire each run's exit onward.
        foreach (var run in runs)
        {
            var last = run[^1];
            var segment = _tileSegment[Key(last.X, last.Y)].Segment;
            var (nx, ny) = last.Ahead;

            if (_beltAt.ContainsKey(Key(nx, ny)))
            {
                var into = _tileSegment[Key(nx, ny)].Segment;
                if (into != segment) network.LinkBelts(segment, into);
                continue;
            }

            // Belts do not feed machines directly -- that is an inserter's job,
            // as in Factorio. A belt pointing at a machine simply backs up.
            var target = resolve(nx, ny);
            if (target.Kind == EndpointKind.Splitter)
                for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                    network.SetOutput(segment, lane, target);
        }

        WireInserters(network, resolve);
        Restore(network, carried);

        _dirty = false;
        Version++;
    }

    /// Whether a tile is the head of a run rather than the middle of one.
    private bool StartsARun(in PlacedBelt belt, HashSet<long> takesFrom, HashSet<long> givesTo)
    {
        if (givesTo.Contains(Key(belt.X, belt.Y))) return true;
        if (FeederCount(belt) != 1) return true;

        var feeder = SoleFeeder(belt);
        if (feeder is null) return true;
        if (feeder.Value.Facing != belt.Facing) return true;
        if (feeder.Value.Speed != belt.Speed) return true;

        // A tile whose feeder is read by an inserter cannot continue that run,
        // because the run has to end at the feeder.
        return takesFrom.Contains(Key(feeder.Value.X, feeder.Value.Y));
    }

    /// How many belts point into a tile. More than one is a merge, which has to
    /// start its own segment so both feeders can hand on independently.
    private int FeederCount(in PlacedBelt belt)
    {
        var count = 0;
        foreach (var direction in new[] { Direction.East, Direction.South, Direction.West, Direction.North })
        {
            var (dx, dy) = Directions.Delta(direction);
            var (nx, ny) = (belt.X - dx, belt.Y - dy);
            if (_beltAt.TryGetValue(Key(nx, ny), out var index) && _belts[index].Facing == direction)
                count++;
        }

        return count;
    }

    private PlacedBelt? SoleFeeder(in PlacedBelt belt)
    {
        foreach (var direction in new[] { Direction.East, Direction.South, Direction.West, Direction.North })
        {
            var (dx, dy) = Directions.Delta(direction);
            var (nx, ny) = (belt.X - dx, belt.Y - dy);
            if (_beltAt.TryGetValue(Key(nx, ny), out var index) && _belts[index].Facing == direction)
                return _belts[index];
        }

        return null;
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

    /// A belt tile resolves to its own segment, so an inserter reads and writes
    /// the exact tile it faces. Both lanes are not addressable from one tile, so
    /// lane 0 is the inserter's side by convention.
    private Endpoint EndpointAt(int x, int y, Func<int, int, Endpoint> resolve)
        => _tileSegment.TryGetValue(Key(x, y), out var found)
            ? Endpoint.Belt(found.Segment, 0)
            : resolve(x, y);

    private readonly record struct Riding(int X, int Y, int WithinTile, int Lane, ItemId Item);

    /// Where every item on every belt is, in world terms, before the segments
    /// are thrown away. Without this, extending a working belt would destroy
    /// everything already on it -- which is what a player does constantly.
    private List<Riding> Snapshot(BeltNetwork network)
    {
        var carried = new List<Riding>();
        if (_tileSegment.Count == 0) return carried;

        // Segment -> its tiles, exit last.
        var tilesBySegment = new Dictionary<int, List<(int X, int Y, int FromExit)>>();
        foreach (var (key, value) in _tileSegment)
        {
            var x = (int)(key >> 32);
            var y = (int)(uint)key;
            if (!tilesBySegment.TryGetValue(value.Segment, out var list))
                tilesBySegment[value.Segment] = list = new List<(int, int, int)>();
            list.Add((x, y, value.DistanceFromExit));
        }

        foreach (var (segment, tiles) in tilesBySegment)
        {
            if (segment >= network.Segments.Count) continue;

            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var source = network.Segments[segment].LaneAt(lane);
                for (var i = 0; i < source.Count; i++)
                {
                    var fromExit = source.PositionOf(i);
                    var tileIndex = fromExit / BeltUnits.Tile;
                    var within = fromExit % BeltUnits.Tile;

                    var tile = tiles.FirstOrDefault(t => t.FromExit == tileIndex * BeltUnits.Tile);
                    if (tile == default && tileIndex * BeltUnits.Tile != 0) continue;

                    carried.Add(new Riding(tile.X, tile.Y, within, lane, source.ItemAt(i)));
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
            if (!_tileSegment.TryGetValue(Key(riding.X, riding.Y), out var found))
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
