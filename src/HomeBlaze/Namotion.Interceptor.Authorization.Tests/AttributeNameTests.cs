using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class AttributeNameTests
{
    [Fact]
    public void WhenAttributesNameEachOtherInACycle_ThenAMembersDenyAppliesAndAMembersAllowDoesNot()
    {
        // Arrange
        var holder = new CyclicAttributeHolder(CreateContext()) { Value = "x" };
        var attribute = Member(holder, nameof(CyclicAttributeHolder.A));
        var policyWithDeny = Compile(Role("Reader",
            allow: [Rule([PermissionAction.Read])],
            deny: [Rule([PermissionAction.Read], members: ["Value"])]));
        var policyWithAllowOnly = Compile(Role("Reader", allow: [Rule([PermissionAction.Read], members: ["Value"])]));

        // Act
        var deniedByMembers = policyWithDeny.CanRead(attribute);
        var allowedByMembers = policyWithAllowOnly.CanRead(attribute);

        // Assert
        Assert.False(deniedByMembers);
        Assert.False(allowedByMembers);
    }

    [Fact]
    public void WhenAnAttributesPropertyIsMissing_ThenEvaluationReturnsInsteadOfThrowing()
    {
        // Arrange
        var holder = new OrphanAttributeHolder(CreateContext()) { Value = "x" };
        var attribute = Member(holder, nameof(OrphanAttributeHolder.Missing_Unit));
        var policy = Compile(Role("Reader",
            allow: [Rule([PermissionAction.Read])],
            deny: [Rule([PermissionAction.Read], members: ["Value"])]));

        // Act
        var canRead = policy.CanRead(attribute);

        // Assert
        Assert.False(canRead);
    }

    [Fact]
    public void WhenAnAttributeOfAnAttributeIsNamed_ThenItsNameIsNestedFromTheProperty()
    {
        // Arrange
        var sensor = new Sensor(CreateContext());
        var policy = Compile(Role("Reader", allow: [Rule([PermissionAction.Read], members: ["Temperature@Unit@Source"])]));
        var sourceAttribute = Attribute(sensor, "Temperature", "Unit").TryGetAttribute("Source")!;
        var unitAttribute = Attribute(sensor, "Temperature", "Unit");

        // Act
        var canReadSource = policy.CanRead(sourceAttribute);
        var canReadUnit = policy.CanRead(unitAttribute);

        // Assert
        Assert.True(canReadSource);
        Assert.False(canReadUnit);
    }
}
