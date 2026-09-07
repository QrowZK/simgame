using Sim;

namespace Sim.Tests;

public class FluidTests
{
    private static readonly ItemId Water = new(0);
    private static readonly ItemId Naphtha = new(1);

    [Fact]
    public void Network_HoldsUpToItsCapacity()
    {
        var net = new FluidNetwork(tiles: 10, throughputPerTick: 100_000);
        Assert.Equal(10 * FluidNetwork.CapacityPerTile, net.Capacity);

        net.BeginTick();
        Assert.Equal(1000, net.TryInsert(Water, 5000));   // clamped to capacity
        Assert.Equal(1000, net.Amount);
        Assert.Equal(0, net.TryInsert(Water, 1));
    }

    [Fact]
    public void Throughput_CapsFlowPerTickIndependentlyOfCapacity()
    {
        var net = new FluidNetwork(tiles: 100, throughputPerTick: FluidNetwork.ThroughputBasic);

        net.BeginTick();
        Assert.Equal(FluidNetwork.ThroughputBasic, net.TryInsert(Water, 10_000));
        Assert.Equal(0, net.TryInsert(Water, 10_000));    // budget spent

        net.BeginTick();
        Assert.Equal(FluidNetwork.ThroughputBasic, net.TryInsert(Water, 10_000));
    }

    [Fact]
    public void MixingFluids_IsRefusedRatherThanAveraged()
    {
        var net = new FluidNetwork(tiles: 10, throughputPerTick: 100_000);
        net.BeginTick();
        net.TryInsert(Water, 500);

        Assert.False(net.Accepts(Naphtha));
        Assert.Equal(0, net.TryInsert(Naphtha, 100));
        Assert.Equal(0, net.TryExtract(Naphtha, 100));
        Assert.Equal(500, net.Amount);
        Assert.Equal(Water, net.Fluid);
    }

    [Fact]
    public void DrainedNetwork_AcceptsADifferentFluid()
    {
        var net = new FluidNetwork(tiles: 10, throughputPerTick: 100_000);
        net.BeginTick();
        net.TryInsert(Water, 500);
        Assert.Equal(500, net.TryExtract(Water, 500));

        Assert.True(net.IsEmpty);
        Assert.True(net.Accepts(Naphtha));
        Assert.Equal(300, net.TryInsert(Naphtha, 300));
    }

    [Fact]
    public void Conservation_FluidIsNeitherCreatedNorLost()
    {
        var world = new World(3);
        // Two runs of pipe with a gap between them, so they stay separate.
        for (var x = 0; x < 20; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputLarge);
        for (var x = 40; x < 60; x++) world.Fluids.AddPipe(x, 0, FluidNetwork.ThroughputLarge);
        var source = world.Fluids.NetworkAt(0, 0);
        var sink = world.Fluids.NetworkAt(40, 0);

        var injected = 0;
        var delivered = 0;

        for (var tick = 0; tick < 5000; tick++)
        {
            world.Tick();
            injected += world.Fluids.Network(source).TryInsert(Water, 50);

            // pump across
            var moved = world.Fluids.Network(source).TryExtract(Water, 50);
            var accepted = world.Fluids.Network(sink).TryInsert(Water, moved);
            // anything the sink refused stays where it was
            world.Fluids.Network(source).TryInsert(Water, moved - accepted);
            delivered += accepted;
        }

        Assert.True(injected > 0);
        Assert.Equal(injected, world.Fluids.TotalFluid());
    }

    [Fact]
    public void PipeLength_DoesNotSlowDelivery()
    {
        // The design point: a long pipe is not a slow pipe. Only bore and pumping
        // set the rate, so a 500-tile run delivers exactly what a 5-tile run does.
        static int DeliveredOver(int tiles)
        {
            var net = new FluidNetwork(tiles, FluidNetwork.ThroughputBasic);
            var total = 0;
            for (var tick = 0; tick < 100; tick++)
            {
                net.BeginTick();
                net.TryInsert(Water, 10_000);
                total += net.TryExtract(Water, 10_000);
            }

            return total;
        }

        Assert.Equal(DeliveredOver(5), DeliveredOver(500));
    }

    [Fact]
    public void APipeCarriesFarMoreThanABelt()
    {
        // Belt: one lane at basic speed is one item per 8 ticks.
        var beltPerTick = (double)BeltUnits.SpeedBasic / BeltUnits.ItemSpacing * BeltSegment.LaneCount;

        // Pipe: even the basic tier moves 200 units per tick.
        var pipePerTick = (double)FluidNetwork.ThroughputBasic;

        Assert.True(pipePerTick > beltPerTick * 100,
            $"pipes should dominate for fluids: pipe={pipePerTick}/t belt={beltPerTick}/t");
    }
}
