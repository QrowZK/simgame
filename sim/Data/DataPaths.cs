namespace Sim.Data;

/// Where the game data comes from.
///
/// Two sources, and the order matters. The **embedded** copy is what a shipped
/// game reads: it is compiled into Sim.dll, so it is present wherever the
/// assembly is. The **directory** walk is for tools and tests that want the
/// files on disk -- the generator writes them, and a test compares the two.
///
/// It was the other way round once, and only the walk existed. That works in a
/// checkout and nowhere else: v0.1.0 shipped with no data directory beside the
/// binary, and New Game died with DirectoryNotFoundException for every player
/// who was not standing inside the repository (ADR 0027).
public static class DataPaths
{
    public static string DataDirectory => Find("data");

    private static string Find(string dirName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, dirName);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "items.json")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate a '{dirName}' directory above {AppContext.BaseDirectory}");
    }
}
