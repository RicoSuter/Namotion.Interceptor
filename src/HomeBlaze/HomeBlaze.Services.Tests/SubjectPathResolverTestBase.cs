using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Base class for SubjectPathResolver tests providing common setup.
/// </summary>
public abstract class SubjectPathResolverTestBase
{
    protected readonly IInterceptorSubjectContext Context;
    protected readonly SubjectPathResolver Resolver;
    protected readonly RootManager RootManager;

    protected SubjectPathResolverTestBase()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        Context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        RootManager = new RootManager(typeRegistry, serializer, Context, null);

        Context.WithService(() => RootManager);
        Context.WithPathResolver();

        Resolver = Context.GetService<SubjectPathResolver>();
    }
}
