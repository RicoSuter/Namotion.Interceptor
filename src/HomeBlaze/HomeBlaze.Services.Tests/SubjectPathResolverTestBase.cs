using HomeBlaze.Services.Tests.Models;
using Moq;
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
        var mockServiceProvider = new Mock<IServiceProvider>();
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);

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
