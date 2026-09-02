namespace Namotion.Interceptor.Connectors.Tests;

public class BoundedTeardownRunTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(100);

    // Strictly above TestTimeout, so a test that hangs fails on its own timeout instead of
    // ambiguously racing the bound it meant to keep out of reach.
    private static readonly TimeSpan UnreachableBound = TimeSpan.FromMinutes(5);

    // A stop that never arrives; the name is the precondition the tests using it depend on.
    private static readonly Task NoStopRequested = new TaskCompletionSource().Task;

    [Fact]
    public async Task WhenTheCoreCompletesOnItsOwn_ThenNothingIsAbandoned()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);

        // Act
        var outcome = await run.RunAsync(() => Task.CompletedTask, NoStopRequested).WaitAsync(TestTimeout);

        // Assert
        Assert.False(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenTheCoreThrows_ThenTheExceptionPropagatesToTheCaller()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(() => throw fault, NoStopRequested)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenTheCoreFaultsAfterReportingTheFault_ThenTheOriginalExceptionStillPropagates()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(() =>
            {
                run.MarkFinalizationStarted(fault);
                throw fault;
            }, NoStopRequested)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenStopIsRequestedAndTheCoreFinishesWithinTheBound_ThenNothingIsAbandoned()
    {
        // Arrange
        var run = new BoundedTeardownRun(UnreachableBound);
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
        using var core = new HungCore();
        var runTask = run.RunAsync(() => core.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
        Assert.True(run.TeardownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WhenTheCoreFaultsIntoFinalizationAndHangs_ThenTheBoundArmsWithoutAnyStop()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        using var core = new HungCore();
        var fault = new InvalidOperationException("finalization fault");
        var runTask = run.RunAsync(() =>
        {
            run.MarkFinalizationStarted(fault);
            return core.Task;
        }, NoStopRequested);

        // Act
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.NotNull(outcome.Fault);
        Assert.Same(fault, outcome.Fault!.SourceException);
    }

    [Fact]
    public async Task WhenFinalizationWasAlreadyMarkedClean_ThenALaterFaultReportIsIgnored()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        using var core = new HungCore();
        var runTask = run.RunAsync(() =>
        {
            run.MarkFinalizationStarted();
            run.MarkFinalizationStarted(new InvalidOperationException("reported second"));
            return core.Task;
        }, NoStopRequested);

        // Act
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenStopWasObservedBeforeTheFaultReport_ThenAbandonmentCarriesNoFault()
    {
        // Arrange
        var run = new BoundedTeardownRun(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = run.ProcessingToken
            .Register(() => processingCancelled.TrySetResult())
            .ConfigureAwait(false);
        using var core = new HungCore();
        var runTask = run.RunAsync(() => core.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        await processingCancelled.Task.WaitAsync(TestTimeout);
        run.MarkFinalizationStarted(new InvalidOperationException("reported after the stop"));
        var outcome = await runTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.AbandonedAtBound);
        Assert.Null(outcome.Fault);
    }

    // An abandoned core the test releases on scope exit, even when an assert fails first.
    private sealed class HungCore : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _completion.Task;

        public void Dispose() => _completion.TrySetResult();
    }
}
