using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A graph with an <see cref="Authorization"/> subject holding the given custom roles and a <see cref="TestDevice"/> at
/// <c>/Devices</c> with another at <c>/Devices/Child</c>, the service over it and a resolver of its paths.
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

    /// <summary>Gets the custom role saved under <paramref name="name"/>.</summary>
    public Role GetRole(string name) => Authorization.Roles[name];

    /// <param name="roleNames">The names of the custom roles; names differing only in case make both unusable.</param>
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
                ["Authorization"] = new Authorization
                {
                    // Case-sensitive, so the tests can configure names that differ only in case.
                    Roles = roleNames.ToDictionary(name => name, _ => new Role(), StringComparer.Ordinal)
                },
                ["Devices"] = new TestDevice { Child = new TestDevice() }
            }
        };

        return new TestAuthorizationGraph(root, service, pathResolver);
    }

    public void Dispose() => Service.Dispose();

    private sealed class SlashPathProvider : PathProviderBase
    {
        public static readonly SlashPathProvider Instance = new();

        public override char PathSeparator => '/';
    }
}
