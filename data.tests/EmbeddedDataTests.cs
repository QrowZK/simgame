using System.IO;
using Sim.Data;
using Xunit;

namespace Data.Tests;

/// v0.1.0 shipped a game that could not start.
///
/// `GameData` read its five JSON files by walking up from the binary looking
/// for a `data` directory. That works in a checkout and nowhere else: an
/// exported build ships no such directory, so New Game threw
/// `DirectoryNotFoundException` for every player who was not standing inside
/// the repository. Every test passed, and so did the smoke run on the exported
/// binary -- because that binary was run from `dist/`, which is *inside* the
/// checkout, so the walk found the repository's own data directory.
///
/// These tests pin the two halves of the fix: the data is inside the assembly,
/// and it is the same data that is in the repository.
public class EmbeddedDataTests
{
    private static readonly string[] Files =
        { "tiers.json", "items.json", "machines.json", "techs.json", "recipes.json" };

    [Fact]
    public void EveryDataFile_IsEmbeddedInTheAssembly()
    {
        foreach (var file in Files)
        {
            var text = GameData.EmbeddedText(file);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{file} is embedded but empty");
        }
    }

    /// The embedded copy is what ships, so it is what has to match the spec.
    /// A build that embedded a stale recipes.json would ship a different game
    /// from the one the repository describes, and nothing else would notice.
    [Fact]
    public void TheEmbeddedCopy_IsTheSameAsTheFilesOnDisk()
    {
        var directory = DataPaths.DataDirectory;

        foreach (var file in Files)
        {
            var onDisk = File.ReadAllText(Path.Combine(directory, file));
            Assert.Equal(onDisk.Replace("\r\n", "\n"),
                         GameData.EmbeddedText(file).Replace("\r\n", "\n"));
        }
    }

    /// The property the shipped build actually needs: a world can be built
    /// with no data directory reachable at all. Nothing here touches the
    /// filesystem, which is the whole point.
    [Fact]
    public void AWorldCanBeBuilt_FromTheEmbeddedDataAlone()
    {
        var data = GameData.LoadEmbedded();

        Assert.NotEmpty(data.Recipes);
        Assert.NotEmpty(data.Items);
        Assert.NotEmpty(data.Techs);

        var catalogue = new Catalogue(data);
        var world = Sim.NewGame.Create(seed: 4242, catalogue);

        for (var i = 0; i < 60; i++) world.Tick();

        Assert.Equal(60, world.TickCount);
        Assert.NotNull(world.Research);
    }
}
