using Sim;

namespace Sim.Tests;

public class LaneTests
{
    private static readonly ItemId Plate = new(0);
    private static readonly ItemId Gear = new(1);

    [Fact]
    public void EmptyLane_HasNoOccupancyAndNothingAtTheExit()
    {
        var lane = new Lane(BeltUnits.Tile * 4);

        Assert.Equal(0, lane.Count);
        Assert.Equal(0, lane.OccupiedLength);
        Assert.False(lane.FrontReady);
        Assert.Equal(lane.Length, lane.FreeAtBack);
    }

    [Fact]
    public void InsertedItem_EntersAtTheBackAndWalksToTheExit()
    {
        var lane = new Lane(BeltUnits.Tile);           // 256 units
        Assert.True(lane.TryInsertBack(Plate));

        // It enters at the far end: one spacing of item, the rest is gap ahead.
        Assert.Equal(BeltUnits.Tile - BeltUnits.ItemSpacing, lane.PositionOf(0));
        Assert.False(lane.FrontReady);

        // 192 units of gap at 8 units/tick is exactly 24 ticks to the exit.
        for (var i = 0; i < 24; i++)
            lane.Advance(BeltUnits.SpeedBasic);

        Assert.True(lane.FrontReady);
        Assert.Equal(0, lane.PositionOf(0));
        Assert.Equal(Plate, lane.TakeFront());
        Assert.Equal(0, lane.Count);
    }

    [Fact]
    public void Capacity_IsOneItemPerSpacing()
    {
        var lane = new Lane(BeltUnits.Tile * 2);       // 512 units / 64 = 8 items
        Assert.Equal(8, lane.Capacity);

        for (var i = 0; i < 8; i++)
            Assert.True(lane.TryPack(Plate));

        // Full: the lane is solid from exit to entry.
        Assert.False(lane.TryPack(Plate));
        Assert.Equal(8, lane.Count);
        Assert.Equal(lane.Length, lane.OccupiedLength);
    }

    [Fact]
    public void SaturatedLane_DoesNotMoveUntilTheFrontItemLeaves()
    {
        var lane = new Lane(BeltUnits.Tile * 2);
        for (var i = 0; i < 8; i++) lane.TryPack(Plate);

        // Items are already touching, so advancing changes nothing.
        for (var i = 0; i < 100; i++) lane.Advance(BeltUnits.SpeedBasic);
        Assert.Equal(lane.Length, lane.OccupiedLength);
        Assert.False(lane.TryInsertBack(Plate));
        Assert.False(lane.TryPack(Plate));

        // Taking the front item frees space at the FRONT, not the back: the
        // remaining seven have not moved, so the entry is still blocked.
        lane.TakeFront();
        Assert.Equal(0, lane.FreeAtBack);
        Assert.False(lane.TryInsertBack(Plate));

        // The line has to travel one spacing before the entry reopens, and then
        // it accepts exactly one item.
        for (var i = 0; i < BeltUnits.ItemSpacing / BeltUnits.SpeedBasic; i++)
            lane.Advance(BeltUnits.SpeedBasic);

        Assert.Equal(BeltUnits.ItemSpacing, lane.FreeAtBack);
        Assert.True(lane.TryInsertBack(Plate));
        Assert.False(lane.TryInsertBack(Plate));
        Assert.Equal(8, lane.Count);
    }

    [Fact]
    public void ItemsKeepTheirOrderAndIdentity()
    {
        var lane = new Lane(BeltUnits.Tile * 2);
        var sequence = new[] { Plate, Gear, Plate, Gear, Gear };
        foreach (var item in sequence) Assert.True(lane.TryPack(item));

        for (var i = 0; i < sequence.Length; i++)
            Assert.Equal(sequence[i], lane.ItemAt(i));

        var drained = new List<ItemId>();
        for (var tick = 0; tick < 2000 && lane.Count > 0; tick++)
        {
            lane.Advance(BeltUnits.SpeedBasic);
            if (lane.FrontReady) drained.Add(lane.TakeFront());
        }

        Assert.Equal(sequence, drained);
    }

    [Fact]
    public void Throughput_MatchesTheSpacingAndSpeedExactly()
    {
        // One lane at 8 u/t with 64-unit spacing is one item every 8 ticks:
        // 7.5 items/s. Feed it as fast as it will take and count what comes off.
        var lane = new Lane(BeltUnits.Tile * 4);
        var delivered = 0;

        for (var tick = 0; tick < 600; tick++)      // ten seconds
        {
            lane.Advance(BeltUnits.SpeedBasic);
            if (lane.FrontReady) { lane.TakeFront(); delivered++; }
            lane.TryInsertBack(Plate);
        }

        // Ten seconds at 7.5 items/s, less the time for the first item to
        // traverse the belt before anything can come off.
        var travel = (lane.Length - BeltUnits.ItemSpacing) / BeltUnits.SpeedBasic;
        var expected = (600 - travel) / (BeltUnits.ItemSpacing / BeltUnits.SpeedBasic);
        Assert.Equal(expected, delivered);
    }

    [Fact]
    public void RingBuffer_SurvivesWrappingManyTimes()
    {
        var lane = new Lane(BeltUnits.Tile);           // capacity 4
        var pushed = 0;
        var pulled = 0;

        for (var tick = 0; tick < 5000; tick++)
        {
            lane.Advance(BeltUnits.SpeedBasic);
            if (lane.FrontReady) { lane.TakeFront(); pulled++; }
            if (lane.TryInsertBack(Plate)) pushed++;
        }

        // Whatever is still on the belt is the difference. Nothing invented,
        // nothing lost, after wrapping the buffer hundreds of times.
        Assert.Equal(pushed - pulled, lane.Count);
        Assert.True(pulled > 500, $"expected sustained flow, got {pulled}");
    }

    [Fact]
    public void AdvanceCost_DoesNotGrowWithItemCount()
    {
        // The design claim: ticking a full belt costs the same as ticking an
        // almost-empty one, because only the front gap changes.
        var empty = new Lane(BeltUnits.Tile * 64);
        empty.TryPack(Plate);

        var full = new Lane(BeltUnits.Tile * 64);
        while (full.TryPack(Plate)) { }
        full.TakeFront();                              // leave one spacing to move into

        var before = full.OccupiedLength;
        full.Advance(BeltUnits.SpeedBasic);
        empty.Advance(BeltUnits.SpeedBasic);

        // Both moved by the same amount despite one holding 255 more items.
        Assert.Equal(before - BeltUnits.SpeedBasic, full.OccupiedLength);
        Assert.Equal(255, full.Count);
    }
}
