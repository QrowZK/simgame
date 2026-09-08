namespace Sim;

/// FNV-1a, 64 bit, over integers.
///
/// Chosen for being three lines and having no state beyond a `ulong`: the hash
/// is part of the determinism contract, so its own implementation has to be
/// something two builds cannot disagree about. A library hash whose seed or
/// vectorisation could differ between runtimes would put the desync detector
/// itself inside the class of bug it exists to find. `string.GetHashCode` is
/// randomised per process in .NET and is exactly that trap, which is why every
/// name below goes in a character at a time.
public static class Hashing
{
    public const ulong Seed = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Mix(ulong hash, byte value) => (hash ^ value) * Prime;

    public static ulong Mix(ulong hash, long value)
    {
        for (var shift = 0; shift < 64; shift += 8)
            hash = Mix(hash, (byte)(value >> shift));
        return hash;
    }

    public static ulong Mix(ulong hash, int value) => Mix(hash, (long)value);

    public static ulong Mix(ulong hash, bool value) => Mix(hash, value ? (byte)1 : (byte)0);

    public static ulong Mix(ulong hash, ulong value) => Mix(hash, unchecked((long)value));

    /// Length first, so "ab" + "c" and "a" + "bc" are different hashes. Without
    /// it a field boundary is invisible and two different worlds collide.
    public static ulong Mix(ulong hash, string value)
    {
        hash = Mix(hash, value.Length);
        foreach (var c in value) hash = Mix(hash, (long)c);
        return hash;
    }
}

public sealed partial class World
{
    /// A hash over everything that decides how this world simulates from here
    /// (ADR 0037). Two peers running lockstep compare it every 60 ticks; the
    /// first tick they disagree on is the tick the desync happened, and without
    /// it the first symptom is a player watching a different factory.
    ///
    /// It walks the same state the save walks, in the same fixed orders, for
    /// the same reason: those orders are already pinned by the byte-identical
    /// save property, so the hash inherits a guarantee rather than inventing a
    /// second one. `StateHashMatchesAcrossASaveRoundTrip` holds the two
    /// together.
    ///
    /// **What is excluded, deliberately:**
    ///
    /// * `LocalIndex` -- which player this peer is looking through. Every peer
    ///   has a different one *by definition*, so hashing it would make two
    ///   correct peers disagree on the first comparison.
    /// * `Player.Intent` -- what the keyboard is holding down. Not saved (a
    ///   loaded world stands still) and not needed: two distinct intents always
    ///   produce two distinct displacements, so an intent that differs shows up
    ///   as a position that differs on the very next tick, and position *is*
    ///   hashed. Hashing it as well would only move the detection one tick
    ///   earlier, at the cost of a hash that changes across a save.
    /// * `Items` -- the item name table. It is built from `data/`, which the
    ///   version handshake (ADR 0035) already pins, and every item below is
    ///   hashed by id against that shared table.
    /// * Anything a renderer keeps: caches, dirty flags, belt map versions.
    ///   None of it is read by the tick.
    ///
    /// Everything else in the world is in here, including the command digest,
    /// which is what makes a *refusal* a hashable event.
    public ulong StateHash()
    {
        var h = Hashing.Seed;

        h = Hashing.Mix(h, Seed);
        h = Hashing.Mix(h, TickCount);
        h = Hashing.Mix(h, _commandDigest);
        h = Hashing.Mix(h, _commandsApplied);
        h = Hashing.Mix(h, _commandsRefused);

        // ---- teams and players, in roster order ----------------------------
        h = Hashing.Mix(h, Teams.Count);
        foreach (var team in Teams)
        {
            h = Hashing.Mix(h, team.Name);
            h = Hashing.Mix(h, team.UnattendedDeliveries);
            if (team.Research is { } research)
            {
                h = Hashing.Mix(h, true);
                h = Hashing.Mix(h, research.SeedDelivered);
                foreach (var tech in research.UnlockedInOrder) h = Hashing.Mix(h, tech);
                foreach (var (objective, item, count) in research.Progress)
                {
                    h = Hashing.Mix(h, objective);
                    h = Hashing.Mix(h, item);
                    h = Hashing.Mix(h, count);
                }
            }
            else
            {
                h = Hashing.Mix(h, false);
            }
        }

        h = Hashing.Mix(h, Players.Count);
        foreach (var player in Players)
        {
            h = Hashing.Mix(h, player.Name);
            h = Hashing.Mix(h, player.TeamId);
            h = Hashing.Mix(h, player.X);
            h = Hashing.Mix(h, player.Y);
            h = Hashing.Mix(h, player.FacingX);
            h = Hashing.Mix(h, player.FacingY);
            h = MixInventory(h, player.Inventory);
        }

        // ---- what is on the map, and who paid for it ------------------------
        foreach (var (x, y, item) in BuiltFrom)
        {
            h = Hashing.Mix(h, x);
            h = Hashing.Mix(h, y);
            h = Hashing.Mix(h, item.Value);
            h = Hashing.Mix(h, OwnerOfAnchor(x, y));
        }

        h = Hashing.Mix(h, MachineCount);
        for (var i = 0; i < MachineCount; i++)
        {
            var machine = Machines[i];
            var placement = PlacementOf(i);
            h = Hashing.Mix(h, machine.Recipe.Id);
            h = Hashing.Mix(h, machine.SourceItem?.Value ?? -1);
            h = Hashing.Mix(h, machine.OutputCapacityPerItem);
            h = MixPlacement(h, placement);
            h = Hashing.Mix(h, IsPlaced(i));
            h = Hashing.Mix(h, machine.RawTicksRemaining);
            h = Hashing.Mix(h, machine.Energy);
            h = Hashing.Mix(h, (int)machine.State);
            foreach (var (item, count) in machine.InputContents.OrderBy(kv => kv.Key.Value))
            {
                h = Hashing.Mix(h, item.Value);
                h = Hashing.Mix(h, count);
            }
            foreach (var (item, count) in machine.OutputContents.OrderBy(kv => kv.Key.Value))
            {
                h = Hashing.Mix(h, item.Value);
                h = Hashing.Mix(h, count);
            }
        }

        h = Hashing.Mix(h, Miners.Count);
        for (var i = 0; i < Miners.Count; i++)
        {
            var miner = Miners[i];
            h = MixPlacement(h, MinerPlacements[i]);
            h = Hashing.Mix(h, miner.Item.Value);
            h = Hashing.Mix(h, miner.CycleTicks);
            h = Hashing.Mix(h, miner.RawTicksRemaining);
            h = Hashing.Mix(h, miner.Buffered);
            h = Hashing.Mix(h, miner.Energy);
            h = Hashing.Mix(h, miner.PowerDraw);
            h = Hashing.Mix(h, (int)miner.State);
        }

        h = Hashing.Mix(h, Extractors.Count);
        for (var i = 0; i < Extractors.Count; i++)
        {
            var extractor = Extractors[i];
            h = MixPlacement(h, ExtractorPlacements[i]);
            h = Hashing.Mix(h, extractor.Fluid.Value);
            h = Hashing.Mix(h, extractor.Ambient);
            h = Hashing.Mix(h, extractor.CycleTicks);
            h = Hashing.Mix(h, extractor.RawTicksRemaining);
            h = Hashing.Mix(h, extractor.Buffered);
            h = Hashing.Mix(h, extractor.Energy);
            h = Hashing.Mix(h, extractor.PowerDraw);
            h = Hashing.Mix(h, (int)extractor.State);
        }

        // ---- the ground, and what has been taken out of it ------------------
        foreach (var (x, y, taken) in Ground.Depletion)
        {
            h = Hashing.Mix(h, x);
            h = Hashing.Mix(h, y);
            h = Hashing.Mix(h, taken);
        }

        // ---- power ----------------------------------------------------------
        foreach (var pole in Power.Poles)
        {
            h = Hashing.Mix(h, pole.X);
            h = Hashing.Mix(h, pole.Y);
            h = Hashing.Mix(h, pole.SupplyRadius);
            h = Hashing.Mix(h, pole.WireRadius);
        }

        for (var i = 0; i < Power.Generators.Count; i++)
        {
            var generator = Power.Generators[i];
            h = MixPlacement(h, Power.GeneratorPlacements[i]);
            h = Hashing.Mix(h, generator.Fuel.Value);
            h = Hashing.Mix(h, generator.OutputPerTick);
            h = Hashing.Mix(h, generator.TicksPerFuel);
            h = Hashing.Mix(h, generator.FuelStock);
            h = Hashing.Mix(h, generator.BurnTicksLeft);
        }

        for (var i = 0; i < Power.Accumulators.Count; i++)
        {
            var accumulator = Power.Accumulators[i];
            h = MixPlacement(h, Power.AccumulatorPlacements[i]);
            h = Hashing.Mix(h, accumulator.Capacity);
            h = Hashing.Mix(h, accumulator.RatePerTick);
            h = Hashing.Mix(h, accumulator.Charge);
        }

        // ---- belts: the tiles, and every item riding them -------------------
        foreach (var belt in BeltMap.Belts)
        {
            h = Hashing.Mix(h, belt.X);
            h = Hashing.Mix(h, belt.Y);
            h = Hashing.Mix(h, (int)belt.Facing);
            h = Hashing.Mix(h, belt.Speed);
        }

        foreach (var inserter in BeltMap.Inserters)
        {
            h = Hashing.Mix(h, inserter.X);
            h = Hashing.Mix(h, inserter.Y);
            h = Hashing.Mix(h, (int)inserter.Facing);
            h = Hashing.Mix(h, inserter.SwingTicks);
            h = Hashing.Mix(h, inserter.StackSize);
        }

        foreach (var end in BeltMap.Undergrounds)
        {
            h = Hashing.Mix(h, end.X);
            h = Hashing.Mix(h, end.Y);
            h = Hashing.Mix(h, (int)end.Facing);
            h = Hashing.Mix(h, end.Speed);
            h = Hashing.Mix(h, end.Reach);
            h = Hashing.Mix(h, end.IsEntrance);
        }

        foreach (var splitter in BeltMap.Splitters)
        {
            h = Hashing.Mix(h, splitter.X);
            h = Hashing.Mix(h, splitter.Y);
            h = Hashing.Mix(h, (int)splitter.Facing);
        }

        var items = new List<ItemId>();
        var gaps = new List<int>();
        foreach (var segment in Belts.Segments)
        {
            h = Hashing.Mix(h, segment.Tiles);
            h = Hashing.Mix(h, segment.Speed);
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                items.Clear();
                gaps.Clear();
                segment.LaneAt(lane).CopyTo(items, gaps);
                h = Hashing.Mix(h, items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    h = Hashing.Mix(h, items[i].Value);
                    h = Hashing.Mix(h, gaps[i]);
                }
            }
        }

        foreach (var endpoint in Belts.LaneOutputs) h = MixEndpoint(h, endpoint);

        foreach (var splitter in Belts.Splitters)
        {
            foreach (var output in splitter.Outputs) h = MixEndpoint(h, output);
            foreach (var buffered in splitter.BufferedItems) h = Hashing.Mix(h, buffered.Value);
            h = Hashing.Mix(h, splitter.Next);
        }

        foreach (var inserter in Belts.Inserters)
        {
            h = MixEndpoint(h, inserter.Source);
            h = MixEndpoint(h, inserter.Target);
            h = Hashing.Mix(h, inserter.SwingTicks);
            h = Hashing.Mix(h, inserter.StackSize);
            h = Hashing.Mix(h, inserter.HeldItem.Value);
            h = Hashing.Mix(h, inserter.Held);
            h = Hashing.Mix(h, inserter.Cooldown);
        }

        // ---- fluids ----------------------------------------------------------
        foreach (var node in Fluids.Nodes)
        {
            h = Hashing.Mix(h, node.X);
            h = Hashing.Mix(h, node.Y);
            h = Hashing.Mix(h, (int)node.Kind);
            h = Hashing.Mix(h, node.Capacity);
            h = Hashing.Mix(h, node.Throughput);
        }

        foreach (var network in Fluids.Networks)
        {
            h = Hashing.Mix(h, network.Fluid.Value);
            h = Hashing.Mix(h, network.Amount);
        }

        // ---- drones and their jobs -------------------------------------------
        foreach (var drone in Logistics.Drones)
        {
            h = Hashing.Mix(h, drone.X);
            h = Hashing.Mix(h, drone.Y);
            h = Hashing.Mix(h, drone.Capacity);
            h = Hashing.Mix(h, drone.Speed);
            h = Hashing.Mix(h, drone.Cargo.Value);
            h = Hashing.Mix(h, drone.CargoCount);
            h = Hashing.Mix(h, drone.Task);
            h = Hashing.Mix(h, drone.Progress);
            h = Hashing.Mix(h, drone.Waiting);
        }

        foreach (var task in Logistics.Tasks)
        {
            h = Hashing.Mix(h, task.Item.Value);
            h = Hashing.Mix(h, task.Count);
            h = Hashing.Mix(h, task.FromX);
            h = Hashing.Mix(h, task.FromY);
            h = Hashing.Mix(h, task.ToX);
            h = Hashing.Mix(h, task.ToY);
            h = Hashing.Mix(h, (int)task.State);
            h = Hashing.Mix(h, task.Drone);
            h = Hashing.Mix(h, task.Delivered);
        }

        // ---- controller programs, and the variables they are holding ---------
        foreach (var controller in Controllers)
        {
            h = Hashing.Mix(h, controller.Source);
            foreach (var (key, value) in controller.State.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                h = Hashing.Mix(h, key);
                h = Hashing.Mix(h, value);
            }
        }

        return h;
    }

    private static ulong MixInventory(ulong h, Inventory inventory)
    {
        foreach (var (item, count) in inventory.Contents.OrderBy(kv => kv.Key.Value))
        {
            h = Hashing.Mix(h, item.Value);
            h = Hashing.Mix(h, count);
        }
        return h;
    }

    private static ulong MixPlacement(ulong h, in MachinePlacement placement)
    {
        h = Hashing.Mix(h, placement.X);
        h = Hashing.Mix(h, placement.Y);
        h = Hashing.Mix(h, placement.Tier);
        h = Hashing.Mix(h, placement.Category);
        h = Hashing.Mix(h, placement.Size);
        return h;
    }

    private static ulong MixEndpoint(ulong h, in Endpoint endpoint)
    {
        h = Hashing.Mix(h, (int)endpoint.Kind);
        h = Hashing.Mix(h, endpoint.Index);
        h = Hashing.Mix(h, endpoint.Lane);
        return h;
    }
}
