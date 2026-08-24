using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// Proves the handler's <c>[RunsAfter(typeof(LifecycleInterceptor))]</c> binds against the merged
/// ordering seam. <c>WithHostedServices</c> registers the handler before it installs the lifecycle,
/// so unordered resolution would keep the handler ahead; only a bound constraint flips it behind.
/// </summary>
public class HostedServiceHandlerOrderTests
{
    [Fact]
    public void WhenHostedServicesAreConfigured_ThenTheHandlerResolvesBehindTheLifecycle()
    {
        // Arrange
        var services = new ServiceCollection();
        var context = InterceptorSubjectContext
            .Create()
            .WithHostedServices(services);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal([nameof(LifecycleInterceptor), "HostedServiceHandler"], handlers);
    }
}
