namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.BracketToSlash() static conversion.
/// </summary>
public class SubjectPathResolverBracketToSlashTests
{
    [Theory]
    [InlineData("Root", "Root")]
    [InlineData("Root.Demo.Conveyor", "Root/Demo/Conveyor")]
    public void SimpleMode_DotsBecomeSeparators(string input, string expected)
    {
        Assert.Equal(expected, SubjectPathResolver.BracketToSlash(input));
    }

    [Theory]
    [InlineData("Root.[Readme.md]", "Root/Readme.md")]
    [InlineData("Docs.architecture.[state.md]", "Docs/architecture/state.md")]
    [InlineData("[key1].[key2]", "key1/key2")]
    [InlineData("[Readme.md]", "Readme.md")]
    [InlineData("Root.[Demo].[Inline.md]", "Root/Demo/Inline.md")]
    [InlineData("Property[key].Next", "Property/key/Next")]
    [InlineData("Demo.[Setup.md]", "Demo/Setup.md")]
    public void BracketMode_PreservesDotsInsideBrackets(string input, string expected)
    {
        Assert.Equal(expected, SubjectPathResolver.BracketToSlash(input));
    }

    [Theory]
    [InlineData("Docs.sub1.sub2.[file.txt]", "Docs/sub1/sub2/file.txt")]
    [InlineData("A.B.C.[d.e]", "A/B/C/d.e")]
    public void MixedMode_MultipleDotSegmentsWithBracketedFile(string input, string expected)
    {
        Assert.Equal(expected, SubjectPathResolver.BracketToSlash(input));
    }
}
