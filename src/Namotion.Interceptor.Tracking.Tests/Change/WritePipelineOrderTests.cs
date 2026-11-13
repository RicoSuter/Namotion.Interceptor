using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the resolved write chain: the change interceptor sits outer of the lifecycle interceptor, so
/// dispatch happens after attach and detach reconciliation. Derived recalculation runs after the
/// whole chain through the core's write observer.
/// </summary>
public class WritePipelineOrderTests
{
    [Fact]
    public void WhenFullPropertyTrackingIsRegistered_ThenWriteChainHasTheExpectedOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act: index 0 is entered first, so it is the outermost interceptor.
        var chain = context.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }

    [Fact]
    public void WhenTransactionsAreRegistered_ThenTransactionInterceptorIsOuterOfTheChangeInterceptor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();

        // Act
        var chain = context.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(SubjectTransactionInterceptor),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }

    [Fact]
    public void WhenContextsAreAggregated_ThenEveryChangeInterceptorPrecedesEveryLifecycleInterceptor()
    {
        // Arrange: each context contributes its own instances, so the ordering edges must bind to
        // every instance of the referenced type rather than just one.
        var parentContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childContext.AddFallbackContext(parentContext);

        // Act
        var chain = childContext.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert: both change interceptors precede both lifecycle interceptors.
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(PropertyValueEqualityCheckHandler),
                typeof(PropertyChangeInterceptor),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }
}
