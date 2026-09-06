using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
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

    /// How many machines a new demo world is built with. The real game will
    /// start from an empty world and a landing site; until worldgen is wired to
    /// the machine graph, a new game is the demo factory.
    public const int DefaultMachineCount = 4096;

    public static World NewGame(int seed, int machineCount = DefaultMachineCount)
    {
        World = DemoWorld.Build(machineCount, seed);
        return World;
    }

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

        // The recipe set has to exist before the save is restored against it.
        // Building a throwaway world is how the demo publishes its recipes;
        // when the game reads /data at runtime this goes away.
        if (DemoWorld.Recipes.Count == 0)
            DemoWorld.Build(1, save.Seed);

        World = SaveGame.Restore(save, DemoWorld.Recipes);
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
