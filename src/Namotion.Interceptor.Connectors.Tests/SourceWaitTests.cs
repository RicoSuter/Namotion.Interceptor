using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceWaitTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheMonitorIsCreated_ThenRegistrationIsIncomplete()
    {
        // Arrange & Act
        var monitor = CreateContext().GetSourceMonitor();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenRegistrationIsCompleted_ThenTheFlagFlipsAndIsIdempotent()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();

        // Act
        monitor.CompleteSourceRegistration();
        monitor.CompleteSourceRegistration();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenACompletionHoldIsTaken_ThenRegistrationIsIncompleteUntilItIsReleased()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var hold = monitor.DeferWaitCompletion();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        hold.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenHoldsAreNested_ThenRegistrationCompletesOnlyAfterTheLastRelease()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var outer = monitor.DeferWaitCompletion();
        var inner = monitor.DeferWaitCompletion();
        inner.Dispose();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        outer.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenTheContextExtensionIsUsed_ThenEveryReachableMonitorIsSignalled()
    {
        // Arrange
        var context = CreateContext();

        // Act
        context.CompleteSourceRegistration();

        // Assert
        Assert.True(context.GetSourceMonitor().IsRegistrationComplete);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenCompleteSourceRegistrationSignalsAll()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        // Act
        child.CompleteSourceRegistration();

        // Assert
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenDeferWaitCompletionIsCalledWithTwoMonitors_ThenBothHoldsAreReleased()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        parentMonitor.CompleteSourceRegistration();
        childMonitor.CompleteSourceRegistration();

        // Act
        var hold = child.DeferWaitCompletion();

        // Assert - both should be incomplete while holds are out
        Assert.False(parentMonitor.IsRegistrationComplete);
        Assert.False(childMonitor.IsRegistrationComplete);

        // Act - release the holds
        hold.Dispose();

        // Assert - both should be complete
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }
}
