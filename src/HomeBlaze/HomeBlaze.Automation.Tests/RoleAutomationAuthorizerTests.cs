using HomeBlaze.Authorization;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Tests;

public class RoleAutomationAuthorizerTests
{
    private readonly TestGraph _graph = new();
    private readonly TestBlinds _blinds = new();

    public RoleAutomationAuthorizerTests() => _graph.Add("Blinds", _blinds);

    private RegisteredSubjectProperty Member(string name) => _blinds.TryGetRegisteredProperty(name)!;

    [Fact]
    public void WhenTheRoleIsReader_ThenNoCallAndNoWriteIsAllowed()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph);

        // Act
        var permissions = authorizer.GetPermissions([BuiltInRoles.ReaderName]);

        // Assert
        Assert.Null(permissions.Error);
        Assert.Equal("no role of the automation allows calling it", permissions.GetOperationDenial(Member("Close")));
        Assert.Equal("no role of the automation allows writing it", permissions.GetPropertyWriteDenial(Member("Position")));
    }

    [Fact]
    public void WhenTheRoleIsOperator_ThenOperationsAndStateCanBeCalledAndWritten()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph);

        // Act
        var permissions = authorizer.GetPermissions([BuiltInRoles.OperatorName]);

        // Assert
        Assert.Null(permissions.GetOperationDenial(Member("Close")));
        Assert.Null(permissions.GetPropertyWriteDenial(Member("Position")));
        Assert.True(AutomationTargets.CanCall(Member("Close"), permissions));
        Assert.False(AutomationTargets.CanCall(Member("Calibrate"), permissions));
    }

    [Fact]
    public void WhenACustomRoleDeniesAnOperation_ThenTheDenialNamesTheRoleAndOtherOperationsStayAllowed()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph, (TestAuthorization.StormBlinds, TestAuthorization.DenyOpen()));

        // Act
        var permissions = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Assert
        Assert.Equal("the role 'StormBlinds' denies calling it", permissions.GetOperationDenial(Member("Open")));
        Assert.Null(permissions.GetOperationDenial(Member("Close")));
        Assert.False(AutomationTargets.CanCall(Member("Open"), permissions));
        Assert.True(AutomationTargets.CanCall(Member("Close"), permissions));
    }

    [Fact]
    public void WhenASystemRuleDenies_ThenTheDenialNamesTheSystemRules()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph);

        // Act
        var permissions = authorizer.GetPermissions([BuiltInRoles.EverythingName]);

        // Assert
        Assert.Equal("the system rules deny writing it", permissions.GetPropertyWriteDenial(Member("Label")));
    }

    [Theory]
    [InlineData("", "the roles of the automation cannot be used: No roles are assigned.")]
    [InlineData("Ghost", "the roles of the automation cannot be used: Role 'Ghost' is not defined.")]
    public void WhenTheRolesCannotBeUsed_ThenThePermissionsHaveTheReasonAndAllowNothing(string role, string expected)
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph);
        string[] roles = role.Length == 0 ? [] : [role];

        // Act
        var permissions = authorizer.GetPermissions(roles);

        // Assert
        Assert.Equal(expected, permissions.Error);
        Assert.False(AutomationTargets.CanCall(Member("Close"), permissions));
        Assert.False(AutomationTargets.CanWrite(Member("Position"), permissions));
    }

    [Fact]
    public void WhenACustomRoleIsAddedLater_ThenTheNextPermissionsUseIt()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph);
        var before = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Act
        TestAuthorization.AddRoles(_graph, (TestAuthorization.StormBlinds, TestAuthorization.DenyOpen()));
        var after = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Assert
        Assert.NotNull(before.Error);
        Assert.Null(after.Error);
        Assert.Null(after.GetOperationDenial(Member("Close")));
    }

    [Fact]
    public void WhenACustomRoleDeniesAPathPattern_ThenCallsAndWritesUnderThePathAreDenied()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph, (TestAuthorization.StormBlinds, TestAuthorization.DenyBlindsPath()));

        // Act
        var permissions = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Assert
        Assert.Equal("the role 'StormBlinds' denies calling it", permissions.GetOperationDenial(Member("Close")));
        Assert.Equal("the role 'StormBlinds' denies writing it", permissions.GetPropertyWriteDenial(Member("Position")));
    }

    [Fact]
    public void WhenTheRoleGainsADenyOrIsRemoved_ThenTheNextPermissionsRefuseWhatWasAllowed()
    {
        // Arrange
        var authorizer = TestAuthorization.Create(_graph, (TestAuthorization.StormBlinds, new Role { Includes = [BuiltInRoles.OperatorName] }));
        var allowed = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Act
        TestAuthorization.AddRoles(_graph, (TestAuthorization.StormBlinds, TestAuthorization.DenyOpen()));
        var denied = authorizer.GetPermissions([TestAuthorization.StormBlinds]);
        TestAuthorization.AddRoles(_graph);
        var removed = authorizer.GetPermissions([TestAuthorization.StormBlinds]);

        // Assert
        Assert.Null(allowed.GetOperationDenial(Member("Open")));
        Assert.NotNull(denied.GetOperationDenial(Member("Open")));
        Assert.NotNull(removed.Error);
        Assert.False(AutomationTargets.CanCall(Member("Close"), removed));
    }
}
