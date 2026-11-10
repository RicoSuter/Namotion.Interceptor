using System.Collections.Immutable;
using HomeBlaze.Authorization;
using HomeBlaze.Components;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A value widget shows a person a value as their permissions show it, such as an order without hidden children.</summary>
public class ValueWidgetPermissionsTests
{
    private const string ChildOrderPath = "/Folder/" + nameof(PermissionsTestFolder.ChildOrder);

    [Fact]
    public async Task WhenAValueWidgetIsBoundToTheChildOrderOfAFolder_ThenItLeavesOutTheChildrenThePersonIsNotShown()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "nosecret",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Folder/Secret/**"], Actions = [PermissionAction.Read] }]
        };
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [role.Name], customRoles: [role]);
        var folder = new PermissionsTestFolder
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Docs"] = new PermissionsTestDevice(), ["Secret"] = new PermissionsTestDevice() },
            ChildOrder = ["Secret", "Docs"]
        };
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Folder"] = folder };
        var resolver = new SubjectPathResolver(() => graph.Root);
        var childOrder = folder.TryGetRegisteredSubject()!.TryGetProperty(nameof(PermissionsTestFolder.ChildOrder))!;

        // Act
        var reading = ValueReader.Read(resolver, graph.Device, ChildOrderPath, null, null, graph.Permissions);
        var resolvedReading = ValueReader.ReadResolved(resolver, childOrder, null, null, graph.Permissions);
        var propertyReading = ValueReader.ReadProperty(resolver, folder, nameof(PermissionsTestFolder.ChildOrder), null, null, graph.Permissions);
        var value = ValueReader.ReadValue(resolver, graph.Device, ChildOrderPath, graph.Permissions);

        // Assert
        Assert.Equal<string>(["Docs"], (ImmutableArray<string>)reading.Value!);
        Assert.DoesNotContain("Secret", reading.Text, StringComparison.Ordinal);
        Assert.Equal<string>(["Docs"], (ImmutableArray<string>)resolvedReading.Value!);
        Assert.Equal<string>(["Docs"], (ImmutableArray<string>)propertyReading.Value!);
        Assert.Equal<string>(["Docs"], (ImmutableArray<string>)value!);
        Assert.Equal<string>(["Secret", "Docs"], folder.ChildOrder);
    }
}
