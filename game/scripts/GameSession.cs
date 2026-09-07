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

    public sealed record SaveSlot(string Path, string Name, DateTime ModifiedUtc, long Tick, int Seed);

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
            try
            {
                var save = SaveGame.FromJson(ReadAllText(path));
                slots.Add(new SaveSlot(path, file[..^5], ModifiedUtc(path), save.Tick, save.Seed));
            }
            catch (Exception e)
            {
                GD.PushWarning($"skipping unreadable save {path}: {e.Message}");
            }
        }

        return slots.OrderByDescending(s => s.ModifiedUtc).ToList();
    }

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

    public static void Delete(string path)
    {
        if (FileAccess.FileExists(path))
            DirAccess.RemoveAbsolute(path);
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
