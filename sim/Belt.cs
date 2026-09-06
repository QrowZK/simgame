namespace Sim;

public static class BeltUnits
{
    /// Sub-tile resolution. Integer throughout: belt positions must never drift,
    /// and floating point across 10,000 ticks would.
    public const int Tile = 256;

    /// Items sit a quarter tile apart, so four per tile per lane.
    public const int ItemSpacing = 64;

    /// Units per tick. 8 u/t = 1.875 tiles/s = 7.5 items/s per lane.
    public const int SpeedBasic = 8;
    public const int SpeedFast = 16;
    public const int SpeedExpress = 24;
    public const int SpeedTurbo = 32;
}

/// One side of a belt: a queue of items travelling toward the lane's exit.
///
/// Items are stored back-to-front with the gap AHEAD of each item, so advancing
/// the whole lane is a single subtraction on the front item's gap -- O(1) per
/// lane per tick no matter how many items are on it. Everything behind the front
/// item keeps its gap unchanged, because gaps are relative. That is the whole
/// trick: a saturated 100-tile belt costs exactly as much to tick as an empty one.
///
/// A ring buffer keeps removal from the front O(1) as well.
public sealed class Lane
{
    private readonly ItemId[] _items;
    private readonly int[] _gaps;
    private int _head;
    private int _count;
    private int _sumGaps;

    /// Lane length in sub-tile units.
    public int Length { get; }

    public Lane(int lengthUnits)
    {
        if (lengthUnits < BeltUnits.ItemSpacing)
            throw new ArgumentOutOfRangeException(nameof(lengthUnits));

        Length = lengthUnits;
        var capacity = lengthUnits / BeltUnits.ItemSpacing;
        _items = new ItemId[capacity];
        _gaps = new int[capacity];
    }

    public int Count => _count;

    public int Capacity => _items.Length;

    /// Distance from the lane exit to the back edge of the last item.
    public int OccupiedLength => _count == 0 ? 0 : _sumGaps + _count * BeltUnits.ItemSpacing;

    public int FreeAtBack => Length - OccupiedLength;

    /// True when the front item has reached the lane exit and can be handed on.
    public bool FrontReady => _count > 0 && _gaps[_head] == 0;

    public ItemId PeekFront() =>
        _count > 0 ? _items[_head] : throw new InvalidOperationException("Lane is empty.");

    /// Slides everything forward by up to `speed`, limited by the gap ahead of
    /// the front item. Items behind are unaffected: their gaps are relative.
    public void Advance(int speed)
    {
        if (_count == 0 || speed <= 0)
            return;

        var move = Math.Min(speed, _gaps[_head]);
        if (move <= 0)
            return;

        _gaps[_head] -= move;
        _sumGaps -= move;
    }

    public ItemId TakeFront()
    {
        if (!FrontReady)
            throw new InvalidOperationException("No item at the lane exit.");

        var item = _items[_head];
        _head = _head + 1 == _items.Length ? 0 : _head + 1;
        _count--;

        if (_count > 0)
        {
            // The next item keeps its position; it is now a full spacing further
            // from the exit than the item that just left.
            _gaps[_head] += BeltUnits.ItemSpacing;
            _sumGaps += BeltUnits.ItemSpacing;
        }
        else
        {
            _sumGaps = 0;
        }

        return item;
    }

    /// Places an item at the lane's entry end. Fails when the last item has not
    /// travelled far enough to leave room, which is what produces backpressure.
    public bool TryInsertBack(ItemId item)
    {
        var free = FreeAtBack;
        if (free < BeltUnits.ItemSpacing)
            return false;

        var tail = _head + _count;
        if (tail >= _items.Length) tail -= _items.Length;

        _items[tail] = item;
        _gaps[tail] = free - BeltUnits.ItemSpacing;
        _sumGaps += _gaps[tail];
        _count++;
        return true;
    }

    /// Appends an item hard up against the one ahead of it. Normal simulation
    /// never does this -- items become compressed by moving, not by being placed
    /// that way -- but a backed-up belt is a legitimate state to construct
    /// directly for setup and tests, where feeding one item per spacing of
    /// travel would take thousands of ticks.
    public bool TryPack(ItemId item)
    {
        if (OccupiedLength + BeltUnits.ItemSpacing > Length)
            return false;

        var tail = _head + _count;
        if (tail >= _items.Length) tail -= _items.Length;

        _items[tail] = item;
        _gaps[tail] = 0;
        _count++;
        return true;
    }

    /// Position of item i measured from the lane exit. O(i), for rendering and
    /// tests rather than the tick loop.
    public int PositionOf(int index)
    {
        if (index < 0 || index >= _count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var pos = 0;
        var cursor = _head;
        for (var i = 0; i <= index; i++)
        {
            pos += _gaps[cursor];
            if (i < index) pos += BeltUnits.ItemSpacing;
            cursor = cursor + 1 == _items.Length ? 0 : cursor + 1;
        }

        return pos;
    }

    public ItemId ItemAt(int index)
    {
        if (index < 0 || index >= _count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var cursor = _head + index;
        if (cursor >= _items.Length) cursor -= _items.Length;
        return _items[cursor];
    }
}

/// A straight run of belt carrying two independent lanes, as in Factorio: items
/// keep to their side and are only mixed deliberately, by a splitter.
public sealed class BeltSegment
{
    public const int LaneCount = 2;

    private readonly Lane[] _lanes;

    public int Speed { get; }
    public int Tiles { get; }

    public BeltSegment(int tiles, int speed)
    {
        if (tiles <= 0) throw new ArgumentOutOfRangeException(nameof(tiles));
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));

        Tiles = tiles;
        Speed = speed;
        var length = tiles * BeltUnits.Tile;
        _lanes = new[] { new Lane(length), new Lane(length) };
    }

    public Lane LaneAt(int index) => _lanes[index];

    public int ItemCount => _lanes[0].Count + _lanes[1].Count;

    public void Advance()
    {
        _lanes[0].Advance(Speed);
        _lanes[1].Advance(Speed);
    }

    /// Items per second this segment can deliver across both lanes, at 60 UPS.
    public double ThroughputPerSecond => 2.0 * Speed * 60.0 / BeltUnits.ItemSpacing;
}
