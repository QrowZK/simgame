using Sim.Save;

namespace Sim.Tests;

/// Save names are typed by a player and become file paths. These are the cases
/// that decide whether that is safe.
public class SaveNameTests
{
    [Theory]
    [InlineData("My Factory", "My Factory")]
    [InlineData("run-2", "run-2")]
    [InlineData("main_base", "main_base")]
    public void OrdinaryNames_SurviveUntouched(string input, string expected) =>
        Assert.Equal(expected, SaveNames.Sanitise(input));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/absolute/path")]
    [InlineData("save/../../../boom")]
    public void NothingCanClimbOutOfTheSaveDirectory(string input)
    {
        var result = SaveNames.Sanitise(input);

        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void AnEmptyOrUnusableName_FallsBackRatherThanProducingAnEmptyPath()
    {
        Assert.Equal(SaveNames.Fallback, SaveNames.Sanitise(""));
        Assert.Equal(SaveNames.Fallback, SaveNames.Sanitise("///"));
        Assert.Equal(SaveNames.Fallback, SaveNames.Sanitise("   "));
    }

    [Fact]
    public void AbsurdlyLongNames_AreTruncated()
    {
        var result = SaveNames.Sanitise(new string('a', 500));
        Assert.Equal(SaveNames.MaxLength, result.Length);
    }

    [Fact]
    public void DotsAreStripped_SoNoNameBecomesADotfileOrAnExtension()
    {
        // A leading dot would make a hidden file, and an embedded one would give
        // the save a second extension the lister cannot read back as a name.
        // Stripping is enough -- ".json" becoming "json" is a fine save name.
        Assert.Equal(SaveNames.Fallback, SaveNames.Sanitise("."));
        Assert.Equal("json", SaveNames.Sanitise(".json"));
        Assert.DoesNotContain(".", SaveNames.Sanitise("my.save.file"));
    }
}
