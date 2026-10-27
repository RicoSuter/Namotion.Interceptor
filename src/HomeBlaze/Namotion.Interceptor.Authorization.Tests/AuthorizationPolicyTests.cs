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

        // Act & Assert
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenAllowRuleWithTagMatches_ThenActionIsAllowedAndOthersAreNot()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state"])]));
        var brightness = Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness));

        // Act & Assert
        Assert.True(policy.CanRead(brightness));
        Assert.False(policy.CanWrite(brightness));
        Assert.False(policy.IsAllowed(brightness, Execute));
    }

    [Fact]
    public void WhenDenyRuleMatches_ThenDenyWinsOverAllow()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, tags: ["secret"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
    }

    [Fact]
    public void WhenDenyIsInAnIncludedRole_ThenIncludingRoleCannotAllowItBack()
    {
        // Arrange
        var home = Create();
        var reader = Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, tags: ["secret"])]);
        var admin = Role("Admin", allow: [Rule(Read, members: ["Password"])], includes: ["Reader"]);
        var policy = Compile([reader, admin], ["Admin"], systemDeny: []);

        // Act & Assert
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
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

        // Act & Assert
        Assert.True(policy.CanRead(brightness));
        Assert.True(policy.CanWrite(brightness));
    }

    [Fact]
    public void WhenMemberGlobIsUsed_ThenOnlyMatchingMembersAreAllowed()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, members: ["Bright*"])]));
        var light = home.Rooms["Kitchen"].Lights["Ceiling"];

        // Act & Assert
        Assert.True(policy.CanRead(Member(light, nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(light, nameof(Light.IsOn))));
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

        // Act & Assert
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"].Sensor!, nameof(Sensor.Temperature))));
    }

    [Fact]
    public void WhenMemberIsAnAttribute_ThenItIsNamedPropertyAtAttribute()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var policy = Compile(Role("Reader", allow: [Rule(Read, members: ["Temperature@Unit"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Attribute(sensor, nameof(Sensor.Temperature), "Unit")));
        Assert.False(policy.CanRead(Member(sensor, nameof(Sensor.Temperature))));
    }

    [Fact]
    public void WhenRuleHasSeveralTags_ThenMemberMustCarryAll()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state", "SECRET"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenSystemDenyRuleMatches_ThenNoRoleAllowsIt()
    {
        // Arrange
        var home = Create();
        var policy = Compile([Role("Reader", allow: [Rule(Read)])], ["Reader"], systemDeny: [Rule(Read, tags: ["secret"])]);

        // Act & Assert
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
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
    public void WhenRoleIsNamedSystem_ThenCompileThrows()
    {
        // Act
        var exception = Assert.Throws<AuthorizationException>(() => Compile(Role("system")));

        // Assert
        Assert.Equal("Role name 'System' is reserved.", exception.Message);
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
        var policy = AuthorizationPolicy.Compile([role], ["Reader"], [], [Execute], TestTags.Default, Policies.SlashPaths);

        // Assert
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenMoreThan64ActionsAreKnown_ThenCompileThrows()
    {
        // Arrange
        var actions = Enumerable.Range(0, 70).Select(i => new PermissionAction($"action{i}")).ToArray();

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationPolicy.Compile([], [], [], actions, TestTags.Default, Policies.SlashPaths));
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

        // Act & Assert
        Assert.False(AuthorizationPolicy.DenyAll.CanRead(member));
        Assert.False(AuthorizationPolicy.DenyAll.IsAllowed(member, new PermissionAction("anything")));
        Assert.False(AuthorizationPolicy.DenyAll.CanRead(Registered(home)));
        Assert.Empty(AuthorizationPolicy.DenyAll.Roles);
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
