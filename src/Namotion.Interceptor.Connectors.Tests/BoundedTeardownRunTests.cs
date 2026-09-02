namespace Namotion.Interceptor.Connectors.Tests;

public class BoundedTeardownRunTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task WhenTheCoreCompletesOnItsOwn_ThenNothingIsAbandoned()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var outcome = await run.RunAsync(() => Task.CompletedTask, stopSignal.Task).WaitAsync(TestTimeout);

        // Assert
        Assert.False(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenTheCoreThrows_ThenTheExceptionPropagatesToTheCaller()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(() => throw fault, stopSignal.Task)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenTheCoreFaultsAfterReportingTheFault_ThenTheOriginalExceptionStillPropagates()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(() =>
            {
                run.MarkFinalizationStarted(fault);
                throw fault;
            }, stopSignal.Task)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenStopIsRequestedAndTheCoreFinishesWithinTheBound_ThenNothingIsAbandoned()
    {
        // Arrange
        var run = new BoundedTeardownRun(TimeSpan.FromSeconds(30));
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = run.RunAsync(async () =>
        {
            await using var registration = run.ProcessingToken
                .Register(() => processingCancelled.TrySetResult())
                .ConfigureAwait(false);
            await processingCancelled.Task.ConfigureAwait(false);
        }, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.False(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenStopIsRequestedAndTheCoreHangs_ThenTheCallerIsReleasedAtTheBound()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = run.RunAsync(() => releaseCore.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
        Assert.True(run.TeardownToken.IsCancellationRequested);
        releaseCore.TrySetResult();
    }

    [Fact]
    public async Task WhenTheCoreFaultsIntoFinalizationAndHangs_ThenTheBoundArmsWithoutAnyStop()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new InvalidOperationException("finalization fault");
        var runTask = run.RunAsync(() =>
        {
            run.MarkFinalizationStarted(fault);
            return releaseCore.Task;
        }, stopSignal.Task);

        // Act
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.NotNull(outcome.Fault);
        Assert.Same(fault, outcome.Fault!.SourceException);
        releaseCore.TrySetResult();
    }

    [Fact]
    public async Task WhenFinalizationWasAlreadyMarkedClean_ThenALaterFaultReportIsIgnored()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = run.RunAsync(() =>
        {
            run.MarkFinalizationStarted();
            run.MarkFinalizationStarted(new InvalidOperationException("reported second"));
            return releaseCore.Task;
        }, stopSignal.Task);

        // Act
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
        releaseCore.TrySetResult();
    }

    [Fact]
    public async Task WhenStopWasObservedBeforeTheFaultReport_ThenAbandonmentCarriesNoFault()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = run.ProcessingToken
            .Register(() => processingCancelled.TrySetResult())
            .ConfigureAwait(false);
        var runTask = run.RunAsync(() => releaseCore.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        await processingCancelled.Task.WaitAsync(TestTimeout);
        run.MarkFinalizationStarted(new InvalidOperationException("reported after the stop"));
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
        releaseCore.TrySetResult();
    }
}
