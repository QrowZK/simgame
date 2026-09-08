using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using Sim.Data;
using Sim.Save;

namespace Game;

/// Owns the running game: the world, the recipe set its saves resolve against,
/// and where saves live on disk.
///
/// The engine layer decides *when* to save; `/sim` decides *what* a save is.
/// This is the seam between those two, and the only place in `/game` that knows
/// a save file is a file.
public static class GameSession
{
    /// Godot's per-user data directory, so saves survive reinstalls and land
    /// somewhere the OS expects rather than beside the executable.
    public const string SaveDirectory = "user://saves";

    public static World? World { get; private set; }

    /// The game's real item and recipe graph, read from `/data` at startup.
    /// Loading it parses five JSON files and registers 600 items, so it is
    /// built once and shared.
    public static Catalogue Catalogue => Sim.Data.Catalogue.Instance;

    /// A new game: a landing site, a starter kit, and no factory. The player
    /// builds everything from here.
    public static World NewGame(int seed)
    {
        World = Sim.NewGame.Create(seed, Catalogue);
        return World;
    }

    /// The old placeholder factory, kept for the renderer's performance runs.
    /// It is not what a player gets.
    public static World DemoFactory(int seed, int machineCount)
    {
        World = DemoWorld.Build(machineCount, seed);
        return World;
    }

    public const int DefaultMachineCount = 4096;

    public static void Adopt(World world) => World = world;

    // ---- save slots --------------------------------------------------------

    /// One save, as the load screen needs to describe it.
    ///
    /// `Readable` false means the file is there and its header did not parse.
    /// The list used to drop those with a warning nobody reads, which is the
    /// worst of the three options: a save that silently disappears looks like
    /// data loss, and the player cannot even delete the thing that is bothering
    /// them. It is a row now, marked, with a reason -- the same rule the rest of
    /// this game follows for refusals.
    public sealed record SaveSlot(
        string Path,
        string Name,
        DateTime ModifiedUtc,
        long Tick,
        int Seed,
        int Machines = 0,
        int Belts = 0,
        int Researched = 0,
        int Techs = 0,
        string Tier = "",
        int Version = 0,
        bool Readable = true,
        string Problem = "")
    {
        /// Playing time, from the sim's own clock at a fixed 60 ticks a second.
        /// Wall-clock would measure how long the window was open, which says
        /// nothing about how far a factory has come.
        public double Hours => Tick / 60.0 / 60.0 / 60.0;
    }

    private static void EnsureDirectory()
    {
        if (!DirAccess.DirExistsAbsolute(SaveDirectory))
            DirAccess.MakeDirRecursiveAbsolute(SaveDirectory);
    }

    /// Sanitising lives in /sim so it can be tested without an engine: a save
    /// name comes from a text box and becomes a path, which is the one part of
    /// this file with a real correctness risk.
    public static string PathFor(string name) => $"{SaveDirectory}/{SaveNames.Sanitise(name)}.json";

    /// Every save on disk, newest first. A save whose header cannot be read is
    /// skipped rather than crashing the menu -- one corrupt file must not make
    /// the others unreachable.
    public static List<SaveSlot> List()
    {
        EnsureDirectory();
        var slots = new List<SaveSlot>();

        using var dir = DirAccess.Open(SaveDirectory);
        if (dir is null) return slots;

        foreach (var file in dir.GetFiles())
        {
            if (!file.EndsWith(".json")) continue;

            var path = $"{SaveDirectory}/{file}";
            var name = file[..^5];

            try
            {
                var save = SaveGame.FromJson(ReadAllText(path));
                slots.Add(new SaveSlot(
                    path, name, ModifiedUtc(path), save.Tick, save.Seed,
                    Machines: save.Machines.Count + save.Miners.Count +
                              save.Generators.Count + save.Accumulators.Count,
                    Belts: save.Belts.Tiles.Count,
                    Researched: save.Research.Unlocked.Count,
                    Techs: Catalogue.Data.Techs.Count,
                    Tier: HighestTier(save),
                    Version: save.Version));
            }
            catch (Exception e)
            {
                // Kept, not dropped. What the player can do with a broken save
                // is delete it, and they cannot do that to a row that is not
                // there.
                slots.Add(new SaveSlot(path, name, ModifiedUtc(path), 0, 0,
                                       Readable: false, Problem: Explain(e)));
            }
        }

        return slots.OrderByDescending(s => s.ModifiedUtc).ToList();
    }


    /// The furthest tier this save has researched into, as "STM Steam".
    ///
    /// Read from the unlocked tech ids rather than from the machines standing
    /// on the map: a player who has researched Steam and not built any Steam
    /// machines yet has still got there.
    private static string HighestTier(Sim.Save.SaveFile save)
    {
        var byId = Catalogue.Data.Techs.ToDictionary(t => t.Id, t => t.Tier);
        var order = Catalogue.Data.Tiers.ToDictionary(t => t.Id, t => t.Index);
        var names = Catalogue.Data.Tiers.ToDictionary(t => t.Id, t => t.Name);

        var best = "";
        foreach (var id in save.Research.Unlocked)
        {
            if (!byId.TryGetValue(id, out var tier)) continue;
            if (best.Length == 0 || order.GetValueOrDefault(tier) > order.GetValueOrDefault(best))
                best = tier;
        }

        return best.Length == 0 ? "" : $"{best} {names.GetValueOrDefault(best, best)}";
    }

    /// Why a save will not open, in a row's worth of words.
    ///
    /// A version mismatch is worth saying exactly -- "save version 11 is not
    /// version 12" tells the player their file is from an older build and is
    /// not corrupt. Anything else is a parse failure, and the serializer's own
    /// message ("'t' is an invalid start of a property name. Expected a '\"'.
    /// Path: $ | LineNumber: 0 | BytePositionInLine: 2.") is a paragraph of
    /// nothing a player can act on -- and long enough to stretch the list out
    /// of the card it lives in, which is how this one was noticed.
    private static string Explain(Exception e) =>
        e is Sim.Save.SaveLoadException && e.Message.Length <= 64
            ? e.Message
            : "header will not parse";

    public static SaveSlot? MostRecent() => List().FirstOrDefault();

    public static bool AnySaves() => MostRecent() is not null;

    // ---- reading and writing ----------------------------------------------

    public static void Save(string name)
    {
        if (World is null) throw new InvalidOperationException("there is no world to save");

        EnsureDirectory();
        WriteAllText(PathFor(name), SaveGame.ToJson(SaveGame.Capture(World)));
    }

    public static World Load(string path)
    {
        var save = SaveGame.FromJson(ReadAllText(path));

        // A save stores the seed, not the map, so the generator is rebuilt from
        // it here. Recipes come from the current data files rather than the
        // file, which is what lets a balance patch reach an existing world.
        var gen = new WorldGen(save.Seed, Sim.NewGame.OreSpecs(Catalogue));

        World = SaveGame.Restore(save, Catalogue.Recipes, gen);
        return World;
    }

    /// Removes a save. Returns false when the file was already gone, which is
    /// not an error worth reporting -- the player wanted it gone either way.
    ///
    /// The path is globalized first. `RemoveAbsolute` takes a filesystem path
    /// and every save path in this game is a `user://` virtual one, so the
    /// previous version could only ever have deleted a file whose name happened
    /// to work as both. It also discarded the result, so a delete that failed
    /// looked exactly like one that worked.
    public static bool Delete(string path)
    {
        if (!FileAccess.FileExists(path)) return false;

        var error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        if (error != Error.Ok)
            throw new System.IO.IOException($"could not delete {path}: {error}");

        return true;
    }

    // ---- Godot file access -------------------------------------------------
    // user:// is a Godot virtual path, so these go through FileAccess rather
    // than System.IO, which cannot resolve it.

    private static string ReadAllText(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
            throw new SaveLoadException($"could not open {path}: {FileAccess.GetOpenError()}");
        return file.GetAsText();
    }

    private static void WriteAllText(string path, string text)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null)
            throw new SaveLoadException($"could not write {path}: {FileAccess.GetOpenError()}");
        file.StoreString(text);
    }

    private static DateTime ModifiedUtc(string path) =>
        DateTimeOffset.FromUnixTimeSeconds((long)FileAccess.GetModifiedTime(path)).UtcDateTime;
}
