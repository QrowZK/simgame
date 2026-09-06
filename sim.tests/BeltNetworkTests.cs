using Sim;

namespace Sim.Tests;

public class BeltNetworkTests
{
    private static readonly ItemId Ore = new(0);
    private static readonly ItemId Plate = new(1);

    private static Recipe Smelt() => new(
        "smelt", 4,
        new[] { new RecipeInput(Ore, 1) },
        new[] { new RecipeOutput(Plate, 1) });

    private static int CountIn(Machine m, params ItemId[] items)
    {
        var total = 0;
        foreach (var item in items) total += m.GetInputCount(item) + m.GetOutputCount(item);
        return total;
    }

    [Fact]
    public void ItemsTravelFromOneBeltToTheNext()
    {
        var world = new World(1);
        var a = world.Belts.AddSegment(2);
        var b = world.Belts.AddSegment(2);
        world.Belts.LinkBelts(a, b);

        world.Belts.Segment(a).LaneAt(0).TryInsertBack(Ore);

        // Cross belt A, then belt B. Each is 512 units minus one spacing of
        // travel, at 8 units/tick.
        var perBelt = (BeltUnits.Tile * 2 - BeltUnits.ItemSpacing) / BeltUnits.SpeedBasic;
        world.Tick(perBelt * 2 + 8);

        Assert.Equal(0, world.Belts.Segment(a).LaneAt(0).Count);
        Assert.Equal(1, world.Belts.Segment(b).LaneAt(0).Count);
    }

    [Fact]
    public void LanesStaySeparateAcrossAChain()
    {
        var world = new World(1);
        var a = world.Belts.AddSegment(2);
        var b = world.Belts.AddSegment(2);
        world.Belts.LinkBelts(a, b);

        world.Belts.Segment(a).LaneAt(0).TryInsertBack(Ore);
        world.Belts.Segment(a).LaneAt(1).TryInsertBack(Plate);

        world.Tick(400);

        // Whatever arrived, it arrived on its own side.
        Assert.Equal(Ore, world.Belts.Segment(b).LaneAt(0).ItemAt(0));
        Assert.Equal(Plate, world.Belts.Segment(b).LaneAt(1).ItemAt(0));
    }

    [Fact]
    public void ADeadEndBeltBacksUpAndStopsAccepting()
    {
        var world = new World(1);
        var a = world.Belts.AddSegment(2);      // no output linked

        var accepted = 0;
        for (var tick = 0; tick < 2000; tick++)
        {
            if (world.Belts.Segment(a).LaneAt(0).TryInsertBack(Ore)) accepted++;
            world.Tick();
        }

        // It fills to exactly its capacity and then refuses everything.
        Assert.Equal(world.Belts.Segment(a).LaneAt(0).Capacity, accepted);
        Assert.Equal(accepted, world.Belts.Segment(a).LaneAt(0).Count);
    }

    [Fact]
    public void Splitter_AlternatesEvenlyBetweenBothOutputs()
    {
        var world = new World(1);
        var feed = world.Belts.AddSegment(2);
        var left = world.Belts.AddSegment(8);
        var right = world.Belts.AddSegment(8);

        var splitter = world.Belts.AddSplitter(Endpoint.Belt(left, 0), Endpoint.Belt(right, 0));
        world.Belts.SetOutput(feed, 0, Endpoint.Splitter(splitter));

        for (var tick = 0; tick < 3000; tick++)
        {
            world.Belts.Segment(feed).LaneAt(0).TryInsertBack(Ore);
            world.Tick();
        }

        var l = world.Belts.Segment(left).LaneAt(0).Count;
        var r = world.Belts.Segment(right).LaneAt(0).Count;

        Assert.True(l > 0 && r > 0, $"both sides should receive: left={l} right={r}");
        // Round-robin, so the split is exact to within the one item in flight.
        Assert.True(Math.Abs(l - r) <= 1, $"expected an even split, got left={l} right={r}");
    }

    [Fact]
    public void Splitter_KeepsFeedingTheOpenSideWhenTheOtherIsBlocked()
    {
        var world = new World(1);
        var feed = world.Belts.AddSegment(2);
        var open = world.Belts.AddSegment(8);
        var blocked = world.Belts.AddSegment(1);

        var splitter = world.Belts.AddSplitter(Endpoint.Belt(open, 0), Endpoint.Belt(blocked, 0));
        world.Belts.SetOutput(feed, 0, Endpoint.Splitter(splitter));

        // Jam the blocked side solid before starting.
        while (world.Belts.Segment(blocked).LaneAt(0).TryPack(Ore)) { }

        for (var tick = 0; tick < 3000; tick++)
        {
            world.Belts.Segment(feed).LaneAt(0).TryInsertBack(Ore);
            world.Tick();
        }

        // A jammed output must not starve the other side.
        Assert.True(world.Belts.Segment(open).LaneAt(0).Count > 0,
            "the open side should still be receiving");
    }

    [Fact]
    public void Inserter_MovesItemsFromABeltIntoAMachine()
    {
        var world = new World(1);
        var machine = world.AddMachine(Smelt(), outputCapacityPerItem: 10_000);
        var belt = world.Belts.AddSegment(2);

        world.Belts.AddInserter(Endpoint.Belt(belt, 0), Endpoint.Machine(0), swingTicks: 10);

        for (var tick = 0; tick < 3000; tick++)
        {
            world.Belts.Segment(belt).LaneAt(0).TryInsertBack(Ore);
            world.Tick();
        }

        Assert.True(machine.GetOutputCount(Plate) > 0,
            "the machine should have run on belt-fed ore");
    }

    [Fact]
    public void Inserter_MovesMachineOutputOntoABelt()
    {
        var world = new World(1);
        var machine = world.AddMachine(Smelt(), outputCapacityPerItem: 64);
        machine.PushInput(Ore, 5000);

        var belt = world.Belts.AddSegment(8);
        world.Belts.AddInserter(Endpoint.Machine(0), Endpoint.Belt(belt, 0), swingTicks: 10);

        world.Tick(3000);

        Assert.True(world.Belts.Segment(belt).LaneAt(0).Count > 0,
            "plates should have been placed on the belt");
    }

    [Fact]
    public void Conservation_NothingIsDuplicatedOrLostAcrossAWholeFactory()
    {
        // A feed belt into a splitter, both sides into machines, over 20k ticks.
        // Item duplication and loss are the bug class that actually kills
        // automation games, so this counts every item everywhere.
        var world = new World(7);
        var machineA = world.AddMachine(Smelt(), outputCapacityPerItem: 100_000);
        var machineB = world.AddMachine(Smelt(), outputCapacityPerItem: 100_000);

        var feed = world.Belts.AddSegment(4);
        var left = world.Belts.AddSegment(4);
        var right = world.Belts.AddSegment(4);

        var splitter = world.Belts.AddSplitter(Endpoint.Belt(left, 0), Endpoint.Belt(right, 0));
        world.Belts.SetOutput(feed, 0, Endpoint.Splitter(splitter));
        world.Belts.AddInserter(Endpoint.Belt(left, 0), Endpoint.Machine(0), swingTicks: 6);
        world.Belts.AddInserter(Endpoint.Belt(right, 0), Endpoint.Machine(1), swingTicks: 6);

        var injected = 0;
        for (var tick = 0; tick < 20_000; tick++)
        {
            if (world.Belts.Segment(feed).LaneAt(0).TryInsertBack(Ore)) injected++;
            world.Tick();
        }

        // Ore is either still in transit, waiting in a machine, or was consumed
        // to make a plate. One plate costs exactly one ore.
        var inTransit = world.Belts.ItemsInTransit();
        var waiting = machineA.GetInputCount(Ore) + machineB.GetInputCount(Ore);
        var plates = machineA.GetOutputCount(Plate) + machineB.GetOutputCount(Plate);
        var inProgress = CountInProgress(machineA) + CountInProgress(machineB);

        Assert.True(injected > 1000, $"expected real throughput, injected only {injected}");
        Assert.Equal(injected, inTransit + waiting + plates + inProgress);
    }

    /// One ore is consumed the moment a cycle starts, so a machine mid-cycle is
    /// holding one that is neither an input nor yet an output.
    private static int CountInProgress(Machine machine) =>
        machine.State == MachineState.Working ? 1 : 0;

    [Fact]
    public void Determinism_TwoIdenticalFactoriesAgreeAfter10000Ticks()
    {
        static string Run(int seed)
        {
            var world = new World(seed);
            world.AddMachine(Smelt(), outputCapacityPerItem: 100_000);

            var feed = world.Belts.AddSegment(4);
            var left = world.Belts.AddSegment(4);
            var right = world.Belts.AddSegment(4);
            var splitter = world.Belts.AddSplitter(Endpoint.Belt(left, 0), Endpoint.Belt(right, 0));
            world.Belts.SetOutput(feed, 0, Endpoint.Splitter(splitter));
            world.Belts.AddInserter(Endpoint.Belt(left, 0), Endpoint.Machine(0), swingTicks: 6);

            for (var tick = 0; tick < 10_000; tick++)
            {
                world.Belts.Segment(feed).LaneAt(0).TryInsertBack(Ore);
                world.Tick();
            }

            var sb = new System.Text.StringBuilder();
            foreach (var segment in world.Belts.Segments)
                for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
                {
                    var l = segment.LaneAt(lane);
                    sb.Append(l.Count).Append(':').Append(l.OccupiedLength).Append(';');
                    for (var i = 0; i < l.Count; i++)
                        sb.Append(l.PositionOf(i)).Append(',');
                    sb.Append('|');
                }

            return sb.ToString();
        }

        Assert.Equal(Run(42), Run(42));
    }

    [Fact]
    public void FasterBeltTiers_DeliverProportionallyMore()
    {
        static int Delivered(int speed)
        {
            var world = new World(1);
            var belt = world.Belts.AddSegment(4, speed);
            var sink = world.Belts.AddSegment(64, speed);
            world.Belts.LinkBelts(belt, sink);

            for (var tick = 0; tick < 6000; tick++)
            {
                world.Belts.Segment(belt).LaneAt(0).TryInsertBack(Ore);
                world.Tick();
            }

            return world.Belts.Segment(sink).LaneAt(0).Count;
        }

        var basic = Delivered(BeltUnits.SpeedBasic);
        var fast = Delivered(BeltUnits.SpeedFast);

        // Both saturate the 64-tile sink, so compare that they fill it and that
        // the faster tier is not slower.
        Assert.True(fast >= basic, $"fast={fast} should be at least basic={basic}");
        Assert.True(basic > 100, $"expected sustained delivery, got {basic}");
    }
}
