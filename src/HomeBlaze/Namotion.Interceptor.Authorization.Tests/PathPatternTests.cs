using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData("/Wifi", "Wifi", true)]
    [InlineData("/Wifi", "Wifi/Ssid", false)]
    [InlineData("/Wifi/Ssid", "Wifi/Ssid", true)]
    [InlineData("/Wifi/Ssid", "Wifi/Password", false)]
    [InlineData("/Wifi/*", "Wifi/Ssid", true)]
    [InlineData("/Wifi/*", "Wifi", false)]
    [InlineData("/Wifi/**", "Wifi", true)]
    [InlineData("/Wifi/**", "Wifi/Ssid", true)]
    [InlineData("/Wifi/**", "Rooms", false)]
    [InlineData("/Rooms", "Rooms", true)]
    [InlineData("/Rooms", "Rooms[Kitchen]/Lights", false)]
    [InlineData("/Rooms[Kitchen]/*", "Rooms[Kitchen]/Lights", true)]
    [InlineData("/Rooms[Kitchen]/*", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", false)]
    [InlineData("/Rooms[Kitchen]/*", "Rooms[Bedroom]/Lights", false)]
    [InlineData("/Rooms[Kitchen]/**", "Rooms[Kitchen]/Lights", true)]
    [InlineData("/Rooms[Kitchen]/**", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/Rooms[Kitchen]/**", "Rooms", false)]
    [InlineData("/Rooms[Kitchen]/**", "Rooms[Bedroom]/Lights", false)]
    [InlineData("/Rooms/**", "Rooms", true)]
    [InlineData("/Rooms/**", "Rooms[Kitchen]/Sensor", true)]
    [InlineData("/Rooms/**", "Wifi/Ssid", false)]
    [InlineData("/Rooms/*/IsOn", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/Rooms/*/*/IsOn", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", false)]
    [InlineData("/Rooms/Lights/IsOn", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/Rooms[*]/Lights/*", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/Rooms[*]/Lights/*", "Rooms[Kitchen]/Lights", false)]
    [InlineData("/*/Lights/*", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/*/Lights", "Rooms[Kitchen]/Lights", true)]
    [InlineData("/*/Lights", "Rooms[Kitchen]/Sensor/Lights", false)]
    [InlineData("/**/Lights", "Lights", true)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Lights", true)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Sensor/Lights", true)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", false)]
    [InlineData("/**/Lights[C*]/IsOn", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/**/Lights[C*]/IsOn", "Rooms[Bedroom]/Lights[Lamp]/IsOn", false)]
    [InlineData("/Rooms[K*]/Sensor", "Rooms[Kitchen]/Sensor", true)]
    [InlineData("/Rooms[K*]/Sensor", "Rooms[Bedroom]/Sensor", false)]
    [InlineData("/Rooms[*]/Sensor", "Rooms/Sensor", false)]
    [InlineData("/Rooms[Kitchen]/Light*", "Rooms[Kitchen]/Lights", true)]
    [InlineData("/Rooms['Kitchen [north]']/Sensor", "Rooms['Kitchen [north]']/Sensor", true)]
    [InlineData("/Rooms['Living Room']/*", "Rooms['Living Room']/Sensor", true)]
    [InlineData("/Items[3]/Value", "Items[30]/Value", false)]
    [InlineData("/Rooms/**/Sensor/**/Lights", "Rooms[Kitchen]/Sensor/Lights", true)]
    [InlineData("/Rooms/**/Sensor/**/Lights", "Rooms[Kitchen]/Sensor/Lights[Ceiling]/IsOn", false)]
    [InlineData("/Rooms/**/IsOn/**", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/**", "Wifi", true)]
    [InlineData("/**", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("/**/**", "Wifi", true)]
    [InlineData("/*", "Wifi", true)]
    [InlineData("/*", "Wifi/Ssid", false)]
    public void WhenPatternIsMatchedAgainstMemberPath_ThenResultIsAsExpected(string pattern, string memberPath, bool expected)
    {
        // Arrange
        var provider = SlashPathProvider.Instance;
        var compiled = PathPattern.Parse(pattern, provider, "Test");
        var (links, member) = ToMemberPath(provider, memberPath);

        // Act
        var result = compiled.Matches(links, member);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/Security/FullReader/Name", true)]
    [InlineData("/Security/Roles[FullReader]/Name", true)]
    [InlineData("/Security/Roles/*", true)]
    [InlineData("/Security/Roles[Full*]/**", true)]
    [InlineData("/Security/Full*/Name", true)]
    [InlineData("/Security/FullReader", false)]
    [InlineData("/Security/FullReader[x]/Name", false)]
    [InlineData("/Security/Other/Name", false)]
    [InlineData("/Security/Roles[Other]/Name", false)]
    public void WhenPatternIsMatchedAgainstAMemberBelowAnInlineKeyWithBothSpellings_ThenEitherSpellingMatches(string pattern, bool expected)
    {
        // Arrange
        var compiled = PathPattern.Parse(pattern, SlashPathProvider.Instance, "Test");
        PathLink[] links = [new("Security", null, null), new("Roles", "FullReader", "FullReader")];

        // Act
        var result = compiled.Matches(links, Name);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/Security/Roles[Wifi]/Name", true)]
    [InlineData("/Security/Roles/**", true)]
    [InlineData("/Security/Wifi/Name", false)]
    public void WhenPatternIsMatchedAgainstAMemberBelowAnInlineKeyShadowedByAProperty_ThenOnlyTheExplicitSpellingMatches(string pattern, bool expected)
    {
        // Arrange
        var compiled = PathPattern.Parse(pattern, SlashPathProvider.Instance, "Test");
        PathLink[] links = [new("Security", null, null), new("Roles", "Wifi", null)];

        // Act
        var result = compiled.Matches(links, Name);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/Security/FullReader/Name", true)]
    [InlineData("/Security/Roles[FullReader]/Name", false)]
    [InlineData("/Security/Roles/**", false)]
    public void WhenPatternIsMatchedAgainstAMemberBelowAnInlineKeyWhoseExplicitFormIsShadowed_ThenOnlyTheBareSpellingMatches(string pattern, bool expected)
    {
        // Arrange
        var compiled = PathPattern.Parse(pattern, SlashPathProvider.Instance, "Test");
        PathLink[] links = [new("Security", null, null), new(null, "FullReader", "FullReader")];

        // Act
        var result = compiled.Matches(links, Name);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenPatternHasManyDoubleStars_ThenAMismatchIsFoundWithoutExponentialBacktracking()
    {
        // Arrange
        var pattern = "/" + string.Join("/", Enumerable.Repeat("**/A", 30)) + "/Missing";
        var compiled = PathPattern.Parse(pattern, SlashPathProvider.Instance, "Test");
        var links = Enumerable.Range(0, 60).Select(_ => new PathLink("A", null, null)).ToArray();

        // Act
        var result = compiled.Matches(links, new PathLink("A", null, null));

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("Rooms", "must start with '/'")]
    [InlineData("", "must start with '/'")]
    [InlineData("/Rooms[", "malformed")]
    [InlineData("/Rooms[]", "malformed")]
    [InlineData("/ Rooms", "whitespace")]
    [InlineData("/Rooms[ Kitchen]", "whitespace")]
    [InlineData("/", "matches no member; use '/**' for the subject and everything below it, or '/*' for its members")]
    [InlineData("/Rooms[Kitchen]", "matches no member; use '/Rooms[Kitchen]/**' for the subject and everything below it, or '/Rooms[Kitchen]/*' for its members")]
    [InlineData("/Rooms[*]", "matches no member")]
    [InlineData("/**/Lights[C*]", "matches no member")]
    [InlineData("/Rooms/**[A]", "matches no member")]
    public void WhenPatternIsInvalid_ThenParseThrowsNamingTheRole(string pattern, string expectedMessagePart)
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(() => PathPattern.Parse(pattern, SlashPathProvider.Instance, "Reader"));

        // Assert
        Assert.Contains("Reader", exception.Message);
        Assert.Contains(expectedMessagePart, exception.Message);
    }

    private static readonly PathLink Name = new("Name", null, null);

    // The last segment of the member path is the member, the segments before it the path of its subject.
    private static (PathLink[] Links, PathLink Member) ToMemberPath(PathProviderBase provider, string memberPath)
    {
        Assert.True(provider.TryParsePath(memberPath, out var segments, out _));
        var links = segments.Take(segments.Count - 1).Select(segment => new PathLink(segment.segment, segment.index, null)).ToArray();
        Assert.Null(segments[^1].index);
        return (links, new PathLink(segments[^1].segment, null, null));
    }
}
