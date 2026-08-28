using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Every compiled write chain runs the lifecycle last, so an ordering attribute that asks for a
/// write interceptor downstream of a lifecycle asks for a position where blocking can deadlock
/// against an opposing gate holder. The registration path rejects it in both registration orders,
/// at AddService time rather than from the lazily compiled chain of an unrelated first write.
/// </summary>
public class WriteChainOrderingValidationTests
{
    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class AfterLifecycleWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    [Fact]
    public void WhenAWriteInterceptorOrderedAfterTheLifecycleRegisters_ThenAddServiceThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => context.AddService<IWriteInterceptor>(new AfterLifecycleWriteInterceptor()));
        Assert.Contains(nameof(AfterLifecycleWriteInterceptor), exception.Message);
        Assert.Contains(nameof(LifecycleInterceptor), exception.Message);
    }

    [Fact]
    public void WhenTheLifecycleRegistersAfterAnInterceptorOrderedAfterIt_ThenTheLifecycleRegistrationThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(new AfterLifecycleWriteInterceptor());

        // Act & Assert: the reverse registration order is rejected at the lifecycle's own
        // registration, naming the offending interceptor.
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithLifecycle());
        Assert.Contains(nameof(AfterLifecycleWriteInterceptor), exception.Message);
    }

    [Fact]
    public void WhenAWriteInterceptorOrderedBeforeTheLifecycleRegisters_ThenTheRegistrationSucceeds()
    {
        // Arrange: [RunsBefore] naming a lifecycle requests exactly the position the chain
        // partition guarantees, so both registration orders stay legal (the standalone
        // change-subscription registration followed by WithLifecycle relies on this).
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();

        // Act
        context.WithLifecycle();

        // Assert
        Assert.NotNull(context.TryGetService<ILifecycleInterceptor>());
    }
}
