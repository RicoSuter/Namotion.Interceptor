using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenHomeBlazeServicesAreResolved_ThenPathResolverIsRegisteredAsLifecycleHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHomeBlazeServices();
        using var serviceProvider = services.BuildServiceProvider();

        // Act
        var resolver = serviceProvider.GetRequiredService<SubjectPathResolver>();
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();
        var lifecycleHandlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Contains(resolver, lifecycleHandlers);
        Assert.Single(lifecycleHandlers.OfType<SubjectPathResolver>());
    }
}
