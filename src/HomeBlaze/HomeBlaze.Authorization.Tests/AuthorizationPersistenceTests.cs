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
    public void WhenAnAuthorizationWithRolesIsSerializedAndLoaded_ThenTheRulesRoundTrip()
    {
        // Arrange
        var context = CreateContext();
        var role = CreateRole(context);
        var authorization = new Authorization(context)
        {
            Roles = new Dictionary<string, Role> { ["MyRole"] = role }
        };

        // Act
        var json = _serializer.Serialize(authorization);
        var loaded = Assert.IsType<Authorization>(_serializer.Deserialize(json));

        // Assert
        var loadedRole = Assert.Single(loaded.Roles);
        Assert.Equal("MyRole", loadedRole.Key);
        Assert.Equal(role.Includes, loadedRole.Value.Includes);
        Assert.Equal(role.Allow, loadedRole.Value.Allow);
        Assert.Equal(role.Deny, loadedRole.Value.Deny);
    }

    [Fact]
    public void WhenAnAuthorizationIsSerialized_ThenRolesAreKeyedByNameWithRulesAsObjectsAndActionsAsNames()
    {
        // Arrange
        var context = CreateContext();
        var authorization = new Authorization(context)
        {
            Roles = new Dictionary<string, Role> { ["MyRole"] = CreateRole(context) }
        };

        // Act
        var json = _serializer.Serialize(authorization);

        // Assert
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(typeof(Authorization).FullName, root.GetProperty("$type").GetString());
        Assert.False(root.TryGetProperty("status", out _));

        var role = root.GetProperty("roles").GetProperty("MyRole");
        Assert.Equal(["Reader"], role.GetProperty("includes").EnumerateArray().Select(element => element.GetString()));
        Assert.False(role.TryGetProperty("usedByAgents", out _));

        var allow = Assert.Single(role.GetProperty("allow").EnumerateArray().ToArray());
        Assert.Equal(["/Home"], allow.GetProperty("paths").EnumerateArray().Select(element => element.GetString()));
        Assert.Equal(["read", "execute"], allow.GetProperty("actions").EnumerateArray().Select(element => element.GetString()));

        var deny = Assert.Single(role.GetProperty("deny").EnumerateArray().ToArray());
        Assert.Equal("""{"paths":["/Home/Bedroom"],"actions":["write"]}""", JsonSerializer.Serialize(deny));
    }

    [Fact]
    public void WhenRolesAreInlinePaths_ThenARoleResolvesAtTheAuthorizationPathAndItsName()
    {
        // Arrange
        var context = CreateContext();
        var role = CreateRole(context);
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = new Authorization(context)
                {
                    Roles = new Dictionary<string, Role> { ["MyRole"] = role }
                }
            }
        };
        var resolver = new SubjectPathResolver(() => root);

        // Act
        var resolved = resolver.ResolveSubject("/Authorization/MyRole", PathStyle.Canonical);
        var resolvedByRegistry = new DefaultPathProvider('/').TryGetSubjectFromPath(root.TryGetRegisteredSubject()!, "/Authorization/MyRole");
        var path = resolver.GetPath(role, PathStyle.Canonical);

        // Assert
        Assert.Same(role, resolved);
        Assert.Same(role, resolvedByRegistry?.Subject);
        Assert.Equal("/Authorization/MyRole", path);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

    private static Role CreateRole(IInterceptorSubjectContext context) =>
        new(context)
        {
            Includes = ["Reader"],
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
