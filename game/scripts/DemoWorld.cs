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

        var smelt = catalogue.Recipe("smelt_chalcopyrite");
        var assemble = catalogue.Recipe("form_copper_plate");
        var ore = catalogue.Item("chalcopyrite");
        var plate = catalogue.Item("copper_ingot");

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
