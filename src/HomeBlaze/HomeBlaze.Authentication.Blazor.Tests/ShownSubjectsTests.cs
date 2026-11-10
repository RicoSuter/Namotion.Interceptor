using HomeBlaze.Authorization;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// Whether a subject is shown and whether a member may be read follow changes of the roles and of the graph, although the
/// answers are remembered.
/// </summary>
public class ShownSubjectsTests
{
    // Reads everything but what is below /Hidden.
    private static readonly AuthorizationRole NoHiddenRole = new()
    {
        Name = "no-hidden",
        Includes = [BuiltInRoles.ReaderName],
        Deny = [new PermissionRule { Paths = ["/Hidden/**"], Actions = [PermissionAction.Read] }]
    };

    // Reads everything but the device.
    private static readonly AuthorizationRole NoDeviceRole = new()
    {
        Name = "no-device",
        Includes = [BuiltInRoles.ReaderName],
        Deny = [new PermissionRule { Paths = ["/Device/**"], Actions = [PermissionAction.Read] }]
    };

    [Fact]
    public async Task WhenTheRolesOfThePersonAreReduced_ThenASubjectShownBeforeIsHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName], customRoles: [NoDeviceRole]);
        var device = graph.Device.TryGetRegisteredSubject()!;
        var shownBefore = graph.Permissions.CanShow(device);
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += changed.Set;

        // Act
        await graph.SetRolesAsync([NoDeviceRole.Name]);

        // Assert
        Assert.True(shownBefore);
        Assert.True(changed.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(graph.Permissions.CanShow(device));
    }

    [Fact]
    public async Task WhenAShownSubjectMovesBelowADeniedPath_ThenItIsHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [NoHiddenRole.Name], customRoles: [NoHiddenRole]);
        var hidden = new PermissionsTestRoot();
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Hidden"] = hidden };
        var device = graph.Device;
        var registeredDevice = device.TryGetRegisteredSubject()!;
        var shownBefore = graph.Permissions.CanShow(registeredDevice);

        // Act
        hidden.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = device };
        var children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        children.Remove("Device");
        graph.Root.Children = children;

        // Assert
        Assert.True(shownBefore);
        Assert.False(graph.Permissions.CanShow(registeredDevice));
    }

    [Fact]
    public async Task WhenAShownSubjectIsKeptUnderTheKeyOfADeniedPath_ThenItIsHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [NoHiddenRole.Name], customRoles: [NoHiddenRole]);
        var device = graph.Device;
        var registeredDevice = device.TryGetRegisteredSubject()!;
        var shownBefore = graph.Permissions.CanShow(registeredDevice);

        // Act
        var children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        children.Remove("Device");
        children["Hidden"] = device;
        graph.Root.Children = children;

        // Assert
        Assert.True(shownBefore);
        Assert.Same(registeredDevice, device.TryGetRegisteredSubject());
        Assert.False(graph.Permissions.CanShow(registeredDevice));
    }

    [Fact]
    public async Task WhenAHiddenSubjectMovesOutOfTheDeniedPath_ThenItIsShown()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [NoHiddenRole.Name], customRoles: [NoHiddenRole]);
        var device = graph.Device;
        var hidden = new PermissionsTestRoot { Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = device } };
        var children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Hidden"] = hidden };
        children.Remove("Device");
        graph.Root.Children = children;
        var registeredDevice = device.TryGetRegisteredSubject()!;
        var shownBefore = graph.Permissions.CanShow(registeredDevice);

        // Act
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Device"] = device };
        hidden.Children = new Dictionary<string, IInterceptorSubject>();

        // Assert
        Assert.False(shownBefore);
        Assert.True(graph.Permissions.CanShow(registeredDevice));
    }

    [Fact]
    public async Task WhenAReadableMemberMovesBelowADeniedPath_ThenItIsNoLongerReadable()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [NoHiddenRole.Name], customRoles: [NoHiddenRole]);
        var device = graph.Device;
        var temperature = device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var readableBefore = graph.Permissions.CanRead(temperature);

        // Act
        var children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children);
        children.Remove("Device");
        children["Hidden"] = device;
        graph.Root.Children = children;

        // Assert
        Assert.True(readableBefore);
        Assert.False(graph.Permissions.CanRead(temperature));
    }
}
