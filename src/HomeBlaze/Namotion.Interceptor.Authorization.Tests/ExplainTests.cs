using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class ExplainTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    [Fact]
    public void WhenAllowed_ThenTheAllowingRoleAndRuleAreNamed()
    {
        // Arrange
        var home = Create();
        var stateRule = Rule(Read, tags: ["state"]);
        var reader = Role("Reader", allow: [stateRule]);
        var policy = Compile(reader);

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Ssid)), PermissionAction.Read);

        // Assert
        Assert.True(decision.IsAllowed);
        Assert.Same(reader, decision.AllowedBy);
        Assert.Same(stateRule, decision.AllowedByRule);
        Assert.Null(decision.DeniedBy);
        Assert.Null(decision.DeniedByRule);
    }

    [Fact]
    public void WhenDeniedByARole_ThenBothSidesAreNamed()
    {
        // Arrange
        var home = Create();
        var reader = Role("Reader", allow: [Rule(Read)]);
        var secretRule = Rule(Read, tags: ["secret"]);
        var agent = Role("Agent", deny: [secretRule], includes: ["Reader"]);
        var policy = Compile([reader, agent], ["Agent"], systemDeny: [Rule(Read, tags: ["secret"])]);

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Password)), PermissionAction.Read);

        // Assert
        Assert.False(decision.IsAllowed);
        Assert.Same(reader, decision.AllowedBy);
        Assert.Same(agent, decision.DeniedBy);
        Assert.Same(secretRule, decision.DeniedByRule);
    }

    [Fact]
    public void WhenDeniedByTheSystem_ThenTheSystemRoleIsNamed()
    {
        // Arrange
        var home = Create();
        var policy = Compile([Role("Reader", allow: [Rule(Read)])], ["Reader"], systemDeny: [Rule(Read, tags: ["secret"])]);

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Password)), PermissionAction.Read);

        // Assert
        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationPolicy.SystemRoleName, decision.DeniedBy!.Name);
        Assert.Equal(["secret"], decision.DeniedByRule!.Tags.AsEnumerable());
    }

    [Fact]
    public void WhenNothingAllows_ThenBothSidesAreNull()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Empty"));

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Ssid)), PermissionAction.Read);

        // Assert
        Assert.Equal(default(PermissionDecision), decision);
    }

    [Fact]
    public void WhenPolicyIsDenyAll_ThenExplainDeniesWithoutRules()
    {
        // Arrange
        var home = Create();

        // Act
        var decision = AuthorizationPolicy.DenyAll.Explain(Member(home.Wifi!, nameof(Wifi.Ssid)), new PermissionAction("anything"));

        // Assert
        Assert.Equal(default(PermissionDecision), decision);
    }
}
