using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class PathSelectorTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];
    private static readonly PermissionAction[] Write = [PermissionAction.Write];

    [Fact]
    public void WhenPathNamesTheHoldingPropertyOfASubject_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(kitchen, nameof(Room.Sensor))));
        Assert.False(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Member(kitchen, nameof(Room.Lights))));
    }

    [Fact]
    public void WhenPathNamesAMember_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Temperature"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit")));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor))));
    }

    [Fact]
    public void WhenPathEndsWithAStar_ThenEveryMemberOfTheSubjectMatchesButNoneOfItsChildren()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/*"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(kitchen, nameof(Room.Lights))));
        Assert.True(policy.CanRead(Member(kitchen, nameof(Room.Sensor))));
        Assert.True(policy.CanRead(Member(kitchen, nameof(Room.Neighbor))));
        Assert.False(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"], nameof(Room.Lights))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Rooms))));
    }

    [Fact]
    public void WhenPathEndsWithADoubleStar_ThenEveryMemberOfTheSubjectAndOfEverySubjectBelowMatches()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/**"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(kitchen, nameof(Room.Lights))));
        Assert.True(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.True(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.True(policy.CanRead(Attribute(kitchen.Sensor!, nameof(Sensor.Temperature), "Unit")));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Rooms))));
    }

    [Fact]
    public void WhenADoubleStarMatchesZeroSegments_ThenTheHoldingPropertyMatchesToo()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Wifi/**", "/**/Sensor"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home, nameof(Home.Wifi))));
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Rooms))));
    }

    [Fact]
    public void WhenPathUsesWildcards_ThenMatchingMembersAreSelected()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Writer", allow: [Rule(Write, paths: ["/**/Lights[C*]/IsOn", "/Rooms[*]/Lights/Bright*"])]));
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        var lamp = home.Rooms["Bedroom"].Lights["Lamp"];

        // Act & Assert
        Assert.True(policy.CanWrite(Member(ceiling, nameof(Light.IsOn))));
        Assert.True(policy.CanWrite(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.IsOn))));
        Assert.False(policy.CanWrite(Member(lamp, nameof(Light.IsOn))));
        Assert.True(policy.CanWrite(Member(lamp, nameof(Light.Brightness))));
        Assert.True(policy.CanWrite(Member(ceiling, nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenPathNamesAMemberOfTheRoot_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Wifi))));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"], nameof(Room.Lights))));
    }

    [Fact]
    public void WhenPathIsTheSeparatorAndAStar_ThenItMatchesTheRootsMembersOrEverything()
    {
        // Arrange
        var home = Create();
        var rootMembers = Compile(Role("Reader", allow: [Rule(Read, paths: ["/*"])]));
        var everything = Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));

        // Act & Assert
        Assert.True(rootMembers.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.False(rootMembers.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.True(everything.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.True(everything.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Rooms[Bedroom]")]
    public void WhenADenyPatternCanMatchNoMember_ThenCompileThrows(string deniedPath)
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(
            () => Compile(Role("Operator", allow: [Rule(Write)], deny: [Rule(Write, paths: [deniedPath])])));

        // Assert
        Assert.Contains("Operator", exception.Message);
        Assert.Contains("matches no member", exception.Message);
    }

    [Fact]
    public void WhenDenyNamesTheHoldingPropertyAlone_ThenTheMembersBelowAreNotDenied()
    {
        // Arrange
        var home = Create();
        var lamp = home.Rooms["Bedroom"].Lights["Lamp"];
        var holdingProperty = Compile(Role("Operator", allow: [Rule(Write)], deny: [Rule(Write, paths: ["/Rooms[Bedroom]/Lights", "/Wifi"])]));
        var branch = Compile(Role("Operator", allow: [Rule(Write)], deny: [Rule(Write, paths: ["/Rooms[Bedroom]/**"])]));

        // Act & Assert
        Assert.False(holdingProperty.CanWrite(Member(home.Rooms["Bedroom"], nameof(Room.Lights))));
        Assert.True(holdingProperty.CanWrite(Member(lamp, nameof(Light.IsOn))));
        Assert.False(holdingProperty.CanWrite(Member(home, nameof(Home.Wifi))));
        Assert.True(holdingProperty.CanWrite(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.False(branch.CanWrite(Member(lamp, nameof(Light.IsOn))));
        Assert.False(branch.CanWrite(Member(home.Rooms["Bedroom"], nameof(Room.Lights))));
        Assert.True(branch.CanWrite(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn))));
    }

    [Fact]
    public void WhenSubjectHasTwoPaths_ThenADenyOnEitherDenies()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Spare/**"])]));
        var otherBranch = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Bedroom]/**"])]));

        // Act & Assert
        Assert.Equal(2, Registered(ceiling).Parents.Length);
        Assert.False(policy.CanRead(Member(ceiling, nameof(Light.Brightness))));
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness))));
        Assert.True(otherBranch.CanRead(Member(ceiling, nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenSubjectHasTwoPaths_ThenAnAllowOnEitherAllows()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Spare/*"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(ceiling, nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenPathAndOtherSelectorsAreCombined_ThenAllMustMatch()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/**"], types: ["ILight"], members: ["IsOn"])]));
        var kitchen = home.Rooms["Kitchen"];

        // Act & Assert
        Assert.True(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn))));
    }

    [Theory]
    [InlineData("/Areas[Garage]/**")]
    [InlineData("/Garage/**")]
    [InlineData("/Areas/**")]
    [InlineData("/Garage/Lights[Door]/Brightness")]
    [InlineData("/Areas[Garage]/Lights[Door]/Brightness")]
    public void WhenDenyNamesAnInlineKeyInEitherSpelling_ThenTheMembersBelowAreDenied(string deniedPath)
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: [deniedPath])]));

        // Act & Assert
        Assert.False(policy.CanRead(Member(door, nameof(Light.Brightness))));
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenAllowNamesAMemberBelowAnInlineKeyBare_ThenOnlyThatMemberIsAllowed()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Garage/Lights"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home.Areas["Garage"], nameof(Room.Lights))));
        Assert.False(policy.CanRead(Member(door, nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Lights))));
    }

    [Fact]
    public void WhenAnInlineKeyIsShadowedByAProperty_ThenTheBareSpellingNamesTheProperty()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Wifi");
        var bare = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Wifi/**"])]));
        var explicitForm = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Areas[Wifi]/**"])]));

        // Act & Assert
        Assert.False(bare.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.True(bare.CanRead(Member(door, nameof(Light.Brightness))));
        Assert.False(explicitForm.CanRead(Member(door, nameof(Light.Brightness))));
        Assert.True(explicitForm.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenTheProviderGivesAMemberNoSegment_ThenItIsMatchedByItsName()
    {
        // Arrange
        var home = Create();
        var policy = AuthorizationPolicy.Compile(
            [Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Spare"])])], ["Reader"], [],
            KnownActions, TestTags.Default, new SpareWithoutSegmentPathProvider());

        // Act & Assert
        Assert.False(policy.CanRead(Member(home, nameof(Home.Spare))));
        Assert.True(policy.CanRead(Member(home, nameof(Home.Wifi))));
    }

    [Fact]
    public void WhenTheProviderRenamesAProperty_ThenItsMemberIsMatchedByTheProvidersSegment()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var policy = AuthorizationPolicy.Compile(
            [Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/temp*"])])], ["Reader"], [],
            KnownActions, TestTags.Default, new TemperatureRenamingPathProvider());

        // Act & Assert
        Assert.True(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit")));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor))));
    }

    // Attribute paths follow the registry's path syntax, which spells an attribute by its name alone.

    [Fact]
    public void WhenPathNamesAnAttribute_ThenOnlyThatAttributeMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Unit"])]));

        // Act & Assert
        Assert.True(policy.CanRead(unit));
        Assert.False(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(unit.TryGetAttribute("Source")!));
    }

    [Fact]
    public void WhenPathNamesAnAttributeOfAnAttribute_ThenOnlyThatAttributeMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Source"])]));

        // Act & Assert
        Assert.True(policy.CanRead(unit.TryGetAttribute("Source")!));
        Assert.False(policy.CanRead(unit));
        Assert.False(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
    }

    [Fact]
    public void WhenPathEndsWithAStar_ThenAttributesOfEveryLevelMatchToo()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/*"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
        Assert.True(policy.CanRead(unit));
        Assert.True(policy.CanRead(unit.TryGetAttribute("Source")!));
    }

    [Fact]
    public void WhenPathNamesAMemberOfASubjectBelowAnAttribute_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var probe = new Light();
        sensor.Temperature_Probe = probe;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Probe/IsOn"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(probe, nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(probe, nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Probe")));
    }

    [Fact]
    public void WhenPathCoversAnAttributeWithADoubleStar_ThenTheAttributeAndEveryMemberBelowItMatch()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var probe = new Light();
        sensor.Temperature_Probe = probe;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Probe/**"])]));

        // Act & Assert
        Assert.False(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Probe")));
        Assert.False(policy.CanRead(Member(probe, nameof(Light.IsOn))));
        Assert.True(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
        Assert.True(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit")));
    }

    // Adds a room with a Door light to Home.Areas under the key and returns the light.
    private static Light AddArea(Home home, string key)
    {
        var door = new Light();
        var room = new Room { Lights = new Dictionary<string, Light> { ["Door"] = door } };
        home.Areas = new Dictionary<string, Room> { [key] = room };
        return door;
    }

    private sealed class SpareWithoutSegmentPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';

        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => !IsSpare(property);

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
            => IsSpare(property) ? null : base.TryGetPropertySegment(property);

        private static bool IsSpare(RegisteredSubjectProperty property)
            => property.Subject is Home && property.Name == nameof(Home.Spare);
    }

    private sealed class TemperatureRenamingPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
            => property.Subject is Sensor && property.Name == nameof(Sensor.Temperature) ? "temperature" : base.TryGetPropertySegment(property);
    }
}
