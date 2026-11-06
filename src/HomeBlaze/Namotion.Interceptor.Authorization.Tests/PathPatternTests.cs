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
        var provider = Policies.SlashPaths;
        var compiled = PathPattern.Parse(pattern, provider, "Test");
        var (links, member) = ToMemberPath(provider, memberPath);

        // Act
        var result = compiled.Matches(links, member, isDeny: false);

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
        var compiled = PathPattern.Parse(pattern, Policies.SlashPaths, "Test");
        PathLink[] links = [new("Security", null), new("Roles", "FullReader", "FullReader", "Roles", "FullReader")];

        // Act
        var allowResult = compiled.Matches(links, Name, isDeny: false);
        var denyResult = compiled.Matches(links, Name, isDeny: true);

        // Assert
        Assert.Equal(expected, allowResult);
        Assert.Equal(expected, denyResult);
    }

    [Theory]
    [InlineData("/Security/Roles[Wifi]/Name", false, true)]
    [InlineData("/Security/Roles/**", false, true)]
    [InlineData("/Security/Wifi/Name", false, false)]
    [InlineData("/Security/Roles[Wifi]/Name", true, true)]
    [InlineData("/Security/Roles/**", true, true)]
    [InlineData("/Security/Wifi/Name", true, true)]
    public void WhenPatternIsMatchedAgainstAMemberBelowAnInlineKeyShadowedByAProperty_ThenAnAllowMatchesOnlyTheExplicitSpellingAndADenyBoth(string pattern, bool isDeny, bool expected)
    {
        // Arrange
        var compiled = PathPattern.Parse(pattern, Policies.SlashPaths, "Test");
        PathLink[] links = [new("Security", null), new("Roles", "Wifi", null, "Roles", "Wifi")];

        // Act
        var result = compiled.Matches(links, Name, isDeny);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/Security/FullReader/Name", false, true)]
    [InlineData("/Security/Roles[FullReader]/Name", false, false)]
    [InlineData("/Security/Roles/**", false, false)]
    [InlineData("/Security/FullReader/Name", true, true)]
    [InlineData("/Security/Roles[FullReader]/Name", true, true)]
    [InlineData("/Security/Roles/**", true, true)]
    public void WhenPatternIsMatchedAgainstAMemberBelowAnInlineKeyWhoseExplicitFormIsShadowed_ThenAnAllowMatchesOnlyTheBareSpellingAndADenyBoth(string pattern, bool isDeny, bool expected)
    {
        // Arrange
        var compiled = PathPattern.Parse(pattern, Policies.SlashPaths, "Test");
        PathLink[] links = [new("Security", null), new(null, "FullReader", "FullReader", "Roles", "FullReader")];

        // Act
        var result = compiled.Matches(links, Name, isDeny);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenPatternHasManyDoubleStars_ThenAMismatchIsFoundWithoutExponentialBacktracking()
    {
        // Arrange
        var pattern = "/" + string.Join("/", Enumerable.Repeat("**/A", 30)) + "/Missing";
        var compiled = PathPattern.Parse(pattern, Policies.SlashPaths, "Test");
        var links = Enumerable.Range(0, 60).Select(_ => new PathLink("A", null)).ToArray();

        // Act
        var result = compiled.Matches(links, new PathLink("A", null), isDeny: false);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("Wifi/Ssid", "Wifi/Ssid", true)]
    [InlineData("Wifi/Ssid", "Wifi/Password", false)]
    [InlineData("Rooms[Kitchen]/**", "Rooms[Kitchen]/Lights[Ceiling]/IsOn", true)]
    [InlineData("Rooms[Kitchen]/**", "Rooms[Bedroom]/Lights", false)]
    [InlineData("*", "Wifi", true)]
    [InlineData("*", "Wifi/Ssid", false)]
    [InlineData("**", "Rooms[Kitchen]/Sensor", true)]
    public void WhenPatternHasNoLeadingSeparator_ThenItMatchesLikeThePatternWithOne(string pattern, string memberPath, bool expected)
    {
        // Arrange
        var provider = Policies.SlashPaths;
        var withoutSeparator = PathPattern.Parse(pattern, provider, "Test");
        var withSeparator = PathPattern.Parse("/" + pattern, provider, "Test");
        var (links, member) = ToMemberPath(provider, memberPath);

        // Act
        var result = withoutSeparator.Matches(links, member, isDeny: false);

        // Assert
        Assert.Equal(expected, result);
        Assert.Equal(withSeparator.Matches(links, member, isDeny: false), result);
    }

    [Fact]
    public void WhenPatternIsAPathTheRegistryWrote_ThenItCompilesAndMatchesThatMember()
    {
        // Arrange
        var home = TestGraph.Create();
        var brightness = TestGraph.Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Models.Light.Brightness));
        var path = brightness.TryGetPath(DefaultPathProvider.Instance, null)!;
        var policy = AuthorizationDefinition.Create(
            [Policies.Role("Reader", allow: [Policies.Rule([PermissionAction.Read], paths: [path])])], [],
            Policies.KnownActions, DefaultPathProvider.Instance, TestTags.Default).Compile(["Reader"]);

        // Act
        var result = policy.CanRead(brightness);
        var canReadKitchenCounter = policy.CanRead(TestGraph.Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Models.Light.Brightness)));

        // Assert
        Assert.Equal("Rooms[Kitchen].Lights[Ceiling].Brightness", path);
        Assert.True(result);
        Assert.False(canReadKitchenCounter);
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("/Rooms[", "malformed")]
    [InlineData("/Rooms[]", "malformed")]
    [InlineData("/ Rooms", "whitespace")]
    [InlineData("/Rooms[ Kitchen]", "whitespace")]
    [InlineData("/", "matches no member; use '**' for the subject and everything below it, or '*' for its members")]
    [InlineData("//", "matches no member; use '**'")]
    [InlineData("/Rooms[Kitchen]", "matches no member; use '/Rooms[Kitchen]/**' for the subject and everything below it, or '/Rooms[Kitchen]/*' for its members")]
    [InlineData("Rooms[Kitchen]", "matches no member; use 'Rooms[Kitchen]/**' for the subject and everything below it, or 'Rooms[Kitchen]/*' for its members")]
    [InlineData("//Rooms/*", "has an empty segment; '/' may only start a pattern and separate segments")]
    [InlineData("/Rooms//*", "has an empty segment")]
    [InlineData("Rooms/", "has an empty segment")]
    [InlineData("/Rooms[Kitchen]/", "has an empty segment")]
    [InlineData("/Rooms['a/']//*", "has an empty segment")]
    [InlineData("/Rooms[*]", "matches no member")]
    [InlineData("/**/Lights[C*]", "matches no member")]
    [InlineData("/Rooms/**[A]", "matches no member")]
    public void WhenPatternIsInvalid_ThenParseThrowsNamingTheRole(string pattern, string expectedMessagePart)
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(() => PathPattern.Parse(pattern, Policies.SlashPaths, "Reader"));

        // Assert
        Assert.Contains("Reader", exception.Message);
        Assert.Contains(expectedMessagePart, exception.Message);
    }

    [Theory]
    [InlineData("/Rooms[a/b]/*")]
    [InlineData("/Rooms['a//b']/*")]
    [InlineData("/Rooms['it''s/']/*")]
    [InlineData("Rooms['/']/**")]
    public void WhenAnIndexContainsTheSeparator_ThenThePatternHasNoEmptySegment(string pattern)
    {
        // Act
        var compiled = PathPattern.Parse(pattern, Policies.SlashPaths, "Test");

        // Assert
        Assert.NotNull(compiled);
    }

    [Theory]
    [InlineData("/Rooms.*", "starts with '/', which is not the path provider's separator '.'; write the pattern with '.' or without a leading separator")]
    [InlineData("\\Rooms.*", "starts with '\\', which is not the path provider's separator '.'")]
    [InlineData("/", "starts with '/'")]
    public void WhenPatternStartsWithAnotherProvidersSeparator_ThenParseThrowsNamingTheSeparator(string pattern, string expectedMessagePart)
    {
        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => PathPattern.Parse(pattern, DefaultPathProvider.Instance, "Reader"));
        Assert.Contains(expectedMessagePart, exception.Message);
    }

    [Fact]
    public void WhenSlashPatternStartsWithABackslash_ThenParseThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => PathPattern.Parse("\\Rooms/*", Policies.SlashPaths, "Reader"));
        Assert.Contains("separator '/'", exception.Message);
    }

    [Theory]
    [InlineData("Security.a/b.Name")]
    [InlineData("Security.a\\b.Name")]
    [InlineData(".Security.Roles['/a'].Name")]
    [InlineData("Areas[/Garage].**")]
    public void WhenASeparatorOfAnotherProviderIsNotTheFirstCharacter_ThenThePatternCompiles(string pattern)
    {
        // Act
        var compiled = PathPattern.Parse(pattern, DefaultPathProvider.Instance, "Test");

        // Assert
        Assert.NotNull(compiled);
    }

    private static readonly PathLink Name = new("Name", null);

    // The last segment of the member path is the member, the segments before it the path of its subject.
    private static (PathLink[] Links, PathLink Member) ToMemberPath(PathProviderBase provider, string memberPath)
    {
        Assert.True(provider.TryParsePath(memberPath, out var segments, out _));
        var links = segments.Take(segments.Count - 1).Select(segment => new PathLink(segment.segment, segment.index)).ToArray();
        Assert.Null(segments[^1].index);
        return (links, new PathLink(segments[^1].segment, null));
    }
}
