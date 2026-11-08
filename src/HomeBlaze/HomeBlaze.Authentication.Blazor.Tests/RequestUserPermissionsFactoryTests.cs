using System.Security.Claims;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// The permissions of a request outside a circuit are decided from the user the request authenticated, without the
/// authentication state of a circuit.
/// </summary>
public class RequestUserPermissionsFactoryTests
{
    [Fact]
    public async Task WhenThePersonHoldsReader_ThenTheirRequestReadsButDoesNotWrite()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var factory = graph.Services.GetRequiredService<IRequestUserPermissionsFactory>();

        // Act
        var permissions = factory.Create(graph.CreateTestUserPrincipal());

        // Assert
        Assert.True(permissions.CanRead(temperature));
        Assert.False(permissions.CanWrite(temperature));
        Assert.True(permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public async Task WhenThePersonMustChangeTheirPassword_ThenTheirRequestMayReadNothing()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var factory = graph.Services.GetRequiredService<IRequestUserPermissionsFactory>();

        // Act
        var permissions = factory.Create(graph.CreateTestUserPrincipal(mustChangePassword: true));

        // Assert
        Assert.False(permissions.CanRead(temperature));
        Assert.False(permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public async Task WhenTheVisitorIsNotSignedIn_ThenTheRolesOfAnonymousDecide()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var factory = graph.Services.GetRequiredService<IRequestUserPermissionsFactory>();

        // Act
        var permissions = factory.Create(new ClaimsPrincipal(new ClaimsIdentity()));

        // Assert
        Assert.True(permissions.CanRead(temperature));
        Assert.False(permissions.HoldsAllRoles([BuiltInRoles.OperatorName]));
    }

    [Fact]
    public async Task WhenTwoPeopleRequest_ThenEachRequestHasThePermissionsOfItsOwnUser()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName], anonymousRoles: []);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var factory = graph.Services.GetRequiredService<IRequestUserPermissionsFactory>();

        // Act
        var person = factory.Create(graph.CreateTestUserPrincipal());
        var visitor = factory.Create(new ClaimsPrincipal(new ClaimsIdentity()));

        // Assert
        Assert.NotSame(person, visitor);
        Assert.True(person.CanRead(temperature));
        Assert.False(visitor.CanRead(temperature));
    }
}
