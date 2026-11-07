using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class ExecuteWithoutReadTests : IDisposable
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen", "Garden");

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
            Name = "Kitchen",
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
        var role = new AuthorizationRole { Name = "Kitchen", Allow = [new PermissionRule { Actions = [AuthorizationActions.Execute] }] };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.True(mayApply);
    }

    [Fact]
    public void WhenAnIncludedCustomRoleAllowsExecute_ThenTheGraphIsWalked()
    {
        // Arrange
        _graph.GetRole("Garden").Allow = [new PermissionRule { Actions = [AuthorizationActions.Execute] }];
        var role = new AuthorizationRole { Name = "Kitchen", Includes = ["Garden"] };

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
            Name = "Kitchen",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Paths = ["/Devices/Child/**"], Actions = [PermissionAction.Read] }]
        };

        // Act
        var mayApply = ExecuteWithoutRead.MayApply(_graph.Service.PreviewPolicy(role).Policy);

        // Assert
        Assert.True(mayApply);
    }
}
