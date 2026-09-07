using System.Text;
using Sim;
using Sim.Save;

namespace Sim.Tests;

/// A save is only worth having if the factory that comes back is the factory
/// that was saved. The determinism contract makes that testable rather than a
/// matter of inspection: reload a save and both copies must stay byte-identical
/// through thousands of further ticks, because any state the save dropped shows
/// up as divergence.
public class SaveGameTests
{
    /// A world with something of everything in it -- machines mid-cycle, a
    /// partly-full belt, a splitter mid-rotation, an inserter mid-swing, fluid
    /// in a pipe network, and items in the player's hands. A save that only
    /// covers machines passes a machines-only test.
    private static World BuildBusyWorld()
    {
        var db = new ItemDatabase();
        var ore = db.Register("iron_ore");
        var plate = db.Register("iron_plate");
        var gear = db.Register("iron_gear");
        var water = db.Register("water");

        // Real terrain, so the ground and a miner are part of what gets saved.
        var gen = new WorldGen(4242, new List<OreSpec>
        {
            new(db.Register("magnetite"), minRing: 0, patchRadius: 8, baseAmount: 900),
        });
        var world = new World(4242, db, gen);

        var smelt = new Recipe("smelt_iron_plate", 192,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) }, powerDraw: 4);
        var assemble = new Recipe("assemble_iron_gear", 120,
            new[] { new RecipeInput(plate, 2) },
            new[] { new RecipeOutput(gear, 1) });

        var furnace = world.TryPlaceMachine(smelt, new MachinePlacement(0, 0, 1, 0, 1),
                                            outputCapacityPerItem: 250)!;
        furnace.PushInput(ore, 5000);

        var bigFurnace = world.TryPlaceMachine(smelt, new MachinePlacement(4, 4, 3, 0, 3),
                                               outputCapacityPerItem: 64)!;
        bigFurnace.PushInput(ore, 5000);

        var assembler = world.TryPlaceMachine(assemble, new MachinePlacement(10, 0, 2, 1, 2),
                                              outputCapacityPerItem: 17)!;
        assembler.PushInput(plate, 400);

        // An unplaced machine too: those must not be given a position on load.
        world.AddMachine(smelt).PushInput(ore, 10);

        var belt = world.Belts.AddSegment(tiles: 8, speed: BeltUnits.SpeedExpress);

        // Lane 0 is fed item by item with the belt running between insertions,
        // so the items end up genuinely spread out. Packing them instead gives
        // every item a gap of zero, and then a save that drops the gaps entirely
        // still round-trips -- belt positions would go untested.
        for (var i = 0; i < 4; i++)
        {
            world.Belts.Segment(belt).LaneAt(0).TryInsertBack(ore);
            world.Tick(7);
        }

        // Lane 1 has to be stocked too: an inserter with an empty source never
        // picks anything up, and then its swing state is always zero and a save
        // that drops it looks correct.
        for (var i = 0; i < 5; i++) world.Belts.Segment(belt).LaneAt(1).TryPack(plate);
        world.Belts.SetOutput(belt, 0, Endpoint.Machine(0));

        // A connected splitter, which keeps its round-robin cursor moving...
        var splitter = world.Belts.AddSplitter(Endpoint.Machine(0), Endpoint.Machine(2));
        world.Belts.Splitters[splitter].Accept(ore);

        // ...and one wired to nothing, which stays backed up. A connected
        // splitter empties within a few ticks, and then its buffer is always
        // empty and a save that drops it looks correct.
        var stalled = world.Belts.AddSplitter(Endpoint.None, Endpoint.None);
        world.Belts.Splitters[stalled].Accept(plate);
        world.Belts.Splitters[stalled].Accept(gear);

        // Feeds the assembler from lane 1, so the arm is genuinely mid-swing
        // when the world is saved.
        world.Belts.AddInserter(Endpoint.Belt(belt, 1), Endpoint.Machine(2),
                                swingTicks: 20, stackSize: 2);

        // A real run of pipe with a tank and a pump on it, so the save has to
        // carry the layout as well as what is in it.
        for (var x = 0; x < 10; x++)
            world.Fluids.AddPipe(200 + x, 200, FluidNetwork.ThroughputLarge);
        world.Fluids.AddTank(210, 200);
        world.Fluids.AddPump(211, 200);

        var pipes = world.Fluids.NetworkAt(200, 200);
        world.Fluids.Network(pipes).BeginTick();
        world.Fluids.Network(pipes).TryInsert(water, 300);

        // A pump mid-cycle, plumbed into its own short run. Ambient rather than
        // on a patch, so it keeps running for the length of the test instead of
        // reporting itself depleted the moment it is placed on bare ground.
        world.Fluids.AddPipe(300, 300, FluidNetwork.ThroughputLarge);
        var pump = new FluidExtractor(water, ambient: true, parallelism: 1, cycleTicks: 25);
        pump.Restore(MachineState.Working, 9, 40, 0);
        world.AddSavedExtractor(pump, new MachinePlacement(300, 301, 3, 5, 1));

        world.PlayerInventory.Add(ore, 250);
        world.PlayerInventory.Add(plate, 40);

        // A miner mid-cycle on a patch that has already been dug into: both the
        // machine and the hole in the ground are state a save has to carry.
        var patch = gen.PatchesInRegion(0, 0).First();
        HandOps.Mine(world.Ground, patch.X, patch.Y, world.PlayerInventory, 137);
        world.TryPlaceMiner(new MachinePlacement(patch.X, patch.Y, 2, 4, 2), cycleTicks: 40,
                            powerDraw: 3);

        // A grid that cannot quite keep up, so machines are mid-brownout when
        // the world is saved and their energy buffers are not all zero.
        world.Power.AddPole(new Pole(patch.X, patch.Y, supplyRadius: 12, wireRadius: 9));
        var coal = db.Register("coal");
        var generator = new Generator(coal, outputPerTick: 9, ticksPerFuel: 250);
        generator.AddFuel(4);
        world.TryPlaceGenerator(generator, new MachinePlacement(patch.X + 5, patch.Y + 5, 1, 2, 1));

        // A machine actually inside the pole's reach, drawing far more than the
        // grid can spare. The others sit at the origin, far off-grid, so
        // without this one no machine ever holds a partial energy buffer and a
        // save that dropped energy would round-trip unnoticed.
        //
        // The heavy draw is the point: it spends most ticks part-way to
        // affording one, which is exactly the state worth saving.
        var hungry = new Recipe("smelt_slowly", 192,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) }, powerDraw: 40);

        var onGrid = world.TryPlaceMachine(hungry, new MachinePlacement(patch.X + 3, patch.Y, 1, 1, 1),
                                           outputCapacityPerItem: 40)!;
        onGrid.PushInput(ore, 900);

        return world;
    }

    /// The generator a load has to be given: a save stores the seed, not the map.
    private static WorldGen GenFor(World world) => world.Ground.Gen;

    private static Dictionary<string, Recipe> Recipes(World world)
    {
        var recipes = new Dictionary<string, Recipe>();
        foreach (var machine in world.Machines)
            recipes[machine.Recipe.Id] = machine.Recipe;
        return recipes;
    }

    /// Every piece of mutable state in the world, as a string. This is the
    /// assertion: if the save drops a field, two worlds that should be identical
    /// will not be.
    private static string Fingerprint(World world)
    {
        var sb = new StringBuilder();
        sb.Append("seed=").Append(world.Seed).Append(" tick=").Append(world.TickCount).Append('\n');

        for (var i = 0; i < world.MachineCount; i++)
        {
            var machine = world.Machines[i];
            var placement = world.PlacementOf(i);
            sb.Append("machine ").Append(i).Append(' ').Append(machine.Recipe.Id)
              .Append(" at ").Append(placement.X).Append(',').Append(placement.Y)
              .Append(" size=").Append(placement.Size)
              .Append(" tier=").Append(placement.Tier)
              .Append(" cat=").Append(placement.Category)
              .Append(" placed=").Append(world.IsPlaced(i))
              .Append(" par=").Append(machine.Parallelism)
              .Append(" cap=").Append(machine.OutputCapacityPerItem)
              .Append(" state=").Append(machine.State)
              .Append(" ticks=").Append(machine.RawTicksRemaining)
              .Append(" energy=").Append(machine.Energy)
              .Append(" draw=").Append(machine.PowerDraw)
              .Append(" in=").Append(Stacks(world, machine.InputContents))
              .Append(" out=").Append(Stacks(world, machine.OutputContents))
              .Append('\n');
        }

        for (var i = 0; i < world.Miners.Count; i++)
        {
            var miner = world.Miners[i];
            var placement = world.MinerPlacements[i];
            sb.Append("miner ").Append(i).Append(' ').Append(world.Items.GetName(miner.Item))
              .Append(" at ").Append(placement.X).Append(',').Append(placement.Y)
              .Append(" size=").Append(placement.Size)
              .Append(" tier=").Append(placement.Tier)
              .Append(" cat=").Append(placement.Category)
              .Append(" cycle=").Append(miner.CycleTicks)
              .Append(" state=").Append(miner.State)
              .Append(" ticks=").Append(miner.RawTicksRemaining)
              .Append(" buffered=").Append(miner.Buffered)
              .Append(" energy=").Append(miner.Energy)
              .Append(" draw=").Append(miner.PowerDraw)
              .Append('\n');
        }

        foreach (var (x, y, taken) in world.Ground.Depletion)
            sb.Append("dug ").Append(x).Append(',').Append(y).Append('=').Append(taken).Append('\n');

        for (var i = 0; i < world.Power.Poles.Count; i++)
        {
            var pole = world.Power.Poles[i];
            sb.Append("pole ").Append(i).Append(' ').Append(pole.X).Append(',').Append(pole.Y)
              .Append(" supply=").Append(pole.SupplyRadius)
              .Append(" wire=").Append(pole.WireRadius).Append('\n');
        }

        for (var i = 0; i < world.Power.Generators.Count; i++)
        {
            var generator = world.Power.Generators[i];
            var placement = world.Power.GeneratorPlacements[i];
            sb.Append("generator ").Append(i)
              .Append(' ').Append(world.Items.GetName(generator.Fuel))
              .Append(" at ").Append(placement.X).Append(',').Append(placement.Y)
              .Append(" size=").Append(placement.Size)
              .Append(" out=").Append(generator.OutputPerTick)
              .Append(" per=").Append(generator.TicksPerFuel)
              .Append(" stock=").Append(generator.FuelStock)
              .Append(" burn=").Append(generator.BurnTicksLeft)
              .Append(" network=").Append(world.Power.NetworkOfGenerator(i))
              .Append('\n');
        }

        sb.Append("networks ").Append(world.Power.NetworkCount).Append('\n');

        for (var s = 0; s < world.Belts.Segments.Count; s++)
        {
            var segment = world.Belts.Segments[s];
            sb.Append("belt ").Append(s).Append(" tiles=").Append(segment.Tiles)
              .Append(" speed=").Append(segment.Speed).Append('\n');

            for (var l = 0; l < BeltSegment.LaneCount; l++)
            {
                var items = new List<ItemId>();
                var gaps = new List<int>();
                segment.LaneAt(l).CopyTo(items, gaps);
                sb.Append("  lane ").Append(l).Append(' ');
                for (var i = 0; i < items.Count; i++)
                    sb.Append(world.Items.GetName(items[i])).Append('@').Append(gaps[i]).Append(' ');
                sb.Append("out=").Append(Show(world.Belts.OutputOf(s, l))).Append('\n');
            }
        }

        for (var i = 0; i < world.Belts.Splitters.Count; i++)
        {
            var splitter = world.Belts.Splitters[i];
            sb.Append("splitter ").Append(i)
              .Append(" a=").Append(Show(splitter.Outputs[0]))
              .Append(" b=").Append(Show(splitter.Outputs[1]))
              .Append(" next=").Append(splitter.Next)
              .Append(" buffer=");
            foreach (var item in splitter.BufferedItems)
                sb.Append(world.Items.GetName(item)).Append(' ');
            sb.Append('\n');
        }

        for (var i = 0; i < world.Belts.Inserters.Count; i++)
        {
            var inserter = world.Belts.Inserters[i];
            sb.Append("inserter ").Append(i)
              .Append(" from=").Append(Show(inserter.Source))
              .Append(" to=").Append(Show(inserter.Target))
              .Append(" swing=").Append(inserter.SwingTicks)
              .Append(" stack=").Append(inserter.StackSize)
              .Append(" held=").Append(inserter.Held)
              .Append(' ').Append(world.Items.GetName(inserter.HeldItem))
              .Append(" cooldown=").Append(inserter.Cooldown).Append('\n');
        }

        for (var i = 0; i < world.Fluids.Nodes.Count; i++)
        {
            var node = world.Fluids.Nodes[i];
            sb.Append("node ").Append(i).Append(' ').Append(node.Kind)
              .Append(' ').Append(node.X).Append(',').Append(node.Y)
              .Append(" cap=").Append(node.Capacity)
              .Append(" rate=").Append(node.Throughput).Append('\n');
        }

        for (var i = 0; i < world.Extractors.Count; i++)
        {
            var extractor = world.Extractors[i];
            var placement = world.ExtractorPlacements[i];
            sb.Append("extractor ").Append(i)
              .Append(' ').Append(world.Items.GetName(extractor.Fluid))
              .Append(" ambient=").Append(extractor.Ambient)
              .Append(" at ").Append(placement.X).Append(',').Append(placement.Y)
              .Append(" size=").Append(placement.Size)
              .Append(" cycle=").Append(extractor.CycleTicks)
              .Append(" draw=").Append(extractor.PowerDraw)
              .Append(" state=").Append(extractor.State)
              .Append(" ticks=").Append(extractor.RawTicksRemaining)
              .Append(" buffered=").Append(extractor.Buffered)
              .Append(" energy=").Append(extractor.Energy)
              .Append('\n');
        }

        for (var i = 0; i < world.Fluids.Networks.Count; i++)
        {
            var network = world.Fluids.Networks[i];
            sb.Append("fluid ").Append(i)
              .Append(" cap=").Append(network.Capacity)
              .Append(" rate=").Append(network.ThroughputPerTick)
              .Append(' ').Append(world.Items.GetName(network.Fluid))
              .Append('=').Append(network.Amount).Append('\n');
        }

        sb.Append("player ").Append(Stacks(world, world.PlayerInventory.Contents)).Append('\n');
        return sb.ToString();
    }

    private static string Stacks(World world, IReadOnlyDictionary<ItemId, int> contents) =>
        string.Join(",", contents.OrderBy(kv => kv.Key.Value)
                                 .Select(kv => world.Items.GetName(kv.Key) + "=" + kv.Value));

    private static string Show(Endpoint endpoint) =>
        $"{endpoint.Kind}:{endpoint.Index}:{endpoint.Lane}";

    /// Asserts the world is genuinely mid-everything before it is saved.
    ///
    /// Without this the suite decays silently: an inserter whose source belt has
    /// drained sits at cooldown 0 forever, and a save that drops its swing state
    /// then round-trips perfectly. That exact hole was real -- the test only
    /// caught it because dropping the field on purpose still passed.
    private static void AssertMidFlight(World world)
    {
        Assert.Contains(world.Machines, m => m.State == MachineState.Working && m.RawTicksRemaining > 0);

        var inserter = world.Belts.Inserters[0];
        Assert.True(inserter.Cooldown > 0 || inserter.Held > 0,
                    "the inserter is idle, so its swing state is not being covered");

        Assert.True(world.Belts.ItemsInTransit() > 0, "no items on belts, so lane state is not covered");

        // Items must be genuinely spread out, not compressed: a lane where every
        // gap is zero cannot detect a save that drops the gaps.
        var items = new List<ItemId>();
        var gaps = new List<int>();
        world.Belts.Segments[0].LaneAt(0).CopyTo(items, gaps);
        Assert.Contains(gaps, g => g > 0);
        Assert.Contains(world.Belts.Splitters, s => s.Buffered > 0);
        Assert.True(world.Fluids.Networks[0].Amount > 0, "no fluid stored, so fluids are not covered");

        Assert.NotEmpty(world.Miners);
        Assert.NotEmpty(world.Ground.Depletion);
        Assert.True(world.Miners[0].Buffered > 0 || world.Miners[0].RawTicksRemaining > 0,
                    "the miner is doing nothing, so its state is not being covered");

        Assert.NotEmpty(world.Fluids.Nodes);
        Assert.NotEmpty(world.Extractors);
        Assert.True(world.Fluids.TotalFluid() > 0, "no fluid stored, so pipe contents are uncovered");
        Assert.True(world.Extractors[0].Buffered > 0 || world.Extractors[0].RawTicksRemaining > 0,
                    "the derrick is idle, so its state is not being covered");

        Assert.NotEmpty(world.Power.Poles);
        Assert.NotEmpty(world.Power.Generators);
        Assert.True(world.Power.Generators[0].IsBurning, "the generator is idle, so burn state is uncovered");

        // Under-supplied on purpose: energy buffers are only interesting when
        // the grid cannot keep up, and a save that dropped them would otherwise
        // round-trip perfectly.
        Assert.True(world.NetworkDemand[0] > world.NetworkSupply[0],
                    "the grid is not under strain, so brownout state is not being covered");
        Assert.Contains(world.Machines, m => m.State == MachineState.Unpowered);

        // Someone must be part-way to affording a tick. Without this the energy
        // buffers are all zero at the save point and a save that dropped them
        // round-trips perfectly -- which is exactly how this test first passed
        // while covering nothing.
        Assert.Contains(world.Machines, m => m.Energy > 0);
    }

    [Fact]
    public void ASavedWorld_ReloadsToTheIdenticalWorld()
    {
        var original = BuildBusyWorld();
        original.Tick(61);           // stop mid-cycle, mid-swing, mid-rotation
        AssertMidFlight(original);

        var json = SaveGame.ToJson(SaveGame.Capture(original));
        var loaded = SaveGame.Restore(SaveGame.FromJson(json), Recipes(original), GenFor(original));

        Assert.Equal(Fingerprint(original), Fingerprint(loaded));
    }

    [Fact]
    public void AReloadedWorld_StaysIdenticalFor10000MoreTicks()
    {
        // The real test. A field the save dropped may look harmless at rest and
        // diverge the moment the sim runs -- a splitter's rotation cursor, an
        // inserter's cooldown, the ticks left in a cycle.
        var original = BuildBusyWorld();
        original.Tick(61);
        AssertMidFlight(original);
        var startedAt = original.TickCount;

        var loaded = SaveGame.Restore(SaveGame.FromJson(SaveGame.ToJson(SaveGame.Capture(original))),
                                      Recipes(original), GenFor(original));

        original.Tick(10_000);
        loaded.Tick(10_000);

        Assert.Equal(Fingerprint(original), Fingerprint(loaded));

        // Sanity: the run actually did something, rather than two idle worlds
        // agreeing about nothing.
        Assert.Equal(startedAt + 10_000, original.TickCount);
        Assert.Contains("iron_plate", Fingerprint(original));
    }

    [Fact]
    public void SavingTwice_ProducesTheSameBytes()
    {
        var world = BuildBusyWorld();
        world.Tick(300);

        Assert.Equal(SaveGame.ToJson(SaveGame.Capture(world)),
                     SaveGame.ToJson(SaveGame.Capture(world)));
    }

    [Fact]
    public void ItemsSurviveARegistrationOrderChange()
    {
        // The reason items are stored by name. If the data files gain an item,
        // every id after it shifts; a save keyed on ids would quietly turn iron
        // into tin.
        var world = BuildBusyWorld();
        world.Tick(100);
        var save = SaveGame.Capture(world);

        // Item 0 in the save is iron_ore. Whatever order a later build registers
        // things in, the save's own table is what resolves its references.
        Assert.Equal("iron_ore", save.Items[0]);

        var loaded = SaveGame.Restore(save, Recipes(world), GenFor(world));
        Assert.Equal(world.PlayerInventory.Count(world.Items.GetId("iron_ore")),
                     loaded.PlayerInventory.Count(loaded.Items.GetId("iron_ore")));
    }

    [Fact]
    public void ASaveFromADifferentVersion_IsRefusedRatherThanGuessedAt()
    {
        var world = BuildBusyWorld();
        var save = SaveGame.Capture(world);
        save.Version = SaveFile.CurrentVersion + 1;

        var error = Assert.Throws<SaveLoadException>(() => SaveGame.Restore(save, Recipes(world), GenFor(world)));
        Assert.Contains("version", error.Message);
    }

    [Fact]
    public void ARecipeThatNoLongerExists_IsNamedRatherThanSilentlyDropped()
    {
        // Losing a machine quietly is worse than failing to load: the player
        // finds the hole in their factory hours later.
        var world = BuildBusyWorld();
        var save = SaveGame.Capture(world);

        var recipes = Recipes(world);
        recipes.Remove("assemble_iron_gear");

        var error = Assert.Throws<SaveLoadException>(() => SaveGame.Restore(save, recipes, GenFor(world)));
        Assert.Contains("assemble_iron_gear", error.Message);
    }

    [Fact]
    public void ACorruptSaveFile_FailsWithAReason()
    {
        var error = Assert.Throws<SaveLoadException>(() => SaveGame.FromJson("{ not json"));
        Assert.Contains("valid JSON", error.Message);
    }

    [Fact]
    public void SavesRoundTripThroughAFileOnDisk()
    {
        var world = BuildBusyWorld();
        world.Tick(250);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SaveGame.Write(world, path);
            var loaded = SaveGame.Read(path, Recipes(world), GenFor(world));
            Assert.Equal(Fingerprint(world), Fingerprint(loaded));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFile_SaysSoRatherThanThrowingSomethingOpaque()
    {
        var error = Assert.Throws<SaveLoadException>(
            () => SaveGame.Read("/nonexistent/nowhere.json", new Dictionary<string, Recipe>()));
        Assert.Contains("no save file", error.Message);
    }
}
