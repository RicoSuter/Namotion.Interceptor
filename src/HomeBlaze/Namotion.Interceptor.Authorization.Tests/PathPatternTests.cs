using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData("/", "", true)]
    [InlineData("/", "Rooms[Kitchen]", true)]
    [InlineData("/Rooms[Kitchen]", "Rooms[Kitchen]", true)]
    [InlineData("/Rooms[Kitchen]", "Rooms[Kitchen]/Lights[Ceiling]", true)]
    [InlineData("/Rooms[Kitchen]", "Rooms[Bedroom]/Lights[Lamp]", false)]
    [InlineData("/Rooms[Kitchen]", "", false)]
    [InlineData("/Rooms", "Rooms[Kitchen]/Lights[Ceiling]", true)]
    [InlineData("/Rooms[*]/Lights", "Rooms[Bedroom]/Lights[Lamp]", true)]
    [InlineData("/Rooms[*]/Lights", "Rooms[Bedroom]/Sensor", false)]
    [InlineData("/Rooms[K*]", "Rooms[Kitchen]", true)]
    [InlineData("/Rooms[K*]", "Rooms[Bedroom]", false)]
    [InlineData("/*/Lights", "Rooms[Kitchen]/Lights[Ceiling]", true)]
    [InlineData("/*/Lights", "Rooms[Kitchen]/Sensor/Lights", false)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Lights[Ceiling]", true)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Sensor/Lights", true)]
    [InlineData("/**/Lights", "Lights", true)]
    [InlineData("/**/Lights", "Rooms[Kitchen]/Sensor", false)]
    [InlineData("/Rooms/**", "Rooms", true)]
    [InlineData("/Rooms/**", "Wifi", false)]
    [InlineData("/Rooms[Kitchen]/Light*", "Rooms[Kitchen]/Lights[Ceiling]", true)]
    [InlineData("/Rooms['Kitchen [north]']", "Rooms['Kitchen [north]']/Sensor", true)]
    [InlineData("/Rooms[*]", "Rooms", false)]
    [InlineData("/Items[3]", "Items[30]", false)]
    [InlineData("/Rooms/**/Sensor/**/Lights", "Rooms[Kitchen]/Sensor/Lights", true)]
    public void WhenPatternIsMatchedAgainstPath_ThenResultIsAsExpected(string pattern, string path, bool expected)
    {
        // Arrange
        var provider = SlashPathProvider.Instance;
        var compiled = PathPattern.Parse(pattern, provider, "Test");
        Assert.True(provider.TryParsePath(path, out var segments, out _));

        // Act
        var result = compiled.Matches(segments);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Rooms", "must start with '/'")]
    [InlineData("", "must start with '/'")]
    [InlineData("/Rooms[", "malformed")]
    [InlineData("/Rooms[]", "malformed")]
    public void WhenPatternIsInvalid_ThenParseThrowsNamingTheRole(string pattern, string expectedMessagePart)
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(() => PathPattern.Parse(pattern, SlashPathProvider.Instance, "Reader"));

        // Assert
        Assert.Contains("Reader", exception.Message);
        Assert.Contains(expectedMessagePart, exception.Message);
    }
}
