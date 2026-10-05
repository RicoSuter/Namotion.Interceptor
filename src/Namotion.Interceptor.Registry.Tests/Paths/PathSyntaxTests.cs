using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathSyntaxTests
{
    private sealed class SlashPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';
    }

    private static string Describe(List<(string segment, string? index)> segments)
        => string.Join("|", segments.Select(s => s.index is null ? s.segment : $"{s.segment}={s.index}"));

    [Theory]
    [InlineData("car.radioStations[jazz108.5Fm].name", "car|radioStations=jazz108.5Fm|name")]
    [InlineData("Devices[192.168.0.1].Status", "Devices=192.168.0.1|Status")]
    [InlineData("Items[a[b].Value", "Items=a[b|Value")]
    [InlineData("notes]v2.Value", "notes]v2|Value")]
    [InlineData("Items[5]", "Items=5")]
    [InlineData("Items[Bob's]", "Items=Bob's")]
    [InlineData("Items['Sensor [Kitchen]'].Value", "Items=Sensor [Kitchen]|Value")]
    [InlineData("Items['Bob''s']", "Items=Bob's")]
    [InlineData("Items['''quoted']", "Items='quoted")]
    [InlineData("Items['']", "Items=")]
    [InlineData(".a..b.", "a|b")]
    public void WhenPathIsWellFormed_ThenParsePathReturnsSegments(string path, string expected)
    {
        // Act
        var segments = DefaultPathProvider.Instance.ParsePath(path);

        // Assert
        Assert.Equal(expected, Describe(segments));
    }

    [Fact]
    public void WhenSeparatorIsSlash_ThenSlashInsideIndexIsLiteral()
    {
        // Act
        var segments = new SlashPathProvider().ParsePath("/Line/Machines[a/b]/Status/");

        // Assert
        Assert.Equal("Line|Machines=a/b|Status", Describe(segments));
    }

    [Theory]
    [InlineData("")]
    [InlineData("...")]
    public void WhenPathHasNoSegments_ThenParsePathReturnsEmptyList(string path)
    {
        // Act
        var segments = DefaultPathProvider.Instance.ParsePath(path);

        // Assert
        Assert.Empty(segments);
    }

    [Theory]
    [InlineData("[5].Value", "Missing segment name at position 0 in path '[5].Value'")]
    [InlineData("a.[5]", "Missing segment name at position 2 in path 'a.[5]'")]
    [InlineData("Items[5", "Unclosed '[' at position 5 in path 'Items[5'")]
    [InlineData("Items[]", "Empty index at position 5 in path 'Items[]'")]
    [InlineData("Items['abc]", "Unclosed quote at position 6 in path 'Items['abc]'")]
    [InlineData("Items['a'b]", "Expected ']' after quoted key at position 9 in path 'Items['a'b]'")]
    [InlineData("Items[5]x", "Expected '.' or end of path after index at position 8 in path 'Items[5]x'")]
    [InlineData("Items[5][6]", "Expected '.' or end of path after index at position 8 in path 'Items[5][6]'")]
    public void WhenPathIsMalformed_ThenTryParsePathReportsReasonAndParsePathThrows(string path, string expectedError)
    {
        // Act
        var parsed = DefaultPathProvider.Instance.TryParsePath(path, out var segments, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(segments);
        Assert.Equal(expectedError, error);
        var exception = Assert.Throws<FormatException>(() => DefaultPathProvider.Instance.ParsePath(path));
        Assert.Equal(expectedError, exception.Message);
    }
}
