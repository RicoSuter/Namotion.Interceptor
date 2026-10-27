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

        // Act & Assert
        Assert.True(Policy().CanWrite(brightness));
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

        // Act & Assert
        Assert.True(policy.CanRead(brightness));
        Assert.True(policy.IsAllowed(brightness, History));
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

        // Act & Assert
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.False(policy.CanRead(Registered(home.Wifi!)));
    }

    [Fact]
    public void WhenTheTreeIsBrowsed_ThenEverythingButWifiIsReadable()
    {
        // Arrange
        var home = Create();
        var policy = Policy();
        var sensor = home.Rooms["Kitchen"].Sensor!;

        // Act & Assert
        Assert.True(policy.CanRead(Registered(home)));
        Assert.True(policy.CanRead(Registered(home.Rooms["Kitchen"])));
        Assert.True(policy.CanRead(Registered(sensor)));
        Assert.True(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit")));
        Assert.True(policy.CanRead(Registered(home.Rooms["Bedroom"].Lights["Lamp"])));
        Assert.False(policy.CanRead(Registered(home.Wifi!)));
    }
}
