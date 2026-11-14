using System.Collections.Immutable;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class WorkedExampleTests
{
    private static readonly IMemberTagProvider Tags = new DelegateTagProvider(GetTags);

    private static ImmutableArray<string> GetTags(RegisteredSubjectProperty member)
    {
        if (member.IsAttribute)
        {
            return ["attribute"];
        }

        if (member.CanContainSubjects)
        {
            return ["subjects"];
        }

        return member.Name switch
        {
            nameof(Light.IsOn) => ["state", "operation"],
            nameof(Wifi.Password) => ["state", "secret"],
            _ => ["state"]
        };
    }

    private static readonly AuthorizationRole FullReader = Role("FullReader", allow:
    [
        Rule([PermissionAction.Read, History], tags: ["state"]),
        Rule([PermissionAction.Read], tags: ["subjects"]),
        Rule([PermissionAction.Read], tags: ["attribute"])
    ]);

    private static readonly AuthorizationRole LightOperator = Role("LightOperator",
        includes: ["FullReader"],
        allow: [Rule([PermissionAction.Write, Execute], types: ["ILight"], members: ["IsOn", "Brightness"])],
        deny: [Rule([PermissionAction.Write, Execute], paths: ["/Rooms[Bedroom]/**"])]);

    private static readonly ImmutableArray<PermissionRule> SystemDeny =
    [
        Rule([PermissionAction.Read, PermissionAction.Write, History], tags: ["secret"]),
        Rule(KnownActions, types: ["Wifi"])
    ];

    private static AuthorizationPolicy Policy() => Compile([FullReader, LightOperator], ["LightOperator"], SystemDeny, Tags);

    [Fact]
    public void WhenKitchenLightIsWritten_ThenItIsAllowed()
    {
        // Arrange
        var home = Create();
        var brightness = Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness));

        // Act
        var canWriteBrightness = Policy().CanWrite(brightness);

        // Assert
        Assert.True(canWriteBrightness);
    }

    [Fact]
    public void WhenBedroomLightIsOperated_ThenTheBranchDenyWins()
    {
        // Arrange
        var home = Create();
        var isOn = Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn));

        // Act
        var decision = Policy().Explain(isOn, Execute);

        // Assert
        Assert.False(decision.IsAllowed);
        Assert.Same(LightOperator, decision.AllowedBy);
        Assert.Same(LightOperator, decision.DeniedBy);
    }

    [Fact]
    public void WhenBedroomLightIsRead_ThenTheBranchDenyDoesNotApplyToRead()
    {
        // Arrange
        var home = Create();
        var policy = Policy();
        var brightness = Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.Brightness));

        // Act
        var canReadBrightness = policy.CanRead(brightness);
        var isAllowedBrightness = policy.IsAllowed(brightness, History);

        // Assert
        Assert.True(canReadBrightness);
        Assert.True(isAllowedBrightness);
    }

    [Fact]
    public void WhenASecretIsRead_ThenTheSystemDeniesIt()
    {
        // Arrange
        var home = Create();
        var policy = Policy();

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Password)), PermissionAction.Read);

        // Assert
        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationPolicy.SystemRoleName, decision.DeniedBy!.Name);
        Assert.Equal(["secret"], decision.DeniedByRule!.Tags.AsEnumerable());
    }

    [Fact]
    public void WhenASubjectTypeIsDeniedBySystem_ThenTheSubjectIsHidden()
    {
        // Arrange
        var home = Create();
        var policy = Policy();

        // Act
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var canReadSubject = policy.IsVisible(Registered(home.Wifi!));

        // Assert
        Assert.False(canReadSsid);
        Assert.False(canReadSubject);
    }

    [Fact]
    public void WhenTheTreeIsBrowsed_ThenEverythingButWifiIsReadable()
    {
        // Arrange
        var home = Create();
        var policy = Policy();
        var sensor = home.Rooms["Kitchen"].Sensor!;

        // Act
        var canReadHomeSubject = policy.IsVisible(Registered(home));
        var canReadKitchenSubject = policy.IsVisible(Registered(home.Rooms["Kitchen"]));
        var canReadSensorSubject = policy.IsVisible(Registered(sensor));
        var canReadSensorTemperatureUnit = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit"));
        var canReadBedroomLampSubject = policy.IsVisible(Registered(home.Rooms["Bedroom"].Lights["Lamp"]));
        var canReadWifiSubject = policy.IsVisible(Registered(home.Wifi!));

        // Assert
        Assert.True(canReadHomeSubject);
        Assert.True(canReadKitchenSubject);
        Assert.True(canReadSensorSubject);
        Assert.True(canReadSensorTemperatureUnit);
        Assert.True(canReadBedroomLampSubject);
        Assert.False(canReadWifiSubject);
    }
}
