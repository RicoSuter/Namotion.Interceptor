using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A graph with an <see cref="Authorization"/> subject holding the given custom roles and a <see cref="TestDevice"/> at
/// <c>/Devices</c> with another at <c>/Devices/Child</c>, the service over it, a resolver of its paths and a fake role
/// store over the roles.
/// </summary>
internal sealed class TestAuthorizationGraph : IDisposable
{
    private TestAuthorizationGraph(TestRoot root, AuthorizationService service, SubjectPathResolver pathResolver)
    {
        Root = root;
        Service = service;
        PathResolver = pathResolver;
    }

    public TestRoot Root { get; }

    public AuthorizationService Service { get; }

    public SubjectPathResolver PathResolver { get; }

    public TestDevice Devices => (TestDevice)Root.Children["Devices"];

    public Authorization Authorization => (Authorization)Root.Children["Authorization"];

    /// <summary>A role store that records the changes and applies the successful ones to <see cref="Authorization"/>.</summary>
    public FakeRoleStore RoleStore { get; private set; } = null!;

    /// <summary>Gets the custom role saved under <paramref name="name"/>.</summary>
    public Role GetRole(string name) => Authorization.Roles[name];

    /// <summary>
    /// Replaces the lists of the custom role named <paramref name="name"/> that are given, keeping the other roles, as
    /// the role store does after a save; every role gets a new subject.
    /// </summary>
    /// <returns>The new subject of the role.</returns>
    public Role SetRole(
        string name,
        IReadOnlyList<string>? includes = null,
        IReadOnlyList<PermissionRule>? allow = null,
        IReadOnlyList<PermissionRule>? deny = null)
    {
        var current = Authorization.Roles[name];
        RoleStore.Apply(name, new AuthorizationRole
        {
            Name = name,
            Includes = [.. includes ?? current.Includes],
            Allow = [.. allow ?? current.Allow],
            Deny = [.. deny ?? current.Deny]
        });
        return GetRole(name);
    }

    /// <param name="roleNames">The names of the custom roles; a name the role store would refuse makes the role unusable.</param>
    public static TestAuthorizationGraph Create(params string[] roleNames)
    {
        // The handlers register the markers that tags are read from, and methods as properties.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);

        TestRoot? root = null;
        var pathResolver = new SubjectPathResolver(() => root);
        context.AddService<ISubjectPathResolver>(pathResolver);

        var service = new AuthorizationService(context, SlashPathProvider.Instance);
        root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = new Authorization(),
                ["Devices"] = new TestDevice { Child = new TestDevice() }
            }
        };

        var graph = new TestAuthorizationGraph(root, service, pathResolver);
        graph.RoleStore = new FakeRoleStore(graph.Authorization);
        graph.Authorization.ReplaceRoles([.. roleNames.Select(name => new AuthorizationRole { Name = name })]);
        return graph;
    }

    public void Dispose() => Service.Dispose();

    private sealed class SlashPathProvider : PathProviderBase
    {
        public static readonly SlashPathProvider Instance = new();

        public override char PathSeparator => '/';
    }
}
