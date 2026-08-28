using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the resolved write chain. The order carries semantics that are otherwise only visible
/// through behavior: the change interceptor must sit outer of the lifecycle interceptor (so
/// dispatch happens after attach/detach reconciliation) and inner of the derived handler (so a
/// triggering write is announced before the recalculations it causes).
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
                typeof(DerivedPropertyChangeHandler),
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
                typeof(DerivedPropertyChangeHandler),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }

    [Fact]
    public void WhenAnUnattributedInterceptorRegistersAfterTracking_ThenItResolvesAfterTheLifecycleButExecutesBeforeIt()
    {
        // Arrange: the two orders deliberately differ for this registration. The resolver keeps
        // insertion order for unattributed services, so GetServices reports the interceptor after
        // the lifecycle; the chain partition compiles every lifecycle last, so execution runs it
        // upstream, outside the topology gate.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var observer = new ChildAttachmentObservingInterceptor();
        context.AddService<IWriteInterceptor>(observer);

        // Act
        var services = context.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();
        var parent = new Person(context) { FirstName = "P" };
        parent.Father = new Person();

        // Assert: the resolver order is unchanged by the partition.
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(DerivedPropertyChangeHandler),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor),
                typeof(ChildAttachmentObservingInterceptor)
            ],
            services);

        // Execution order, observed behaviorally: when the observer's next() returned, the
        // lifecycle downstream of it had already reconciled the write, so the assigned child is
        // attached. An interceptor executing downstream of the lifecycle would observe the child
        // before the reconcile, unattached.
        Assert.True(observer.ChildWasAttachedAfterNext);
    }

    private sealed class ChildAttachmentObservingInterceptor : IWriteInterceptor
    {
        public bool? ChildWasAttachedAfterNext;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (context.Property.Name == nameof(Person.Father) && context.NewValue is IInterceptorSubject child)
            {
                ChildWasAttachedAfterNext = child.TryGetContext() is not null;
            }
        }
    }
}
