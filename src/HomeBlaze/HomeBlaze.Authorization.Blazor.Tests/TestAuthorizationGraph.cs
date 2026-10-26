using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A graph with an <see cref="Authorization"/> subject holding the given custom roles, and the service over it.
/// </summary>
internal sealed class TestAuthorizationGraph : IDisposable
{
    private TestAuthorizationGraph(TestRoot root, AuthorizationService service)
    {
        Root = root;
        Service = service;
    }

    public TestRoot Root { get; }

    public AuthorizationService Service { get; }

    public Authorization Authorization => (Authorization)Root.Children["Authorization"];

    /// <summary>Gets the custom role saved under <paramref name="name"/>.</summary>
    public Role GetRole(string name) => Authorization.Roles[name];

    /// <param name="roleNames">The names of the custom roles; names differing only in case make both unusable.</param>
    public static TestAuthorizationGraph Create(params string[] roleNames)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var service = new AuthorizationService(context, SlashPathProvider.Instance);
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = new Authorization
                {
                    // Case-sensitive, so the tests can configure names that differ only in case.
                    Roles = roleNames.ToDictionary(name => name, _ => new Role(), StringComparer.Ordinal)
                }
            }
        };

        return new TestAuthorizationGraph(root, service);
    }

    public void Dispose() => Service.Dispose();

    private sealed class SlashPathProvider : PathProviderBase
    {
        public static readonly SlashPathProvider Instance = new();

        public override char PathSeparator => '/';
    }
}
