using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceStateTests
{
    [Fact]
    public void WhenReadingTheEnum_ThenUnclaimedIsTheDefault()
    {
        // Arrange & Act
        var state = default(SourceState);

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }

    [Fact]
    public void WhenNoSourceClaimedTheProperty_ThenGetSourceStateReturnsUnclaimed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }

    [Fact]
    public void WhenSourceClaimedTheProperty_ThenGetSourceStateReturnsTheSourcesState()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Connecting, state);
    }

    [Fact]
    public void WhenTransitioningToTheSameState_ThenNoEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        var raised = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref raised);

        // Act
        source.ReportConnecting();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void WhenTransitioningToSynchronized_ThenLastSynchronizedAtIsSetBeforeTheEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        DateTimeOffset? observedInHandler = null;
        source.StateChanged += (_, _) => observedInHandler = source.LastSynchronizedAt;

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
        Assert.NotNull(source.LastSynchronizedAt);
        Assert.Equal(source.LastSynchronizedAt, observedInHandler);
    }

    [Fact]
    public void WhenStopped_ThenNoFurtherTransitionSucceeds()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        source.ReportStopped();
        var eventsAfterStop = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref eventsAfterStop);
        var timestampAtStop = source.LastSynchronizedAt;

        // Act
        source.ReportConnecting();
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(0, eventsAfterStop);
        Assert.Equal(timestampAtStop, source.LastSynchronizedAt);
    }

    [Fact]
    public void WhenAThrowingHandlerIsSubscribed_ThenTheTransitionStillCompletes()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.StateChanged += (_, _) => throw new InvalidOperationException("handler is buggy");

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }

    [Fact]
    public void WhenAThrowingHandlerIsSubscribedBeforeAnotherHandler_ThenTheOtherHandlerStillObservesTheTransition()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        var laterHandlerRaised = 0;
        source.StateChanged += (_, _) => throw new InvalidOperationException("handler is buggy");
        source.StateChanged += (_, _) => Interlocked.Increment(ref laterHandlerRaised);

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
        Assert.Equal(1, laterHandlerRaised);
    }

    [Fact]
    public void WhenSourceTransitionsAgainAfterEventCapture_ThenCurrentStateReflectsTheLatestStateWhileNewStateStaysFrozen()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        SourceEvent? capturedEvent = null;
        source.StateChanged += (_, sourceEvent) => capturedEvent ??= sourceEvent;
        source.ReportSynchronized();

        // Act
        source.ReportStopped();

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(SourceState.Synchronized, capturedEvent.Value.NewState);
        Assert.Equal(SourceState.Stopped, capturedEvent.Value.CurrentState);
    }

    [Fact]
    public async Task WhenBufferingStartsOutsideThePump_ThenTheSourceReportsConnecting()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        ((ISourceStateReporter)source).ReportSynchronized();

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenTheInitialLoadCompletesOutsideThePump_ThenTheSourceReportsSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }

    [Fact]
    public async Task WhenTheInitialLoadThrows_ThenTheSourceDoesNotReportSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new ThrowingLoadSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.LoadInitialStateAndResumeAsync(CancellationToken.None));
        Assert.Equal(SourceState.Connecting, source.State);
    }

    [Fact]
    public void WhenAConnectorDetectsLossBeforeBuffering_ThenReportConnectionLostTransitions()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        ((ISourceStateReporter)source).ReportSynchronized();

        // Act
        source.ReportConnectionLost();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
    }
}

/// <summary>
/// A source that exposes the transition seam directly, so state machine behaviour can be tested
/// without a pump, a network, or a hosted service lifecycle.
/// </summary>
internal class TestStateSource : SubjectSourceBase
{
    public TestStateSource(IInterceptorSubject rootSubject)
        : base(rootSubject.Context, NullLogger.Instance)
    {
        RootSubject = rootSubject;
    }

    public override IInterceptorSubject RootSubject { get; }

    public void ReportConnecting() => ((ISourceStateReporter)this).ReportConnecting();

    public void ReportSynchronized() => ((ISourceStateReporter)this).ReportSynchronized();

    public void ReportStopped() => TransitionTo(SourceState.Stopped);

    /// <summary>How many times the pump body has been entered. Used to prove the terminal guard works.</summary>
    public int ExecuteCount;

    protected override Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExecuteCount);
        return Task.FromResult<IAsyncDisposable?>(null);
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success);
}

internal sealed class ThrowingLoadSource : TestStateSource
{
    public ThrowingLoadSource(IInterceptorSubject rootSubject) : base(rootSubject) { }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("load failed");
}
