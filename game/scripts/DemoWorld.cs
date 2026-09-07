using System.Collections.Generic;
using Sim;

namespace Game;

/// Builds the world the Phase 1 renderer draws. Placeholder content: a grid of
/// machines covering every tier and category, deliberately including starved and
/// backpressured ones so all three status colours appear on screen.
///
/// Pure Sim types, no Godot -- the game layer decides what world to build, but
/// the world itself stays engine-free.
public static class DemoWorld
{
    /// Grid stride. Machines are square and at most this many tiles per side.
    private const int MaxFootprint = 3;

    public static World Build(int machineCount, int seed)
    {
        // Real recipes from /data, not invented ones. A placeholder factory
        // whose recipes do not exist in the catalogue cannot have its own save
        // loaded back -- which is exactly the bug this replaced.
        var catalogue = Sim.Data.Catalogue.Instance;
        var db = catalogue.Items;

        // A powered recipe, so the placeholder factory exercises the grid at
        // scale rather than leaving TickPower measuring an empty world.
        var smelt = catalogue.Recipe("crush_chalcopyrite");
        var assemble = catalogue.Recipe("form_copper_plate");
        var ore = catalogue.Item("chalcopyrite");
        var plate = catalogue.Item("copper_ingot");
        var coal = catalogue.Item("coal_deposit");

        var world = new World(seed, db);
        var side = (int)System.Math.Ceiling(System.Math.Sqrt(machineCount));

        for (var i = 0; i < machineCount; i++)
        {
            var cell = i % side;
            var row = i / side;
            var tier = (byte)((cell / 4 + row / 4) % MeshKitTiers);
            var category = (byte)((cell + row * 3) % MeshKitCategories);

            // Footprints vary, so the grid strides by the largest of them --
            // otherwise a 3x3 would swallow its neighbours' tiles and the
            // placement check would reject most of the demo.
            var size = (byte)(category switch
            {
                3 => 3,   // stands in for the bulk process units
                5 => 2,
                _ => 1,
            });

            var x = cell * MaxFootprint;
            var y = row * MaxFootprint;
            var recipe = (i % 3 == 0) ? assemble : smelt;
            var placement = new MachinePlacement(x, y, tier, category, size);

            switch (i % 4)
            {
                case 0:
                    // Starved: placed, powered, but nothing feeding it.
                    world.TryPlaceMachine(recipe, placement);
                    break;

                case 1:
                    // Backpressured: one cycle's worth of room, then it stalls
                    // with its inputs untouched.
                    var blocked = world.TryPlaceMachine(recipe, placement, outputCapacityPerItem: 1);
                    if (blocked is not null) Feed(blocked, recipe, 64);
                    break;

                default:
                    var running = world.TryPlaceMachine(recipe, placement, outputCapacityPerItem: 4096);
                    if (running is not null) Feed(running, recipe, 100_000);
                    break;
            }
        }

        // A grid over the whole factory: poles on a spacing that keeps one
        // network, and generators fuelled well past the length of any test run.
        for (var y = 0; y <= side * MaxFootprint; y += 8)
            for (var x = 0; x <= side * MaxFootprint; x += 8)
                world.Power.AddPole(new Pole(x, y, supplyRadius: 6, wireRadius: 9));

        for (var y = 0; y <= side * MaxFootprint; y += 24)
            for (var x = 0; x <= side * MaxFootprint; x += 24)
            {
                var generator = new Generator(coal, outputPerTick: 400, ticksPerFuel: 100_000);
                generator.AddFuel(1000);
                world.TryPlaceGenerator(generator, new MachinePlacement(x + 1, y + 1, 1, 6, 1));
            }

        // A bank of accumulators on the same grid, so the placeholder factory
        // exercises storage as well -- and so the renderer has some to draw.
        for (var y = 0; y <= side * MaxFootprint; y += 24)
            for (var x = 0; x <= side * MaxFootprint; x += 24)
                world.TryPlaceAccumulator(new Accumulator(capacity: 100_000, ratePerTick: 200),
                                          new MachinePlacement(x + 3, y + 1, 1, 7, 1));

        // A belt line with inserters at both ends, so the placeholder factory
        // exercises the belt compiler and the renderer has items to draw. It
        // runs along y=-4, clear of the machine grid, and is fed by hand from
        // the machine at the origin.
        var beltY = -4;
        var beltLength = System.Math.Min(48, side * MaxFootprint + 1);
        for (var x = 0; x < beltLength; x++)
            world.BeltMap.PlaceBelt(x, beltY, Direction.East, BeltUnits.SpeedFast);

        // A machine off the end of the line with an inserter feeding it, so the
        // demo shows the whole chain -- belt, arm, machine -- rather than a
        // belt that goes nowhere.
        world.TryPlaceMachine(smelt, new MachinePlacement(0, beltY - 2, 1, 1, 1));
        world.BeltMap.PlaceInserter(0, beltY - 1, Direction.North);

        // A second line, clear of the first and of the machine beside it, that exists so the
        // renderer has one of everything a belt map can hold: a paired tunnel
        // with items riding under it, a splitter with a branch, and a lone
        // unpaired tunnel end. None of these are drawable-by-inspection --
        // they had to be on screen to be checked.
        var showY = beltY - 4;
        for (var x = 0; x < 6; x++)
            world.BeltMap.PlaceBelt(x, showY, Direction.East, BeltUnits.SpeedFast);

        world.BeltMap.PlaceUnderground(6, showY, Direction.East, BeltUnits.SpeedFast, 4, out _);
        world.BeltMap.PlaceUnderground(10, showY, Direction.East, BeltUnits.SpeedFast, 4, out _);

        for (var x = 11; x < 15; x++)
            world.BeltMap.PlaceBelt(x, showY, Direction.East, BeltUnits.SpeedFast);

        world.BeltMap.PlaceSplitter(15, showY, Direction.East);
        for (var x = 16; x < 19; x++)
        {
            world.BeltMap.PlaceBelt(x, showY, Direction.East, BeltUnits.SpeedFast);
            world.BeltMap.PlaceBelt(x, showY + 1, Direction.East, BeltUnits.SpeedFast);
        }

        // Deliberately alone: an unpaired end behaves as a one-tile belt, and
        // a player who cannot tell it from half a tunnel cannot debug the line.
        world.BeltMap.PlaceUnderground(22, showY, Direction.East, BeltUnits.SpeedFast, 4, out _);

        world.SyncBelts();

        // Something to carry. Fed straight onto the line rather than through a
        // machine, because the demo exists to measure drawing and ticking.
        // Every segment, not just the one under the first tile: the inserter
        // breaks the run where it reads it, so the line is more than one
        // segment and filling only the first loads eight items onto a
        // forty-eight tile belt.
        for (var segment = 0; segment < world.Belts.Segments.Count; segment++)
            for (var lane = 0; lane < BeltSegment.LaneCount; lane++)
            {
                var line = world.Belts.Segments[segment].LaneAt(lane);
                for (var i = 0; i < line.Capacity; i++)
                    if (!line.TryPack(ore)) break;
            }

        // A pipe run with a tank and a pump on it, so the placeholder factory
        // exercises the plumbing as well as the grid.
        for (var x = 0; x <= side * MaxFootprint; x++)
            world.Fluids.AddPipe(x, -2, FluidNetwork.ThroughputLarge);
        world.Fluids.AddTank(-2, -2);
        world.Fluids.AddPump(-1, -2);

        // A drone fleet and a controller commanding it, so the renderer and the
        // script editor both have something real to show.
        for (var i = 0; i < System.Math.Max(4, machineCount / 40); i++)
            world.Logistics.AddDrone(new Drone(4 + i * 5, 4 + i * 3, capacity: 40, speed: 25));

        world.AddController(@"
-- Keep the far machines fed from the near ones.
while true do
  if queue.pending() < 4 then
    queue.haul('chalcopyrite', 20, 0, 0, 21, 21)
  end
  world.sleep(20)
end
");

        // Something in the player's hands, so the panel's load button has
        // work to do on the starved machines.
        world.PlayerInventory.Add(ore, 500);
        world.PlayerInventory.Add(plate, 200);

        return world;
    }

    private static void Feed(Machine machine, Recipe recipe, int amount)
    {
        foreach (var input in recipe.Inputs)
            machine.PushInput(input.Item, amount);
    }

    // Kept in sync with MeshKit; duplicated as plain ints so this file stays
    // free of any Godot reference.
    private const int MeshKitTiers = 8;
    private const int MeshKitCategories = 10;
}
