using System.Diagnostics.CodeAnalysis;
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

        // Act
        var canReadKitchenSensor = policy.CanRead(Member(kitchen, nameof(Room.Sensor)));
        var canReadKitchenTemperature = policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature)));
        var canReadKitchenLights = policy.CanRead(Member(kitchen, nameof(Room.Lights)));

        // Assert
        Assert.True(canReadKitchenSensor);
        Assert.False(canReadKitchenTemperature);
        Assert.False(canReadKitchenLights);
    }

    [Fact]
    public void WhenPathNamesAMember_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Temperature"])]));

        // Act
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));
        var canReadTemperatureUnit = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit"));
        var canReadKitchenSensor = policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor)));

        // Assert
        Assert.True(canReadSensorTemperature);
        Assert.False(canReadTemperatureUnit);
        Assert.False(canReadKitchenSensor);
    }

    [Fact]
    public void WhenPathEndsWithAStar_ThenEveryMemberOfTheSubjectMatchesButNoneOfItsChildren()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/*"])]));

        // Act
        var canReadKitchenLights = policy.CanRead(Member(kitchen, nameof(Room.Lights)));
        var canReadKitchenSensor = policy.CanRead(Member(kitchen, nameof(Room.Sensor)));
        var canReadKitchenNeighbor = policy.CanRead(Member(kitchen, nameof(Room.Neighbor)));
        var canReadCeilingIsOn = policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn)));
        var canReadKitchenTemperature = policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature)));
        var canReadBedroomLights = policy.CanRead(Member(home.Rooms["Bedroom"], nameof(Room.Lights)));
        var canReadRooms = policy.CanRead(Member(home, nameof(Home.Rooms)));

        // Assert
        Assert.True(canReadKitchenLights);
        Assert.True(canReadKitchenSensor);
        Assert.True(canReadKitchenNeighbor);
        Assert.False(canReadCeilingIsOn);
        Assert.False(canReadKitchenTemperature);
        Assert.False(canReadBedroomLights);
        Assert.False(canReadRooms);
    }

    [Fact]
    public void WhenPathEndsWithADoubleStar_ThenEveryMemberOfTheSubjectAndOfEverySubjectBelowMatches()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/**"])]));

        // Act
        var canReadKitchenLights = policy.CanRead(Member(kitchen, nameof(Room.Lights)));
        var canReadCeilingIsOn = policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn)));
        var canReadKitchenTemperature = policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature)));
        var canReadTemperatureUnit = policy.CanRead(Attribute(kitchen.Sensor!, nameof(Sensor.Temperature), "Unit"));
        var canReadLampIsOn = policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn)));
        var canReadRooms = policy.CanRead(Member(home, nameof(Home.Rooms)));

        // Assert
        Assert.True(canReadKitchenLights);
        Assert.True(canReadCeilingIsOn);
        Assert.True(canReadKitchenTemperature);
        Assert.True(canReadTemperatureUnit);
        Assert.False(canReadLampIsOn);
        Assert.False(canReadRooms);
    }

    [Fact]
    public void WhenADoubleStarMatchesZeroSegments_ThenTheHoldingPropertyMatchesToo()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Wifi/**", "/**/Sensor"])]));

        // Act
        var canReadWifi = policy.CanRead(Member(home, nameof(Home.Wifi)));
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var canReadKitchenSensor = policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor)));
        var canReadRooms = policy.CanRead(Member(home, nameof(Home.Rooms)));

        // Assert
        Assert.True(canReadWifi);
        Assert.True(canReadSsid);
        Assert.True(canReadKitchenSensor);
        Assert.False(canReadRooms);
    }

    [Fact]
    public void WhenPathUsesWildcards_ThenMatchingMembersAreSelected()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Writer", allow: [Rule(Write, paths: ["/**/Lights[C*]/IsOn", "/Rooms[*]/Lights/Bright*"])]));
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        var lamp = home.Rooms["Bedroom"].Lights["Lamp"];

        // Act
        var canWriteCeilingIsOn = policy.CanWrite(Member(ceiling, nameof(Light.IsOn)));
        var canWriteCounterIsOn = policy.CanWrite(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.IsOn)));
        var canWriteLampIsOn = policy.CanWrite(Member(lamp, nameof(Light.IsOn)));
        var canWriteLampBrightness = policy.CanWrite(Member(lamp, nameof(Light.Brightness)));
        var canWriteCeilingBrightness = policy.CanWrite(Member(ceiling, nameof(Light.Brightness)));

        // Assert
        Assert.True(canWriteCeilingIsOn);
        Assert.True(canWriteCounterIsOn);
        Assert.False(canWriteLampIsOn);
        Assert.True(canWriteLampBrightness);
        Assert.True(canWriteCeilingBrightness);
    }

    [Fact]
    public void WhenPathNamesAMemberOfTheRoot_ThenOnlyThatMemberMatches()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms"])]));

        // Act
        var canReadRooms = policy.CanRead(Member(home, nameof(Home.Rooms)));
        var canReadWifi = policy.CanRead(Member(home, nameof(Home.Wifi)));
        var canReadBedroomLights = policy.CanRead(Member(home.Rooms["Bedroom"], nameof(Room.Lights)));

        // Assert
        Assert.True(canReadRooms);
        Assert.False(canReadWifi);
        Assert.False(canReadBedroomLights);
    }

    [Fact]
    public void WhenPathIsTheSeparatorAndAStar_ThenItMatchesTheRootsMembersOrEverything()
    {
        // Arrange
        var home = Create();
        var rootMembers = Compile(Role("Reader", allow: [Rule(Read, paths: ["/*"])]));
        var everything = Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));

        // Act
        var rootMembersCanReadRooms = rootMembers.CanRead(Member(home, nameof(Home.Rooms)));
        var rootMembersCanReadSsid = rootMembers.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var everythingCanReadRooms = everything.CanRead(Member(home, nameof(Home.Rooms)));
        var everythingCanReadSsid = everything.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.True(rootMembersCanReadRooms);
        Assert.False(rootMembersCanReadSsid);
        Assert.True(everythingCanReadRooms);
        Assert.True(everythingCanReadSsid);
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

        // Act
        var holdingPropertyCanWriteBedroomLights = holdingProperty.CanWrite(Member(home.Rooms["Bedroom"], nameof(Room.Lights)));
        var holdingPropertyCanWriteLampIsOn = holdingProperty.CanWrite(Member(lamp, nameof(Light.IsOn)));
        var holdingPropertyCanWriteWifi = holdingProperty.CanWrite(Member(home, nameof(Home.Wifi)));
        var holdingPropertyCanWriteSsid = holdingProperty.CanWrite(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var branchCanWriteLampIsOn = branch.CanWrite(Member(lamp, nameof(Light.IsOn)));
        var branchCanWriteBedroomLights = branch.CanWrite(Member(home.Rooms["Bedroom"], nameof(Room.Lights)));
        var branchCanWriteCeilingIsOn = branch.CanWrite(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn)));

        // Assert
        Assert.False(holdingPropertyCanWriteBedroomLights);
        Assert.True(holdingPropertyCanWriteLampIsOn);
        Assert.False(holdingPropertyCanWriteWifi);
        Assert.True(holdingPropertyCanWriteSsid);
        Assert.False(branchCanWriteLampIsOn);
        Assert.False(branchCanWriteBedroomLights);
        Assert.True(branchCanWriteCeilingIsOn);
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

        // Act
        var canReadCeiling = policy.CanRead(Member(ceiling, nameof(Light.Brightness)));
        var canReadCounter = policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness)));
        var otherBranchCanReadCeiling = otherBranch.CanRead(Member(ceiling, nameof(Light.Brightness)));

        // Assert
        Assert.Equal(2, Registered(ceiling).Parents.Length);
        Assert.False(canReadCeiling);
        Assert.True(canReadCounter);
        Assert.True(otherBranchCanReadCeiling);
    }

    [Fact]
    public void WhenSubjectHasTwoPaths_ThenAnAllowOnEitherAllows()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Spare/*"])]));

        // Act
        var canReadCeilingBrightness = policy.CanRead(Member(ceiling, nameof(Light.Brightness)));
        var canReadCounterBrightness = policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness)));

        // Assert
        Assert.True(canReadCeilingBrightness);
        Assert.False(canReadCounterBrightness);
    }

    [Fact]
    public void WhenPathAndOtherSelectorsAreCombined_ThenAllMustMatch()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/**"], types: ["ILight"], members: ["IsOn"])]));
        var kitchen = home.Rooms["Kitchen"];

        // Act
        var canReadCeilingIsOn = policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn)));
        var canReadCeilingBrightness = policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.Brightness)));
        var canReadKitchenTemperature = policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature)));
        var canReadLampIsOn = policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn)));

        // Assert
        Assert.True(canReadCeilingIsOn);
        Assert.False(canReadCeilingBrightness);
        Assert.False(canReadKitchenTemperature);
        Assert.False(canReadLampIsOn);
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

        // Act
        var canReadDoorBrightness = policy.CanRead(Member(door, nameof(Light.Brightness)));
        var canReadCeilingBrightness = policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness)));

        // Assert
        Assert.False(canReadDoorBrightness);
        Assert.True(canReadCeilingBrightness);
    }

    [Theory]
    [InlineData("", "/Areas['']/**")]
    [InlineData("'quoted", "/Areas['''quoted']/**")]
    [InlineData("it's", "/Areas[it's]/**")]
    [InlineData("a]b", "/Areas['a]b']/**")]
    public void WhenDenyNamesAKeyThatNeedsQuoting_ThenTheMembersBelowAreDenied(string key, string deniedPath)
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, key);
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: [deniedPath])]));

        // Act
        var canReadDoorBrightness = policy.CanRead(Member(door, nameof(Light.Brightness)));
        var canReadCeilingBrightness = policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness)));

        // Assert
        Assert.False(canReadDoorBrightness);
        Assert.True(canReadCeilingBrightness);
    }

    [Fact]
    public void WhenAllowNamesAMemberBelowAnInlineKeyBare_ThenOnlyThatMemberIsAllowed()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Garage/Lights"])]));

        // Act
        var canReadGarageLights = policy.CanRead(Member(home.Areas["Garage"], nameof(Room.Lights)));
        var canReadDoorBrightness = policy.CanRead(Member(door, nameof(Light.Brightness)));
        var canReadKitchenLights = policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Lights)));

        // Assert
        Assert.True(canReadGarageLights);
        Assert.False(canReadDoorBrightness);
        Assert.False(canReadKitchenLights);
    }

    [Fact]
    public void WhenAnInlineKeyIsShadowedByAProperty_ThenAnAllowInTheBareSpellingNamesOnlyThePropertyAndADenyBoth()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Wifi");
        var bareAllow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Wifi/**"])]));
        var bare = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Wifi/**"])]));
        var explicitForm = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Areas[Wifi]/**"])]));

        // Act
        var bareAllowCanReadSsid = bareAllow.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var bareAllowCanReadDoorBrightness = bareAllow.CanRead(Member(door, nameof(Light.Brightness)));
        var bareCanReadSsid = bare.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var bareCanReadDoorBrightness = bare.CanRead(Member(door, nameof(Light.Brightness)));
        var explicitFormCanReadDoorBrightness = explicitForm.CanRead(Member(door, nameof(Light.Brightness)));
        var explicitFormCanReadSsid = explicitForm.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.True(bareAllowCanReadSsid);
        Assert.False(bareAllowCanReadDoorBrightness);
        Assert.False(bareCanReadSsid);
        Assert.False(bareCanReadDoorBrightness);
        Assert.False(explicitFormCanReadDoorBrightness);
        Assert.True(explicitFormCanReadSsid);
    }

    [Fact]
    public void WhenAnAttributeAddedLaterShadowsTheBareSpellingOfAnInlineKey_ThenADenyInThatSpellingStillApplies()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Garage/**"])]));
        var member = Member(door, nameof(Light.Brightness));

        // Act
        var canReadBefore = policy.CanRead(member);
        Member(home, nameof(Home.Wifi)).AddAttribute("Garage", typeof(string), _ => "x", null);
        var canReadAfter = policy.CanRead(member);

        // Assert
        Assert.False(canReadBefore);
        Assert.False(canReadAfter);
    }

    [Theory]
    [InlineData("/Areas[Garage]/**")]
    [InlineData("/Areas/**")]
    public void WhenAnAttributeAddedLaterShadowsTheOwnSegmentOfAnInlineProperty_ThenADenyInTheExplicitSpellingStillApplies(string deniedPath)
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: [deniedPath])]));
        var member = Member(door, nameof(Light.Brightness));

        // Act
        var canReadBefore = policy.CanRead(member);
        Member(home, nameof(Home.Wifi)).AddAttribute("Areas", typeof(string), _ => "x", null);
        var canReadAfter = policy.CanRead(member);

        // Assert
        Assert.False(canReadBefore);
        Assert.False(canReadAfter);
    }

    [Fact]
    public void WhenAnAttributeAddedLaterShadowsTheBareSpellingOfAnInlineKey_ThenAnAllowInThatSpellingStopsMatching()
    {
        // Arrange
        var home = Create();
        var door = AddArea(home, "Garage");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Garage/**"])]));
        var member = Member(door, nameof(Light.Brightness));

        // Act
        var canReadBefore = policy.CanRead(member);
        Member(home, nameof(Home.Wifi)).AddAttribute("Garage", typeof(string), _ => "x", null);
        var canReadAfter = policy.CanRead(member);

        // Assert
        Assert.True(canReadBefore);
        Assert.False(canReadAfter);
    }

    [Fact]
    public void WhenTheOwnSegmentOfAnInlinePropertyWithoutAnIndexIsShadowed_ThenADenyWithPathsAppliesAndAnAllowWithPathsDoesNot()
    {
        // Arrange
        var light = new Light();
        _ = new TestShadowedInlineReference(CreateContext()) { Label = "label", Child = light };
        var pathProvider = new AttributeBasedPathProvider("test");
        var deny = AuthorizationPolicy.Compile(
            [Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["Other.**"])])], ["Reader"], [],
            KnownActions, TestTags.Default, pathProvider);
        var allow = AuthorizationPolicy.Compile(
            [Role("Reader", allow: [Rule(Read, paths: ["Child.**"])])], ["Reader"], [],
            KnownActions, TestTags.Default, pathProvider);

        // Act
        var denyPolicyAllows = deny.CanRead(Member(light, nameof(Light.IsOn)));
        var allowPolicyAllows = allow.CanRead(Member(light, nameof(Light.IsOn)));

        // Assert
        Assert.False(denyPolicyAllows);
        Assert.False(allowPolicyAllows);
    }

    [Fact]
    public void WhenTheProviderExcludesAMember_ThenItIsMatchedByItsName()
    {
        // Arrange
        var home = Create();
        var policy = AuthorizationPolicy.Compile(
            [Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Spare"])])], ["Reader"], [],
            KnownActions, TestTags.Default, new SpareExcludingPathProvider());

        // Act
        var canReadSpare = policy.CanRead(Member(home, nameof(Home.Spare)));
        var canReadWifi = policy.CanRead(Member(home, nameof(Home.Wifi)));

        // Assert
        Assert.False(canReadSpare);
        Assert.True(canReadWifi);
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

        // Act
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));
        var canReadTemperatureUnit = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit"));
        var canReadKitchenSensor = policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Sensor)));

        // Assert
        Assert.True(canReadSensorTemperature);
        Assert.False(canReadTemperatureUnit);
        Assert.False(canReadKitchenSensor);
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

        // Act
        var canReadUnit = policy.CanRead(unit);
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));
        var canReadUnitSource = policy.CanRead(unit.TryGetAttribute("Source")!);

        // Assert
        Assert.True(canReadUnit);
        Assert.False(canReadSensorTemperature);
        Assert.False(canReadUnitSource);
    }

    [Fact]
    public void WhenPathNamesAnAttributeOfAnAttribute_ThenOnlyThatAttributeMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Source"])]));

        // Act
        var canReadUnitSource = policy.CanRead(unit.TryGetAttribute("Source")!);
        var canReadUnit = policy.CanRead(unit);
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));

        // Assert
        Assert.True(canReadUnitSource);
        Assert.False(canReadUnit);
        Assert.False(canReadSensorTemperature);
    }

    [Fact]
    public void WhenPathEndsWithAStar_ThenAttributesOfEveryLevelMatchToo()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/*"])]));

        // Act
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));
        var canReadUnit = policy.CanRead(unit);
        var canReadUnitSource = policy.CanRead(unit.TryGetAttribute("Source")!);

        // Assert
        Assert.True(canReadSensorTemperature);
        Assert.True(canReadUnit);
        Assert.True(canReadUnitSource);
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

        // Act
        var canReadProbeIsOn = policy.CanRead(Member(probe, nameof(Light.IsOn)));
        var canReadProbeBrightness = policy.CanRead(Member(probe, nameof(Light.Brightness)));
        var canReadTemperatureProbe = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Probe"));

        // Assert
        Assert.True(canReadProbeIsOn);
        Assert.False(canReadProbeBrightness);
        Assert.False(canReadTemperatureProbe);
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

        // Act
        var canReadTemperatureProbe = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Probe"));
        var canReadProbeIsOn = policy.CanRead(Member(probe, nameof(Light.IsOn)));
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));
        var canReadTemperatureUnit = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit"));

        // Assert
        Assert.False(canReadTemperatureProbe);
        Assert.False(canReadProbeIsOn);
        Assert.True(canReadSensorTemperature);
        Assert.True(canReadTemperatureUnit);
    }

    // Adds a room with a Door light to Home.Areas under the key and returns the light.
    private static Light AddArea(Home home, string key)
    {
        var door = new Light();
        var room = new Room { Lights = new Dictionary<string, Light> { ["Door"] = door } };
        home.Areas = new Dictionary<string, Room> { [key] = room };
        return door;
    }

    private sealed class SpareExcludingPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (IsSpare(property))
            {
                segment = null;
                return false;
            }

            return base.TryGetPropertySegment(property, out segment);
        }

        private static bool IsSpare(RegisteredSubjectProperty property)
            => property.Subject is Home && property.Name == nameof(Home.Spare);
    }

    private sealed class TemperatureRenamingPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (property.Subject is Sensor && property.Name == nameof(Sensor.Temperature))
            {
                segment = "temperature";
                return true;
            }

            return base.TryGetPropertySegment(property, out segment);
        }
    }
}
