using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceDisposeIsolationTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenOneMonitorsUnregisterThrows_ThenTheOtherMonitorStillUnregisters()
    {
        // Arrange
        // What the first throw skips can never be unwound later, so it stays registered for good,
        // holding the source and the subtree under its root.
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var root = new Person(child);
        var source = new TestStateSource(root);

        // The order the source itself unwinds in. Arming the first is what makes a throw skip one.
        var monitors = ((IInterceptorSubject)root).Context.GetServices<SourceMonitor>();
        Assert.Equal(2, monitors.Length);

        // Started rather than registered by hand: only StartAsync records the monitors on the source,
        // and Dispose unwinds that record, so a hand-registered source would have nothing to unwind.
        await source.StartAsync(CancellationToken.None);
        foreach (var monitor in monitors)
        {
            monitor.CompleteSourceRegistration();
        }

        ArmThrowingUnregister(monitors[0], child);

        // Act
        Assert.ThrowsAny<Exception>(source.Dispose);

        // Assert
        foreach (var monitor in monitors)
        {
            Assert.DoesNotContain(source, monitor.Sources);
        }
    }

    [Fact]
    public async Task WhenUnregisterThrowsOnDispose_ThenThePumpIsStillCancelled()
    {
        // Arrange
        // The unregister loop runs before the base disposal that cancels the pump's token source, so
        // a throw used to leave a "disposed" source still pumping.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        await source.StartAsync(CancellationToken.None);
        monitor.CompleteSourceRegistration();

        ArmThrowingUnregister(monitor, context);

        // Act
        Assert.ThrowsAny<Exception>(source.Dispose);

        // Assert
        Assert.NotNull(source.ExecuteTask);
        await source.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Leaves <paramref name="monitor"/> with a pending wait whose scope walk throws, which is what
    /// makes its <c>Unregister</c> throw.
    /// </summary>
    private static void ArmThrowingUnregister(SourceMonitor monitor, IInterceptorSubjectContext context)
    {
        // The walk runs per registered source, so without a source that outlives the one under test
        // it would stop throwing the moment that one is unregistered, which is exactly when the
        // throw has to happen. Rooted on an unrelated subject, so it is in no other test's scope.
        monitor.Register(new TestStateSource(new Person(context)));

        // The hold keeps registration incomplete while the wait is created, so its own fast-path
        // check short-circuits before walking any scope. Releasing it triggers the first walk.
        var hold = monitor.DeferWaitCompletion();
        monitor.CompleteSourceRegistration();

        // On the monitor directly, not through the subject extension, which would create a wait on
        // every reachable monitor and throw out of the arming itself.
        var poisonWait = monitor.WaitForSynchronizationAsync(new PoisonAnchor(context), CancellationToken.None);
        Assert.False(poisonWait.IsCompleted);

        Assert.ThrowsAny<Exception>(hold.Dispose);
    }
}
