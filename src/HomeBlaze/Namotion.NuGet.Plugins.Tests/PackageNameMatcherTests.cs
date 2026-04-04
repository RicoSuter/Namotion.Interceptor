using Xunit;

namespace Namotion.NuGet.Plugins.Tests;

public class PackageNameMatcherTests
{
    [Theory]
    [InlineData("MyCompany.*.Abstractions", "MyCompany.Devices.Abstractions", true)]
    [InlineData("MyCompany.*.Abstractions", "MyCompany.Shared.Abstractions", true)]
    [InlineData("MyCompany.*.Abstractions", "Microsoft.Extensions.Logging.Abstractions", false)]
    [InlineData("MyCompany.*.Abstractions", "MyCompany.Abstractions", false)]
    [InlineData("MyCompany.*.Abstractions", "MyCompany.Devices.Abstractions.Extra", false)]
    [InlineData("MyCompany.*.Abstractions", "MyCompany.Devices.Philips.Hue.Abstractions", true)]
    public void WhenPatternHasWildcard_ThenMatchesCorrectly(string pattern, string packageName, bool expected)
    {
        // Act
        var result = PackageNameMatcher.IsMatch(packageName, pattern);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Exact.Package.Name", "Exact.Package.Name", true)]
    [InlineData("Exact.Package.Name", "Other.Package.Name", false)]
    [InlineData("Exact.Package.Name", "Exact.Package.Name.Extra", false)]
    public void WhenPatternIsExact_ThenMatchesExactly(string pattern, string packageName, bool expected)
    {
        // Act
        var result = PackageNameMatcher.IsMatch(packageName, pattern);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*.Abstractions", "MyCompany.Abstractions", true)]
    [InlineData("*.Abstractions", "Abstractions", false)]
    [InlineData("MyCompany.*", "MyCompany.Anything", true)]
    [InlineData("MyCompany.*", "MyCompany.Deeply.Nested", true)]
    public void WhenWildcardAtEdge_ThenMatchesCorrectly(string pattern, string packageName, bool expected)
    {
        // Act
        var result = PackageNameMatcher.IsMatch(packageName, pattern);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*.*.Abstractions", "MyCompany.Devices.Abstractions", true)]
    [InlineData("*.*.Abstractions", "A.B.Abstractions", true)]
    [InlineData("*.*.Abstractions", "Single.Abstractions", false)]
    public void WhenMultipleWildcards_ThenMatchesCorrectly(string pattern, string packageName, bool expected)
    {
        // Act
        var result = PackageNameMatcher.IsMatch(packageName, pattern);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenMatchingAgainstMultiplePatterns_ThenReturnsTrueIfAnyMatches()
    {
        // Arrange
        var patterns = new[] { "MyCompany.*.Abstractions", "OtherCompany.*" };

        // Act & Assert
        Assert.True(PackageNameMatcher.IsMatchAny("MyCompany.Devices.Abstractions", patterns));
        Assert.True(PackageNameMatcher.IsMatchAny("OtherCompany.Foo", patterns));
        Assert.False(PackageNameMatcher.IsMatchAny("Unrelated.Package", patterns));
    }

    [Fact]
    public void WhenPatternsListIsEmpty_ThenNothingMatches()
    {
        // Act
        var result = PackageNameMatcher.IsMatchAny("Any.Package", []);

        // Assert
        Assert.False(result);
    }
}
