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
