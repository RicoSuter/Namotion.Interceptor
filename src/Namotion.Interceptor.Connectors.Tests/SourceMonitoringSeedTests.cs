using System.Collections.Immutable;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceMonitoringSeedTests
{
    [Fact]
    public void WhenASubjectIsAttachedBeforeSourceMonitoringIsConfigured_ThenCurrentStateReadsThroughToTheSource()
    {
        // Arrange - build the tree BEFORE WithSourceMonitoring is ever called, the exact footgun
        // documented in docs/connectors-monitoring.md.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        var source = new TestStateSource(root);
        property.SetSource(source);

        // Act - configure monitoring only now, once the tree already exists.
        context.WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, source.State, DateTimeOffset.UtcNow) { Monitor = monitor };

        // Assert
        Assert.Equal(SourceState.Connecting, sourceEvent.CurrentState);
    }

    [Fact]
    public void WhenTheRootSubjectIsAttachedBeforeSourceMonitoringIsConfigured_ThenCurrentStateReadsThroughToTheSource()
    {
        // Arrange - the root subject attaches to the context the moment it is constructed, so it is
        // the case most likely to predate WithSourceMonitoring().
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var root = new Person(context);
        var property = new PropertyReference(root, nameof(Person.FirstName));
        var source = new TestStateSource(root);
        property.SetSource(source);

        // Act
        context.WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, source.State, DateTimeOffset.UtcNow) { Monitor = monitor };

        // Assert
        Assert.Equal(SourceState.Connecting, sourceEvent.CurrentState);
    }

    [Fact]
    public void WhenASeededSubjectDetachesAfterward_ThenItIsNoLongerAMember()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;

        // Act - seeding must pick up `child`, and an ordinary detach afterward must still remove it:
        // the seed must not permanently pin membership.
        context.WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        Assert.True(monitor.IsMember(child));

        root.Mother = null;

        // Assert
        Assert.False(monitor.IsMember(child));
    }

    [Fact]
    public void WhenLifecycleInterceptorIsUnreachable_ThenWithSourceMonitoringThrows()
    {
        // Arrange - a hypothetical IInterceptorSubjectContext implementation whose LifecycleInterceptor
        // resolution is broken, modelling the "guaranteed present but genuinely unreachable" state
        // WithSourceMonitoring must guard against rather than silently skip past. WithParents/
        // WithLifecycle always register a LifecycleInterceptor on a well-behaved context, so this is
        // the only way to construct the absence this guard exists for.
        var context = new LifecycleInterceptorHidingContext(
            InterceptorSubjectContext.Create().WithFullPropertyTracking());

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithSourceMonitoring());
        Assert.Contains("LifecycleInterceptor", exception.Message);
    }
}

/// <summary>
/// Delegates every operation to an inner context, except that LifecycleInterceptor resolution always
/// comes back empty, simulating a context implementation whose service resolution is broken for that
/// one type even though every other service (including the one WithParents actually registers on the
/// inner context) resolves normally.
/// </summary>
internal sealed class LifecycleInterceptorHidingContext(IInterceptorSubjectContext inner) : IInterceptorSubjectContext
{
    public void AddService<TService>(TService service) => inner.AddService(service);

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists) =>
        inner.TryAddService(factory, exists);

    public TInterface? TryGetService<TInterface>() =>
        typeof(TInterface) == typeof(LifecycleInterceptor) ? default : inner.TryGetService<TInterface>();

    public ImmutableArray<TInterface> GetServices<TInterface>() =>
        typeof(TInterface) == typeof(LifecycleInterceptor) ? ImmutableArray<TInterface>.Empty : inner.GetServices<TInterface>();

    public bool AddFallbackContext(IInterceptorSubjectContext context) => inner.AddFallbackContext(context);

    public bool RemoveFallbackContext(IInterceptorSubjectContext context) => inner.RemoveFallbackContext(context);
}
