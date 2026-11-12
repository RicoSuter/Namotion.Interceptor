using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class AuthorizationPolicyTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];
    private static readonly PermissionAction[] Write = [PermissionAction.Write];

    [Fact]
    public void WhenGraphIsBuilt_ThenMembersHaveTheExpectedPaths()
    {
        // Arrange
        var home = Create();

        // Act
        var brightness = Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness));
        var unit = Attribute(home.Rooms["Kitchen"].Sensor!, nameof(Sensor.Temperature), "Unit");

        // Assert
        Assert.Equal("Rooms[Kitchen]/Lights[Ceiling]/Brightness", brightness.TryGetPath(Policies.SlashPaths, null));
        Assert.True(unit.IsAttribute);
        Assert.Equal("Unit", unit.BrowseName);
    }

    [Fact]
    public void WhenNoRuleMatches_ThenActionIsDenied()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Empty"));

        // Act
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.False(canReadSsid);
    }

    [Fact]
    public void WhenAllowRuleWithTagMatches_ThenActionIsAllowedAndOthersAreNot()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state"])]));
        var brightness = Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness));

        // Act
        var canReadBrightness = policy.CanRead(brightness);
        var canWriteBrightness = policy.CanWrite(brightness);
        var isAllowedBrightness = policy.IsAllowed(brightness, Execute);

        // Assert
        Assert.True(canReadBrightness);
        Assert.False(canWriteBrightness);
        Assert.False(isAllowedBrightness);
    }

    [Fact]
    public void WhenDenyRuleMatches_ThenDenyWinsOverAllow()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, tags: ["secret"])]));

        // Act
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));
        var canReadPassword = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password)));

        // Assert
        Assert.True(canReadSsid);
        Assert.False(canReadPassword);
    }

    [Fact]
    public void WhenDenyIsInAnIncludedRole_ThenIncludingRoleCannotAllowItBack()
    {
        // Arrange
        var home = Create();
        var reader = Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, tags: ["secret"])]);
        var admin = Role("Admin", allow: [Rule(Read, members: ["Password"])], includes: ["Reader"]);
        var policy = Compile([reader, admin], ["Admin"], systemDeny: []);

        // Act
        var canReadPassword = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password)));

        // Assert
        Assert.False(canReadPassword);
    }

    [Fact]
    public void WhenSeveralRolesAreNamed_ThenTheirRulesCombine()
    {
        // Arrange
        var home = Create();
        var reader = Role("Reader", allow: [Rule(Read, tags: ["state"])]);
        var writer = Role("Writer", allow: [Rule(Write, types: ["ILight"])]);
        var policy = Compile([reader, writer], ["Reader", "Writer"], systemDeny: []);
        var brightness = Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness));

        // Act
        var canReadBrightness = policy.CanRead(brightness);
        var canWriteBrightness = policy.CanWrite(brightness);

        // Assert
        Assert.True(canReadBrightness);
        Assert.True(canWriteBrightness);
    }

    [Fact]
    public void WhenMemberGlobIsUsed_ThenOnlyMatchingMembersAreAllowed()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, members: ["Bright*"])]));
        var light = home.Rooms["Kitchen"].Lights["Ceiling"];

        // Act
        var canReadLightBrightness = policy.CanRead(Member(light, nameof(Light.Brightness)));
        var canReadLightIsOn = policy.CanRead(Member(light, nameof(Light.IsOn)));

        // Assert
        Assert.True(canReadLightBrightness);
        Assert.False(canReadLightIsOn);
    }

    [Theory]
    [InlineData("ILight")]
    [InlineData("Light")]
    [InlineData("Namotion.Interceptor.Authorization.Tests.Models.ILight")]
    [InlineData("Namotion.Interceptor.Authorization.Tests.Models.Light")]
    public void WhenTypeIsNamed_ThenSubjectsAssignableToItMatch(string typeName)
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, types: [typeName])]));

        // Act
        var canReadCeilingIsOn = policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn)));
        var canReadKitchenTemperature = policy.CanRead(Member(home.Rooms["Kitchen"].Sensor!, nameof(Sensor.Temperature)));

        // Assert
        Assert.True(canReadCeilingIsOn);
        Assert.False(canReadKitchenTemperature);
    }

    [Fact]
    public void WhenMemberIsAnAttribute_ThenItIsNamedPropertyAtAttribute()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var policy = Compile(Role("Reader", allow: [Rule(Read, members: ["Temperature@Unit"])]));

        // Act
        var canReadTemperatureUnit = policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit"));
        var canReadSensorTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));

        // Assert
        Assert.True(canReadTemperatureUnit);
        Assert.False(canReadSensorTemperature);
    }

    [Fact]
    public void WhenRuleHasSeveralTags_ThenMemberMustCarryAll()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state", "SECRET"])]));

        // Act
        var canReadPassword = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password)));
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.True(canReadPassword);
        Assert.False(canReadSsid);
    }

    [Fact]
    public void WhenSystemDenyRuleMatches_ThenNoRoleAllowsIt()
    {
        // Arrange
        var home = Create();
        var policy = Compile([Role("Reader", allow: [Rule(Read)])], ["Reader"], systemDeny: [Rule(Read, tags: ["secret"])]);

        // Act
        var canReadPassword = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password)));
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.False(canReadPassword);
        Assert.True(canReadSsid);
    }

    [Fact]
    public void WhenRuleNamesAnUnknownAction_ThenCompileThrows()
    {
        // Arrange
        var role = Role("Reader", allow: [Rule([new PermissionAction("histroy")])]);

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));

        // Assert
        Assert.Equal("Action 'histroy' of role 'Reader' is not a known action.", exception.Message);
    }

    [Fact]
    public void WhenRuleHasNoActions_ThenCompileThrows()
    {
        // Arrange
        var role = Role("Reader", allow: [Rule([], tags: ["state"])]);

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));

        // Assert
        Assert.Equal("A rule of role 'Reader' has no actions.", exception.Message);
    }

    [Fact]
    public void WhenRuleHasAnEmptySelectorValue_ThenCompileThrows()
    {
        // Arrange
        var role = Role("Reader", allow: [Rule(Read, tags: [" "])]);

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));

        // Assert
        Assert.Equal("A rule of role 'Reader' has an empty tag.", exception.Message);
    }

    [Fact]
    public void WhenARuleIsNull_ThenCompileThrowsNamingTheRole()
    {
        // Arrange
        var role = new AuthorizationRole { Name = "Reader", Allow = [null!] };

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));

        // Assert
        Assert.Equal("A rule of role 'Reader' is null.", exception.Message);
    }

    [Fact]
    public void WhenARoleNamedSystemAllows_ThenTheSystemDenyRulesStillDeny()
    {
        // Arrange
        var home = Create();
        var system = Role("system", allow: [Rule(Read), Rule(Read, tags: ["secret"])]);

        // Act
        var policy = Compile([system], ["system"], systemDeny: [Rule(Read, tags: ["secret"])]);

        // Assert
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenASystemDenyRuleHasAnInvalidPath_ThenCompileThrowsNamingTheSystemRules()
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(
            () => Compile([Role("Reader")], ["Reader"], systemDeny: [Rule(Read, paths: ["/"])]));

        // Assert
        Assert.StartsWith("Path pattern '/' of the system rules matches no member", exception.Message);
    }

    [Fact]
    public void WhenActionIsNotKnownToThePolicy_ThenIsAllowedThrows()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read)]));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => policy.IsAllowed(Member(home.Wifi!, nameof(Wifi.Ssid)), new PermissionAction("subscribe")));
    }

    [Fact]
    public void WhenKnownActionsOmitReadAndWrite_ThenTheyAreKnownAnyway()
    {
        // Arrange
        var home = Create();
        var role = Role("Reader", allow: [Rule(Read)]);

        // Act
        var policy = AuthorizationDefinition.Create([role], [], [Execute], Policies.SlashPaths, TestTags.Default).Compile(["Reader"]);
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.True(canReadSsid);
    }

    [Fact]
    public void WhenMoreThan64ActionsAreKnown_ThenCompileThrows()
    {
        // Arrange
        var actions = Enumerable.Range(0, 70).Select(i => new PermissionAction($"action{i}")).ToArray();

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationDefinition.Create([], [], actions, Policies.SlashPaths, TestTags.Default));
        Assert.Equal("At most 64 actions are supported.", exception.Message);
    }

    [Fact]
    public void WhenTheTagProviderReturnsADefaultArray_ThenEvaluationThrowsNamingTheProvider()
    {
        // Arrange
        var home = Create();
        var provider = new DelegateTagProvider(_ => default);
        var policy = Policies.Compile([Role("Reader", allow: [Rule(Read, tags: ["state"])])], ["Reader"], [], provider);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.Contains(typeof(DelegateTagProvider).FullName!, exception.Message);
    }

    [Fact]
    public void WhenPolicyIsDenyAll_ThenEverythingIsDeniedForAnyAction()
    {
        // Arrange
        var home = Create();
        var member = Member(home.Wifi!, nameof(Wifi.Ssid));

        // Act
        var canReadMember = AuthorizationPolicy.DenyAll.CanRead(member);
        var isAnythingAllowed = AuthorizationPolicy.DenyAll.IsAllowed(member, new PermissionAction("anything"));
        var canReadSubject = AuthorizationPolicy.DenyAll.IsVisible(Registered(home));
        var roles = AuthorizationPolicy.DenyAll.Roles;

        // Assert
        Assert.False(canReadMember);
        Assert.False(isAnythingAllowed);
        Assert.False(canReadSubject);
        Assert.Empty(roles);
    }

    [Fact]
    public void WhenCompiled_ThenRolesIsTheClosureIncludedFirst()
    {
        // Arrange
        var reader = Role("Reader");
        var agent = Role("Agent", includes: ["Reader"]);

        // Act
        var policy = Compile([reader, agent], ["Agent"], systemDeny: []);

        // Assert
        Assert.Equal(["Reader", "Agent"], policy.Roles.Select(role => role.Name));
    }
}
