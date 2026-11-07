using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class CaseRuleTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    [Fact]
    public void WhenATagDiffersInCase_ThenTheRuleStillMatches()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["STATE"])]));

        // Act
        var canReadSsid = policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid)));

        // Assert
        Assert.True(canReadSsid);
    }

    [Fact]
    public void WhenAnActionDiffersInCase_ThenTheRuleStillMatches()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Operator", allow: [Rule([new PermissionAction("EXECUTE")])]));

        // Act
        var isAllowed = policy.IsAllowed(Member(home.Wifi!, nameof(Wifi.Ssid)), Execute);

        // Assert
        Assert.True(isAllowed);
    }

    [Fact]
    public void WhenATypeDiffersInCase_ThenTheRuleDoesNotMatch()
    {
        // Arrange
        var home = Create();
        var ssid = Member(home.Wifi!, nameof(Wifi.Ssid));
        var differentCase = Compile(Role("Reader", allow: [Rule(Read, types: [nameof(Wifi).ToLowerInvariant()])]));
        var sameCase = Compile(Role("Reader", allow: [Rule(Read, types: [typeof(Wifi).FullName!])]));

        // Act
        var differentCaseCanRead = differentCase.CanRead(ssid);
        var sameCaseCanRead = sameCase.CanRead(ssid);

        // Assert
        Assert.False(differentCaseCanRead);
        Assert.True(sameCaseCanRead);
    }

    [Fact]
    public void WhenAMemberDiffersInCase_ThenTheRuleDoesNotMatch()
    {
        // Arrange
        var home = Create();
        var ssid = Member(home.Wifi!, nameof(Wifi.Ssid));
        var differentCase = Compile(Role("Reader", allow: [Rule(Read, members: ["ssid"])]));
        var sameCase = Compile(Role("Reader", allow: [Rule(Read, members: ["Ssid"])]));

        // Act
        var differentCaseCanRead = differentCase.CanRead(ssid);
        var sameCaseCanRead = sameCase.CanRead(ssid);

        // Assert
        Assert.False(differentCaseCanRead);
        Assert.True(sameCaseCanRead);
    }

    [Fact]
    public void WhenAPathDiffersInCase_ThenTheRuleDoesNotMatch()
    {
        // Arrange
        var home = Create();
        var ssid = Member(home.Wifi!, nameof(Wifi.Ssid));
        var differentCase = Compile(Role("Reader", allow: [Rule(Read, paths: ["/wifi/ssid"])]));
        var sameCase = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Wifi/Ssid"])]));

        // Act
        var differentCaseCanRead = differentCase.CanRead(ssid);
        var sameCaseCanRead = sameCase.CanRead(ssid);

        // Assert
        Assert.False(differentCaseCanRead);
        Assert.True(sameCaseCanRead);
    }

    [Fact]
    public void WhenAMemberNamesAnAttributeOfAnAttribute_ThenOnlyThatAttributeMatches()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var unit = Attribute(sensor, nameof(Sensor.Temperature), "Unit");
        var source = unit.TryGetAttribute("Source")!;
        var policy = Compile(Role("Reader", allow: [Rule(Read, members: ["Temperature@Unit@Source"])]));

        // Act
        var canReadSource = policy.CanRead(source);
        var canReadUnit = policy.CanRead(unit);
        var canReadTemperature = policy.CanRead(Member(sensor, nameof(Sensor.Temperature)));

        // Assert
        Assert.True(canReadSource);
        Assert.False(canReadUnit);
        Assert.False(canReadTemperature);
    }

    [Fact]
    public void WhenARoleDenyAndASystemDenyBothMatch_ThenTheRoleIsReportedFirst()
    {
        // Arrange
        var home = Create();
        var roleDeny = Rule(Read, members: ["Ssid"]);
        var role = Role("Reader", allow: [Rule(Read)], deny: [roleDeny]);
        var policy = Compile([role], ["Reader"], systemDeny: [Rule(Read, members: ["Ssid"])]);

        // Act
        var decision = policy.Explain(Member(home.Wifi!, nameof(Wifi.Ssid)), PermissionAction.Read);

        // Assert
        Assert.False(decision.IsAllowed);
        Assert.Same(role, decision.DeniedBy);
        Assert.Same(roleDeny, decision.DeniedByRule);
    }
}
