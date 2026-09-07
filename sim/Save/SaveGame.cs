using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Save;

/// Raised when a save cannot be loaded. Always carries what specifically was
/// wrong: "could not load" with no reason turns a recoverable data problem into
/// a lost factory.
public sealed class SaveLoadException : Exception
{
    public SaveLoadException(string message) : base(message) { }
}

/// Captures a World to a save file and rebuilds one from it.
///
/// Loading takes the running game's recipe set rather than reading recipes out
/// of the file. That is the deliberate choice: a save then picks up balance
/// changes instead of freezing the recipes it was written with, which is what a
/// factory game wants from a patch. The cost is that a removed recipe id makes
/// old saves unloadable, so the loader names the missing recipe rather than
/// dropping the machine and leaving the player to notice a hole in their
/// factory hours later.
public static class SaveGame
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ---- capture -----------------------------------------------------------

    public static SaveFile Capture(World world)
    {
        var save = new SaveFile
        {
            Version = SaveFile.CurrentVersion,
            Seed = world.Seed,
            Tick = world.TickCount,
            Items = world.Items.Names.ToList(),
        };

        foreach (var (item, count) in world.PlayerInventory.Contents.OrderBy(kv => kv.Key.Value))
            save.Player.Add(new StackSave { Item = item.Value, Count = count });

        for (var i = 0; i < world.MachineCount; i++)
            save.Machines.Add(CaptureMachine(world, i));

        for (var i = 0; i < world.Miners.Count; i++)
        {
            var miner = world.Miners[i];
            var placement = world.MinerPlacements[i];
            save.Miners.Add(new MinerSave
            {
                Item = miner.Item.Value,
                X = placement.X,
                Y = placement.Y,
                Tier = placement.Tier,
                Category = placement.Category,
                Size = placement.Size,
                CycleTicks = miner.CycleTicks,
                TicksRemaining = miner.RawTicksRemaining,
                Buffered = miner.Buffered,
                Energy = miner.Energy,
                PowerDraw = miner.PowerDraw / Math.Max(1, placement.Area),
                State = miner.State,
            });
        }

        foreach (var (x, y, taken) in world.Ground.Depletion)
            save.Depletion.Add(new DepletionSave { X = x, Y = y, Taken = taken });

        foreach (var pole in world.Power.Poles)
            save.Poles.Add(new PoleSave
            {
                X = pole.X,
                Y = pole.Y,
                SupplyRadius = pole.SupplyRadius,
                WireRadius = pole.WireRadius,
            });

        for (var i = 0; i < world.Power.Accumulators.Count; i++)
        {
            var accumulator = world.Power.Accumulators[i];
            var placement = world.Power.AccumulatorPlacements[i];
            save.Accumulators.Add(new AccumulatorSave
            {
                Capacity = accumulator.Capacity,
                RatePerTick = accumulator.RatePerTick,
                Charge = accumulator.Charge,
                X = placement.X,
                Y = placement.Y,
                Tier = placement.Tier,
                Category = placement.Category,
                Size = placement.Size,
            });
        }

        for (var i = 0; i < world.Power.Generators.Count; i++)
        {
            var generator = world.Power.Generators[i];
            var placement = world.Power.GeneratorPlacements[i];
            save.Generators.Add(new GeneratorSave
            {
                Fuel = generator.Fuel.Value,
                OutputPerTick = generator.OutputPerTick,
                TicksPerFuel = generator.TicksPerFuel,
                FuelStock = generator.FuelStock,
                BurnTicksLeft = generator.BurnTicksLeft,
                X = placement.X,
                Y = placement.Y,
                Tier = placement.Tier,
                Category = placement.Category,
                Size = placement.Size,
            });
        }

        save.Belts = CaptureBelts(world.Belts, world.BeltMap);

        for (var i = 0; i < world.Extractors.Count; i++)
        {
            var extractor = world.Extractors[i];
            var placement = world.ExtractorPlacements[i];
            save.Extractors.Add(new ExtractorSave
            {
                Fluid = extractor.Fluid.Value,
                Ambient = extractor.Ambient,
                X = placement.X,
                Y = placement.Y,
                Tier = placement.Tier,
                Category = placement.Category,
                Size = placement.Size,
                CycleTicks = extractor.CycleTicks,
                PowerDraw = extractor.PowerDraw / Math.Max(1, placement.Area),
                TicksRemaining = extractor.RawTicksRemaining,
                Buffered = extractor.Buffered,
                Energy = extractor.Energy,
                State = extractor.State,
            });
        }

        foreach (var drone in world.Logistics.Drones)
            save.Drones.Add(new DroneSave
            {
                X = drone.X,
                Y = drone.Y,
                Capacity = drone.Capacity,
                Speed = drone.Speed,
                Cargo = drone.Cargo.Value,
                CargoCount = drone.CargoCount,
                Task = drone.Task,
                Progress = drone.Progress,
                Waiting = drone.Waiting,
            });

        foreach (var task in world.Logistics.Tasks)
            save.Tasks.Add(new HaulTaskSave
            {
                Item = task.Item.Value,
                Count = task.Count,
                FromX = task.FromX,
                FromY = task.FromY,
                ToX = task.ToX,
                ToY = task.ToY,
                State = task.State,
                Drone = task.Drone,
                Delivered = task.Delivered,
            });

        foreach (var controller in world.Controllers)
            save.Controllers.Add(new ControllerSave
            {
                Source = controller.Source,
                State = controller.State
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new StateEntry { Key = kv.Key, Value = kv.Value })
                    .ToList(),
            });

        foreach (var node in world.Fluids.Nodes)
            save.FluidNodes.Add(new FluidNodeSave
            {
                X = node.X,
                Y = node.Y,
                Kind = node.Kind,
                Capacity = node.Capacity,
                Throughput = node.Throughput,
            });

        foreach (var network in world.Fluids.Networks)
            save.Fluids.Add(new FluidNetworkSave
            {
                Fluid = network.Fluid.Value,
                Amount = network.Amount,
            });

        return save;
    }

    private static MachineSave CaptureMachine(World world, int index)
    {
        var machine = world.Machines[index];
        var placement = world.PlacementOf(index);

        var entry = new MachineSave
        {
            Recipe = machine.Recipe.Id,
            // Unscale: the constructor multiplies by parallelism, so storing the
            // scaled value would multiply it again on every load.
            OutputCapacityPerItem = machine.OutputCapacityPerItem / machine.Parallelism,
            X = placement.X,
            Y = placement.Y,
            Tier = placement.Tier,
            Category = placement.Category,
            Size = placement.Size,
            Placed = world.IsPlaced(index),
            TicksRemaining = machine.RawTicksRemaining,
            Energy = machine.Energy,
            State = machine.State,
        };

        foreach (var (item, count) in machine.InputContents.OrderBy(kv => kv.Key.Value))
            entry.Inputs.Add(new StackSave { Item = item.Value, Count = count });
        foreach (var (item, count) in machine.OutputContents.OrderBy(kv => kv.Key.Value))
            entry.Outputs.Add(new StackSave { Item = item.Value, Count = count });

        return entry;
    }

    private static BeltNetworkSave CaptureBelts(BeltNetwork belts, BeltMap map)
    {
        var save = new BeltNetworkSave();

        foreach (var belt in map.Belts)
            save.Tiles.Add(new BeltTileSave
            {
                X = belt.X,
                Y = belt.Y,
                Facing = (int)belt.Facing,
                Speed = belt.Speed,
            });

        foreach (var inserter in map.Inserters)
            save.InserterTiles.Add(new InserterTileSave
            {
                X = inserter.X,
                Y = inserter.Y,
                Facing = (int)inserter.Facing,
                SwingTicks = inserter.SwingTicks,
                StackSize = inserter.StackSize,
            });

        foreach (var end in map.Undergrounds)
            save.UndergroundTiles.Add(new UndergroundTileSave
            {
                X = end.X,
                Y = end.Y,
                Facing = (int)end.Facing,
                Speed = end.Speed,
                Reach = end.Reach,
                Entrance = end.IsEntrance,
            });

        foreach (var splitter in map.Splitters)
            save.SplitterTiles.Add(new SplitterTileSave
            {
                X = splitter.X,
                Y = splitter.Y,
                Facing = (int)splitter.Facing,
            });

        foreach (var segment in belts.Segments)
        {
            var entry = new BeltSegmentSave { Tiles = segment.Tiles, Speed = segment.Speed };
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var items = new List<ItemId>();
                var gaps = new List<int>();
                segment.LaneAt(lane).CopyTo(items, gaps);
                entry.Lanes.Add(new LaneSave
                {
                    Items = items.Select(i => i.Value).ToList(),
                    Gaps = gaps,
                });
            }

            save.Segments.Add(entry);
        }

        foreach (var endpoint in belts.LaneOutputs)
            save.LaneOutputs.Add(ToSave(endpoint));

        foreach (var splitter in belts.Splitters)
            save.Splitters.Add(new SplitterSave
            {
                Outputs = splitter.Outputs.Select(ToSave).ToList(),
                Buffer = splitter.BufferedItems.Select(i => i.Value).ToList(),
                Next = splitter.Next,
            });

        foreach (var inserter in belts.Inserters)
            save.Inserters.Add(new InserterSave
            {
                Source = ToSave(inserter.Source),
                Target = ToSave(inserter.Target),
                SwingTicks = inserter.SwingTicks,
                StackSize = inserter.StackSize,
                HeldItem = inserter.HeldItem.Value,
                Held = inserter.Held,
                Cooldown = inserter.Cooldown,
            });

        return save;
    }

    private static EndpointSave ToSave(Endpoint endpoint) =>
        new() { Kind = endpoint.Kind, Index = endpoint.Index, Lane = endpoint.Lane };

    // ---- restore -----------------------------------------------------------

    /// Rebuilds a World. `recipes` is the running game's recipe set, keyed by
    /// recipe id.
    public static World Restore(SaveFile save, IReadOnlyDictionary<string, Recipe> recipes)
        => Restore(save, recipes, null);

    /// `gen` is the world generator to restore against. Passing null gives a
    /// world with no terrain, which is right for tests that only care about
    /// machines and wrong for a real load -- a save stores the seed, not the
    /// map, so the caller has to supply the generator built from it.
    public static World Restore(SaveFile save, IReadOnlyDictionary<string, Recipe> recipes,
                                WorldGen? gen)
    {
        if (save.Version != SaveFile.CurrentVersion)
            throw new SaveLoadException(
                $"save version {save.Version} is not version {SaveFile.CurrentVersion}; " +
                "this save was written by a different build of the game");

        // Rebuild the item table in the save's own order, so every index in the
        // file resolves to the same item it was written for. Registering by name
        // is what makes the save independent of the data files' current order.
        var items = new ItemDatabase();
        foreach (var name in save.Items)
            items.Register(name);

        var world = new World(save.Seed, items, gen);
        world.RestoreTick(save.Tick);

        world.Ground.Restore(save.Depletion.Select(d => (d.X, d.Y, d.Taken)));

        world.PlayerInventory.Restore(save.Player.Select(s => (Item(s.Item, save), s.Count)).ToList());

        foreach (var entry in save.Machines)
            RestoreMachine(world, entry, recipes, save);

        foreach (var entry in save.Miners)
        {
            var placement = new MachinePlacement(entry.X, entry.Y, (byte)entry.Tier,
                                                 (byte)entry.Category, (byte)entry.Size);
            var miner = world.AddSavedMiner(Item(entry.Item, save), placement, entry.CycleTicks,
                                            entry.PowerDraw);
            miner.Restore(entry.State, entry.TicksRemaining, entry.Buffered, entry.Energy);
        }

        foreach (var entry in save.Poles)
            world.Power.AddPole(new Pole(entry.X, entry.Y, entry.SupplyRadius, entry.WireRadius));

        foreach (var entry in save.Generators)
        {
            var generator = new Generator(Item(entry.Fuel, save), entry.OutputPerTick,
                                          entry.TicksPerFuel);
            generator.Restore(entry.FuelStock, entry.BurnTicksLeft);
            world.TryPlaceGenerator(generator, new MachinePlacement(
                entry.X, entry.Y, (byte)entry.Tier, (byte)entry.Category, (byte)entry.Size));
        }

        for (var i = 0; i < save.Accumulators.Count; i++)
        {
            var entry = save.Accumulators[i];
            var accumulator = new Accumulator(entry.Capacity, entry.RatePerTick);
            accumulator.Restore(entry.Charge);
            world.TryPlaceAccumulator(accumulator, new MachinePlacement(
                entry.X, entry.Y, (byte)entry.Tier, (byte)entry.Category, (byte)entry.Size));
        }

        foreach (var entry in save.Extractors)
        {
            var placement = new MachinePlacement(entry.X, entry.Y, (byte)entry.Tier,
                                                 (byte)entry.Category, (byte)entry.Size);
            var extractor = new FluidExtractor(Item(entry.Fluid, save), entry.Ambient,
                                               placement.Area, entry.CycleTicks, entry.PowerDraw);
            extractor.Restore(entry.State, entry.TicksRemaining, entry.Buffered, entry.Energy);
            world.AddSavedExtractor(extractor, placement);
        }

        // Tasks before drones, because a drone's Task index points into this
        // list and a drone restored first would point at nothing.
        foreach (var entry in save.Tasks)
        {
            var task = new HaulTask(Item(entry.Item, save), entry.Count,
                                    entry.FromX, entry.FromY, entry.ToX, entry.ToY);
            task.Restore(entry.State, entry.Drone, entry.Delivered);
            world.Logistics.AddTask(task);
        }

        foreach (var entry in save.Drones)
        {
            var drone = new Drone(entry.X, entry.Y, entry.Capacity, entry.Speed);
            drone.Restore(entry.X, entry.Y, Item(entry.Cargo, save), entry.CargoCount,
                          entry.Task, entry.Progress, entry.Waiting);
            world.Logistics.AddDrone(drone);
        }

        foreach (var entry in save.Controllers)
        {
            var controller = world.AddController(entry.Source);
            controller.RestoreState(entry.State.Select(
                e => new KeyValuePair<string, string>(e.Key, e.Value)));
        }

        RestoreBelts(world, save.Belts, save);

        // Nodes first, in file order, so the rebuilt networks are numbered the
        // same way they were when the file was written.
        foreach (var node in save.FluidNodes)
            switch (node.Kind)
            {
                case FluidNodeKind.Tank: world.Fluids.AddTank(node.X, node.Y, node.Capacity); break;
                case FluidNodeKind.Pump: world.Fluids.AddPump(node.X, node.Y, node.Throughput); break;
                default: world.Fluids.AddPipe(node.X, node.Y, node.Throughput); break;
            }

        for (var i = 0; i < save.Fluids.Count && i < world.Fluids.NetworkCount; i++)
        {
            var network = world.Fluids.Network(i);
            var entry = save.Fluids[i];
            network.Restore(network.Capacity, network.ThroughputPerTick,
                            Item(entry.Fluid, save), entry.Amount);
        }

        return world;
    }

    private static void RestoreMachine(World world, MachineSave entry,
                                       IReadOnlyDictionary<string, Recipe> recipes, SaveFile save)
    {
        if (!recipes.TryGetValue(entry.Recipe, out var recipe))
            throw new SaveLoadException(
                $"this save contains a machine running '{entry.Recipe}', which no longer exists " +
                "in the game's recipes");

        var placement = new MachinePlacement(entry.X, entry.Y, (byte)entry.Tier,
                                             (byte)entry.Category, (byte)entry.Size);

        var machine = entry.Placed
            ? world.TryPlaceMachine(recipe, placement, entry.OutputCapacityPerItem)
            : world.AddMachine(recipe, placement, entry.OutputCapacityPerItem);

        if (machine is null)
            throw new SaveLoadException(
                $"two machines in this save occupy the tile {entry.X},{entry.Y}");

        machine.Restore(entry.State, entry.TicksRemaining,
                        entry.Inputs.Select(s => (Item(s.Item, save), s.Count)).ToList(),
                        entry.Outputs.Select(s => (Item(s.Item, save), s.Count)).ToList(),
                        entry.Energy);
    }

    private static void RestoreBelts(World world, BeltNetworkSave save, SaveFile file)
    {
        var belts = world.Belts;

        // Tiles first. Segments are compiled from them, so placing the tiles in
        // their saved order and compiling reproduces exactly the segment list
        // that was saved -- which is what makes restoring lane contents by
        // index below correct.
        foreach (var entry in save.Tiles)
            world.BeltMap.PlaceBelt(entry.X, entry.Y, (Direction)entry.Facing, entry.Speed);

        foreach (var entry in save.InserterTiles)
            world.BeltMap.PlaceInserter(entry.X, entry.Y, (Direction)entry.Facing,
                                        entry.SwingTicks, entry.StackSize);

        // Underground ends restore their saved role rather than re-deriving it
        // from placement order: a save is not placed in the order it was built
        // in the first place -- it is placed in list order -- and a pair whose
        // entrance loaded second would come back as two entrances.
        foreach (var entry in save.UndergroundTiles)
            world.BeltMap.RestoreUnderground(entry.X, entry.Y, (Direction)entry.Facing,
                                             entry.Speed, entry.Reach, entry.Entrance);

        foreach (var entry in save.SplitterTiles)
            world.BeltMap.PlaceSplitter(entry.X, entry.Y, (Direction)entry.Facing);

        var compiled = save.Tiles.Count > 0
                       || save.UndergroundTiles.Count > 0
                       || save.SplitterTiles.Count > 0;
        if (compiled) world.SyncBelts();

        for (var i = 0; i < save.Segments.Count; i++)
        {
            var entry = save.Segments[i];

            // A compiled world already has its segments; a hand-built one still
            // needs them made.
            var id = compiled ? i : belts.AddSegment(entry.Tiles, entry.Speed);
            if (id >= belts.Segments.Count) continue;

            var segment = belts.Segment(id);

            for (var lane = 0; lane < entry.Lanes.Count && lane < BeltSegment.LaneCount; lane++)
            {
                var laneSave = entry.Lanes[lane];
                segment.LaneAt(lane).Restore(
                    laneSave.Items.Select(v => Item(v, file)).ToList(), laneSave.Gaps);
            }
        }

        // Compiled worlds already have their outputs wired by the compile; a
        // hand-built one carries them in the save.
        if (!compiled)
            for (var i = 0; i < save.LaneOutputs.Count; i++)
            {
                var segment = i / BeltSegment.LaneCount;
                var lane = i % BeltSegment.LaneCount;
                belts.SetOutput(segment, lane, FromSave(save.LaneOutputs[i]));
            }

        // A compiled world made its splitters from tiles already, wired to
        // whatever the tiles say; only what they are holding still needs
        // restoring. A hand-built one carries its wiring in the save.
        for (var i = 0; i < save.Splitters.Count; i++)
        {
            var entry = save.Splitters[i];
            var id = compiled
                ? i
                : belts.AddSplitter(FromSave(entry.Outputs[0]), FromSave(entry.Outputs[1]));
            if (id >= belts.Splitters.Count) continue;
            belts.Splitters[id].Restore(entry.Buffer.Select(v => Item(v, file)).ToList(), entry.Next);
        }

        foreach (var entry in save.Inserters)
        {
            // A compiled world made its inserters from tiles already; only what
            // their arms are holding still needs restoring.
            var id = compiled
                ? save.Inserters.IndexOf(entry)
                : belts.AddInserter(FromSave(entry.Source), FromSave(entry.Target),
                                    entry.SwingTicks, entry.StackSize);
            if (id < 0 || id >= belts.Inserters.Count) continue;
            belts.Inserters[id].Restore(Item(entry.HeldItem, file), entry.Held, entry.Cooldown);
        }
    }

    private static Endpoint FromSave(EndpointSave save) => save.Kind switch
    {
        EndpointKind.Belt => Endpoint.Belt(save.Index, save.Lane),
        EndpointKind.Machine => Endpoint.Machine(save.Index),
        EndpointKind.Splitter => Endpoint.Splitter(save.Index),
        _ => Endpoint.None,
    };

    /// Resolves a saved item index. Out-of-range means the file is inconsistent
    /// with its own item table, which is corruption rather than a version skew.
    private static ItemId Item(int index, SaveFile save)
    {
        if (index < 0 || index >= save.Items.Count)
            throw new SaveLoadException(
                $"item index {index} is outside this save's table of {save.Items.Count} items");
        return new ItemId(index);
    }

    // ---- files -------------------------------------------------------------

    public static string ToJson(SaveFile save) => JsonSerializer.Serialize(save, Options);

    public static SaveFile FromJson(string json)
    {
        SaveFile? save;
        try
        {
            save = JsonSerializer.Deserialize<SaveFile>(json, Options);
        }
        catch (JsonException e)
        {
            throw new SaveLoadException("this save file is not valid JSON: " + e.Message);
        }

        return save ?? throw new SaveLoadException("this save file is empty");
    }

    public static void Write(World world, string path) =>
        File.WriteAllText(path, ToJson(Capture(world)));

    public static World Read(string path, IReadOnlyDictionary<string, Recipe> recipes,
                             WorldGen? gen = null)
    {
        if (!File.Exists(path))
            throw new SaveLoadException($"there is no save file at {path}");
        return Restore(FromJson(File.ReadAllText(path)), recipes, gen);
    }
}
