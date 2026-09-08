using Sim.Data;

namespace Sim;

/// One rung of the opening, as a player reads it.
public readonly struct GuideStep
{
    /// Imperative, one action: "Place the Uplink".
    public string Title { get; }

    /// One or two sentences: why, and how.
    public string Detail { get; }

    /// The key that starts it, or "" when it is not a key but a place to walk
    /// to or a thing to wait for.
    public string Key { get; }

    public bool Done { get; }

    public GuideStep(string title, string detail, string key, bool done)
    {
        Title = title;
        Detail = detail;
        Key = key;
        Done = done;
    }
}

/// The first five minutes, derived from world state (docs/0030).
///
/// Nothing here is stored, subscribed to or advanced. Every step asks the world
/// a question -- is an Uplink placed, has research got its first ore, has
/// anything reached the Uplink that the player did not carry -- and answers it
/// the same way whichever order the player did things in. That is deliberate,
/// and it is the finding Wube published about the tutorial they deleted: a
/// tutorial that watches for *inputs* teaches the player to solve the tutorial.
/// A player who places the Uplink before mining, belts before hand-feeding, or
/// works the whole thing out with no panel open, gets credit for what is true.
///
/// The consequences of that choice, both of them deliberate:
///
/// - Steps are **monotone**, not merely conditional. "You are holding ore" stops
///   being true the moment the ore is smelted, so a step is done once any later
///   step is done. Without this the guide would walk backwards, which is worse
///   than saying nothing.
/// - A step can be done before it is reached, and then `Current` skips it.
///
/// Deterministic and allocation-cheap enough to call once a frame: it reads
/// counts and set membership, and does not tick anything.
public static class Guide
{
    // The rungs of the opening ladder in `data/techs.json`. Named here because
    // the *order* of the opening is a design decision that lives with this
    // file; what each rung costs and pays out lives in the data.
    public const string TechFirstOre = "tech_start_uplink";
    public const string TechFirstMetal = "tech_start_smelting";
    public const string TechHaulage = "tech_start_haulage";
    public const string TechLine = "tech_start_line";

    /// What the player is told to put down first, and what the guide watches
    /// for to know they did.
    public const string FurnaceItem = "man_furnace";

    /// The first step that is not done, or null when the ladder is finished or
    /// this world has no research to hang it on.
    public static GuideStep? Current(World world)
    {
        var steps = Steps(world);
        foreach (var step in steps)
            if (!step.Done) return step;

        return null;
    }

    /// Every step, in order, each with its own done-ness. A world without
    /// research -- the demo factory, a throughput harness -- has no opening and
    /// gets an empty list rather than a ladder of steps it can never complete.
    public static IReadOnlyList<GuideStep> Steps(World world)
    {
        var research = world.Research;
        if (research is null) return Array.Empty<GuideStep>();

        // `Implies` says whether this step being done proves every earlier one
        // happened. A delivery does: you cannot deliver an ore you did not mine
        // into an Uplink you did not place. Placing something does not -- an
        // Uplink on the map says nothing about whether anything has been mined,
        // and a guide that ticked "find ore" because a building went down would
        // be reporting work the player has not done, which is the one failure
        // mode worse than having no guide.
        var raw = new List<(string Title, string Detail, string Key, bool Done, bool Implies)>();

        var ores = Accepts(research, TechFirstOre);

        raw.Add(("Find ore and mine it",
                 "Press P to survey: the panel ranks what is nearby and marks what you can " +
                 "actually use. Walk to the nearest usable patch and mine it by hand.",
                 "P",
                 ores.Any(ore => Holding(world, ore)),
                 false));

        raw.Add(("Place the Uplink",
                 "You are carrying it. Press B, pick the Uplink, and put it down somewhere " +
                 "you will not mind walking to -- everything you research is delivered here.",
                 "B",
                 world.Uplinks.Count > 0,
                 false));

        raw.Add(("Deliver one ore to the Uplink",
                 "Stand next to the Uplink and hand it a single ore. That is the whole loop " +
                 "the rest of the game is made of, at its smallest.",
                 "",
                 research.IsTechUnlocked(TechFirstOre),
                 true));

        raw.Add(("Put down the Manual Furnace",
                 "The Uplink gave you one -- a stone furnace. Place it near your ore, then load it by hand: ore " +
                 "in, ingots out.",
                 "B",
                 PlacedFrom(world, FurnaceItem),
                 false));

        raw.Add(("Deliver three ingots",
                 "Smelt what you mined and carry the ingots to the Uplink. Three trips is " +
                 "about where carrying things stops being interesting.",
                 "",
                 research.IsTechUnlocked(TechFirstMetal),
                 true));

        raw.Add(("Deliver six more ingots",
                 "You have inserters now. An inserter between the furnace and the Uplink " +
                 "walks that last stretch for you.",
                 "",
                 research.IsTechUnlocked(TechHaulage),
                 true));

        raw.Add(("Let the belt make the delivery",
                 "Build a belt from the furnace to the Uplink, with an inserter at each end. " +
                 "Then stand still and watch something arrive that you did not carry.",
                 "B",
                 world.UnattendedDeliveries > 0,
                 true));

        raw.Add(("Deliver twelve ingots without carrying them",
                 "Fill the furnace, leave it running, and let the line finish the objective " +
                 "while you go and find the next ore patch.",
                 "",
                 research.IsTechUnlocked(TechLine),
                 true));

        raw.Add(("Build a Steam Machine Hull and deliver it",
                 "Bronze is copper and tin alloyed together, and eight plates of it make a " +
                 "hull. Delivering one opens the Steam tier: powered machines, and a miner " +
                 "that digs without you.",
                 "",
                 SteamOpen(research),
                 true));

        // Monotone from the back, but only across the steps that prove
        // something. Without this a player who smelts the ore they are carrying
        // watches step one un-tick, because "you are holding ore" stopped being
        // true the moment they used it.
        var steps = new GuideStep[raw.Count];
        var proven = false;
        for (var i = raw.Count - 1; i >= 0; i--)
        {
            var done = raw[i].Done || proven;
            proven = proven || (raw[i].Done && raw[i].Implies);
            steps[i] = new GuideStep(raw[i].Title, raw[i].Detail, raw[i].Key, done);
        }

        return steps;
    }

    /// The items a rung will accept, read out of the tech data rather than
    /// listed here -- adding a smeltable ore to `progression.json` has to widen
    /// what the first step is looking for, with no second place to edit.
    private static IReadOnlyList<string> Accepts(Research research, string techId)
    {
        foreach (var tech in research.AllTechs)
        {
            if (tech.Id != techId) continue;
            var needs = research.Objective(tech).Needs;
            if (needs.Count > 0) return needs[0].Accepts;
        }

        return Array.Empty<string>();
    }

    private static bool Holding(World world, string itemId)
        => world.Items.TryGetId(itemId, out var id) && world.PlayerInventory.Count(id) > 0;

    /// Whether anything on the map was placed from this item. Asked of the
    /// machines rather than of the build log, because a machine retasked to a
    /// different recipe (ADR 0021) is still the furnace the player put down.
    private static bool PlacedFrom(World world, string itemId)
    {
        if (!world.Items.TryGetId(itemId, out var id)) return false;

        foreach (var machine in world.Machines)
            if (machine.SourceItem is { } source && source.Equals(id))
                return true;

        return false;
    }

    /// The ladder ends where the authored tech tree begins: any Steam line
    /// open means a hull was delivered and the opening has handed over.
    private static bool SteamOpen(Research research)
    {
        foreach (var tech in research.AllTechs)
            if (tech.Tier == "STM" && research.IsTechUnlocked(tech.Id))
                return true;

        return false;
    }
}
