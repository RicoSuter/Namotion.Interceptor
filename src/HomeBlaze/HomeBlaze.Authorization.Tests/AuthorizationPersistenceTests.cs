using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class AuthorizationPersistenceTests
{
    private static readonly PermissionAction Execute = new("execute");

    private readonly ConfigurableSubjectSerializer _serializer;

    public AuthorizationPersistenceTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(Authorization).Assembly);
        _serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void WhenAnAuthorizationWithRolesIsSerializedAndLoaded_ThenNeitherTheJsonNorTheLoadedSubjectHoldsRoles()
    {
        // Arrange
        var context = CreateContext();
        var authorization = new Authorization(context);
        authorization.ReplaceRoles([CreateRole()]);

        // Act
        var json = _serializer.Serialize(authorization);
        var loaded = Assert.IsType<Authorization>(_serializer.Deserialize(json));

        // Assert
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(typeof(Authorization).FullName, root.GetProperty("$type").GetString());
        Assert.False(root.TryGetProperty("roles", out _));
        Assert.False(root.TryGetProperty("status", out _));
        Assert.Empty(loaded.Roles);
    }

    [Fact]
    public void WhenRolesAreInlinePaths_ThenARoleResolvesAtTheAuthorizationPathAndItsName()
    {
        // Arrange
        var context = CreateContext();
        var authorization = new Authorization(context);
        authorization.ReplaceRoles([CreateRole()]);
        var role = authorization.Roles["myrole"];
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization }
        };
        var resolver = new SubjectPathResolver(() => root);

        // Act
        var resolved = resolver.ResolveSubject("/Authorization/myrole", PathStyle.Canonical);
        var resolvedByRegistry = new DefaultPathProvider('/').TryGetSubjectFromPath(root.TryGetRegisteredSubject()!, "/Authorization/myrole");
        var path = resolver.GetPath(role, PathStyle.Canonical);

        // Assert
        Assert.Same(role, resolved);
        Assert.Same(role, resolvedByRegistry?.Subject);
        Assert.Equal("/Authorization/myrole", path);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

    private static AuthorizationRole CreateRole() =>
        new()
        {
            Name = "myrole",
            Includes = ["reader"],
            Allow =
            [
                new PermissionRule
                {
                    Paths = ["/Home"],
                    Types = ["ILight"],
                    Members = ["TurnOn", "Brightness"],
                    Tags = ["state"],
                    Actions = [PermissionAction.Read, Execute]
                }
            ],
            Deny =
            [
                new PermissionRule
                {
                    Paths = ["/Home/Bedroom"],
                    Actions = [PermissionAction.Write]
                }
            ]
        };
}
