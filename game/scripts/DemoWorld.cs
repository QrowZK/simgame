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
        var db = new ItemDatabase();
        var ore = db.Register("iron_ore");
        var plate = db.Register("iron_plate");
        var gear = db.Register("iron_gear");

        var smelt = new Recipe("smelt_iron_plate", 192,
            new[] { new RecipeInput(ore, 1) },
            new[] { new RecipeOutput(plate, 1) });

        var assemble = new Recipe("assemble_iron_gear", 120,
            new[] { new RecipeInput(plate, 2) },
            new[] { new RecipeOutput(gear, 1) });

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
