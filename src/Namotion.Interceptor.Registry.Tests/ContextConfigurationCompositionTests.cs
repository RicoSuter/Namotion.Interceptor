using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class ContextConfigurationCompositionTests
{
    [Fact]
    public void WhenRegistryContextsShareRegistryBeforeComposition_ThenLifecycleResolutionThrows()
    {
        // Arrange
        var registry = new SubjectRegistry();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(registry);
        child.AddService(registry);
        parent.WithRegistry();
        child.WithRegistry();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }

    [Fact]
    public void WhenRegistryContextsShareLifecycleBeforeComposition_ThenRegistryResolutionThrows()
    {
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(lifecycle);
        child.AddService(lifecycle);
        parent.WithRegistry();
        child.WithRegistry();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ISubjectRegistry).FullName!, exception.Message);
    }
}
