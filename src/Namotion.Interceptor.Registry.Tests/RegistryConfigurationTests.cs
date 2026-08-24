using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// <c>WithRegistry()</c> establishes the lifecycle before it publishes the registry, so a lifecycle
/// conflict surfaces with no registry left behind, and both services register idempotently.
/// </summary>
public class RegistryConfigurationTests
{
    [Fact]
    public void WhenWithRegistryIsCalled_ThenTheLifecycleIsInstalledWithIt()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Assert
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
        Assert.Single(context.GetServices<ISubjectRegistry>());
    }

    [Fact]
    public void WhenWithRegistryAndWithLifecycleAreRepeated_ThenTheServicesStaySingle()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle()
            .WithRegistry()
            .WithLifecycle();

        // Assert
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
        Assert.Single(context.GetServices<ISubjectRegistry>());
        Assert.Equal(2, context.GetServices<ILifecycleHandler>().Length);
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenWithRegistryThrowsBeforePublishingTheRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert: the conflict must fire while installing the lifecycle, before the registry
        // is published, so the failed configuration does not leave a half-registered registry that
        // would observe a lifecycle it was not written for.
        Assert.Throws<InvalidOperationException>(() => context.WithRegistry());
        Assert.Null(context.TryGetService<ISubjectRegistry>());
        Assert.Empty(context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenASecondRegistryIsRegisteredDirectly_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act & Assert: SubjectRegistry is the singleton authority for ISubjectRegistry on its
        // context, like the lifecycle is for ILifecycleInterceptor.
        var exception = Assert.Throws<InvalidOperationException>(() => context.AddService(new SubjectRegistry()));
        Assert.Contains("singleton contract", exception.Message);
    }

    private sealed class CustomLifecycle : ILifecycleInterceptor
    {
        public void EnterStructuralWriteGate()
        {
        }

        public void ExitStructuralWriteGate()
        {
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
            => throw new NotSupportedException();

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
            => throw new NotSupportedException();

        public bool TryAddProperties(SubjectPropertyRegistrationContext registration)
            => throw new NotSupportedException();

        public void OnContextComposed(IInterceptorSubject subject)
        {
        }

        public void OnContextDecomposed(IInterceptorSubject subject)
        {
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => next(ref context);
    }
}
