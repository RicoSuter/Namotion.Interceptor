using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class ExecuteWithoutReadTests : IDisposable
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("kitchen", "garden");

    public void Dispose() => _graph.Dispose();

    [Theory]
    [InlineData(BuiltInRoles.ReaderName)]
    [InlineData(BuiltInRoles.OperatorName)]
    [InlineData(BuiltInRoles.EverythingName)]
    public void WhenOnlyBuiltInRolesAllowExecute_ThenTheGraphIsNotWalked(string builtInRole)
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "kitchen",
            Includes = [builtInRole],
            Allow = [new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] }]
        };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.False(mayApply);
    }

    [Fact]
    public void WhenTheRoleAllowsExecute_ThenTheGraphIsWalked()
    {
        // Arrange
        var role = new AuthorizationRole { Name = "kitchen", Allow = [new PermissionRule { Actions = [AuthorizationActions.Execute] }] };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.True(mayApply);
    }

    [Fact]
    public void WhenAnIncludedCustomRoleAllowsExecute_ThenTheGraphIsWalked()
    {
        // Arrange
        _graph.SetRole("garden", allow: [new PermissionRule { Actions = [AuthorizationActions.Execute] }]);
        var role = new AuthorizationRole { Name = "kitchen", Includes = ["garden"] };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.True(mayApply);
    }

    [Fact]
    public void WhenTheRoleDeniesReadOnWhatABuiltInRoleAllowsToExecute_ThenTheGraphIsWalked()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "kitchen",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Paths = ["/Devices/Child/**"], Actions = [PermissionAction.Read] }]
        };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.True(mayApply);
    }
}
