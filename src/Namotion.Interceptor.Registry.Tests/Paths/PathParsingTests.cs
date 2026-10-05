using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathParsingTests
{
    [Fact]
    public void WhenDictionaryKeyContainsPathSeparator_ThenParsePathKeepsKeyIntact()
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var segments = pathProvider.ParsePath("car.radioStations[jazz108.5Fm].name");

        // Assert
        Assert.Equal(3, segments.Count);
        Assert.Equal(("car", (string?)null), segments[0]);
        Assert.Equal(("radioStations", (string?)"jazz108.5Fm"), segments[1]);
        Assert.Equal(("name", (string?)null), segments[2]);
    }

    [Fact]
    public void WhenIndexIsInteger_ThenParsePathReturnsItsText()
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var segments = pathProvider.ParsePath("items[5]");

        // Assert
        Assert.Equal(("items", (string?)"5"), Assert.Single(segments));
    }

    [Theory]
    [InlineData("devices[192.168.0.1].value", "devices", "192.168.0.1")]
    [InlineData("items[a[b].value", "items", "a[b")]
    [InlineData("items[a]]b].value", "items", "a]b")]
    [InlineData("items[a]]].value", "items", "a]")]
    [InlineData("items[]]].value", "items", "]")]
    [InlineData(@"items[C:\temp].value", "items", @"C:\temp")]
    public void WhenIndexContainsSpecialCharacters_ThenTryParsePathReadsThemLiterally(
        string path, string expectedSegment, string expectedIndex)
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var parsed = pathProvider.TryParsePath(path, out var segments, out var error);

        // Assert
        Assert.True(parsed, error);
        Assert.Equal(2, segments!.Count);
        Assert.Equal((expectedSegment, (string?)expectedIndex), segments[0]);
        Assert.Equal(("value", (string?)null), segments[1]);
    }

    [Fact]
    public void WhenSeparatorIsSlash_ThenSlashInsideIndexIsLiteral()
    {
        // Arrange
        var pathProvider = new DefaultPathProvider('/');

        // Act
        var parsed = pathProvider.TryParsePath("floors[1/2]/rooms[a.b]", out var segments, out _);

        // Assert
        Assert.True(parsed);
        Assert.Equal(new (string segment, string? index)[] { ("floors", "1/2"), ("rooms", "a.b") }, segments!);
    }

    [Theory]
    [InlineData("/a//b/")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    public void WhenPathHasEmptySegments_ThenTheyAreSkipped(string path)
    {
        // Arrange
        var pathProvider = new DefaultPathProvider('/');

        // Act
        var parsed = pathProvider.TryParsePath(path, out var segments, out _);

        // Assert
        Assert.True(parsed);
        Assert.Equal(new (string segment, string? index)[] { ("a", null), ("b", null) }, segments!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WhenPathIsEmpty_ThenTryParsePathReturnsNoSegments(string? path)
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var parsed = pathProvider.TryParsePath(path!, out var segments, out _);

        // Assert
        Assert.True(parsed);
        Assert.Empty(segments!);
    }

    [Theory]
    [InlineData("a[b", "Unclosed '[' at position 1 in path 'a[b'")]
    [InlineData("a[b]]", "Unclosed '[' at position 1 in path 'a[b]]'")]
    [InlineData("a[]", "Empty index at position 1 in path 'a[]'")]
    [InlineData("[a]", "Missing segment name at position 0 in path '[a]'")]
    [InlineData("a.[b]", "Missing segment name at position 2 in path 'a.[b]'")]
    [InlineData("a[1]b", "Expected '.' or end of path after index at position 4 in path 'a[1]b'")]
    [InlineData("a[1][2]", "Expected '.' or end of path after index at position 4 in path 'a[1][2]'")]
    public void WhenPathIsMalformed_ThenTryParsePathReportsReasonAndParsePathThrows(string path, string expectedError)
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var parsed = pathProvider.TryParsePath(path, out var segments, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(segments);
        Assert.Equal(expectedError, error);
        var exception = Assert.Throws<FormatException>(() => pathProvider.ParsePath(path));
        Assert.Equal(expectedError, exception.Message);
    }

    [Fact]
    public void WhenNameContainsClosingBracket_ThenItIsPartOfTheName()
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var parsed = pathProvider.TryParsePath("notes]v2[x].a]", out var segments, out var error);

        // Assert
        Assert.True(parsed, error);
        Assert.Equal(new (string segment, string? index)[] { ("notes]v2", "x"), ("a]", null) }, segments!);
    }

    [Fact]
    public void WhenEscapedIndexIsLong_ThenTryParsePathUnescapesIt()
    {
        // Arrange
        var pathProvider = DefaultPathProvider.Instance;
        var key = new string('k', 300) + "]" + new string('k', 300);
        var path = "items[" + key.Replace("]", "]]") + "].value";

        // Act
        var parsed = pathProvider.TryParsePath(path, out var segments, out var error);

        // Assert
        Assert.True(parsed, error);
        Assert.Equal(new (string segment, string? index)[] { ("items", key), ("value", null) }, segments!);
    }

    [Theory]
    [InlineData('[', '[', ']')]
    [InlineData(']', '[', ']')]
    [InlineData('.', '[', '[')]
    public void WhenProviderCharactersCollide_ThenParsingThrows(char separator, char indexOpen, char indexClose)
    {
        // Arrange
        var pathProvider = new CollidingPathProvider(separator, indexOpen, indexClose);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => pathProvider.TryParsePath("a", out _, out _));
    }

    [Fact]
    public void WhenProviderCharactersCollide_ThenEveryCallThrows()
    {
        // Arrange
        var pathProvider = new CollidingPathProvider('.', '[', '[');

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => pathProvider.TryParsePath("a", out _, out _));
        Assert.Throws<InvalidOperationException>(() => pathProvider.TryParsePath("a", out _, out _));
    }
}

internal sealed class CollidingPathProvider(char pathSeparator, char indexOpen, char indexClose) : PathProviderBase
{
    public override char PathSeparator => pathSeparator;

    public override char IndexOpen => indexOpen;

    public override char IndexClose => indexClose;
}
