using Sim;
using Sim.Data;
using Sim.Save;

namespace Sim.Tests;

/// Retasking a placed machine, and the question ADR 0015 deferred and ADR 0021
/// answers: what happens to what is already inside it.
///
/// The invariant every test here is really testing is **conservation**: a
/// recipe change moves items between the machine and the player and creates or
/// destroys none. A machine that quietly eats a stack of ore on a recipe change
/// is the kind of thing that loses a save's worth of trust, so the number
/// handed back is asserted exactly rather than as "more than zero".
public class RecipeChangeTests
{
    private static readonly Catalogue Data = Catalogue.Instance;
    private static readonly BuildCatalogue Buildables = new(Catalogue.Instance);

    /// Every recipe by id, which is what the loader looks names up in.
    private static readonly Dictionary<string, Recipe> Recipes =
        Catalogue.Instance.Data.Recipes.ToDictionary(r => r.Id, r => Catalogue.Instance.Recipe(r.Id));

    /// A furnace on the ground, placed the way a player places one.
    private static (World World, int Index) FurnaceWorld()
    {
        var world = new World(seed: 7, Data.Items);
        var furnace = Buildables.Find("man_furnace")!;
        world.PlayerInventory.Add(furnace.Item, 1);

        var result = world.TryBuild(Buildables, furnace.Item, 0, 0, Data.Recipe("smelt_chalcopyrite"));
        Assert.Equal(BuildResult.Ok, result);
        return (world, 0);
    }

    [Fact]
    public void ARetaskedMachine_MakesTheNewThing()
    {
        var (world, index) = FurnaceWorld();

        Assert.Equal(RecipeChangeResult.Ok,
            world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out _));

        Assert.Equal("smelt_magnetite", world.Machines[index].Recipe.Id);

        world.PlayerInventory.Add(Data.Item("magnetite"), 1);
        HandOps.Insert(world.PlayerInventory, world.Machines[index], Data.Item("magnetite"), 1);
        world.Tick(Data.Recipe("smelt_magnetite").DurationTicks);

        Assert.Equal(1, world.Machines[index].GetOutputCount(Data.Item("iron_ingot")));
    }

    /// The eviction rule, in the messiest state a machine can be in: a part-full
    /// input buffer, finished goods in the output buffer, and a cycle in flight
    /// whose inputs have already been taken out of the input buffer and whose
    /// outputs have not been written yet.
    ///
    /// Exact counts on all three. A test that asserted "something came back"
    /// would pass while the in-flight batch vanished, which is the one of the
    /// three a player is least able to notice.
    [Fact]
    public void Retasking_HandsBackTheInputsTheOutputsAndTheCycleInFlight()
    {
        var (world, index) = FurnaceWorld();
        var machine = world.Machines[index];
        var ore = Data.Item("chalcopyrite");
        var ingot = Data.Item("copper_ingot");

        // Three ore in: one is eaten by the cycle that starts on the next tick,
        // two stay in the buffer.
        machine.PushInput(ore, 3);
        world.Tick(1);
        Assert.Equal(MachineState.Working, machine.State);
        Assert.Equal(2, machine.GetInputCount(ore));

        // Finish one cycle so there is something in the output buffer too, then
        // start another and stop part way through it.
        world.Tick(Data.Recipe("smelt_chalcopyrite").DurationTicks);
        Assert.Equal(1, machine.GetOutputCount(ingot));
        world.Tick(4);
        Assert.Equal(MachineState.Working, machine.State);

        var carriedOre = world.PlayerInventory.Count(ore);
        var carriedIngot = world.PlayerInventory.Count(ingot);

        Assert.Equal(RecipeChangeResult.Ok,
            world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out var evicted));

        // 1 left in the buffer + 1 in the cycle in flight + 1 finished ingot.
        Assert.Equal(3, evicted);
        Assert.Equal(carriedOre + 2, world.PlayerInventory.Count(ore));
        Assert.Equal(carriedIngot + 1, world.PlayerInventory.Count(ingot));

        // And the machine really is empty and stopped, not just reported so.
        Assert.Empty(machine.InputContents);
        Assert.Empty(machine.OutputContents);
        Assert.Equal(0, machine.RawTicksRemaining);
        Assert.Equal(MachineState.Idle, machine.State);
    }

    /// Conservation, stated as itself: across a retask, the total number of
    /// units in the world is unchanged. This is the property; the counts above
    /// are the arithmetic that makes it legible.
    [Fact]
    public void Retasking_CreatesAndDestroysNothing()
    {
        var (world, index) = FurnaceWorld();
        var machine = world.Machines[index];
        machine.PushInput(Data.Item("chalcopyrite"), 5);
        world.Tick(Data.Recipe("smelt_chalcopyrite").DurationTicks + 3);

        int Total() =>
            world.PlayerInventory.Contents.Values.Sum() +
            machine.InputContents.Values.Sum() + machine.OutputContents.Values.Sum() +
            (machine.RawTicksRemaining > 0
                ? machine.Recipe.Inputs.Sum(i => i.Count * machine.Parallelism)
                : 0);

        var before = Total();
        world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out _);
        Assert.Equal(before, Total());
    }

    /// Re-picking the recipe a machine is already running must cost nothing.
    /// A picker that dumps the machine when the player clicks the highlighted
    /// row is a trap, and a mid-cycle one would lose the batch in flight.
    [Fact]
    public void PickingTheSameRecipe_ChangesNothingAndEvictsNothing()
    {
        var (world, index) = FurnaceWorld();
        var machine = world.Machines[index];
        machine.PushInput(Data.Item("chalcopyrite"), 4);
        world.Tick(3);

        var carried = world.PlayerInventory.Contents.Values.Sum();

        Assert.Equal(RecipeChangeResult.AlreadyRunning,
            world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_chalcopyrite"),
                                  out var evicted));

        Assert.Equal(0, evicted);
        Assert.Equal(carried, world.PlayerInventory.Contents.Values.Sum());
        Assert.Equal(3, machine.GetInputCount(Data.Item("chalcopyrite")));
        Assert.Equal(MachineState.Working, machine.State);
    }

    /// A furnace told to run an assembler's recipe is refused, and refusing
    /// costs the machine nothing -- the same rule builds have: a refusal never
    /// takes anything.
    [Fact]
    public void AMachineRefusesARecipeItCannotRun_AndKeepsItsContents()
    {
        var (world, index) = FurnaceWorld();
        var machine = world.Machines[index];
        machine.PushInput(Data.Item("chalcopyrite"), 2);

        Assert.Equal(RecipeChangeResult.CannotRun,
            world.TryChangeRecipe(Buildables, index, Data.Recipe("comp_stm_cable"), out var evicted));

        Assert.Equal(0, evicted);
        Assert.Equal("smelt_chalcopyrite", machine.Recipe.Id);
        Assert.Equal(2, machine.GetInputCount(Data.Item("chalcopyrite")));
        Assert.Empty(world.PlayerInventory.Contents);
    }

    /// A machine at a tier above its own is still refused after placement, not
    /// only before it. The ladder's rule is "its own tier and every tier below",
    /// and the picker asks the same catalogue the build menu does.
    [Fact]
    public void AManualFurnace_IsStillRefusedASteamRecipeAfterPlacement()
    {
        var (world, index) = FurnaceWorld();
        var steamOnly = Buildables.RecipesFor(Buildables.Find("stm_furnace")!)
            .First(r => !Buildables.CanRun(Buildables.Find("man_furnace")!, r));

        Assert.Equal(RecipeChangeResult.CannotRun,
            world.TryChangeRecipe(Buildables, index, steamOnly, out _));
    }

    /// A machine nobody placed -- headless analysis and the demo world build
    /// these -- has no buildable to check against, so it says so rather than
    /// guessing. Exposing the limit beats a picker that silently allows
    /// anything.
    [Fact]
    public void AMachineThatWasNeverPlacedFromAnItem_CannotBeRetasked()
    {
        var world = new World(seed: 7, Data.Items);
        world.AddMachine(Data.Recipe("smelt_chalcopyrite"));

        Assert.Equal(RecipeChangeResult.UnknownMachine,
            world.TryChangeRecipe(Buildables, 0, Data.Recipe("smelt_magnetite"), out _));
        Assert.Equal("smelt_chalcopyrite", world.Machines[0].Recipe.Id);
    }

    /// Both ends of the range, and the one past the last machine in
    /// particular: an off-by-one there is an unhandled exception rather than a
    /// refusal, and the UI holds an index from the last frame.
    [Fact]
    public void ThereIsNoMachineAtAnIndexOutOfRange()
    {
        var (world, _) = FurnaceWorld();
        foreach (var index in new[] { -1, world.MachineCount, world.MachineCount + 3 })
            Assert.Equal(RecipeChangeResult.NoMachine,
                world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out _));
    }

    /// The state the renderer reads is updated by the retask, not by the next
    /// tick. A machine retasked while the game is paused would otherwise keep
    /// drawing the Working attachment of a cycle that no longer exists.
    [Fact]
    public void ARetaskShowsTheNewStateBeforeTheNextTick()
    {
        var (world, index) = FurnaceWorld();
        world.Machines[index].PushInput(Data.Item("chalcopyrite"), 2);
        world.Tick(3);
        Assert.Equal(MachineState.Working, world.MachineStates[index]);

        world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out _);
        Assert.Equal(MachineState.Idle, world.MachineStates[index]);
    }

    /// A retasked machine has to survive a save. Two things could round-trip
    /// wrong and neither is visible in a screenshot: the recipe (loading the
    /// one it was placed with) and the source item (loading a machine nothing
    /// can retask again). The world is deliberately messy -- mid-cycle, with
    /// both buffers part-full -- because a tidy one hides both.
    [Fact]
    public void ARetaskedMachine_SurvivesASaveRoundTrip()
    {
        var (world, index) = FurnaceWorld();
        world.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_magnetite"), out _);

        var machine = world.Machines[index];
        machine.PushInput(Data.Item("magnetite"), 3);
        world.Tick(Data.Recipe("smelt_magnetite").DurationTicks + 5);
        Assert.Equal(MachineState.Working, machine.State);

        var json = SaveGame.ToJson(SaveGame.Capture(world));
        var loaded = SaveGame.Restore(SaveGame.FromJson(json), Recipes);
        var reloaded = loaded.Machines[index];

        Assert.Equal("smelt_magnetite", reloaded.Recipe.Id);
        Assert.Equal(machine.GetInputCount(Data.Item("magnetite")),
                     reloaded.GetInputCount(Data.Item("magnetite")));
        Assert.Equal(machine.GetOutputCount(Data.Item("iron_ingot")),
                     reloaded.GetOutputCount(Data.Item("iron_ingot")));
        Assert.Equal(machine.RawTicksRemaining, reloaded.RawTicksRemaining);
        Assert.Equal(json, SaveGame.ToJson(SaveGame.Capture(loaded)));

        // And it can still be retasked after loading, which is what the source
        // item is stored for.
        Assert.Equal(RecipeChangeResult.Ok,
            loaded.TryChangeRecipe(Buildables, index, Data.Recipe("smelt_cassiterite"), out _));
    }

    /// The save version moved with the format. An older file is refused rather
    /// than loaded with every machine unable to be retasked.
    [Fact]
    public void AnOlderSave_IsRefusedRatherThanLoadedWithoutSourceItems()
    {
        var (world, _) = FurnaceWorld();
        var save = SaveGame.Capture(world);
        Assert.Equal(10, save.Version);

        save.Version = 9;
        Assert.Throws<SaveLoadException>(() => SaveGame.Restore(save, Recipes));
    }

    /// Crude oil is buried like an ore because the derrick has to stand on
    /// something. Hands are not a derrick. The miner still works, which is the
    /// half of this that a blanket ban in `Ground.Extract` would have broken.
    [Fact]
    public void HandsRefuseAFluidDeposit_ButAMinerOnOneStillWorks()
    {
        var world = NewGame.Create(seed: 4, Data);
        var oil = Data.Item("crude_oil");
        var hit = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0)
            .First(h => h.Item.Equals(oil));

        var before = world.Ground.RemainingAt(hit.X, hit.Y);
        Assert.Equal(0, HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory, 10));
        Assert.Equal(before, world.Ground.RemainingAt(hit.X, hit.Y));
        Assert.Equal(0, world.PlayerInventory.Count(oil));

        var derrick = world.TryPlaceMiner(new MachinePlacement(hit.X, hit.Y, 1, 8), cycleTicks: 30);
        Assert.NotNull(derrick);
        world.Tick(120);
        Assert.True(derrick!.Buffered > 0, "a derrick on an oil patch must still extract");
    }

    /// Solids are untouched by the fluid gate. Asserted separately because a
    /// refusal that refuses everything passes the test above.
    [Fact]
    public void HandsStillDigASolidDeposit()
    {
        var world = NewGame.Create(seed: 4, Data);
        var ore = Data.Item("chalcopyrite");
        var hit = new Prospector(radius: 400).Scan(world.Ground.Gen, 0, 0)
            .First(h => h.Item.Equals(ore));

        Assert.Equal(10, HandOps.Mine(world.Ground, hit.X, hit.Y, world.PlayerInventory, 10));
        Assert.Equal(10, world.PlayerInventory.Count(ore));
    }
}
