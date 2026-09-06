namespace Data.Tests;

internal static class DataPaths
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
