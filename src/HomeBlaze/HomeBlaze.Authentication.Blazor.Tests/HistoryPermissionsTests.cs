using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>Reading the history of a member requires reading the member.</summary>
public class HistoryPermissionsTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WhenTheRolesAllowTheHistoryOfAMember_ThenThePersonReadsItOnlyWhenTheyMayReadTheMember(bool isReadDenied, bool expected)
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "history",
            Allow = [new PermissionRule { Paths = ["/Device/**"], Actions = [PermissionAction.Read, AuthorizationActions.History] }],
            Deny = isReadDenied ? [new PermissionRule { Members = [nameof(PermissionsTestDevice.Temperature)], Actions = [PermissionAction.Read] }] : []
        };
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [role.Name], customRoles: [role]);
        var temperature = graph.Device.TryGetRegisteredProperty(nameof(PermissionsTestDevice.Temperature))!;

        // Act
        var canReadHistory = graph.Permissions.CanReadHistory(temperature);

        // Assert
        Assert.Equal(expected, canReadHistory);
        Assert.Equal(expected, graph.Permissions.CanRead(temperature));
    }
}
